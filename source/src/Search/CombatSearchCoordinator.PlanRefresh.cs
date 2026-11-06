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
        SolverResult? restored = TryReplayContinuationRoute(root, names, damage, replayPolicy, token, out string currentOutcome);
        int? selectedRank = restored == null ? null : 0;
        List<MultiplayerFullRouteReplayReport> fullRoutes = [new(0, currentOutcome, restored?.BestNode.ActionCount ?? 0)];
        List<(int Rank, SolverResult Result)> verified = [];
        if (restored != null) verified.Add((0, restored));
        // Current + at most two alternatives share the original recovery allowance.
        // Old rank only defines a stable visit order; all comparison inputs are freshly simulated.
        foreach (MultiplayerReplayCandidate retained in request.Candidates
                     .Where(candidate => candidate.OriginalRank > 0).OrderBy(candidate => candidate.OriginalRank).Take(2))
        {
            token.ThrowIfCancellationRequested();
            if (retained.Route.Count == 0)
            {
                fullRoutes.Add(new(retained.OriginalRank, "no_retained_full_route", 0));
                continue;
            }
            SolverSearchProfile? remainingProbe = ContinuationSeedIncumbentBudget.Remaining(probeProfile,
                clock.ElapsedMilliseconds, totals.Snapshot().ExpandedNodes - nodesBefore);
            if (remainingProbe == null || budget.IsExpired || !budget.HasExpandedNodeBudgetRemaining)
            {
                fullRoutes.Add(new(retained.OriginalRank, "recovery_budget_exhausted", 0));
                break;
            }
            SearchPolicySnapshot alternativePolicy = replayPolicy with
            {
                Profile = remainingProbe, ContinuationRouteReplayActions = retained.Route,
                ContinuationRouteReplayBaseline = (retained.Baseline ?? request.Baseline) is { } ownBaseline
                    ? ownBaseline with { ExplicitRouteAdoption = false } : null,
                // A route rejected by recovery's extra quality checks must not leave a
                // winning witness that can bypass those checks at Coordinator exit.
                OptimizationSession = null,
            };
            SolverResult? alternative = TryReplayContinuationRoute(root, names, damage, alternativePolicy, token,
                out string outcome);
            if (alternative != null && !CanAcceptRecoveredAlternative(request, alternative, restored, out outcome))
                alternative = null;
            fullRoutes.Add(new(retained.OriginalRank, outcome, alternative?.BestNode.ActionCount ?? 0));
            if (alternative == null) continue;
            verified.Add((retained.OriginalRank, alternative));
            if (restored == null || IsBetterPotionPolicyResult(root, policy, alternative, restored))
            {
                restored = alternative;
                selectedRank = retained.OriginalRank;
            }
        }
        MultiplayerPlanRefreshMetrics? metrics = null;
        MultiplayerPlanRefreshDecision decision = selectedRank is > 0
            ? MultiplayerPlanRefreshDecision.Reselect : MultiplayerPlanRefreshDecision.Continue;
        string reason = selectedRank is > 0 ? "retained_alternative_replayed" : "whole_route_replayed";
        bool wholeRoute = restored != null;
        if (restored != null)
        {
            // Keep newly proved alternatives available for the next teammate update.
            restored.MultiplayerReplayCandidates = Array.AsReadOnly(verified
                .OrderBy(item => ReferenceEquals(item.Result, restored) ? 0 : 1).ThenBy(item => item.Rank)
                .Select((item, rank) => MultiplayerPlanRefreshRequest.CaptureCandidate(rank, item.Result)).ToArray());
        }
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
            remaining?.SoftTimeBudgetMilliseconds ?? 0, remaining?.MaxExpandedNodes ?? 0)
        {
            SelectedOriginalRank = selectedRank, FullRoutes = fullRoutes.AsReadOnly(),
        };
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

    private static bool CanAcceptRecoveredAlternative(MultiplayerPlanRefreshRequest request,
        SolverResult candidate, SolverResult? current, out string reason)
    {
        reason = "missing_current_baseline";
        if (request.Baseline is not { } baseline) return false;
        reason = "alternative_hp_regression";
        if (candidate.ProjectedBattleHpLost > baseline.ProjectedBattleHpLost) return false;
        reason = "alternative_death_save_regression";
        if (candidate.Snapshot.ProjectedDeathSaveUseCount > baseline.DeathSaveUses) return false;
        reason = "alternative_growth_changed";
        if (candidate.Snapshot.GrowthRewards != baseline.GrowthRewards) return false;
        reason = "alternative_relic_counters_changed";
        if (candidate.Snapshot.RelicCounters != baseline.RelicCounters) return false;
        bool won = IsCompleteVictory(candidate);
        if (!won && (request.SourceWasCompleteVictory || current != null && IsCompleteVictory(current)))
        {
            reason = "complete_victory_must_be_preserved";
            return false;
        }
        // Until P2 unifies tail values, do not compare a shorter surviving prefix with
        // a full horizon. A terminal victory needs no unplayed tail.
        if (!won && (request.EvaluationTurn != candidate.Snapshot.Turn
            || current != null && current.Snapshot.Turn != candidate.Snapshot.Turn))
        {
            reason = "evaluation_window_mismatch";
            return false;
        }
        reason = won ? "acceptable_alternative_victory" : "equivalent_alternative_horizon";
        return true;
    }
}
