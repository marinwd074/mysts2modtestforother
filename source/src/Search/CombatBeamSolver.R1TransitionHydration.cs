using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private bool CanUseR1TransitionHydration(
        PlanAction action,
        ReplayForkSeed? replayForkSeed = null,
        RoundReplayCheckpointCapture? roundCheckpointCapture = null,
        CardChoiceReplayCapture? cardChoiceCapture = null)
        => policy.R1TransitionHydrationCache != null
            && !string.IsNullOrEmpty(policy.R0TransitionPolicyIdentity)
            && !policy.VerifyIncrementalSearch
            && replayForkSeed == null
            && roundCheckpointCapture == null
            && cardChoiceCapture == null
            && _executionChoiceReplayCheckpoint == null
            && _cardChoiceReplayCheckpoint == null
            && _potionChoiceReplayCheckpoint == null
            && _roundReplayCheckpoint == null
            && CombatTransitionMemo.IsActionEligibleForTesting(action);

    private ReplayCacheKey R1TransitionHydrationKey(
        SearchNode parent,
        PlanAction action)
        => new(
            parent.StateKey,
            PolicyActionIdentityToken(action),
            policy.R0TransitionPolicyIdentity,
            ActionReplayCache.CurrentContractVersion);

    private bool TryReadR1TransitionHydration(
        SearchNode parent,
        PlanAction action,
        bool eligible,
        out SimulationSnapshot snapshot)
    {
        snapshot = null!;
        if (_continuationSeedProbe
            || !eligible
            || policy.R1TransitionHydrationCache is not { } cache)
        {
            return false;
        }

        ReplayCacheKey key = R1TransitionHydrationKey(parent, action);
        if (!cache.MayContain(key))
            return false;

        ContinuationStamp parentStamp = ContinuationStamp.CapturePredicted(
            _player,
            parent.Snapshot.Simulator,
            parent.Turn,
            _forecast,
            _startTurnNumber);

        SearchMeasurement forkMeasurement = _run.Performance.Begin();
        R1TransitionHydrationSeed seed = null!;
        bool found;
        try
        {
            found = cache.TryFork(
                key,
                parentStamp.CombatIdentity,
                parentStamp.StateText,
                out seed);
        }
        finally
        {
            _run.Performance.End(SearchMetricPhase.Fork, forkMeasurement);
        }
        if (!found)
            return false;

        _run.ForkCount++;
        SimulationSnapshot hydrated = Snapshot(
            seed.Simulator,
            seed.Turn,
            parent.ActionCount + 1,
            seed.ShufflesCrossed,
            seed.BoundaryReason,
            seed.ProcessedEnemyDeaths);

        if (hydrated.StateKey != seed.ExpectedOutputStateKey)
        {
            hydrated.ReleaseSimulator();
            cache.RecordOutputMismatch(key);
            return false;
        }

        cache.RecordHydrationHit();
        _run.TransitionCount++;
        _run.TransitionCacheHits++;
        _run.R1TransitionHydrationHits++;
        snapshot = hydrated;
        return true;
    }

    private void ObserveR1TransitionHydration(
        SearchNode parent,
        PlanAction action,
        SimulationSnapshot output,
        bool eligible)
    {
        if (!eligible
            || policy.R1TransitionHydrationCache is not { } cache)
        {
            return;
        }

        ReplayCacheKey key = R1TransitionHydrationKey(parent, action);
        if (!_continuationSeedProbe && !cache.MayContain(key))
            return;

        ContinuationStamp parentStamp = ContinuationStamp.CapturePredicted(
            _player,
            parent.Snapshot.Simulator,
            parent.Turn,
            _forecast,
            _startTurnNumber);
        ContinuationStamp outputStamp = ContinuationStamp.CapturePredicted(
            _player,
            output.Simulator,
            output.Turn,
            _forecast,
            _startTurnNumber);

        if (_continuationSeedProbe)
        {
            cache.Store(
                key,
                parentStamp.CombatIdentity,
                parentStamp.StateText,
                outputStamp.StateText,
                output);
            return;
        }

        if (!cache.MayContain(key))
            return;
        cache.ValidateRealReplay(
            key,
            parentStamp.CombatIdentity,
            parentStamp.StateText,
            output.StateKey,
            outputStamp.StateText);
    }

    private void StoreR1TransitionHydration(
        SearchNode parent,
        PlanAction action,
        SimulationSnapshot output)
    {
        bool eligible = CanUseR1TransitionHydration(action);
        if (_continuationSeedProbe)
            ObserveR1TransitionHydration(parent, action, output, eligible);
    }
}
