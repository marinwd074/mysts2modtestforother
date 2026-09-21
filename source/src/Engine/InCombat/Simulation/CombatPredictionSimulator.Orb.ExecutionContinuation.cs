using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    private bool ContinueOrbChannelBatch<TOrb>(
        Player player,
        int count,
        int nextIndex)
        where TOrb : OrbModel
    {
        for (int index = nextIndex; index < count; index++)
        {
            if (OrbChannel(player, CanonicalModels.Orb<TOrb>().ToMutable()))
                continue;

            if (HasPendingChoice)
            {
                AppendExecutionContinuation(
                    new OrbChannelBatchExecutionFrame<TOrb>(
                        player,
                        count,
                        index + 1));
            }
            return false;
        }
        return true;
    }

    private bool ContinueOrbEvokeNext(
        Player player,
        OrbModel orb,
        int repeat,
        bool dequeue,
        int nextIndex,
        bool resolveDeathsFirst,
        ISet<uint> processedEnemyDeaths)
    {
        if (resolveDeathsFirst && State.CombatState is ICombatPredictionEnemyDeathSink initialDeathSink)
        {
            initialDeathSink.ResolvePendingEnemyDeaths(this, processedEnemyDeaths);
            if (HasPendingChoice)
            {
                AppendExecutionContinuation(
                    new OrbEvokeNextExecutionFrame(
                        player,
                        orb,
                        repeat,
                        dequeue,
                        nextIndex,
                        ResolveDeathsFirst: false,
                        processedEnemyDeaths));
                return false;
            }
        }

        for (int index = nextIndex; index < repeat; index++)
        {
            OrbEvoke(player, orb, dequeue: dequeue && index == repeat - 1);
            if (HasPendingChoice)
            {
                AppendExecutionContinuation(
                    new OrbEvokeNextExecutionFrame(
                        player,
                        orb,
                        repeat,
                        dequeue,
                        index + 1,
                        ResolveDeathsFirst: true,
                        processedEnemyDeaths));
                return false;
            }

            if (State.CombatState is not ICombatPredictionEnemyDeathSink deathSink)
                continue;

            deathSink.ResolvePendingEnemyDeaths(this, processedEnemyDeaths);
            if (HasPendingChoice)
            {
                AppendExecutionContinuation(
                    new OrbEvokeNextExecutionFrame(
                        player,
                        orb,
                        repeat,
                        dequeue,
                        index + 1,
                        ResolveDeathsFirst: false,
                        processedEnemyDeaths));
                return false;
            }
        }

        return true;
    }

    private bool ContinueOrbPassiveTriggers(
        OrbModel orb,
        Creature? target,
        int triggerCount,
        int nextIndex,
        ISet<uint> processedEnemyDeaths)
    {
        for (int index = nextIndex; index < triggerCount; index++)
        {
            OrbPassive(orb, target, processedEnemyDeaths);
            if (!HasPendingChoice)
                continue;

            AppendExecutionContinuation(
                new OrbPassiveTriggerExecutionFrame(
                    orb,
                    target,
                    triggerCount,
                    index + 1,
                    processedEnemyDeaths));
            return false;
        }

        return true;
    }

    private bool ContinueOrbPassiveAfterModel(ISet<uint> processedEnemyDeaths)
    {
        if (State.CombatState is ICombatPredictionEnemyDeathSink deathSink)
        {
            deathSink.ResolvePendingEnemyDeaths(this, processedEnemyDeaths);
            if (HasPendingChoice)
                return false;
        }
        return true;
    }

    private static void PrepareExecutionOrb(
        OrbModel orb,
        PredictionForkContext context)
    {
        if (context.TryRemap(orb, out OrbModel? _))
            return;

        OrbModel fork = PredictionUtils.CloneModelForSimulation(orb);
        context.Register(orb, fork);
    }

    private static void PrepareExecutionEnemyDeathSet(
        ISet<uint> processedEnemyDeaths,
        PredictionForkContext context)
    {
        if (context.TryRemap(processedEnemyDeaths, out ISet<uint>? _))
            return;

        context.Register<ISet<uint>>(
            processedEnemyDeaths,
            new HashSet<uint>(processedEnemyDeaths));
    }

    private sealed record OrbChannelBatchExecutionFrame<TOrb>(
        Player Player,
        int Count,
        int NextIndex) : ICombatPredictionExecutionFrame
        where TOrb : OrbModel
    {
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context) => this;

        public bool Resume(CombatPredictionSimulator simulator)
            => simulator.ContinueOrbChannelBatch<TOrb>(Player, Count, NextIndex);
    }

    private sealed record OrbChannelExecutionFrame(
        Player Player,
        OrbModel Orb) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
            => PrepareExecutionOrb(Orb, context);

        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Orb = context.RequireRemap(Orb) };

        public bool Resume(CombatPredictionSimulator simulator)
            => simulator.ContinueOrbChannelAfterEvoke(Player, Orb);
    }

    private sealed record OrbEvokeAfterModelExecutionFrame(
        OrbModel Orb,
        IReadOnlyList<Creature> Targets) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
            => PrepareExecutionOrb(Orb, context);

        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Orb = context.RequireRemap(Orb) };

        public bool Resume(CombatPredictionSimulator simulator)
        {
            HookMirrors.AfterOrbEvoked(simulator, Orb, Targets);
            return !simulator.HasPendingChoice;
        }
    }

    private sealed record OrbEvokeNextExecutionFrame(
        Player Player,
        OrbModel Orb,
        int Repeat,
        bool Dequeue,
        int NextIndex,
        bool ResolveDeathsFirst,
        ISet<uint> ProcessedEnemyDeaths) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
        {
            PrepareExecutionOrb(Orb, context);
            PrepareExecutionEnemyDeathSet(ProcessedEnemyDeaths, context);
        }

        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with
            {
                Orb = context.RequireRemap(Orb),
                ProcessedEnemyDeaths = context.RequireRemap(ProcessedEnemyDeaths)
            };

        public bool Resume(CombatPredictionSimulator simulator)
            => simulator.ContinueOrbEvokeNext(
                Player,
                Orb,
                Repeat,
                Dequeue,
                NextIndex,
                ResolveDeathsFirst,
                ProcessedEnemyDeaths);
    }

    private sealed record OrbPassiveTriggerExecutionFrame(
        OrbModel Orb,
        Creature? Target,
        int TriggerCount,
        int NextIndex,
        ISet<uint> ProcessedEnemyDeaths) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
        {
            PrepareExecutionOrb(Orb, context);
            PrepareExecutionEnemyDeathSet(ProcessedEnemyDeaths, context);
        }

        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with
            {
                Orb = context.RequireRemap(Orb),
                ProcessedEnemyDeaths = context.RequireRemap(ProcessedEnemyDeaths)
            };

        public bool Resume(CombatPredictionSimulator simulator)
            => simulator.ContinueOrbPassiveTriggers(
                Orb,
                Target,
                TriggerCount,
                NextIndex,
                ProcessedEnemyDeaths);
    }

    private sealed record OrbPassiveAfterModelExecutionFrame(
        ISet<uint> ProcessedEnemyDeaths) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
            => PrepareExecutionEnemyDeathSet(ProcessedEnemyDeaths, context);

        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { ProcessedEnemyDeaths = context.RequireRemap(ProcessedEnemyDeaths) };

        public bool Resume(CombatPredictionSimulator simulator)
            => simulator.ContinueOrbPassiveAfterModel(ProcessedEnemyDeaths);
    }
}
