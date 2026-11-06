using System.Diagnostics;

namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    private static (SolverResult? Result, SearchPolicySnapshot Policy, MultiplayerPlanRefreshReport Report)
        TryRefreshPlan(CombatRootSnapshot root, SolverDisplayNames names, BattleDamageSnapshot damage,
            SearchPolicySnapshot policy, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        MultiplayerPlanRefreshRequest request = policy.MultiplayerPlanRefresh!;
        SearchRequestWallClockBudget budget = policy.RecoveryBudget
            ?? throw new InvalidOperationException("Plan recovery requires a request budget.");
        SearchRequestWorkTotals totals = policy.RequestWorkTotals!;
        Stopwatch clock = Stopwatch.StartNew();
        long nodesBefore = totals.Snapshot().ExpandedNodes;
        SolverSearchProfile probeProfile = policy.Profile with
        {
            SoftTimeBudgetMilliseconds = Math.Min(60, policy.Profile.SoftTimeBudgetMilliseconds),
            MaxExpandedNodes = Math.Min(192, policy.Profile.MaxExpandedNodes),
        };
        SearchPolicySnapshot replayPolicy = policy with
        {
            Profile = probeProfile, Interaction = null,
            ContinuationRouteReplayActions = request.Route,
            // Automatic repair cannot manufacture explicit route-adoption intent.
            ContinuationRouteReplayBaseline = request.Baseline is { } baseline
                ? baseline with { ExplicitRouteAdoption = false } : null,
        };
        SolverResult? restored = TryReplayContinuationRoute(root, names, damage, replayPolicy, token);
        MultiplayerPlanRefreshMetrics? metrics = null;
        MultiplayerPlanRefreshDecision decision = MultiplayerPlanRefreshDecision.Continue;
        string reason = "whole_route_replayed";
        bool wholeRoute = restored != null;
        if (restored == null)
        {
            decision = MultiplayerPlanRefreshDecision.FullRestart;
            reason = "recovery_probe_exhausted";
            if (ContinuationSeedIncumbentBudget.Remaining(probeProfile, clock.ElapsedMilliseconds,
                    totals.Snapshot().ExpandedNodes - nodesBefore) is { } prefixProfile
                && !budget.IsExpired && budget.HasExpandedNodeBudgetRemaining)
            {
                MultiplayerPlanRefreshResult refresh = MultiplayerPlanRefresher.Refresh(root, names, damage,
                    replayPolicy with { Profile = prefixProfile }, request.Candidates, token);
                metrics = refresh.Metrics;
                reason = refresh.Reason;
                if (refresh.Decision != MultiplayerPlanRefreshDecision.FullRestart
                    && refresh.Result is { } candidate
                    && IsAcceptableContinuationRouteResult(replayPolicy, candidate, out _))
                {
                    restored = candidate;
                    decision = refresh.Decision;
                }
                else if (refresh.Decision != MultiplayerPlanRefreshDecision.FullRestart)
                    reason = "materialized_quality_unproven";
            }
        }
        token.ThrowIfCancellationRequested();
        SolverSearchProfile? remaining = budget.RemainingProfile(policy.Profile);
        MultiplayerPlanRefreshReport report = new(request.SourceRouteIdentity, decision, reason,
            wholeRoute, metrics, clock.ElapsedMilliseconds, totals.Snapshot().ExpandedNodes - nodesBefore,
            remaining?.SoftTimeBudgetMilliseconds ?? 0, remaining?.MaxExpandedNodes ?? 0);
        policy.Diagnostics.Info("[CombatSolver/MultiplayerPlanRefresh] MP_PLAN_REFRESH_WORKER "
            + System.Text.Json.JsonSerializer.Serialize(report));
        if (restored == null && remaining == null)
            throw new SearchRecoveryBudgetExhaustedException();
        return (restored, policy with
        {
            Profile = remaining ?? policy.Profile,
            MultiplayerPlanRefresh = null,
            ContinuationRouteReplayActions = [],
            ContinuationRouteReplayBaseline = null,
        }, report);
    }
}
