using System.Diagnostics;

namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    private static SolverResult RunNoveltyPortfolioPass(
        CombatRootSnapshot root, SolverDisplayNames names, BattleDamageSnapshot damage,
        SearchPolicySnapshot policy, SolverSearchProfile profile, Stopwatch clock,
        SolverPotionPolicy? potionOverride, CancellationToken cancellation,
        Action<SolverProgress>? progress, Action<SolverResult>? publish,
        Func<SolverResult, SolverSearchProfile, SolverResult?>? runCrossFamilyScout,
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
            policy with { NoveltySearch = policy.NoveltySearch ?? new() }, cancellation,
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
        if (!settled && exploration != null && runCrossFamilyScout != null)
        {
            SolverSearchProfile? beforeScout = NoveltyPortfolioBudget.Remaining(
                profile,
                clock.ElapsedMilliseconds,
                explorationExpanded);
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
            exploration.NoveltyPortfolio = Describe("exploration_finished", exploration, null, "exploration");
            return exploration;
        }
        cancellation.ThrowIfCancellationRequested();
        SolverResult baseline = solveBaseline(remaining);
        bool selectExploration = baseline.ResultScope == SolverResultScope.SearchCompletion
            && exploration != null && IsCompleteVictory(exploration)
            && IsBetterPotionPolicyResult(root, policy, exploration, baseline);
        SolverResult selected = selectExploration ? exploration! : baseline;
        selected.NoveltyPortfolio = Describe("portfolio", exploration, baseline,
            selectExploration ? "exploration" : "beam");
        return selected;

        NoveltyPortfolioTelemetry Describe(string stop, SolverResult? scout, SolverResult? beam, string selectedMethod)
            => new(stop, explorationExpanded, explorationElapsed, scout?.NoveltySearch,
                beam != null, selectedMethod, scout?.ProjectedBattleHpLost,
                scout != null && IsCompleteVictory(scout), beam?.ProjectedBattleHpLost,
                beam != null && IsCompleteVictory(beam), remaining?.MaxExpandedNodes ?? 0,
                remaining?.SoftTimeBudgetMilliseconds ?? 0);
    }

    private static SolverResult RunLocalCoreCurrentTurnQualityFirst(
        CombatRootSnapshot root,
        SolverDisplayNames names,
        BattleDamageSnapshot damage,
        SearchPolicySnapshot policy,
        SolverSearchProfile profile,
        Stopwatch clock,
        SolverPotionPolicy? potionOverride,
        CancellationToken cancellation,
        Action<SolverProgress>? progress,
        Func<SolverSearchProfile, SolverResult> solveBaseline)
    {
        SolverSearchProfile? scoutProfile =
            policy.NoveltyBudget.Exploration(profile, root.IsActEndingBoss);
        if (scoutProfile == null)
            return solveBaseline(profile);

        SearchRequestWorkTotals totals = policy.RequestWorkTotals
            ?? throw new InvalidOperationException(
                "Local-core current-turn quality scout requires request work totals.");
        long expandedBefore = totals.Snapshot().ExpandedNodes;
        long elapsedBefore = clock.ElapsedMilliseconds;

        SearchPolicySnapshot scoutPolicy = policy with
        {
            CurrentTurnOnly = true,
            NoveltySearch = null,
            UseNoveltyPortfolio = false,
            UseBeamWidthPortfolio = false,
            UseP3CrossFamilyScheduling = false,
        };
        CombatBeamSolver scoutSolver = new(
            root,
            names,
            damage,
            scoutPolicy,
            cancellation,
            progress,
            scoutProfile,
            potionPolicyOverride: potionOverride,
            reserveScenarioReevaluationBudget: false);
        SolverResult scout = RunResumableMemberToCompletion(
            scoutSolver,
            cancellation,
            scoutPolicy.Diagnostics);

        long scoutExpanded = totals.Snapshot().ExpandedNodes - expandedBefore;
        long scoutElapsed = Math.Max(0, clock.ElapsedMilliseconds - elapsedBefore);
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] MP_LOCAL_CURRENT_TURN_QUALITY_SCOUT " +
            $"expanded={scoutExpanded} elapsed_ms={scoutElapsed} " +
            $"projected_hp_loss={scout.ProjectedBattleHpLost} " +
            $"actions={string.Join(',', scout.BestNode.Actions.Select(action => action.CardId ?? action.Kind.ToString()))}");

        if (ResolveTakeoverResult(scout, policy.Interaction) is { } adopted)
        {
            adopted.NoveltyPortfolio = new(
                "local_current_turn_adopted",
                scoutExpanded,
                scoutElapsed,
                null,
                false,
                "adopted",
                scout.ProjectedBattleHpLost,
                IsCompleteVictory(scout),
                null,
                false,
                0,
                0);
            return adopted;
        }

        SolverSearchProfile? remaining = NoveltyPortfolioBudget.Remaining(
            profile,
            clock.ElapsedMilliseconds,
            scoutExpanded);
        if (remaining == null)
        {
            scout.NoveltyPortfolio = new(
                "local_current_turn_budget_exhausted",
                scoutExpanded,
                scoutElapsed,
                null,
                false,
                "current_turn",
                scout.ProjectedBattleHpLost,
                IsCompleteVictory(scout),
                null,
                false,
                0,
                0);
            return scout;
        }

        cancellation.ThrowIfCancellationRequested();
        SolverResult baseline = solveBaseline(remaining);
        baseline.NoveltyPortfolio = new(
            "local_current_turn_first",
            scoutExpanded,
            scoutElapsed,
            null,
            true,
            "beam",
            scout.ProjectedBattleHpLost,
            IsCompleteVictory(scout),
            baseline.ProjectedBattleHpLost,
            IsCompleteVictory(baseline),
            remaining.MaxExpandedNodes,
            remaining.SoftTimeBudgetMilliseconds);
        return baseline;
    }
}
