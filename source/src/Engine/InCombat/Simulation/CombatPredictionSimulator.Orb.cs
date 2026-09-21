using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Orbs;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    private const int MaxSimulatedChanneledOrbs = 1000;

    public void AddOrbSlots(Player player, int amount)
    {
        if (amount < 0)
            throw new ArgumentOutOfRangeException(nameof(amount));
        SimOrbQueue queue = State.GetPlayerCombatState(player).OrbQueue;
        int added = Math.Min(OrbQueue.maxCapacity - queue.Capacity, amount);
        if (added > 0)
            queue.AddCapacity(added);
    }

    // Mirrors OrbModel.TriggerPassive without VFX/SFX, waits, or real model-stack updates.
    internal void TriggerOrbPassive(
        OrbModel orb,
        Creature? target,
        ISet<uint>? processedEnemyDeaths = null)
    {
        if (HasPendingChoice)
            return;

        processedEnemyDeaths ??= new HashSet<uint>();
        var triggerCount = HookMirrors.ModifyOrbPassiveTriggerCount(this, orb, 1, out _);
        if (HasPendingChoice)
        {
            AppendExecutionContinuation(
                new OrbPassiveTriggerExecutionFrame(
                    orb,
                    target,
                    triggerCount,
                    NextIndex: 0,
                    processedEnemyDeaths));
            return;
        }
        // Vanilla calls Hook.AfterModifyingOrbPassiveTriggerCount here, but all listeners are cosmetic.
        _ = ContinueOrbPassiveTriggers(
            orb,
            target,
            triggerCount,
            nextIndex: 0,
            processedEnemyDeaths);
    }

    // Mirrors OrbCmd.Channel<T> without mutating the real orb queue.
    public void OrbChannel<T>(Player player, int count = 1) where T : OrbModel
        => _ = ContinueOrbChannelBatch<T>(player, count, nextIndex: 0);

    // Mirrors OrbCmd.Channel without VFX/SFX, waits, real queue mutation, or async hook execution.
    public bool OrbChannel(Player player, OrbModel orb)
    {
        if (IsOverOrEnding || HasPendingChoice)
        {
            return false;
        }

        if (History.Count<CombatPredictionOrbChanneledEntry>() >= MaxSimulatedChanneledOrbs)
        {
            History.RecordRisk(PredictionRiskReason.OrbChannelLimitExceeded);
            return false;
        }

        var orbQueue = State.GetPlayerCombatState(player).OrbQueue;
        if (player.Character.BaseOrbSlotCount == 0 && orbQueue.Capacity == 0)
        {
            orbQueue.AddCapacity(1);
        }

        orb.AssertMutable();
        orb.Owner = player;

        if (orbQueue.Capacity > 0 && orbQueue.Orbs.Count >= orbQueue.Capacity)
        {
            OrbEvokeNext(player);
            if (HasPendingChoice)
            {
                AppendExecutionContinuation(new OrbChannelExecutionFrame(player, orb));
                return false;
            }

        }

        return ContinueOrbChannelAfterEvoke(player, orb);
    }

    private bool ContinueOrbChannelAfterEvoke(Player player, OrbModel orb)
    {
        var orbQueue = State.GetPlayerCombatState(player).OrbQueue;

        // Vanilla resumes after EvokeNext and immediately attempts the enqueue. If Evoke
        // side effects refill the slot, do not restart OrbChannel and evoke a second orb.
        if (orbQueue.Capacity > 0 && orbQueue.Orbs.Count >= orbQueue.Capacity)
        {
            History.RecordRisk(PredictionRiskReason.MethodMirrorIncomplete);
            return false;
        }

        if (!orbQueue.TryEnqueue(orb))
            return false;

        History.OrbChanneled(orb);
        HookMirrors.AfterOrbChanneled(this, player, orb);
        return !HasPendingChoice;
    }

    // Mirrors OrbCmd.EvokeNext without mutating the real orb queue.
    public void OrbEvokeNext(Player player, int repeat = 1, bool dequeue = true)
    {
        if (HasPendingChoice)
            return;

        var orbQueue = State.GetPlayerCombatState(player).OrbQueue;
        if (orbQueue.Orbs.Count == 0)
            return;

        _ = ContinueOrbEvokeNext(
            player,
            orbQueue.Orbs[0],
            repeat,
            dequeue,
            nextIndex: 0,
            resolveDeathsFirst: false,
            new HashSet<uint>());
    }

    // Mirrors OrbCmd.Evoke without VFX/SFX, choice-context model stack updates, or real queue mutation.
    public void OrbEvoke(Player player, OrbModel evokedOrb, bool dequeue = true)
    {
        if (IsOverOrEnding || HasPendingChoice)
        {
            return;
        }

        var orbQueue = State.GetPlayerCombatState(player).OrbQueue;
        if (orbQueue.Orbs.Count <= 0)
        {
            return;
        }

        if (dequeue)
        {
            _ = orbQueue.Remove(evokedOrb);
        }

        var targets = OrbMirrors.InvokeEvoke(this, evokedOrb);
        if (HasPendingChoice)
        {
            AppendExecutionContinuation(
                new OrbEvokeAfterModelExecutionFrame(evokedOrb, targets.ToArray()));
            return;
        }

        // Vanilla calls evokedOrb.RemoveInternal after AfterOrbEvoked when dequeue succeeds.
        // We only remove from the shadow queue because mutating the real orb would affect
        // gameplay state and save data.
        HookMirrors.AfterOrbEvoked(this, evokedOrb, targets);
    }

    // Mirrors OrbCmd.Passive without VFX/SFX, choice-context model stack updates, or real orb mutation.
    public void OrbPassive(
        OrbModel orb,
        Creature? target = null,
        ISet<uint>? processedEnemyDeaths = null)
    {
        if (IsOverOrEnding || HasPendingChoice)
        {
            return;
        }

        processedEnemyDeaths ??= new HashSet<uint>();
        OrbMirrors.InvokePassive(this, orb, target);
        if (HasPendingChoice)
        {
            if (State.CombatState is ICombatPredictionEnemyDeathSink)
            {
                AppendExecutionContinuation(
                    new OrbPassiveAfterModelExecutionFrame(processedEnemyDeaths));
            }
            return;
        }

        _ = ContinueOrbPassiveAfterModel(new HashSet<uint>(processedEnemyDeaths));
    }
}
