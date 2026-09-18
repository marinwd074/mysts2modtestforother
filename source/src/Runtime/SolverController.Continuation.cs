using System.Diagnostics;
using System.Runtime;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace CombatSolver;

internal static partial class SolverController
{

    private static bool HasCurrentStoppedRoute()
    {
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        return state != null
            && CombatManager.Instance.IsInProgress
            && _combat.StoppedSearch is { StoppedResult: not null, StoppedStamp: { } stamp }
            && stamp == LiveCombatStamp.Capture(state);
    }

    private static bool TryAdoptStoppedRoute()
    {
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        NGame? host = NGame.Instance;
        if (state == null || host == null || _combat.StoppedSearch is not { } stopped)
            return false;
        LiveCombatStamp stamp = LiveCombatStamp.Capture(state);
        SolverResult? result = stopped.TakeStoppedResult(stamp);
        _combat.StoppedSearch = null;
        if (result == null)
            return false;

        _combat.LatestResult = result;
        _combat.LatestStamp = stamp;
        _combat.ContinuationSource = result;
        BattleDamageTracker.RegisterPlan(state, result);
        SolverOverlaySnapshot snapshot = SolverOverlaySnapshot.CaptureWithReviewedWorldlines(
            result,
            UnexpectedReplanCount > 0,
            _combat.ReviewedWorldlinesTotal);
        SolverOverlay.ShowResult(host, MarkRouteAdopted(snapshot));
        SolverOverlay.RefreshControls();
        Entry.Logger.Info(
            $"[CombatSolver/Test] STOPPED_ROUTE_ADOPTED turn={result.StartTurnNumber} actions={result.BestNode.Actions.Count}");
        return true;
    }

    internal static void RecordManualProjectionComparisonForTesting(
        int previousProjectedBattleHpLost,
        int currentProjectedBattleHpLost)
    {
        AssertMainThread();
        if (!UnattendedTestRunner.IsActive)
            throw new InvalidOperationException("手操战损比较入口只能在无人测试中使用。");
        RecordManualProjectionComparison(
            new ManualProjectionBaseline(1, previousProjectedBattleHpLost, "test_manual_state_change"),
            currentTurnNumber: 1,
            currentProjectedBattleHpLost);
    }

