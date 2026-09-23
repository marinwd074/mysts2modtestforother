using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static partial class SolverController
{
    private sealed record SafeExecutionExpectedPostAction(
        ContinuationStamp Continuation,
        StateFingerprint ContinuationFingerprint,
        StateFingerprint RemoteFingerprint,
        SearchBoundaryReason BoundaryReason);

    private sealed record SafeExecutionPostActionComparison(
        bool ContinuationMatched,
        bool RemoteMatched,
        ContinuationStamp ActualContinuation,
        StateFingerprint ActualContinuationFingerprint,
        StateFingerprint ActualRemoteFingerprint,
        IReadOnlyList<string> ContinuationDifferences);

    /// <summary>
    /// Replays exactly one already-selected local action from the current live root using the
    /// production combat simulator. The detached replay is prediction-only and never widens
    /// RootActionPlayers or submits a native action.
    /// </summary>
    private static async Task<SafeExecutionExpectedPostAction>
        CaptureExpectedSafeExecutionPostActionAsync(
            CombatState state,
            SolverDeploymentSession deployment,
            PlanAction action,
            CancellationToken token)
    {
        AssertMainThread();
        SearchPolicySnapshot replayPolicy = deployment.SafeReplayPolicy
            ?? throw new InvalidOperationException(
                "多人 Safe Execute 缺少 U1 动作后态回放策略。");
        SolverDisplayNames displayNames = SolverDisplayNames.Capture(state);
        BattleDamageSnapshot battleDamage = BattleDamageTracker.Observe(state);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(state);
        long startedAt = Environment.TickCount64;

        SafeExecutionExpectedPostAction expected = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            CombatBeamSolver replay = new(
                root,
                displayNames,
                battleDamage,
                replayPolicy,
                token);
            SimulationSnapshot snapshot = replay.ReplayDiagnosticPrefix([action]);
            try
            {
                if (snapshot.BoundaryReason != SearchBoundaryReason.None)
                {
                    throw new InvalidOperationException(
                        $"U1 one-action replay reached boundary {snapshot.BoundaryReason} " +
                        $"for {action.CardId ?? action.Kind.ToString()}.");
                }

                ContinuationStamp continuation =
                    replay.CaptureDiagnosticContinuation(snapshot);
                StateFingerprint remote =
                    MultiplayerContinuationRemoteFingerprint.CapturePredicted(
                        snapshot.Simulator,
                        root.PlayerIdentity);
                return new SafeExecutionExpectedPostAction(
                    continuation,
                    FingerprintSafeContinuation(continuation),
                    remote,
                    snapshot.BoundaryReason);
            }
            finally
            {
                snapshot.ReleaseSimulator();
            }
        }, token);

        Entry.Logger.Info(
            $"[CombatSolver/MultiplayerSafeExecute] U1_EXPECTED_POST_STATE " +
            $"action={action.CardId ?? action.Kind.ToString()} " +
            $"state_fp={FormatSafeFingerprint(expected.ContinuationFingerprint)} " +
            $"remote_fp={FormatSafeFingerprint(expected.RemoteFingerprint)} " +
            $"boundary={expected.BoundaryReason} " +
            $"elapsed_ms={Environment.TickCount64 - startedAt}");
        return expected;
    }

    private static SafeExecutionPostActionComparison CompareSafeExecutionPostAction(
        CombatState state,
        Player localPlayer,
        SafeExecutionExpectedPostAction expected)
    {
        ContinuationStamp actualContinuation =
            ContinuationStamp.CaptureLive(state, state.Players.ToArray());
        StateFingerprint actualRemote =
            MultiplayerContinuationRemoteFingerprint.CaptureLive(
                state,
                localPlayer);
        IReadOnlyList<string> differences =
            expected.Continuation.DescribeDifferences(
                actualContinuation,
                maximumDifferences: 8);

        return new SafeExecutionPostActionComparison(
            ContinuationMatched: differences.Count == 0,
            RemoteMatched: expected.RemoteFingerprint == actualRemote,
            ActualContinuation: actualContinuation,
            ActualContinuationFingerprint:
                FingerprintSafeContinuation(actualContinuation),
            ActualRemoteFingerprint: actualRemote,
            ContinuationDifferences: differences);
    }

    private static void LogSafeExecutionPostActionComparison(
        MultiplayerSafeExecutionSession safeSession,
        int turn,
        int actionIndex,
        PlanAction action,
        SafeExecutionExpectedPostAction expected,
        SafeExecutionPostActionComparison comparison)
    {
        Entry.Logger.Info(
            $"[CombatSolver/MultiplayerSafeExecute] U1_POST_STATE_COMPARE " +
            $"request_id={safeSession.RequestId} turn={turn} action_index={actionIndex} " +
            $"card={action.CardId ?? "-"} " +
            $"continuation_match={comparison.ContinuationMatched.ToString().ToLowerInvariant()} " +
            $"remote_match={comparison.RemoteMatched.ToString().ToLowerInvariant()} " +
            $"expected_state_fp={FormatSafeFingerprint(expected.ContinuationFingerprint)} " +
            $"actual_state_fp={FormatSafeFingerprint(comparison.ActualContinuationFingerprint)} " +
            $"expected_remote_fp={FormatSafeFingerprint(expected.RemoteFingerprint)} " +
            $"actual_remote_fp={FormatSafeFingerprint(comparison.ActualRemoteFingerprint)} " +
            $"differences=[{FormatSafeDiagnosticTokens(comparison.ContinuationDifferences)}]");
    }

    private static StateFingerprint FingerprintSafeContinuation(
        ContinuationStamp continuation)
    {
        StateFingerprintBuilder fingerprint = new();
        fingerprint.Add(continuation.StateText);
        return fingerprint.Finish();
    }

    private static string FormatSafeFingerprint(StateFingerprint fingerprint)
        => $"{fingerprint.First:X16}:{fingerprint.Second:X16}";
}
