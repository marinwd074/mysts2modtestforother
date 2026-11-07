using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;

namespace CombatSolver;

internal static partial class SolverController
{
    // Called only after RequestSearch's native-action and root-capture barriers.
    // The retained result is a suggestion; normal search completion owns adoption.
    private static MultiplayerPlanRefreshRequest? CapturePendingMultiplayerPlanRefresh(
        CombatState state, LiveCombatStamp currentStamp)
    {
        AssertMainThread();
        if (!_combat.PendingMultiplayerPlanRefresh)
            return null;
        _combat.PendingMultiplayerPlanRefresh = false;
        SolverResult? source = _combat.LatestResult;
        LiveCombatStamp? previousStamp = _combat.LatestStamp;
        // Consume the suggestion before any capture can fail. Otherwise a setup
        // failure would leave an old same-turn result looking already published.
        _combat.LatestResult = null;
        _combat.LatestStamp = null;
        _combat.ContinuationSource = null;
        _combat.LastPlanRefreshDecisionTimestampMilliseconds = null;
        if (source == null
            || previousStamp == null
            || source.StartTurnNumber != LocalContext.GetMe(state)?.PlayerCombatState?.TurnNumber
            || source.MultiplayerReplayCandidates.Count == 0)
        {
            Entry.Logger.Info("[CombatSolver/MultiplayerPlanRefresh] MP_PLAN_REFRESH_CAPTURE rejected=root_or_candidate_mismatch reason=source_stamp_turn_or_candidates");
            return null;
        }
        if (!MultiplayerPlanRefreshContracts.IsReplayCompatible(
                previousStamp.StateText, currentStamp.StateText, out string reason, includeFieldValues: false))
        {
            Entry.Logger.Info($"[CombatSolver/MultiplayerPlanRefresh] MP_PLAN_REFRESH_CAPTURE rejected=root_or_candidate_mismatch reason={reason}");
            return null;
        }
        MultiplayerPlanRefreshRequest request = MultiplayerPlanRefreshRequest.Capture(source);
        Entry.Logger.Info(
            $"[CombatSolver/MultiplayerPlanRefresh] MP_PLAN_REFRESH_CAPTURE source_route_identity={request.SourceRouteIdentity} " +
            $"candidates={request.Candidates.Count} route_actions={request.Route.Count} worker_pending=true");
        return request;
    }

    private static void RecordAcceptedPlanRefresh(SolverResult result, int generation)
    {
        if (result.PlanRefresh is not { } report)
            return;
        _combat.LastPlanRefreshDecisionTimestampMilliseconds =
            report.Decision == MultiplayerPlanRefreshDecision.FullRestart
                ? null : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Entry.Logger.Info(
            $"[CombatSolver/MultiplayerPlanRefresh] MP_PLAN_REFRESH_ACCEPT generation={generation} " +
            $"decision={report.Decision} source_route_identity={report.SourceRouteIdentity} " +
            $"refreshed_route_identity={result.RouteIdentity} reason={report.Reason}");
    }
}
