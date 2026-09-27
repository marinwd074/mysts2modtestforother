using System.Security.Cryptography;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

/// <summary>
/// Exact combat-scoped R0 memo. Entries contain no live simulator and are never deployment authority.
/// </summary>
internal sealed class CombatTransitionMemo
{
    private const int MaximumEntries = 4096;
    private readonly object _gate = new();
    private readonly Dictionary<R0TransitionIndexKey, List<Entry>> _entries = [];
    private string? _combatIdentity;
    private int _entryCount;
    private int _hits;
    private int _collisionRejects;
    private int _droppedStores;

    private sealed record Entry(string ParentStateText, SimulationSnapshot Output);

    internal int EntryCount { get { lock (_gate) return _entryCount; } }
    internal int Hits { get { lock (_gate) return _hits; } }
    internal int CollisionRejects { get { lock (_gate) return _collisionRejects; } }
    internal int DroppedStores { get { lock (_gate) return _droppedStores; } }

    internal void BindCombat(string combatIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(combatIdentity);
        lock (_gate)
        {
            if (string.Equals(_combatIdentity, combatIdentity, StringComparison.Ordinal))
                return;
            _combatIdentity = combatIdentity;
            _entries.Clear();
            _entryCount = 0;
            _hits = 0;
            _collisionRejects = 0;
            _droppedStores = 0;
        }
    }

    internal static string CapturePolicyIdentity(SearchPolicySnapshot policy)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = 1,
            Solver = typeof(CombatTransitionMemo).Module.ModuleVersionId,
            Game = typeof(CombatState).Module.ModuleVersionId,
            policy.Profile,
            policy.RoutePolicy,
            policy.CurrentTurnOnly,
            policy.UseMultiplayerTeamObjective,
            policy.MultiplayerCombatObjectiveStrategy,
            policy.MultiplayerEnemyDurabilityRatio,
            policy.UseMultiplayerTeammateForecast,
            policy.UseMultiplayerScenarioReevaluation,
            policy.PotionPolicy,
            policy.PotionStrategy.Directives,
            policy.GrowthBudgets,
            policy.RelicTargets,
            policy.Act3BossStrategy,
            policy.BrightestFlameMaxHpLossLimit,
            policy.GrowthOpportunityTargets,
            policy.IgnoreLongTermRewards,
            policy.IncludeTurnSetup,
            policy.TheftPolicy,
            policy.ActTransitionBossHpStrategy,
            policy.FinalBossHpStrategy,
            policy.AcceptableBattleHpLoss,
            policy.StopAtAcceptableBattleHpLoss,
        });
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    internal bool MayContain(StateFingerprint parentState, PlanAction action, string policyIdentity)
    {
        if (!R0TransitionActionKey.TryCreate(action, out R0TransitionActionKey actionKey))
            return false;
        lock (_gate)
            return _entries.ContainsKey(new(parentState, actionKey, policyIdentity));
    }

    internal bool TryReadTerminal(
        StateFingerprint parentState,
        PlanAction action,
        string policyIdentity,
        string parentStateText,
        out SimulationSnapshot snapshot)
    {
        snapshot = null!;
        if (!R0TransitionActionKey.TryCreate(action, out R0TransitionActionKey actionKey))
            return false;
        R0TransitionIndexKey key = new(parentState, actionKey, policyIdentity);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out List<Entry>? bucket))
                return false;
            foreach (Entry entry in bucket)
            {
                if (!string.Equals(entry.ParentStateText, parentStateText, StringComparison.Ordinal))
                    continue;
                _hits++;
                snapshot = entry.Output.CloneValueOnlyForTransitionMemo();
                return true;
            }
            _collisionRejects++;
            return false;
        }
    }

    internal void StoreTerminal(
        StateFingerprint parentState,
        PlanAction action,
        string policyIdentity,
        string parentStateText,
        SimulationSnapshot output)
    {
        if (!R0TransitionActionKey.TryCreate(action, out R0TransitionActionKey actionKey)
            || !IsSafeTerminalOutput(output))
            return;

        R0TransitionIndexKey key = new(parentState, actionKey, policyIdentity);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out List<Entry>? bucket))
            {
                if (bucket.Any(entry =>
                        string.Equals(entry.ParentStateText, parentStateText, StringComparison.Ordinal)))
                    return;
            }
            else
            {
                bucket = [];
            }

            if (_entryCount >= MaximumEntries)
            {
                _droppedStores++;
                return;
            }
            if (!_entries.ContainsKey(key))
                _entries.Add(key, bucket);
            bucket.Add(new Entry(parentStateText, output.CloneValueOnlyForTransitionMemo()));
            _entryCount++;
        }
    }

    internal bool ContainsIndexForTesting(
        StateFingerprint parentState,
        PlanAction action,
        string policyIdentity)
        => MayContain(parentState, action, policyIdentity);

    internal static bool IsActionEligibleForTesting(PlanAction action)
        => R0TransitionActionKey.TryCreate(action, out _);

    private static bool IsSafeTerminalOutput(SimulationSnapshot output)
        => output.BoundaryReason == SearchBoundaryReason.None
            && (output.PlayerDead || output.AllEnemiesDead)
            && !output.HasRisk
            && output.PredictionGaps.All(static gap => gap.Compensated);

    private readonly record struct R0TransitionIndexKey(
        StateFingerprint ParentState,
        R0TransitionActionKey Action,
        string PolicyIdentity);

    private readonly record struct R0TransitionActionKey(
        int Turn,
        string CardId,
        int CardOccurrence,
        int TargetIndex,
        uint? TargetCombatId,
        string CardStateKey,
        int CardStateOccurrence,
        int CardUpgradeLevel,
        string CardEnchantmentId)
    {
        internal static bool TryCreate(PlanAction action, out R0TransitionActionKey key)
        {
            if (action.Kind != PlanActionKind.PlayCard
                || action.Choice != null
                || action.NestedChoices is { Count: > 0 }
                || action.NestedChoicesBeforePrimary != 0
                || action.TurnStartChoices is { Count: > 0 }
                || action.RelicEffects is { Count: > 0 }
                || action.AutoPlayedCards is { Count: > 0 }
                || action.ShadowForecast != null
                || action.ReplayCount != 0
                || action.EndsPlayerTurn
                || action.PotionSlot >= 0
                || !string.IsNullOrEmpty(action.PotionId))
            {
                key = default;
                return false;
            }

            key = new(
                action.Turn,
                action.CardId,
                action.CardOccurrence,
                action.TargetIndex,
                action.TargetCombatId,
                action.CardStateKey,
                action.CardStateOccurrence,
                action.CardUpgradeLevel,
                action.CardEnchantmentId);
            return true;
        }
    }
}
