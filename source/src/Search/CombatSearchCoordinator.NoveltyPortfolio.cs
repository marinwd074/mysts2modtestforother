using System.Diagnostics;

namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    private static bool CanLimitSoloZeroLossRefinement(CombatRootSnapshot root, SearchPolicySnapshot policy)
        => policy.UseUpstreamSearchOptimizations && policy.UseBeamWidthPortfolio
            && policy.RoutePolicy == SearchRoutePolicy.SinglePlayerFullRoute
            && root.PlayerCount == 1 && root.StartTurnNumber == 1 && !root.IsActEndingBoss
            && !policy.IncludeTurnSetup && !policy.CurrentTurnOnly
            && policy.BeamWidthPortfolioWidths is not { Count: > 0 }
            && policy.StopAtAcceptableBattleHpLoss && policy.AcceptableBattleHpLoss == 0
            && !policy.EffectiveHasGrowthTargets && policy.RelicTargets.Count == 0
            && policy.TheftPolicy == null && !policy.PotionStrategy.HasForcedDirectives;

    private static bool IsCleanZeroLossTarget(SearchPolicySnapshot policy, SolverResult? result)
        => result is { ProjectedBattleHpLost: 0 } && IsCleanPotionFreeVictory(result)
            && HasReachedAcceptableBattleHpLoss(policy, result);

    private static bool IsCleanPotionFreeVictory(SolverResult? result)
        => result is { PotionCount: 0, CombatEndedTurn: not null,
            ResultScope: SolverResultScope.SearchCompletion, BoundaryReason: SearchBoundaryReason.None }
            && !result.Snapshot.HasRisk && result.Snapshot.ProjectedDeathSaveUseCount == 0
            && IsCompleteVictory(result);

    private static SolverResult RunNoveltyPortfolioPass(
        CombatRootSnapshot root, SolverDisplayNames names, BattleDamageSnapshot damage,
        SearchPolicySnapshot policy, SolverSearchProfile profile, Stopwatch clock,
        SolverPotionPolicy? potionOverride, CancellationToken cancellation,
        Action<SolverProgress>? progress, Action<SolverResult>? publish,
        Func<SolverResult, SolverSearchProfile, SolverResult?>? runCrossFamilyScout,
        Func<SolverSearchProfile, SolverResult>? runWideProbe,
        Func<SolverSearchProfile, SolverResult> solveBaseline)
    {
        SolverSearchProfile? explorationProfile = policy.NoveltyBudget.Exploration(profile, root.IsActEndingBoss);
        if (explorationProfile == null)
        {
            SolverResult unchanged = solveBaseline(profile);
            unchanged.NoveltyPortfolio = new("budget_too_small", 0, 0, null, true, "beam",
                null, false, unchanged.ProjectedBattleHpLost, IsCompleteVictory(unchanged),
                profile.MaxExpandedNodes, profile.SoftTimeBudgetMilliseconds);
            return unchanged;
        }
        SearchRequestWorkTotals totals = policy.RequestWorkTotals
            ?? throw new InvalidOperationException("Novelty portfolio requires request work totals.");
        long expandedBefore = totals.Snapshot().ExpandedNodes;
        // Missing mandatory potion routes are a defined search boundary. Beam still gets
        // the remainder; simulation errors and caller cancellation propagate normally.
        SolverResult? exploration = SolveOptionalPotionPosterior(new CombatBeamSolver(root, names, damage,
            policy with { NoveltySearch = policy.NoveltySearch ?? new(), RequestMemberKind = SearchRequestMemberKind.Novelty }, cancellation,
            progress, explorationProfile, potionPolicyOverride: potionOverride), policy, "novelty_exploration");
        long explorationExpanded = totals.Snapshot().ExpandedNodes - expandedBefore;
        long explorationElapsed = clock.ElapsedMilliseconds;
        if (exploration != null && IsCompleteVictory(exploration)) publish?.Invoke(exploration);
        if (exploration != null && ResolveTakeoverResult(exploration, policy.Interaction) is { } adopted)
        {
            adopted.NoveltyPortfolio = new("adopted", explorationExpanded, explorationElapsed,
                exploration.NoveltySearch, false, "adopted", exploration.ProjectedBattleHpLost,
                IsCompleteVictory(exploration), null, false, 0, 0);
            return adopted;
        }
        bool settled = exploration != null && (exploration.ResultScope != SolverResultScope.SearchCompletion
            || !policy.PotionStrategy.HasForcedDirectives && HasReachedAcceptableBattleHpLoss(policy, exploration));
        SolverResult? wideProbe = null;
        if (!settled && runWideProbe != null && exploration != null && IsCompleteVictory(exploration)
            && NoveltyPortfolioBudget.Remaining(profile, clock.ElapsedMilliseconds, explorationExpanded) is { } beforeWide
            && beforeWide.SoftTimeBudgetMilliseconds >= 48_000 && beforeWide.MaxExpandedNodes >= 32_000)
        {
            // Give a naturally complete zero-loss line an earlier chance to settle the request.
            // Failed probes spend only their measured portion of the original budget.
            var wideProfile = beforeWide with
            {
                BeamWidth = BeamWidthPortfolio.ScaledWidth(profile.BeamWidth, BeamWidthPortfolio.WideRefinementRatio),
                SoftTimeBudgetMilliseconds = Math.Min(12_000, beforeWide.SoftTimeBudgetMilliseconds / 8),
                MaxExpandedNodes = beforeWide.MaxExpandedNodes / 8,
            };
            long started = clock.ElapsedMilliseconds;
            wideProbe = runWideProbe(wideProfile);
            bool hit = IsCleanZeroLossTarget(policy, wideProbe)
                && IsBetterPotionPolicyResult(root, policy, wideProbe, exploration);
            policy.Diagnostics.Info($"[CombatSolver/Test] SOLO_ZERO_LOSS_WIDE_PROBE " +
                $"target_met={hit} beam={wideProfile.BeamWidth} elapsed_ms={clock.ElapsedMilliseconds - started} " +
                $"loss={wideProbe.ProjectedBattleHpLost} turn={wideProbe.CombatEndedTurn}");
            if (ResolveTakeoverResult(wideProbe, policy.Interaction) is { } wideAdopted)
                return wideAdopted;
            if (hit)
            {
                wideProbe.NoveltyPortfolio = new("zero_loss_wide", explorationExpanded, explorationElapsed,
                    exploration.NoveltySearch, true, "wide_probe", exploration.ProjectedBattleHpLost,
                    true, wideProbe.ProjectedBattleHpLost, true,
                    wideProfile.MaxExpandedNodes, wideProfile.SoftTimeBudgetMilliseconds);
                return wideProbe;
            }
        }
        if (!settled && exploration != null && runCrossFamilyScout != null)
        {
            SolverSearchProfile? beforeScout = NoveltyPortfolioBudget.Remaining(
                profile,
                clock.ElapsedMilliseconds,
                totals.Snapshot().ExpandedNodes - expandedBefore);
            SolverSearchProfile? scoutProfile = beforeScout == null
                ? null
                : NoveltyPortfolioBudget.CrossFamilyScout(explorationProfile, beforeScout);
            if (scoutProfile != null)
            {
                SolverResult? scout = runCrossFamilyScout(exploration, scoutProfile);
                if (scout != null && scout.ResultScope != SolverResultScope.SearchCompletion)
                    return scout;
            }
        }
        long portfolioExpanded = totals.Snapshot().ExpandedNodes - expandedBefore;
        SolverSearchProfile? remaining = NoveltyPortfolioBudget.Remaining(profile,
            clock.ElapsedMilliseconds, portfolioExpanded);
        if (settled || remaining == null)
        {
            if (exploration == null)
                throw new PotionPolicyUnsatisfiedException("Novelty search exhausted the request before satisfying mandatory potion use.");
            SolverResult finished = PreferWideProbe(exploration) ? wideProbe! : exploration;
            finished.NoveltyPortfolio = Describe("exploration_finished", exploration, wideProbe,
                ReferenceEquals(finished, wideProbe) ? "wide_probe" : "exploration");
            return finished;
        }
        cancellation.ThrowIfCancellationRequested();
        SolverResult baseline = solveBaseline(remaining);
        bool selectExploration = baseline.ResultScope == SolverResultScope.SearchCompletion
            && exploration != null && IsCompleteVictory(exploration)
            && IsBetterPotionPolicyResult(root, policy, exploration, baseline);
        SolverResult selected = selectExploration ? exploration! : baseline;
        bool selectWide = PreferWideProbe(selected);
        if (selectWide) selected = wideProbe!;
        selected.NoveltyPortfolio = Describe("portfolio", exploration, selectWide ? wideProbe : baseline,
            selectWide ? "wide_probe" : selectExploration ? "exploration" : "beam");
        return selected;

        bool PreferWideProbe(SolverResult current)
            => current.ResultScope == SolverResultScope.SearchCompletion
                && wideProbe is { ResultScope: SolverResultScope.SearchCompletion } candidate
                && !candidate.Snapshot.HasRisk && IsCompleteVictory(candidate)
                && IsBetterPotionPolicyResult(root, policy, candidate, current);

        NoveltyPortfolioTelemetry Describe(string stop, SolverResult? scout, SolverResult? beam, string selectedMethod)
            => new(stop, explorationExpanded, explorationElapsed, scout?.NoveltySearch,
                beam != null, selectedMethod, scout?.ProjectedBattleHpLost,
                scout != null && IsCompleteVictory(scout), beam?.ProjectedBattleHpLost,
                beam != null && IsCompleteVictory(beam), remaining?.MaxExpandedNodes ?? 0,
                remaining?.SoftTimeBudgetMilliseconds ?? 0);
    }


}
