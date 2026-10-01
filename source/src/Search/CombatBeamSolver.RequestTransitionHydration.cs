using System.Globalization;
using System.Text;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private const int RequestHydrationContractVersion = 1;
    private const int RequestHydrationMaximumPrefixActions = 8;
    private string? _requestHydrationPolicyIdentity;

    private bool TryRequestHydrationKey(
        SearchNode parent, PlanAction action, out ReplayCacheKey key,
        ReplayForkSeed? replayForkSeed = null,
        RoundReplayCheckpointCapture? roundCheckpointCapture = null,
        CardChoiceReplayCapture? cardChoiceCapture = null)
    {
        key = default;
        if (policy.RequestTransitionHydrationCache == null)
            return false;
        using SearchMeasurementScope measure = _run.Performance.Measure(SearchMetricPhase.TransitionHydration);
        using SearchMeasurementScope keyMeasure = policy.RequestTransitionHydrationCache.Measure(SearchMetricPhase.HydrationKey);
        if (policy.VerifyIncrementalSearch
            || _continuationSeedProbe || _includeTurnSetup || policy.CurrentTurnOnly
            || policy.UseMultiplayerTeamObjective || policy.UseMultiplayerTeammateForecast
            || policy.UseMultiplayerScenarioReevaluation
            || replayForkSeed != null || roundCheckpointCapture != null || cardChoiceCapture != null
            || _executionChoiceReplayCheckpoint != null || _cardChoiceReplayCheckpoint != null
            || _potionChoiceReplayCheckpoint != null || _roundReplayCheckpoint != null
            || parent.ActionCount >= RequestHydrationMaximumPrefixActions
            || parent.Turn != _startTurnNumber || !parent.Snapshot.HasSimulator
            || !CombatTransitionMemo.IsActionEligibleForTesting(action))
            return false;

        // Keep the whole path in the key: an equal modeled state does not imply equal
        // prediction history, cumulative loss or resource objectives. Do not materialize Actions.
        StringBuilder path = new();
        AppendAction(path, action);
        int count = 0;
        for (SearchNode? cursor = parent; cursor?.Action is { } prior; cursor = cursor.Parent)
        {
            if (!CombatTransitionMemo.IsActionEligibleForTesting(prior))
                return false;
            AppendAction(path, prior);
            count++;
        }
        if (count != parent.ActionCount)
            return false;

        // Member width/rank band/node/time allowance only control enumeration/retention;
        // this ordinary one-action replay has no Choice or round continuation. Keep the
        // original full policy and the effective branch ceilings; do not weaken R0's identity.
        _requestHydrationPolicyIdentity ??= CombatTransitionMemo.CapturePolicyIdentity(policy)
            + FormattableString.Invariant($":{_profile.MaxCardBranchesPerNode}:{_profile.MaxPileChoiceBranchesPerAction}:{_profile.MaxHandChoiceBranchesPerAction}");
        key = new(parent.StateKey, path.ToString(), _requestHydrationPolicyIdentity,
            RequestHydrationContractVersion);
        return true;

        static void AppendAction(StringBuilder text, PlanAction value)
        {
            text.Append(value.Turn).Append('/');
            AppendText(value.CardId);
            text.Append(value.CardOccurrence).Append('/').Append(value.TargetIndex).Append('/')
                .Append(value.TargetCombatId?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('/');
            AppendText(value.CardStateKey);
            text.Append(value.CardStateOccurrence).Append('/').Append(value.CardUpgradeLevel).Append('/');
            AppendText(value.CardEnchantmentId);
            void AppendText(string item) => text.Append(item.Length).Append(':').Append(item).Append('/');
        }
    }

    private ContinuationStamp CaptureRequestHydrationParent(SearchNode parent)
    {
        using SearchMeasurementScope measure = policy.RequestTransitionHydrationCache!.Measure(SearchMetricPhase.HydrationParentValidation);
        SimulationSnapshot snapshot = parent.Snapshot;
        ContinuationStamp stamp = ContinuationStamp.CapturePredicted(
            _player, snapshot.Simulator, parent.Turn, _forecast, _startTurnNumber);
        return stamp with
        {
            StateText = stamp.StateText + FormattableString.Invariant(
                $";hydration_path={parent.ActionCount}/{snapshot.HistoryEntryCount}/{snapshot.CumulativePlayerHpLost}/{snapshot.RecoveredPlayerHp}/{snapshot.TeamCumulativeHpLost}/{snapshot.TeamLossRatio:R}/{snapshot.WorstPlayerLossRatio:R}")
        };
    }

    private bool TryReadRequestTransitionHydration(
        SearchNode parent, ReplayCacheKey key, out SimulationSnapshot snapshot)
    {
        snapshot = null!;
        R1TransitionHydrationCache cache = policy.RequestTransitionHydrationCache!;
        using SearchMeasurementScope measure = _run.Performance.Measure(SearchMetricPhase.TransitionHydration);
        using (cache.Measure(SearchMetricPhase.HydrationLookup))
            // Unvalidated metadata cannot hydrate. Its independent replay will capture
            // and compare the full parent/output once; do not capture the parent twice.
            if (!cache.MayHydrate(key))
                return false;
        ContinuationStamp parentStamp = CaptureRequestHydrationParent(parent);
        R1TransitionHydrationSeed seed;
        using (cache.Measure(SearchMetricPhase.HydrationFork))
            if (!cache.TryFork(key, parentStamp.CombatIdentity, parentStamp.StateText, out seed))
                return false;

        _run.ForkCount++;
        SimulationSnapshot hydrated;
        using (cache.Measure(SearchMetricPhase.HydrationSnapshot))
            hydrated = Snapshot(seed.Simulator, seed.Turn, parent.ActionCount + 1,
                seed.ShufflesCrossed, seed.BoundaryReason, seed.ProcessedEnemyDeaths);
        if (hydrated.StateKey != seed.ExpectedOutputStateKey)
        {
            hydrated.ReleaseSimulator();
            cache.RecordOutputMismatch(key);
            return false;
        }
        cache.RecordHydrationHit();
        _run.TransitionCount++;
        _run.TransitionCacheHits++;
        snapshot = hydrated;
        return true;
    }

    private void ObserveRequestTransitionHydration(
        SearchNode parent, ReplayCacheKey key, SimulationSnapshot output)
    {
        R1TransitionHydrationCache cache = policy.RequestTransitionHydrationCache!;
        using SearchMeasurementScope measure = _run.Performance.Measure(SearchMetricPhase.TransitionHydration);
        using (cache.Measure(SearchMetricPhase.HydrationLookup))
            if (!cache.CanObserveSearchReplay(key))
                return;
        if (!output.HasSimulator || output.BoundaryReason != SearchBoundaryReason.None
            || output.PlayerDead || output.AllEnemiesDead || output.HasRisk
            || output.PredictionGaps.Any(static gap => !gap.Compensated)
            || _executionChoiceReplayCheckpoint != null || _cardChoiceReplayCheckpoint != null)
        {
            if (cache.MayContain(key))
                cache.RecordOutputMismatch(key);
            return;
        }
        ContinuationStamp parentStamp = CaptureRequestHydrationParent(parent);
        ContinuationStamp outputStamp;
        using (cache.Measure(SearchMetricPhase.HydrationOutputValidation))
            outputStamp = ContinuationStamp.CapturePredicted(
                _player, output.Simulator, output.Turn, _forecast, _startTurnNumber);
        using (cache.Measure(SearchMetricPhase.HydrationStore))
            cache.ObserveSearchReplay(key, parentStamp.CombatIdentity, parentStamp.StateText,
                outputStamp.StateText, output);
    }
}
