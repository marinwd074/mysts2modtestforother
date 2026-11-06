using System.Diagnostics;
using CombatSolver.Engine.Common;

namespace CombatSolver;

internal sealed record RetainedPrefixReplay(
    SimulationSnapshot? Snapshot,
    IReadOnlyList<PlanAction> Actions,
    int Transitions,
    string Reason) : IDisposable
{
    public void Dispose() => Snapshot?.ReleaseSimulator();
}

internal sealed partial class CombatBeamSolver
{
    // Prefix replay has no Beam execution session. Give the whole batch one ledger
    // owner, including transitions performed before a rejection or cancellation.
    internal RetainedPrefixWorkScope MeasureRetainedPrefixBatch()
    {
        if (_executionSessionCreated)
            throw new InvalidOperationException("Retained replay work must have one owner.");
        _executionSessionCreated = true;
        BeginSearchEfficiencyMember();
        return new(this, policy.RequestWorkTotals, cancellationToken);
    }

    internal sealed class RetainedPrefixWorkScope(
        CombatBeamSolver owner, SearchRequestWorkTotals? totals, CancellationToken token) : IDisposable
    {
        private readonly IDisposable _isolation = SimulationNotificationIsolation.Enter();
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly long _allocated = GC.GetAllocatedBytesForCurrentThread();
        private readonly int _gen0 = GC.CollectionCount(0), _gen1 = GC.CollectionCount(1), _gen2 = GC.CollectionCount(2);
        private readonly TimeSpan _pause = GC.GetTotalPauseDuration();
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                owner.CompleteExecutionLifecycle(totals,
                    Stopwatch.GetElapsedTime(_started), GC.GetAllocatedBytesForCurrentThread() - _allocated,
                    GC.CollectionCount(0) - _gen0, GC.CollectionCount(1) - _gen1, GC.CollectionCount(2) - _gen2,
                    GC.GetTotalPauseDuration() - _pause, token.IsCancellationRequested ? "Canceled" : "Disposed");
            }
            finally { _isolation.Dispose(); }
        }
    }

    // Unlike diagnostic whole-sequence replay, a retained suggestion is validated
    // action by action on this new root. Victory safely truncates its stale tail.
    internal RetainedPrefixReplay ReplayRetainedPrefix(IReadOnlyList<PlanAction> actions)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int transitionsBefore = _run.TransitionCount;
        SimulationSnapshot snapshot = Replay([]);
        SearchNode node = new(null, 0, snapshot.PotionUseCount, snapshot.PotionStrategicCost,
            _startTurnNumber, SearchRouteTraits.None, 0, snapshot.Score, snapshot.StateKey,
            snapshot.HasRisk, snapshot.BoundaryReason,
            snapshot.PlayerDead || snapshot.AllEnemiesDead || snapshot.BoundaryReason != SearchBoundaryReason.None,
            null, snapshot, CombatProgressState.Capture(snapshot));
        bool transferred = false;
        try
        {
            foreach (PlanAction action in actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SolverInterimResultOrdering.IsCompleteVictory(node.ActionCount,
                    node.Snapshot.AllEnemiesDead, node.Snapshot.PlayerDead, node.Snapshot.ProjectedPlayerHp))
                    break;
                if (policy.RecoveryBudget?.IsExpired == true)
                    return Rejected("prefix_time_budget");
                if (node.IsTerminal || node.Snapshot.HasRisk)
                    return Rejected("prefix_boundary");
                if (action.Kind != PlanActionKind.PlayCard || action.EndsPlayerTurn
                    || !action.IsExecutable || action.ShadowForecast != null || action.Turn != node.Turn)
                    return Rejected("prefix_action_shape");
                SearchNode? next = TryApplyPrefixAction(node, action);
                if (next == null)
                    return Rejected("prefix_action_unavailable");
                node = next;
            }
            if (node.ActionCount == 0 || node.Snapshot.PlayerDead || node.Snapshot.HasRisk
                || node.Snapshot.BoundaryReason != SearchBoundaryReason.None)
                return Rejected("prefix_boundary");

            RetainedPrefixReplay result = new(node.Snapshot, Array.AsReadOnly(actions.Take(node.ActionCount).ToArray()),
                _run.TransitionCount - transitionsBefore,
                node.ActionCount < actions.Count ? "victory_truncated" : "complete_prefix");
            transferred = true;
            return result;
        }
        catch (PredictionUnsupportedException)
        {
            return Rejected("prefix_prediction_unsupported");
        }
        catch (NativeChoicePlanMismatchException)
        {
            return Rejected("prefix_choice_unavailable");
        }
        catch (InvalidPlannedChoiceBranchException)
        {
            return Rejected("prefix_choice_unavailable");
        }
        finally
        {
            if (!transferred) node.Snapshot.ReleaseSimulator();
        }

        RetainedPrefixReplay Rejected(string reason)
            => new(null, [], _run.TransitionCount - transitionsBefore, reason);
    }
}
