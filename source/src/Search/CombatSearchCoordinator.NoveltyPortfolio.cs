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

    private static bool IsProbeVictory(SolverResult result)
        => result.BoundaryReason is SearchBoundaryReason.None or SearchBoundaryReason.TimeLimit or SearchBoundaryReason.NodeLimit
            && IsRiskFreePotionFreeVictory(result);

    private static SolverResult RunNoveltyPortfolioPass(
        CombatRootSnapshot root, SolverDisplayNames names, BattleDamageSnapshot damage,
        SearchPolicySnapshot policy, SolverSearchProfile profile, Stopwatch clock,
        SolverPotionPolicy? potionOverride, CancellationToken cancellation,
        Action<SolverProgress>? progress, Action<SolverResult>? publish,
        Func<SolverResult, SolverSearchProfile, SolverResult?>? runCrossFamilyScout,
        Func<SolverSearchProfile, SolverResult>? runWideProbe,
        Func<SolverSearchProfile, SolverResult>? runConfirmedProbe,
        Func<SolverSearchProfile, SolverResult> solveBaseline,
        out bool confirmedProbeVictory)
    {
        confirmedProbeVictory = false;
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
        int earlyWinTurn = root.StartTurnNumber + 3;
        bool compareRankBands = !profile.SecondRankBand && !profile.BaseScoreOnly
            && (root.Enemies.Count >= 4 || root.Enemies.Count == 1
                && (exploration?.CombatEndedTurn is not { } explorationTurn || explorationTurn <= earlyWinTurn));
        SolverResult? probeWinner = null;
        string probeWinnerMethod = compareRankBands ? "beam_probe" : "narrow_probe";
        if (!settled && runConfirmedProbe != null && (root.Enemies.Count > 1 || compareRankBands)
            && NoveltyPortfolioBudget.Remaining(profile, clock.ElapsedMilliseconds, explorationExpanded) is { } beforeProbe
            && beforeProbe.BeamWidth >= 3 && beforeProbe.SoftTimeBudgetMilliseconds >= 80_000
            && beforeProbe.MaxExpandedNodes >= 48_000)
        {
            // Keep the ordinary width for large groups so early targeting choices have room.
            // Compare two ranking bands for those groups and for short single-enemy fights.
            int probeWidth = compareRankBands ? profile.BeamWidth
                : BeamWidthPortfolio.ScaledWidth(profile.BeamWidth, BeamWidthPortfolio.NarrowRefinementRatio);
            SolverSearchProfile probeProfile = beforeProbe with
            {
                BeamWidth = probeWidth,
                SoftTimeBudgetMilliseconds = Math.Min(15_000, beforeProbe.SoftTimeBudgetMilliseconds / 5),
                // Cap work as well as time so a quickly found win does not spend the
                // entire time slice expanding alternatives before the second width runs.
                MaxExpandedNodes = (int)Math.Min(beforeProbe.MaxExpandedNodes / 5, (compareRankBands ? 72L : 96L) * probeWidth),
            };
            SolverResult first = runConfirmedProbe(probeProfile);
            string probeKind = compareRankBands ? "RANK_BAND" : "NARROW_BEAM";
            policy.Diagnostics.Info($"[CombatSolver/Test] SOLO_{probeKind}_PROBE " +
                $"beam={probeProfile.BeamWidth} loss={first.ProjectedBattleHpLost} " +
                $"turn={first.CombatEndedTurn} boundary={first.BoundaryReason}");
            if (ResolveTakeoverResult(first, policy.Interaction) is { } adoptedProbe)
                return adoptedProbe;
            if (IsProbeVictory(first) && BeatsExploration(first))
                probeWinner = first;
            if (probeWinner != null && (!compareRankBands || first.CombatEndedTurn <= earlyWinTurn
                    && first.BoundaryReason != SearchBoundaryReason.TimeLimit))
            {
                publish?.Invoke(first);
                if (NoveltyPortfolioBudget.Remaining(profile, clock.ElapsedMilliseconds,
                        totals.Snapshot().ExpandedNodes - expandedBefore) is { } beforeCheck)
                {
                    SolverSearchProfile checkProfile = beforeCheck with
                    {
                        SecondRankBand = compareRankBands || beforeCheck.SecondRankBand,
                        SoftTimeBudgetMilliseconds = Math.Clamp(beforeCheck.SoftTimeBudgetMilliseconds / 5, 1,
                            compareRankBands ? 12_000 : 9_000),
                        MaxExpandedNodes = (int)Math.Clamp(beforeCheck.MaxExpandedNodes / 5, 1,
                            (compareRankBands ? 72L : 80L) * beforeCheck.BeamWidth),
                    };
                    SolverResult check = runConfirmedProbe(checkProfile);
                    if (ResolveTakeoverResult(check, policy.Interaction) is { } adoptedCheck)
                        return adoptedCheck;
                    if (IsProbeVictory(check) && BeatsExploration(check))
                    {
                        int? checkQuality = CompareCompletedResultPrimaryQuality(root, policy, check, first);
                        bool checkComplete = IsCleanPotionFreeVictory(check);
                        confirmedProbeVictory = compareRankBands
                            ? checkQuality == 0 && check.ProjectedBattleHpLost == first.ProjectedBattleHpLost
                                && check.BoundaryReason != SearchBoundaryReason.TimeLimit
                            : checkQuality is >= 0 || checkQuality.HasValue && checkComplete;
                        policy.Diagnostics.Info($"[CombatSolver/Test] SOLO_{probeKind}_CONFIRMATION " +
                            $"accepted={confirmedProbeVictory} first_loss={first.ProjectedBattleHpLost} " +
                            $"first_turn={first.CombatEndedTurn} check_loss={check.ProjectedBattleHpLost} " +
                            $"check_turn={check.CombatEndedTurn} check_boundary={check.BoundaryReason}");
                        if (confirmedProbeVictory)
                        {
                            bool selectCheck = checkQuality < 0 || checkQuality == 0
                                && (checkComplete || !IsCleanPotionFreeVictory(first))
                                && IsBetterPotionPolicyResult(root, policy, check, first);
                            SolverResult winner = selectCheck ? check : first;
                            winner.NoveltyPortfolio = Describe(compareRankBands ? "rank_band_confirmed" : "narrow_confirmed",
                                exploration, winner, selectCheck ? "beam_check" : probeWinnerMethod, beforeCheck);
                            return winner;
                        }
                        // A failed confirmation only rules out the shortcut. Keep its
                        // verified victory in the final comparison with the normal pass.
                        if (IsBetterPotionPolicyResult(root, policy, check, first))
                        {
                            probeWinner = check;
                            probeWinnerMethod = "beam_check";
                        }
                    }
                }
            }
        }
        SolverResult? wideProbe = null;
        if (!settled && runWideProbe != null && exploration != null && IsCompleteVictory(exploration)
            && NoveltyPortfolioBudget.Remaining(profile, clock.ElapsedMilliseconds,
                totals.Snapshot().ExpandedNodes - expandedBefore) is { } beforeWide
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
            SolverResult? finished = exploration ?? probeWinner;
            if (finished == null)
                throw new PotionPolicyUnsatisfiedException("Novelty search exhausted the request before satisfying mandatory potion use.");
            if (PreferProbe(probeWinner, finished)) finished = probeWinner!;
            if (PreferProbe(wideProbe, finished)) finished = wideProbe!;
            finished.NoveltyPortfolio = Describe("exploration_finished", exploration, wideProbe ?? probeWinner,
                MethodOf(finished, exploration), remaining);
            return finished;
        }
        cancellation.ThrowIfCancellationRequested();
        SolverResult baseline = solveBaseline(remaining);
        bool selectExploration = baseline.ResultScope == SolverResultScope.SearchCompletion
            && exploration != null && IsCompleteVictory(exploration)
            && IsBetterPotionPolicyResult(root, policy, exploration, baseline);
        SolverResult selected = selectExploration ? exploration! : baseline;
        if (PreferProbe(probeWinner, selected)) selected = probeWinner!;
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
            if (ReferenceEquals(selected, probeWinner)) return probeWinnerMethod;
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
