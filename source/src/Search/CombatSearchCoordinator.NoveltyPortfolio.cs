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
        => result?.BoundaryReason == SearchBoundaryReason.None && IsRiskFreePotionFreeVictory(result);

    private static bool IsRiskFreePotionFreeVictory(SolverResult? result)
        => result is { PotionCount: 0, CombatEndedTurn: not null,
            ResultScope: SolverResultScope.SearchCompletion }
            && !result.Snapshot.HasRisk && result.Snapshot.ProjectedDeathSaveUseCount == 0
            && IsCompleteVictory(result);

    private static bool IsMultiEnemyProbeVictory(SolverResult result)
        => result.BoundaryReason is SearchBoundaryReason.None or SearchBoundaryReason.TimeLimit or SearchBoundaryReason.NodeLimit
            && IsRiskFreePotionFreeVictory(result);

    private static SolverResult RunNoveltyPortfolioPass(
        CombatRootSnapshot root, SolverDisplayNames names, BattleDamageSnapshot damage,
        SearchPolicySnapshot policy, SolverSearchProfile profile, Stopwatch clock,
        SolverPotionPolicy? potionOverride, CancellationToken cancellation,
        Action<SolverProgress>? progress, Action<SolverResult>? publish,
        Func<SolverResult, SolverSearchProfile, SolverResult?>? runCrossFamilyScout,
        Func<SolverSearchProfile, SolverResult>? runWideProbe,
        Func<SolverSearchProfile, SolverResult>? runMultiEnemyProbe,
        Func<SolverSearchProfile, SolverResult> solveBaseline,
        out bool confirmedMultiEnemyVictory)
    {
        confirmedMultiEnemyVictory = false;
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
        SolverResult? multiEnemyWinner = null;
        if (!settled && runMultiEnemyProbe != null
            && NoveltyPortfolioBudget.Remaining(profile, clock.ElapsedMilliseconds, explorationExpanded) is { } beforeNarrow
            && beforeNarrow.BeamWidth >= 3 && beforeNarrow.SoftTimeBudgetMilliseconds >= 80_000
            && beforeNarrow.MaxExpandedNodes >= 48_000)
        {
            // Try the cheaper width first. A bounded member can still return a full
            // victory. The ordinary width must corroborate the complete outcome.
            int narrowWidth = BeamWidthPortfolio.ScaledWidth(profile.BeamWidth, BeamWidthPortfolio.NarrowRefinementRatio);
            SolverSearchProfile narrowProfile = beforeNarrow with
            {
                BeamWidth = narrowWidth,
                SoftTimeBudgetMilliseconds = Math.Min(15_000, beforeNarrow.SoftTimeBudgetMilliseconds / 5),
                // Cap work as well as time so a quickly found win does not spend the
                // entire time slice expanding alternatives before the second width runs.
                MaxExpandedNodes = (int)Math.Min(beforeNarrow.MaxExpandedNodes / 5, 96L * narrowWidth),
            };
            SolverResult narrow = runMultiEnemyProbe(narrowProfile);
            policy.Diagnostics.Info($"[CombatSolver/Test] SOLO_NARROW_BEAM_PROBE " +
                $"beam={narrowProfile.BeamWidth} loss={narrow.ProjectedBattleHpLost} " +
                $"turn={narrow.CombatEndedTurn} boundary={narrow.BoundaryReason}");
            if (ResolveTakeoverResult(narrow, policy.Interaction) is { } adoptedNarrow)
                return adoptedNarrow;
            if (IsMultiEnemyProbeVictory(narrow) && BeatsExploration(narrow))
            {
                multiEnemyWinner = narrow;
                publish?.Invoke(narrow);
                if (NoveltyPortfolioBudget.Remaining(profile, clock.ElapsedMilliseconds,
                        totals.Snapshot().ExpandedNodes - expandedBefore) is { } beforeCheck)
                {
                    SolverSearchProfile checkProfile = beforeCheck with
                    {
                        SoftTimeBudgetMilliseconds = Math.Clamp(beforeCheck.SoftTimeBudgetMilliseconds / 5, 1, 9_000),
                        MaxExpandedNodes = (int)Math.Clamp(beforeCheck.MaxExpandedNodes / 5, 1, 80L * beforeCheck.BeamWidth),
                    };
                    SolverResult check = runMultiEnemyProbe(checkProfile);
                    if (ResolveTakeoverResult(check, policy.Interaction) is { } adoptedCheck)
                        return adoptedCheck;
                    if (IsMultiEnemyProbeVictory(check) && BeatsExploration(check))
                    {
                        int? checkQuality = CompareCompletedResultPrimaryQuality(root, policy, check, narrow);
                        bool checkComplete = IsCleanPotionFreeVictory(check);
                        confirmedMultiEnemyVictory = checkQuality is >= 0 || checkQuality.HasValue && checkComplete;
                        policy.Diagnostics.Info($"[CombatSolver/Test] SOLO_NARROW_BEAM_CONFIRMATION " +
                            $"accepted={confirmedMultiEnemyVictory} narrow_loss={narrow.ProjectedBattleHpLost} " +
                            $"narrow_turn={narrow.CombatEndedTurn} check_loss={check.ProjectedBattleHpLost} " +
                            $"check_turn={check.CombatEndedTurn} check_boundary={check.BoundaryReason}");
                        if (confirmedMultiEnemyVictory)
                        {
                            bool selectCheck = checkQuality < 0 || checkQuality == 0
                                && (checkComplete || !IsCleanPotionFreeVictory(narrow))
                                && IsBetterPotionPolicyResult(root, policy, check, narrow);
                            SolverResult winner = selectCheck ? check : narrow;
                            winner.NoveltyPortfolio = Describe("narrow_confirmed", exploration, winner,
                                selectCheck ? "beam_check" : "narrow_probe", beforeCheck);
                            return winner;
                        }
                    }
                }
            }
        }
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
                // The zero-loss stop still applies as soon as the member finds a win.
                // Leave enough time for the wider frontier on a slower host.
                SoftTimeBudgetMilliseconds = Math.Min(22_000, beforeWide.SoftTimeBudgetMilliseconds / 5),
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
            SolverResult? finished = exploration ?? multiEnemyWinner;
            if (finished == null)
                throw new PotionPolicyUnsatisfiedException("Novelty search exhausted the request before satisfying mandatory potion use.");
            if (PreferProbe(multiEnemyWinner, finished)) finished = multiEnemyWinner!;
            if (PreferProbe(wideProbe, finished)) finished = wideProbe!;
            finished.NoveltyPortfolio = Describe("exploration_finished", exploration, wideProbe ?? multiEnemyWinner,
                MethodOf(finished, exploration), remaining);
            return finished;
        }
        cancellation.ThrowIfCancellationRequested();
        SolverResult baseline = solveBaseline(remaining);
        bool selectExploration = baseline.ResultScope == SolverResultScope.SearchCompletion
            && exploration != null && IsCompleteVictory(exploration)
            && IsBetterPotionPolicyResult(root, policy, exploration, baseline);
        SolverResult selected = selectExploration ? exploration! : baseline;
        if (PreferProbe(multiEnemyWinner, selected)) selected = multiEnemyWinner!;
        if (PreferProbe(wideProbe, selected)) selected = wideProbe!;
        selected.NoveltyPortfolio = Describe("portfolio", exploration,
            ReferenceEquals(selected, exploration) ? baseline : selected, MethodOf(selected, exploration), remaining);
        return selected;

        bool BeatsExploration(SolverResult candidate)
            => exploration == null || !IsCompleteVictory(exploration)
                || IsBetterPotionPolicyResult(root, policy, candidate, exploration);

        bool PreferProbe(SolverResult? candidate, SolverResult current)
            => current.ResultScope == SolverResultScope.SearchCompletion
                && candidate is { ResultScope: SolverResultScope.SearchCompletion }
                && !candidate.Snapshot.HasRisk && IsCompleteVictory(candidate)
                && IsBetterPotionPolicyResult(root, policy, candidate, current);

        string MethodOf(SolverResult selected, SolverResult? scout)
        {
            if (ReferenceEquals(selected, wideProbe)) return "wide_probe";
            if (ReferenceEquals(selected, multiEnemyWinner)) return "narrow_probe";
            return ReferenceEquals(selected, scout) ? "exploration" : "beam";
        }

        NoveltyPortfolioTelemetry Describe(string stop, SolverResult? scout, SolverResult? beam, string selectedMethod,
            SolverSearchProfile? remaining)
            => new(stop, explorationExpanded, explorationElapsed, scout?.NoveltySearch,
                beam != null, selectedMethod, scout?.ProjectedBattleHpLost,
                scout != null && IsCompleteVictory(scout), beam?.ProjectedBattleHpLost,
                beam != null && IsCompleteVictory(beam), remaining?.MaxExpandedNodes ?? 0,
                remaining?.SoftTimeBudgetMilliseconds ?? 0);
    }


}
