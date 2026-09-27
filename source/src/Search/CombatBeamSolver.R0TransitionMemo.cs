using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    internal const int ProductionShadowReplayObservationLimit = 64;
    internal const int ProductionShadowReplayFutureTurnReserve = 16;

    private bool WantsR0ShadowReplayTiming()
        => policy.DetailedDiagnostics || policy.ShadowReplaySamplingBudget != null;

    private bool CanUseR0TransitionMemoForReplay(
        PlanAction action,
        ReplayForkSeed? replayForkSeed,
        RoundReplayCheckpointCapture? roundCheckpointCapture,
        CardChoiceReplayCapture? cardChoiceCapture)
        => policy.R0TransitionMemo != null
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

    private bool TryReadR0TerminalTransition(
        SearchNode parent,
        PlanAction action,
        out SimulationSnapshot snapshot)
    {
        snapshot = null!;
        CombatTransitionMemo memo = policy.R0TransitionMemo!;
        if (!memo.MayContain(parent.StateKey, action, policy.R0TransitionPolicyIdentity))
            return false;
        string parentStateText = ContinuationStamp.CapturePredicted(
            _player,
            parent.Snapshot.Simulator,
            parent.Turn,
            _forecast,
            _startTurnNumber).StateText;
        return memo.TryReadTerminal(
            parent.StateKey,
            action,
            policy.R0TransitionPolicyIdentity,
            parentStateText,
            out snapshot);
    }

    private void StoreR0TerminalTransition(
        SearchNode parent,
        PlanAction action,
        SimulationSnapshot output)
    {
        CombatTransitionMemo memo = policy.R0TransitionMemo!;
        string parentStateText = ContinuationStamp.CapturePredicted(
            _player,
            parent.Snapshot.Simulator,
            parent.Turn,
            _forecast,
            _startTurnNumber).StateText;
        memo.StoreTerminal(
            parent.StateKey,
            action,
            policy.R0TransitionPolicyIdentity,
            parentStateText,
            output,
            IsPureTransitionForR0Memo(parent.Snapshot, output));
    }

    private static bool IsPureTransitionForR0Memo(
        SimulationSnapshot before,
        SimulationSnapshot after)
    {
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)after.Simulator;
        foreach (CombatPredictionHistoryEntry entry in
                 simulator.History.EntriesFrom(before.HistoryEntryCount))
        {
            if (!IsPureHistoryEntry(entry))
                return false;
        }
        return true;
    }
}