    internal static bool ActivateTurnSetupResult(
        NGame host,
        CombatState state,
        int lifecycleGeneration,
        SolverResult result)
    {
        AssertMainThread();
        Player? player = LocalContext.GetMe(state);
        if (!IsCurrentCombatLifecycle(state, lifecycleGeneration)
            || _solverDisabled
            || _combat.AutomaticSearchPaused
            || !CombatManager.Instance.IsInProgress
            || state.Players.Count != 1
            || !SolverSessionCapabilities.Capture(state).CanSearch
            || state.CurrentSide != CombatSide.Player
            || player?.PlayerCombatState?.Phase != PlayerTurnPhase.Play
            || result.TurnSetupPlayState is not { } expected
            || ContinuationStamp.CaptureLive(state) != expected)
        {
            Entry.Logger.Info("[CombatSolver/Test] TURN_SETUP_RESULT_REJECT reason=live_state_changed");
            return false;
        }

        LiveCombatStamp stamp = LiveCombatStamp.Capture(state);
        CancelSearch();
        RecordReviewedWorldlines(result);
        _combat.State = state;
        _combat.LatestResult = result;
        _combat.LatestStamp = stamp;
        _combat.ContinuationSource = result.ResultScope == SolverResultScope.CurrentTurnAdoption
            ? null
            : result;
        if (!result.WasRestoredFromCache)
        {
            _combat.SearchesStarted++;
            _combat.ReplanCounts[ReplanCause.InitialSearch] = _combat.ReplanCounts.GetValueOrDefault(ReplanCause.InitialSearch) + 1;
        }
        else
            _combat.RoutesRestored++;
        if (UnattendedTestRunner.IsActive)
        {
            LastCompletedResultForTesting = result;
            LastTurnSetupResultForTesting = result;
        }
        BattleDamageTracker.RegisterPlan(state, result);
        CombatBugReportExporter.RecordCheckpoint(
            state,
            result.ResultScope switch
            {
                SolverResultScope.CurrentTurnAdoption => "turn_setup_current_turn_adopted",
                SolverResultScope.RouteAdoption => "turn_setup_route_adopted",
                _ => "turn_setup_result",
            },
            result,
            DescribeReplanAudit());
        SolverOverlaySnapshot snapshot = result.ResultScope == SolverResultScope.CurrentTurnAdoption
            ? SolverOverlaySnapshot.CaptureCurrentTurn(SolverCurrentTurnPreview.FromResult(result))
            : SolverOverlaySnapshot.CaptureWithReviewedWorldlines(
                result,
                UnexpectedReplanCount > 0,
                _combat.ReviewedWorldlinesTotal);
        if (result.ResultScope == SolverResultScope.RouteAdoption)
            snapshot = MarkRouteAdopted(snapshot);
        SolverOverlay.ShowResult(host, snapshot);
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_SETUP_RESULT_ACCEPTED turn={player.PlayerCombatState.TurnNumber} " +
            $"scope={result.ResultScope}");
        Entry.Logger.Info(SolverDiagnostics.DescribeResult(result));
        if (_combat.FullAutoEnabled
            && result.ResultScope != SolverResultScope.CurrentTurnAdoption)
        {
            Task fullAutoTask = StartCombatDeferredOperation(
                token => StartFullAutoAfterTurnSetupAsync(host, state, result, token));
            if (UnattendedAsyncActivityTracker.IsRequestActive)
                fullAutoTask = UnattendedAsyncActivityTracker.Track(fullAutoTask);
            TaskHelper.RunSafely(fullAutoTask);
        }
        return true;
    }

    internal static void ShowTurnSetupContinuationPreview(
        NGame host,
        CombatState state,
        int turn)
    {
        AssertMainThread();
        if (!ReferenceEquals(_combat.State, state)
            || _combat.ContinuationSource is not { } source)
        {
            throw new InvalidOperationException("回合准备页面没有可显示的既有跨回合路线。");
        }
        SolverOverlay.ShowResult(
            host,
            SolverOverlaySnapshot.CapturePendingTurnSetup(
                source,
                turn,
                UnexpectedReplanCount > 0,
                _combat.ReviewedWorldlinesTotal));
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_SETUP_RESULT_PREVIEW turn={turn} " +
            "source=continuation native_choice_pending=true");
    }

    internal static bool TryGetPlannedTurnSetupChoices(
        CombatState state,
        int turn,
        out IReadOnlyList<PlanCardChoice>? choices)
    {
        AssertMainThread();
        choices = null;
        if (!ReferenceEquals(_combat.State, state)
            || _combat.ContinuationSource is not { } source
            || _combat.LastSolverDeployedTurn != turn - 1
            || !source.Continuations.Any(item => item.StartTurnNumber == turn))
        {
            return false;
        }

        PlanAction? previousEndTurn = source.BestNode.Actions.FirstOrDefault(action =>
            action.Turn == turn - 1
            && (action.Kind == PlanActionKind.EndTurn || action.EndsPlayerTurn));
        PlanCardChoice[] planned = previousEndTurn?.TurnStartChoices?
            .Where(choice => choice.Timing == PlanChoiceTiming.PlayerTurnStart)
            .ToArray() ?? [];
        if (planned.Length == 0)
            return false;
        choices = planned;
        return true;
    }

    private static string DescribeReplanAudit()
    {
        string differences = _combat.LastContinuationDifferences.Count == 0
            ? "-"
            : string.Join(System.Environment.NewLine, _combat.LastContinuationDifferences);
        string manualComparison = _combat.LastManualProjectionComparison is { } comparison
            ? $"previous={comparison.PreviousProjectedBattleHpLost} current={comparison.CurrentProjectedBattleHpLost} " +
              $"difference={comparison.Difference} original_turn={comparison.OriginalTurnNumber} " +
              $"current_turn={comparison.CurrentTurnNumber}"
            : "-";
        return DescribeReplanCounts() +
               System.Environment.NewLine + "last_state_differences=" + differences +
               System.Environment.NewLine + "last_manual_projection_comparison=" + manualComparison;
    }

    private static string DescribeReplanCounts()
    {
        string counts = string.Join(' ', Enum.GetValues<ReplanCause>()
            .Select(cause => $"{CauseToken(cause)}={_combat.ReplanCounts.GetValueOrDefault(cause)}"));
        return $"searches={_combat.SearchesStarted} reused={_combat.ContinuationsReused} restored={_combat.RoutesRestored} {counts} " +
               $"control_mode={ControlModeForBugReport} " +
               $"last_solver_deployed_turn={_combat.LastSolverDeployedTurn?.ToString() ?? "-"}";
    }

    private static void MarkManualControlObserved(string reason)
    {
        if (_combat.ManualControlObserved)
            return;
        _combat.ManualControlObserved = true;
        Entry.Logger.Info($"[CombatSolver/Test] CONTROL_MODE_CHANGED mode=manual_plus_solver reason={reason}");
    }

    private static string CauseToken(ReplanCause cause)
        => cause switch
        {
            ReplanCause.InitialSearch => "initial_search",
            ReplanCause.StateMismatch => "state_mismatch",
            ReplanCause.ManualDivergence => "manual_divergence",
            ReplanCause.ContinuationMissing => "continuation_missing",
            ReplanCause.DeploymentDrift => "deployment_drift",
            ReplanCause.PlanExhausted => "plan_exhausted",
            ReplanCause.ExplicitRequest => "explicit_request",
            _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, null),
        };

    private static SolverOverlaySnapshot MarkRouteAdopted(SolverOverlaySnapshot snapshot)
        => snapshot with
        {
            StatusText = "方案就绪 · 已采用当前路线",
            StatusTone = SolverOverlayTone.Success,
        };

    private static void RecordManualProjectionComparison(
        ManualProjectionBaseline baseline,
        int currentTurnNumber,
        int currentProjectedBattleHpLost)
    {
        ManualProjectionComparison comparison = new(
            baseline.StartTurnNumber,
            currentTurnNumber,
            baseline.ProjectedBattleHpLost,
            currentProjectedBattleHpLost,
            baseline.StateDifference,
            baseline.OriginalCheckpointId,
            CombatBugReportExporter.CurrentSearchRootId);
        _combat.LastManualProjectionComparison = comparison;
        CombatBugReportExporter.RecordComparisonReference(baseline.OriginalCheckpointId);
        string direction;
        if (comparison.Difference < 0)
        {
            direction = "IMPROVED";
            _combat.ManualRouteImprovementDetected = true;
            _combat.BugReportIssues.Record(
                CombatBugReportIssueKind.BetterWorldline,
                $"预计战损 {comparison.PreviousProjectedBattleHpLost} → {comparison.CurrentProjectedBattleHpLost}，" +
                $"下降 {-comparison.Difference} HP");
        }
        else if (comparison.Difference > 0)
        {
            direction = "WORSENED";
            _combat.BugReportIssues.Record(
                CombatBugReportIssueKind.ManualHpLossIncreased,
                $"预计战损 {comparison.PreviousProjectedBattleHpLost} → {comparison.CurrentProjectedBattleHpLost}，" +
                $"增加 {comparison.Difference} HP");
        }
        else
        {
            direction = "UNCHANGED";
        }

        Entry.Logger.Info(
            $"[CombatSolver/Test] MANUAL_ROUTE_{direction} " +
            $"original_turn={comparison.OriginalTurnNumber} current_turn={comparison.CurrentTurnNumber} " +
            $"previous_projected_battle_hp_lost={comparison.PreviousProjectedBattleHpLost} " +
            $"current_projected_battle_hp_lost={comparison.CurrentProjectedBattleHpLost} " +
            $"difference={comparison.Difference} {comparison.StateDifference}");
    }
}
