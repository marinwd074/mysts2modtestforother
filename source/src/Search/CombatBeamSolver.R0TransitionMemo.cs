using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    internal const int ProductionShadowReplayObservationLimit = 64;
    internal const int ProductionShadowReplayFutureTurnReserve = 16;
    internal int R0TerminalStoreTextCapturesForTesting { get; private set; }

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
            CaptureR0TerminalEvaluationContext(parent.Snapshot),
            parent.ActionCount + 1,
            parent.Snapshot.HistoryEntryCount,
            out snapshot);
    }

    private void StoreR0TerminalTransition(
        SearchNode parent,
        PlanAction action,
        SimulationSnapshot output)
    {
        if (!CombatTransitionMemo.IsSafeTerminalOutput(output))
            return;
        CombatTransitionMemo memo = policy.R0TransitionMemo!;
        R0TerminalStoreTextCapturesForTesting++;
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
            CaptureR0TerminalEvaluationContext(parent.Snapshot),
            parent.Snapshot.HistoryEntryCount,
            CaptureR0PlayerLosses(output),
            output,
            IsPureTransitionForR0Memo(parent.Snapshot, output));
    }

    private CombatTransitionMemo.TerminalEvaluationContext CaptureR0TerminalEvaluationContext(
        SimulationSnapshot parent)
        => new(
            root.InitialPlayerMaxHp,
            _strategicBossHpRelief,
            root.PostCombatRelicHeal,
            parent.CumulativePlayerHpLost,
            parent.RecoveredPlayerHp,
            CaptureR0PlayerLosses(parent));

    private IReadOnlyList<CombatTransitionMemo.TerminalPlayerLoss> CaptureR0PlayerLosses(
        SimulationSnapshot snapshot)
    {
        CombatPredictionSimulator simulator = snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        return simulator.State.RootCapturedPlayers
            .Select(player => new CombatTransitionMemo.TerminalPlayerLoss(
                player.NetId.ToString(), root.CapturedPlayerMaxHp(player),
                Math.Max(0, combat.GetCumulativeHpLost(player.Creature))))
            .ToArray();
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
