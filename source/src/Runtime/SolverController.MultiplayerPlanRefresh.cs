using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal static partial class SolverController
{
    private static MultiplayerPlanRefreshDecision TryBoundedMultiplayerPlanRefresh(
        NGame host,
        CombatState state,
        long worldVersion)
    {
        AssertMainThread();
        Stopwatch stopwatch = Stopwatch.StartNew();
        _combat.PendingMultiplayerPlanRefresh = false;

        SolverResult? source = _combat.LatestResult;
        LiveCombatStamp currentStamp = LiveCombatStamp.Capture(state);
        if (source == null
            || _combat.LatestStamp is not { } previousStamp
            || !MultiplayerPlanRefreshContracts.IsReplayCompatible(
                previousStamp.StateText,
                currentStamp.StateText,
                out _)
            || source.StartTurnNumber != LocalContext.GetMe(state)?.PlayerCombatState?.TurnNumber
            || source.MultiplayerReplayCandidates.Count == 0)
        {
            DropRetainedPlanForFullRestart();
            LogPlanRefresh(
                MultiplayerPlanRefreshDecision.FullRestart,
                worldVersion,
                source,
                refreshed: null,
                replayedCandidates: 0,
                firstActionChanged: false,
                stopwatch.Elapsed,
                "root_or_candidate_mismatch");
            return MultiplayerPlanRefreshDecision.FullRestart;
        }

        try
        {
            BattleDamageSnapshot battleDamage = BattleDamageTracker.Observe(state);
            SolverDisplayNames displayNames = SolverDisplayNames.Capture(state);
            SolverSettingsSnapshot settings = SolverSettings.Capture();
            SolverTheftPolicy? theftPolicy = ResolveTheftPolicy(state);
            SearchPolicySnapshot searchPolicy = CaptureSearchPolicy(
                settings,
                state,
                includeTurnSetup: false,
                theftPolicy: theftPolicy);
            CombatRootSnapshot root = CombatRootSnapshot.Capture(state);
            MultiplayerPlanRefreshResult evaluation = MultiplayerPlanRefresher.Refresh(
                root, displayNames, battleDamage, searchPolicy, source.MultiplayerReplayCandidates);
            if (evaluation.Decision == MultiplayerPlanRefreshDecision.FullRestart)
            {
                DropRetainedPlanForFullRestart();
                LogPlanRefresh(evaluation.Decision, worldVersion, source, evaluation.Result,
                    evaluation.ReplayedCandidates, evaluation.FirstActionChanged,
                    stopwatch.Elapsed, evaluation.Reason);
                return evaluation.Decision;
            }
            SolverResult refreshed = evaluation.Result
                ?? throw new InvalidOperationException("A successful plan refresh must supply a result.");
            MultiplayerPlanRefreshDecision decision = evaluation.Decision;
            _combat.State = state;
            _combat.LatestResult = refreshed;
            _combat.LatestStamp = currentStamp;
            _combat.LatestRouteInvalidationVersion = MultiplayerRouteChangeTracker.InvalidationVersion;
            _combat.ContinuationSource = refreshed;
            _combat.AwaitingMultiplayerContinuation = false;
            _combat.LastPlanRefreshDecisionTimestampMilliseconds =
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            BattleDamageTracker.RegisterPlan(state, refreshed);
            SolverOverlay.ShowResult(
                host,
                SolverOverlaySnapshot.CaptureWithReviewedWorldlines(
                    refreshed,
                    UnexpectedReplanCount > 0,
                    _combat.ReviewedWorldlinesTotal));
            LogPlanRefresh(
                decision,
                worldVersion,
                source,
                refreshed,
                evaluation.ReplayedCandidates,
                evaluation.FirstActionChanged,
                stopwatch.Elapsed,
                evaluation.Reason);
            return decision;
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn(
                $"[CombatSolver/MultiplayerPlanRefresh] MP_PLAN_REFRESH_REPLAY_FAILED " +
                $"world_version={worldVersion} exception={ex.GetType().Name} message={ex.Message}");
            DropRetainedPlanForFullRestart();
            LogPlanRefresh(
                MultiplayerPlanRefreshDecision.FullRestart,
                worldVersion,
                source,
                refreshed: null,
                replayedCandidates: 0,
                firstActionChanged: false,
                stopwatch.Elapsed,
                "replay_or_materialize_exception");
            return MultiplayerPlanRefreshDecision.FullRestart;
        }
    }

    private static void DropRetainedPlanForFullRestart()
    {
        _combat.PendingMultiplayerPlanRefresh = false;
        _combat.LastPlanRefreshDecisionTimestampMilliseconds = null;
        _combat.LatestResult = null;
        _combat.LatestStamp = null;
        _combat.ContinuationSource = null;
        InvalidateRenderedRouteAdoptionSeed();
        SolverOverlay.RefreshControls();
    }

    private static void LogPlanRefresh(
        MultiplayerPlanRefreshDecision decision,
        long worldVersion,
        SolverResult? source,
        SolverResult? refreshed,
        int replayedCandidates,
        bool firstActionChanged,
        TimeSpan elapsed,
        string reason)
    {
        Entry.Logger.Info(
            $"[CombatSolver/MultiplayerPlanRefresh] MP_PLAN_REFRESH " +
            $"decision={decision.ToString().ToLowerInvariant()} " +
            $"full_restart={(decision == MultiplayerPlanRefreshDecision.FullRestart).ToString().ToLowerInvariant()} " +
            $"prefix_replay={(replayedCandidates > 0).ToString().ToLowerInvariant()} " +
            $"first_action_changed={firstActionChanged.ToString().ToLowerInvariant()} " +
            $"candidate_count={source?.MultiplayerReplayCandidates.Count ?? 0} " +
            $"replayed_candidates={replayedCandidates} world_version={worldVersion} " +
            $"source_route_identity={source?.RouteIdentity ?? "-"} " +
            $"refreshed_route_identity={refreshed?.RouteIdentity ?? "-"} " +
            $"bounded_expanded_nodes={refreshed?.ExpandedNodes.ToString() ?? "-"} " +
            $"bounded_boundary={refreshed?.BoundaryReason.ToString() ?? "-"} " +
            $"replay_latency_ms={elapsed.TotalMilliseconds:F2} reason={reason}");
    }
}
