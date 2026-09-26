using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    private static readonly SearchWorkAllowance ResumableMemberAllowance = new(256);
    private static readonly SearchWorkAllowance E3FixedMemberAllowance = new(
        maxParentCommits: 256,
        maxTransitions: 1_024);

    private static SolverResult RunResumableMemberToCompletion(
        CombatBeamSolver solver,
        CancellationToken cancellationToken,
        SearchDiagnosticsSink diagnostics)
    {
        using CombatBeamSolver.SearchMemberExecutionSession session = solver.CreateExecutionSession();
        int yields = 0;
        while (true)
        {
            SearchStepResult step = session.Step(ResumableMemberAllowance, cancellationToken);
            if (step.Status == SearchStepStatus.Yielded)
            {
                yields++;
                continue;
            }
            if (step.Status == SearchStepStatus.Canceled)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            }
            if (step.Status != SearchStepStatus.Completed || session.Result == null)
            {
                throw new InvalidOperationException(
                    $"成员级搜索会话异常结束：status={step.Status} result={session.Result != null}。");
            }
            if (yields > 0)
            {
                diagnostics.Info(
                    $"[CombatSolver/Test] SEARCH_MEMBER_RESUME yields={yields} " +
                    $"committed_parents={step.TotalCommittedParents}");
            }
            return session.Result;
        }
    }

    public static SolverResult Solve(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback)
    {
        SearchRequestWorkTotals requestWorkTotals = new();
        BeamWidthPortfolioTelemetry portfolioTelemetry = new();
        policy = policy with
        {
            RequestWorkTotals = requestWorkTotals,
            PortfolioTelemetry = portfolioTelemetry,
        };
        SearchInteractionState? interaction = policy.Interaction;
        SolverResult? currentCompleteAdoptableResult = null;
        SolverInterimResult? currentDisplayedResult = null;
        SolverProgress? lastProgress = null;
        int currentTurnPreviewVersion = 0;
        int speculativeRouteVersion = 0;
        SolverCurrentTurnPreview? currentTurnPreview = null;
        SolverSpeculativeRoutePreview? speculativeRoutePreview = null;
        SolverRouteAdoptionSeed? currentRouteAdoptionSeed = null;

        bool TryPromoteDisplayedResult(SolverInterimResult candidate)
        {
            if (currentDisplayedResult != null)
            {
                if (candidate == currentDisplayedResult)
                    return true;
                if (!SolverInterimResultOrdering.CanPromoteDisplayedResult(
                        candidate,
                        currentDisplayedResult))
                    return false;
            }
            currentDisplayedResult = candidate;
            return true;
        }

        void PublishAdoptableResult(SolverResult result)
        {
            if (result.OnlyDeathRoutesFound
                || !SolverInterimResultOrdering.IsCompleteVictory(
                    result.BestNode.ActionCount,
                    result.Snapshot.AllEnemiesDead,
                    result.Snapshot.PlayerDead,
                    result.Snapshot.ProjectedPlayerHp))
            {
                return;
            }

            SolverInterimResult summary = BuildInterimResult(root, policy, result);
            bool promoted = TryPromoteDisplayedResult(summary);
            if (!promoted && summary != currentDisplayedResult)
                return;
            currentCompleteAdoptableResult = result;
            portfolioTelemetry.RecordCandidatePublished(
                result.SearchEfficiencyOrigin,
                result.SearchEfficiencyEvaluationContextId ?? string.Empty);
            currentTurnPreview = SolverCurrentTurnPreview.FromResult(
                result,
                ++currentTurnPreviewVersion);
            speculativeRoutePreview = SolverSpeculativeRoutePreview.FromResult(
                result,
                ++speculativeRouteVersion);
            SolverRouteAdoptionSeed seed = new(
                speculativeRoutePreview.CandidateVersion,
                result.BestNode.Actions,
                () => result);
            currentRouteAdoptionSeed = seed;
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SEARCH_INTERIM_RESULT potions={result.ProjectedBattlePotionCount} " +
                $"projected_battle_hp_lost={result.ProjectedBattleHpLost}");
            if (lastProgress != null && progressCallback != null)
            {
                lastProgress = lastProgress with
                {
                    CurrentBestResult = currentDisplayedResult,
                    CurrentTurnPreview = currentTurnPreview,
                    SpeculativeRoutePreview = speculativeRoutePreview,
                    RouteAdoptionSeed = currentRouteAdoptionSeed,
                };
                progressCallback(lastProgress);
            }
        }

        Action<SolverProgress>? enrichedProgressCallback = progressCallback == null
            ? null
            : progress =>
            {
                lastProgress = progress;
                // Supplemental searches publish their own local previews. Once a global best exists,
                // keep those previews and their adoption seed together unless that local result wins globally.
                bool acceptsRouteUpdate = currentDisplayedResult == null;
                if (progress.CurrentBestResult is { } candidate)
                {
                    acceptsRouteUpdate = TryPromoteDisplayedResult(candidate);
                }
                else if (currentDisplayedResult != null)
                {
                    acceptsRouteUpdate = false;
                }

                if (acceptsRouteUpdate)
                {
                    if (progress.CurrentTurnPreview is { } current)
                    {
                        currentTurnPreview = current;
                        currentTurnPreviewVersion = Math.Max(
                            currentTurnPreviewVersion,
                            current.CandidateVersion);
                    }
                    if (progress.SpeculativeRoutePreview is { } speculative)
                    {
                        speculativeRoutePreview = speculative;
                        currentRouteAdoptionSeed = progress.RouteAdoptionSeed;
                        speculativeRouteVersion = Math.Max(
                            speculativeRouteVersion,
                            speculative.CandidateVersion);
                    }
                    if (progress.OfficialPublishedOrigin is { } officialOrigin
                        && !string.IsNullOrWhiteSpace(
                            progress.OfficialPublishedEvaluationContextId))
                    {
                        portfolioTelemetry.RecordCandidatePublished(
                            officialOrigin,
                            progress.OfficialPublishedEvaluationContextId);
                        policy.Diagnostics.Info(
                            $"[CombatSolver/Test] SEARCH_E1_EARLY_PUBLISH " +
                            $"candidate_id={officialOrigin.CandidateId} " +
                            $"context={progress.OfficialPublishedEvaluationContextId}");
                    }
                }
                progressCallback(progress with
                {
                    CurrentBestResult = currentDisplayedResult,
                    CurrentTurnPreview = currentTurnPreview,
                    SpeculativeRoutePreview = speculativeRoutePreview,
                    RouteAdoptionSeed = currentRouteAdoptionSeed,
                });
            };
        try
        {
            SolverResult result = SolveCore(
                root,
                displayNames,
                battleDamage,
                policy,
                cancellationToken,
                enrichedProgressCallback,
                interaction == null ? null : PublishAdoptableResult);
            SolverResult selected = ResolveTakeoverResult(result, interaction) ?? result;
            if (interaction?.CurrentTakeoverRequest?.Kind == SearchTakeoverKind.ApplyCurrentTurn
                && selected.ResultScope == SolverResultScope.SearchCompletion
                && currentCompleteAdoptableResult != null)
            {
                selected = currentCompleteAdoptableResult;
            }
            portfolioTelemetry.RecordCandidatePublished(
                selected.SearchEfficiencyOrigin,
                selected.SearchEfficiencyEvaluationContextId ?? string.Empty);
            PopulateRequestWorkTotals(selected, requestWorkTotals);
            selected.PortfolioTelemetry = portfolioTelemetry;
            LogSearchEfficiencySummary(root, policy.Diagnostics, selected, portfolioTelemetry);
            return selected;
        }
        catch (OperationCanceledException)
            when (interaction?.CurrentTakeoverRequest?.Kind == SearchTakeoverKind.ApplyCurrentTurn
                  && currentCompleteAdoptableResult != null)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SEARCH_INTERIM_ADOPTED " +
                $"potions={currentCompleteAdoptableResult.ProjectedBattlePotionCount} " +
                $"projected_battle_hp_lost={currentCompleteAdoptableResult.ProjectedBattleHpLost}");
            portfolioTelemetry.RecordCandidatePublished(
                currentCompleteAdoptableResult.SearchEfficiencyOrigin,
                currentCompleteAdoptableResult.SearchEfficiencyEvaluationContextId ?? string.Empty);
            PopulateRequestWorkTotals(currentCompleteAdoptableResult, requestWorkTotals);
            currentCompleteAdoptableResult.PortfolioTelemetry = portfolioTelemetry;
            LogSearchEfficiencySummary(
                root,
                policy.Diagnostics,
                currentCompleteAdoptableResult,
                portfolioTelemetry);
            return currentCompleteAdoptableResult;
        }
    }

    private static void LogSearchEfficiencySummary(
        CombatRootSnapshot root,
        SearchDiagnosticsSink diagnostics,
        SolverResult selected,
        BeamWidthPortfolioTelemetry telemetry)
    {
        CandidateOrigin? origin = selected.SearchEfficiencyOrigin;
        string? contextId = selected.SearchEfficiencyEvaluationContextId;
        CandidateMilestones? milestones = telemetry.FindCandidateMilestones(origin, contextId);
        if (origin != null && contextId != null && milestones != null)
        {
            SearchEfficiencyMemberReport? member = telemetry.FindSearchMember(origin.SearchMemberId);
            string FormatTimestamp(long? ticks)
                => ticks.HasValue
                    ? telemetry.ToRequestMilliseconds(ticks.Value).ToString(
                        "F3",
                        System.Globalization.CultureInfo.InvariantCulture)
                    : "-";
            diagnostics.Info(
                $"[CombatSolver/Test] SEARCH_E0_TIMELINE candidate_id={origin.CandidateId} " +
                $"player_count={root.PlayerCount} " +
                $"member_id={origin.SearchMemberId} member_kind={member?.Kind ?? "unknown"} " +
                $"generated_ms={FormatTimestamp(origin.FirstGeneratedTicks)} " +
                $"evaluated_ms={FormatTimestamp(milestones.EvaluatedTicks)} " +
                $"selected_ms={FormatTimestamp(milestones.SelectedTicks)} " +
                $"published_ms={FormatTimestamp(milestones.PublishedTicks)} " +
                $"expanded_at_generation={origin.ExpandedAtGeneration} " +
                $"turn_depth={origin.TurnDepth} context={contextId}");
        }

        foreach (SearchEfficiencyMemberReport member in telemetry.SearchMembers)
        {
            double elapsedMs = member.CompletedTicks.HasValue
                ? BeamWidthPortfolioTelemetry.DurationMilliseconds(
                    Math.Max(0, member.CompletedTicks.Value - member.StartedTicks))
                : 0d;
            diagnostics.Info(
                $"[CombatSolver/Test] SEARCH_E0_MEMBER member_id={member.MemberId} " +
                $"kind={member.Kind} beam={member.BeamWidth} " +
                $"second_rank_band={member.SecondRankBand} base_score_only={member.BaseScoreOnly} " +
                $"novelty={member.Novelty} elapsed_ms={elapsedMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"expanded={member.ExpandedNodes} transitions={member.TransitionCount}");
        }
        foreach (SearchEfficiencyPhaseReport phase in telemetry.SearchPhases)
        {
            double exclusiveMs = BeamWidthPortfolioTelemetry.DurationMilliseconds(phase.ExclusiveTicks);
            diagnostics.Info(
                $"[CombatSolver/Test] SEARCH_E0_PHASE member_id={phase.SearchMemberId} " +
                $"phase={phase.Phase} calls={phase.CallCount} " +
                $"exclusive_ms={exclusiveMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
        }
    }

    private static bool IsAdoptionResult(SolverResult result)
        => result.ResultScope is SolverResultScope.CurrentTurnAdoption
            or SolverResultScope.RouteAdoption
            || SolverInterimResultOrdering.IsCompleteVictory(
                result.BestNode.ActionCount,
                result.Snapshot.AllEnemiesDead,
                result.Snapshot.PlayerDead,
                result.Snapshot.ProjectedPlayerHp);

    private static SolverResult? ResolveTakeoverResult(
        SolverResult result,
        SearchInteractionState? interaction)
    {
        SearchTakeoverRequest? request = interaction?.CurrentTakeoverRequest;
        if (request == null)
            return null;
        if (result.ResultScope is SolverResultScope.CurrentTurnAdoption
            or SolverResultScope.RouteAdoption)
        {
            return result;
        }
        if (request.Kind == SearchTakeoverKind.AdoptRoute)
            return request.RouteAdoptionSeed?.Materialize();
        return IsAdoptionResult(result) ? result : null;
    }

    private static SolverResult SolveCore(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback,
        Action<SolverResult>? interimResultCallback)
    {
        if (policy.CurrentTurnOnly)
        {
            // Multiplayer current-turn advice must not enter novelty/long-horizon
            // portfolio passes even if a caller supplied those knobs from settings.
            policy = policy with
            {
                NoveltySearch = null,
                UseNoveltyPortfolio = false,
            };
        }
        Stopwatch requestClock = Stopwatch.StartNew();
        IReadOnlyList<PlanAction> continuationSeedActions =
            policy.RoutePolicy == SearchRoutePolicy.MultiplayerSinglePlayerCore
            && !policy.IncludeTurnSetup
                ? policy.ContinuationSeedActions
                : [];
        if (policy.ContinuationSeedActions.Count > 0)
        {
            // P2 owns continuation repair as an independent member. Ordinary Beam, Novelty,
            // and potion members must never inherit the suggestion into their frontier.
            policy = policy with { ContinuationSeedActions = [] };
        }
        bool forcedSmartGradient = policy.PotionPolicy == SolverPotionPolicy.Smart
            && policy.PotionStrategy.HasForcedDirectives;
        SearchPolicySnapshot forcedBaselinePolicy = forcedSmartGradient
            ? policy with { PotionStrategy = policy.PotionStrategy.ForForcedBaseline() }
            : policy;
        SolverPotionPolicy? initialPotionPolicyOverride = policy.PotionPolicy == SolverPotionPolicy.Smart
            && !policy.PotionStrategy.HasForcedDirectives
                ? SolverPotionPolicy.Disabled
                : null;
        // The progress bar represents the whole request. Individual Beam, novelty,
        // refinement and potion-audit searches all consume this same time budget.
        SolverSearchProfile profile = policy.Profile;
        if (policy.BudgetOverrideMilliseconds is { } deepBudget)
            profile = profile with { SoftTimeBudgetMilliseconds = deepBudget };
        if (progressCallback != null)
        {
            long completedSearches = 0;
            long completedElapsed = 0;
            int lastExpanded = 0;
            long lastElapsed = 0;
            Action<SolverProgress> publishProgress = progressCallback;
            progressCallback = progress =>
            {
                if (progress.ExpandedNodes < lastExpanded
                    || progress.ElapsedMilliseconds < lastElapsed)
                {
                    completedSearches += lastExpanded;
                    completedElapsed += lastElapsed;
                }
                lastExpanded = progress.ExpandedNodes;
                lastElapsed = progress.ElapsedMilliseconds;
                publishProgress(progress with
                {
                    ReviewedWorldlines = completedSearches + progress.ExpandedNodes,
                    ElapsedMilliseconds = completedElapsed + progress.ElapsedMilliseconds,
                    RequestBudgetMilliseconds = profile.SoftTimeBudgetMilliseconds,
                });
            };
        }
        SmartLayerMemoryForecast memoryForecast = new();
        bool continuationSeedConsumed = false;
        // One search profile drives primary search and all supplemental audits.
        if (root.IsActEndingBoss && profile.BeamWidth < 45)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] ACT_ENDING_BOSS_SEARCH_OVERRIDE " +
                $"beam={profile.BeamWidth}->45 reason=preserve_survival_routes");
            profile = profile with { BeamWidth = 45 };
        }
        // 一轮完整的深化搜索：主搜索（Smart 时先按无主动用药跑）＋补充审计。抬节点上限重搜时
        // 原样再走一遍，所以抽成一个本地函数；每一轮自带一只秒表，补充审计那边算剩余预算靠它。
        SolverResult? takeoverResult = null;
        bool passSettled = false;
        SolverResult RunSearchPass(SolverSearchProfile passProfile, Stopwatch passClock)
        {
            long passAllocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
            long passTransitionsAtStart = policy.RequestWorkTotals?.Snapshot().TransitionCount ?? 0;
            SearchPolicySnapshot passPolicy = forcedBaselinePolicy;
            SearchPolicySnapshot beamPolicy = passPolicy.NoveltySearch == null
                ? passPolicy : passPolicy with { NoveltySearch = null };
            SolverSearchProfile activeProfile = passProfile;
            Stopwatch activeClock = passClock;
            SolverResult? continuationSeedIncumbent = null;

            SolverResult SolveMember(SolverSearchProfile memberProfile, bool refinement)
            {
                Action<SolverProgress>? memberProgressCallback = refinement && progressCallback != null
                    ? progress => progressCallback(progress with { Phase = "正在精炼路线" })
                    : progressCallback;
                CombatBeamSolver solver = new(
                    root,
                    displayNames,
                    battleDamage,
                    beamPolicy,
                    cancellationToken,
                    memberProgressCallback,
                    memberProfile,
                    potionPolicyOverride: initialPotionPolicyOverride);
                return RunResumableMemberToCompletion(
                    solver,
                    cancellationToken,
                    beamPolicy.Diagnostics);
            }
            // 基线成员一跑完就按今天的方式把完整结果发布给覆盖层（覆盖层的中途路线走
            // SolverProgress，见 RunBeamWidthPortfolioPass 的注释）；精炼成员只有更优时才会
            // 在本轮末尾再发布一次，所以同一份结果不会发布两遍。
            SolverResult? publishedBaseline = null;
            Action<SolverResult>? publishBaseline =
                policy.UseBeamWidthPortfolio && interimResultCallback != null
                    ? baseline =>
                    {
                        publishedBaseline = baseline;
                        interimResultCallback(baseline);
                    }
                    : null;
            SolverResult RunBaseline(SolverSearchProfile baselineProfile)
                => RunBeamWidthPortfolioPass(root, beamPolicy, baselineProfile,
                    ReferenceEquals(baselineProfile, activeProfile)
                        ? activeClock
                        : Stopwatch.StartNew(),
                    SolveMember, publishBaseline);
            SolverResult? earlySmartPotionBaseline = null;
            SolverResult? earlySmartPotionScout = null;
            SolverResult? RunCrossFamilyScout(
                SolverResult provisionalPotionFree,
                SolverSearchProfile scoutProfile)
            {
                earlySmartPotionBaseline = provisionalPotionFree;
                earlySmartPotionScout = SearchEarlySmartPotionScout(
                    root,
                    displayNames,
                    battleDamage,
                    beamPolicy,
                    cancellationToken,
                    progressCallback,
                    scoutProfile,
                    provisionalPotionFree,
                    interimResultCallback);
                return earlySmartPotionScout;
            }
            SolverResult RunPrimary()
                => policy.UseNoveltyPortfolio
                    ? RunNoveltyPortfolioPass(root, displayNames, battleDamage, passPolicy, activeProfile,
                        activeClock, initialPotionPolicyOverride, cancellationToken, progressCallback,
                        interimResultCallback, RunCrossFamilyScout, RunBaseline)
                    : RunBaseline(activeProfile);

            if (!continuationSeedConsumed
                && continuationSeedActions.Count > 0
                && ContinuationSeedIncumbentBudget.Probe(passProfile) is { } seedProfile)
            {
                continuationSeedConsumed = true;
                SearchRequestWorkTotals totals = policy.RequestWorkTotals
                    ?? throw new InvalidOperationException(
                        "P2 continuation-seed incumbent requires request work totals.");
                SearchRequestWorkSnapshot beforeSeed = totals.Snapshot();
                long seedStartedMs = passClock.ElapsedMilliseconds;
                SearchPolicySnapshot seedPolicy = beamPolicy with
                {
                    Interaction = null,
                    NoveltySearch = null,
                    UseNoveltyPortfolio = false,
                    ContinuationSeedActions = continuationSeedActions,
                    ContinuationEnumerationHintActions = [],
                };
                try
                {
                    CombatBeamSolver seedSolver = new(
                        root,
                        displayNames,
                        battleDamage,
                        seedPolicy,
                        cancellationToken,
                        progressCallback: null,
                        seedProfile,
                        potionPolicyOverride: initialPotionPolicyOverride,
                        reserveScenarioReevaluationBudget: false,
                        continuationSeedProbe: true);
                    SolverResult seedResult = RunResumableMemberToCompletion(
                        seedSolver,
                        cancellationToken,
                        seedPolicy.Diagnostics);
                    seedResult.SingleSessionSearch = true;
                    PopulateSingleSessionTotals(seedResult);
                    bool admissible = seedResult.ResultScope == SolverResultScope.SearchCompletion
                        && IsCompleteVictory(seedResult);
                    if (admissible)
                    {
                        continuationSeedIncumbent = seedResult;
                        interimResultCallback?.Invoke(seedResult);
                    }
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] P2_CONTINUATION_SEED_INCUMBENT " +
                        $"status={(admissible ? "admissible" : "incomplete")} " +
                        $"actions={continuationSeedActions.Count} " +
                        $"expanded={seedResult.ExpandedNodes} transitions={seedResult.TransitionCount} " +
                        $"elapsed_ms={seedResult.Elapsed.TotalMilliseconds:F1} " +
                        $"published={admissible.ToString().ToLowerInvariant()}");
                }
                catch (ContinuationSeedRejectedException rejected)
                {
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] P2_CONTINUATION_SEED_INCUMBENT " +
                        $"status=rejected reason={rejected.Reason} " +
                        $"actions={continuationSeedActions.Count} published=false");
                }

                SearchRequestWorkSnapshot afterSeed = totals.Snapshot();
                long seedElapsedMs = Math.Max(0, passClock.ElapsedMilliseconds - seedStartedMs);
                long seedExpanded = Math.Max(
                    0, afterSeed.ExpandedNodes - beforeSeed.ExpandedNodes);
                activeProfile = ContinuationSeedIncumbentBudget.Remaining(
                        passProfile,
                        seedElapsedMs,
                        seedExpanded)
                    ?? throw new InvalidOperationException(
                        "P2 continuation-seed incumbent exhausted the primary request budget.");
                activeClock = Stopwatch.StartNew();
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] P2_CONTINUATION_SEED_BUDGET " +
                    $"probe_nodes={seedExpanded}/{seedProfile.MaxExpandedNodes} " +
                    $"probe_ms={seedElapsedMs}/{seedProfile.SoftTimeBudgetMilliseconds} " +
                    $"remaining_nodes={activeProfile.MaxExpandedNodes} " +
                    $"remaining_ms={activeProfile.SoftTimeBudgetMilliseconds}");
            }

            SolverResult SelectContinuationSeedIncumbent(SolverResult ordinary)
            {
                if (continuationSeedIncumbent == null
                    || ordinary.ResultScope != SolverResultScope.SearchCompletion
                    || !IsBetterPotionPolicyResult(
                        root,
                        policy,
                        continuationSeedIncumbent,
                        ordinary))
                {
                    return ordinary;
                }

                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] P2_CONTINUATION_SEED_SELECTED " +
                    $"seed_hp_lost={continuationSeedIncumbent.ProjectedBattleHpLost} " +
                    $"ordinary_hp_lost={ordinary.ProjectedBattleHpLost}");
                return continuationSeedIncumbent;
            }

            bool hasForcedBaseline = forcedSmartGradient;
            SolverResult passResult;
            try
            {
                passResult = RunPrimary();
            }
            catch (PotionPolicyUnsatisfiedException) when (forcedSmartGradient)
            {
                // If mandatory potions alone cannot produce a usable route, restore ordinary
                // mixed Smart search so an optional potion may still rescue the fight.
                hasForcedBaseline = false;
                passPolicy = policy;
                beamPolicy = policy.NoveltySearch == null
                    ? policy : policy with { NoveltySearch = null };
                passResult = RunPrimary();
            }
            NoveltyPortfolioTelemetry? noveltyPass = passResult.NoveltyPortfolio;
            ObserveSmartLayerMemory(
                policy, memoryForecast, passAllocatedAtStart, passTransitionsAtStart,
                passResult, activeProfile,
                completedPotionCount: hasForcedBaseline
                    ? policy.PotionStrategy.ForcedDirectiveCount
                    : 0);
            if (policy.MeasurePhasePerformance)
                policy.Diagnostics.Info(SolverDiagnostics.DescribeSearchPhasePerformance(passResult));
            passResult.SingleSessionSearch = true;
            PopulateSingleSessionTotals(passResult);
            if (!ReferenceEquals(passResult, publishedBaseline))
                interimResultCallback?.Invoke(passResult);
            if (ResolveTakeoverResult(passResult, policy.Interaction) is { } passTakeover)
            {
                takeoverResult = passTakeover;
                return passResult;
            }
            if (passResult.DeterministicBlockPotionInserted)
            {
                passSettled = true;
                return SelectContinuationSeedIncumbent(passResult);
            }
            if (!policy.PotionStrategy.HasForcedDirectives || hasForcedBaseline)
            {
                if (!hasForcedBaseline && HasReachedAcceptableBattleHpLoss(policy, passResult))
                {
                    passSettled = true;
                    return SelectContinuationSeedIncumbent(passResult);
                }
                passResult = RunSupplementalAudits(
                    root,
                    displayNames,
                    battleDamage,
                    policy.NoveltySearch == null
                        ? policy
                        : policy with { NoveltySearch = null },
                    cancellationToken,
                    progressCallback,
                    activeProfile,
                    activeClock,
                    passResult,
                    memoryForecast,
                    interimResultCallback,
                    earlySmartPotionBaseline,
                    earlySmartPotionScout);
                // The final potion audit may return another result object. Keep the
                // primary-pass observations alongside the request's final outcome.
                passResult.NoveltyPortfolio = noveltyPass;
            }
            return SelectContinuationSeedIncumbent(passResult);
        }

        SolverResult result = RunSearchPass(profile, requestClock);
        if (takeoverResult != null)
            return takeoverResult;
        // 打到可接受战损就收手那一条和改动之前一样直接返回，连 SEARCH_SESSION 都不打。
        if (passSettled || policy.FixedBudget)
            return result;
        result = EscalateSearchWhenNoVictory(
            root,
            policy,
            profile,
            requestClock,
            result,
            RunSearchPass,
            () => takeoverResult != null
                || passSettled
                || cancellationToken.IsCancellationRequested
                || policy.Interaction?.CurrentTakeoverRequest != null);
        if (takeoverResult != null)
            return takeoverResult;
        if (passSettled)
            return result;
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] SEARCH_SESSION mode=single_anytime " +
            $"total_budget_ms={profile.SoftTimeBudgetMilliseconds}");
        return result;
    }

    /// <summary>
    /// 主搜索的宽度组合接线，开关开关两种情况都走这里，所以逐成员诊断和
    /// <see cref="BeamWidthPortfolioTelemetry" /> 在关闭时同样存在（单成员一行）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **基线成员逐位不变**：关闭时用请求自己的 <paramref name="profile" /> 实例直接求解；打开时
    /// 组合器把全部共享预算给首个成员，宽度就是基线宽度，其余 Profile 维度照抄。成员只有 Beam
    /// 宽度、分到的节点上限，以及（仅精炼成员）收紧到剩余时间的软时间预算三处不同。
    /// </para>
    /// <para>
    /// 界面的中途路线走 <c>SolverProgress</c> 回调：搜索发布进度，运行时把进度里的
    /// <c>SpeculativeRoutePreview</c> / <c>CurrentTurnPreview</c> 渲染出来。因此基线成员一完成就用
    /// <paramref name="publishBaseline" />（协调器已有的 interim 回调）把完整结果推出去，
    /// 玩家看到第一条路线的时刻不受后面的精炼影响。
    /// </para>
    /// <para>
    /// 精炼成员的准入全部交给 <see cref="BeamWidthPortfolioGate" />：基线必须已经把这一宽度搜干净、
    /// 自己没吃掉超过四分之一的时间预算，剩余节点、剩余时间、现有内存压力信号报告的余量都够按宽度
    /// 外推的估算，才会启动。成员顺序执行，不并行。
    /// </para>
    /// </remarks>
    private static SolverResult RunBeamWidthPortfolioPass(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverSearchProfile profile,
        Stopwatch passClock,
        Func<SolverSearchProfile, bool, SolverResult> solveMember,
        Action<SolverResult>? publishBaseline)
    {
        SearchRequestWorkTotals totals = policy.RequestWorkTotals
            ?? throw new InvalidOperationException("Beam 宽度组合需要请求级工作量记录。");
        BeamWidthPortfolioTelemetry telemetry = policy.PortfolioTelemetry
            ?? throw new InvalidOperationException("Beam 宽度组合需要请求级诊断记录。");
        List<BeamWidthPortfolioMemberCost> costs = [];
        BeamWidthPortfolioBaseline baseline = default;
        bool baselineObserved = false;
        SolverResult? bestPublishedMember = null;
        long expandedByMembers = 0;

        long RemainingMilliseconds()
            => profile.SoftTimeBudgetMilliseconds - passClock.ElapsedMilliseconds;

        BeamWidthPortfolioRun<SolverResult> RunMember(SolverSearchProfile memberProfile)
        {
            // 精炼成员沿用现有的软时间预算取消：把它收紧到本轮预算的剩余部分，成员自己就会在
            // 预算耗尽时停下，不必另造一套超时。基线成员原样不动。
            SolverSearchProfile effectiveProfile = baselineObserved
                ? memberProfile with
                {
                    SoftTimeBudgetMilliseconds = (int)Math.Clamp(
                        RemainingMilliseconds(), 1, memberProfile.SoftTimeBudgetMilliseconds),
                }
                : memberProfile;
            SearchRequestWorkSnapshot before = totals.Snapshot();
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            long startedMilliseconds = passClock.ElapsedMilliseconds;
            SolverResult memberResult = solveMember(effectiveProfile, baselineObserved);
            long memberElapsed = Math.Max(0, passClock.ElapsedMilliseconds - startedMilliseconds);
            long memberAllocated = Math.Max(
                0, GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore);
            long managedHeapAfter = GC.GetTotalMemory(forceFullCollection: false);
            SearchRequestWorkSnapshot after = totals.Snapshot();
            long expanded = after.ExpandedNodes - before.ExpandedNodes;
            expandedByMembers += expanded;
            costs.Add(new BeamWidthPortfolioMemberCost(memberElapsed, memberAllocated, managedHeapAfter));
            bool won = IsCompleteVictory(memberResult);
            bool terminal = won || memberResult.Snapshot.PlayerDead;
            if (!baselineObserved)
            {
                baseline = new BeamWidthPortfolioBaseline(
                    memberResult.BoundaryReason == SearchBoundaryReason.None,
                    IsProvenZeroDamageRoute(root, policy, memberResult),
                    memberElapsed,
                    expanded,
                    memberAllocated,
                    effectiveProfile.BeamWidth);
                baselineObserved = true;
                telemetry.RecordFirstRoutePublished(passClock.Elapsed.TotalMilliseconds);
                bestPublishedMember = memberResult;
                publishBaseline?.Invoke(memberResult);
            }
            else if (publishBaseline != null
                     && bestPublishedMember != null
                     && IsBetterPotionPolicyResult(root, policy, memberResult, bestPublishedMember))
            {
                bestPublishedMember = memberResult;
                publishBaseline(memberResult);
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] BEAM_WIDTH_PORTFOLIO_EARLY_IMPROVEMENT " +
                    $"beam={effectiveProfile.BeamWidth} " +
                    $"second_rank_band={effectiveProfile.SecondRankBand} " +
                    $"base_score_only={effectiveProfile.BaseScoreOnly} " +
                    $"elapsed_ms={passClock.ElapsedMilliseconds}");
            }
            return new BeamWidthPortfolioRun<SolverResult>(
                memberResult,
                expanded,
                after.TransitionCount - before.TransitionCount,
                memberResult.BoundaryReason.ToString(),
                terminal,
                won,
                terminal ? memberResult.ProjectedBattleHpLost : null,
                memberResult.PotionCount)
            {
                StopPortfolio = memberResult.ResultScope != SolverResultScope.SearchCompletion,
            };
        }

        string? RejectMember(BeamWidthPortfolioMemberSpec member)
            => baselineObserved
                ? BeamWidthPortfolioGate.RejectRefinement(
                    baseline,
                    member.BeamWidth,
                    profile.MaxExpandedNodes - expandedByMembers,
                    RemainingMilliseconds(),
                    profile.SoftTimeBudgetMilliseconds,
                    policy.MemoryPressureSignal.RemainingBytes)
                : null;

        BeamWidthPortfolioOutcome<SolverResult> outcome = policy.UseBeamWidthPortfolio
            ? BeamWidthPortfolio.Run(
                BeamWidthPortfolio.ProductionMembers(profile.BeamWidth, policy.BeamWidthPortfolioWidths),
                profile.MaxExpandedNodes,
                profile,
                RunMember,
                (candidate, current) => IsBetterPotionPolicyResult(root, policy, candidate, current),
                RejectMember,
                policy.Diagnostics.Info)
            : SingleMemberOutcome(profile, RunMember);
        RecordPortfolioMembers(policy, telemetry, outcome, costs);
        return outcome.Selected;
    }

    /// <summary>
    /// 组合关闭时的一条成员明细。求解走请求自己的 Profile 实例，可比性与选中理由按组合器同一条
    /// 规矩判定，A/B 才能并排读同一张表。
    /// </summary>
    private static BeamWidthPortfolioOutcome<SolverResult> SingleMemberOutcome(
        SolverSearchProfile profile,
        Func<SolverSearchProfile, BeamWidthPortfolioRun<SolverResult>> runMember)
    {
        BeamWidthPortfolioRun<SolverResult> run = runMember(profile);
        bool comparable = run.Terminal
            || !string.Equals(
                run.Termination, BeamWidthPortfolio.NodeLimitTermination, StringComparison.Ordinal);
        string selectionReason = run.StopPortfolio
            ? BeamWidthPortfolio.SelectionStopped
            : comparable
                ? BeamWidthPortfolio.SelectionBest
                : BeamWidthPortfolio.SelectionBaselineFallback;
        BeamWidthPortfolioMember member = new(
            profile.BeamWidth,
            profile.SecondRankBand,
            profile.BaseScoreOnly,
            profile.MaxExpandedNodes,
            Ran: true,
            run.ExpandedNodes,
            run.TransitionCount,
            run.Termination,
            run.Terminal,
            run.Won,
            run.BattleHpLost,
            run.PotionCount,
            Compared: run.StopPortfolio || comparable,
            SkippedReason: run.StopPortfolio || comparable
                ? null
                : BeamWidthPortfolio.SkippedNodeLimitNotTerminal);
        return new BeamWidthPortfolioOutcome<SolverResult>(
            run.Result, 0, selectionReason, [member], run.ExpandedNodes, run.TransitionCount);
    }

    /// <summary>逐成员一行诊断，同时把明细与托管堆峰值写进请求级记录。</summary>
    private static void RecordPortfolioMembers(
        SearchPolicySnapshot policy,
        BeamWidthPortfolioTelemetry telemetry,
        BeamWidthPortfolioOutcome<SolverResult> outcome,
        IReadOnlyList<BeamWidthPortfolioMemberCost> costs)
    {
        int costIndex = 0;
        for (int index = 0; index < outcome.Members.Count; index++)
        {
            BeamWidthPortfolioMember member = outcome.Members[index];
            BeamWidthPortfolioMemberCost cost = member.Ran
                ? costs[costIndex++]
                : default;
            BeamWidthPortfolioMemberReport report = new(
                member.BeamWidth,
                member.SecondRankBand,
                member.BaseScoreOnly,
                member.NodeBudget,
                member.Ran,
                Selected: index == outcome.SelectedIndex,
                member.Compared,
                member.SkippedReason,
                member.ExpandedNodes,
                member.TransitionCount,
                member.Termination,
                member.Terminal,
                member.Won,
                member.BattleHpLost,
                member.PotionCount,
                cost.ElapsedMilliseconds,
                cost.AllocatedBytes,
                cost.ManagedHeapBytesAfter);
            telemetry.RecordMember(report);
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] BEAM_WIDTH_PORTFOLIO_MEMBER index={index} " +
                $"beam={report.BeamWidth} second_rank_band={report.SecondRankBand} " +
                $"base_score_only={report.BaseScoreOnly} " +
                $"nodes={report.NodeBudget} ran={report.Ran} " +
                $"selected={report.Selected} compared={report.Compared} " +
                $"skipped={report.SkippedReason ?? "-"} " +
                $"elapsed_ms={report.ElapsedMilliseconds} " +
                $"allocated_delta={report.AllocatedBytes} " +
                $"managed_heap_after={report.ManagedHeapBytesAfter} " +
                $"expanded={report.ExpandedNodes} transitions={report.TransitionCount} " +
                $"termination={report.Termination ?? "-"} won={report.Won?.ToString() ?? "-"} " +
                $"battle_hp_lost={report.BattleHpLost?.ToString() ?? "-"} " +
                $"potions={report.PotionCount?.ToString() ?? "-"}");
        }
        if (costIndex != costs.Count)
        {
            throw new InvalidOperationException(
                $"组合成员明细与实测开销条数不一致：明细 {costIndex} 条，实测 {costs.Count} 条。");
        }
    }

    /// <summary>
    /// 基线是否已经拿到「证明最优」的那一类结果：零战损、零主动用药、没卖血、满血且最大生命没掉。
    /// 与搜索里 <c>ProvenZeroDamage</c> 提前收手的条件同一套，只是从返回结果上复算，不在搜索里加观察点。
    /// </summary>
    private static bool IsProvenZeroDamageRoute(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
        => !policy.EffectiveHasGrowthTargets
            && IsCompleteVictory(result)
            && result.ExplicitPotionCount == 0
            && result.FutureSoldHp == 0
            && result.ProjectedBattleHpLost - result.BattleHpLostSoFar == 0
            && result.Snapshot.PlayerMaxHp >= root.InitialPlayerMaxHp
            && result.Snapshot.PlayerHp >= result.Snapshot.PlayerMaxHp;

    private static SolverResult RunSupplementalAudits(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback,
        SolverSearchProfile profile,
        Stopwatch requestClock,
        SolverResult primary,
        SmartLayerMemoryForecast memoryForecast,
        Action<SolverResult>? interimResultCallback,
        SolverResult? earlySmartPotionBaseline,
        SolverResult? earlySmartPotionScout)
    {
        long remainingMilliseconds = profile.SoftTimeBudgetMilliseconds - requestClock.ElapsedMilliseconds;
        if (remainingMilliseconds <= 0)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SUPPLEMENTAL_AUDIT_BUDGET exhausted=true " +
                $"elapsed_ms={requestClock.ElapsedMilliseconds} " +
                $"budget_ms={profile.SoftTimeBudgetMilliseconds}");
            return primary;
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(remainingMilliseconds));
        SolverResult selected = primary;
        try
        {
            if (!policy.PotionStrategy.HasForcedDirectives)
            {
                selected = AuditRequiredPotionUse(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    deadline.Token,
                    progressCallback,
                    profile,
                    selected);
            }
            if (ResolveTakeoverResult(selected, policy.Interaction) is { } requiredTakeoverResult)
                return requiredTakeoverResult;
            if (!policy.PotionStrategy.HasForcedDirectives
                && HasReachedAcceptableBattleHpLoss(policy, selected))
                return selected;
            if (TryReuseEarlySmartPotionScout(
                    root,
                    policy,
                    selected,
                    earlySmartPotionBaseline,
                    earlySmartPotionScout,
                    out SolverResult? reusedScout))
            {
                selected = reusedScout!;
            }
            else
            {
                selected = AuditSmartPotionUse(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    deadline.Token,
                    cancellationToken,
                    progressCallback,
                    profile,
                    selected,
                    memoryForecast,
                    interimResultCallback);
            }
            if (HasReachedAcceptableBattleHpLoss(policy, selected))
                return selected;
            if (policy.PotionPolicy != SolverPotionPolicy.Smart)
            {
                selected = AuditOpeningPowerUse(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    deadline.Token,
                    progressCallback,
                    profile,
                    selected);
                if (HasReachedAcceptableBattleHpLoss(policy, selected))
                    return selected;
            }
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SUPPLEMENTAL_AUDIT_BUDGET exhausted=true " +
                $"elapsed_ms={requestClock.ElapsedMilliseconds} " +
                $"budget_ms={profile.SoftTimeBudgetMilliseconds} " +
                $"selected_potions={selected.PotionCount}");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return selected;
    }

    private static SolverResult AuditOpeningPowerUse(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback,
        SolverSearchProfile profile,
        SolverResult primary)
    {
        int primaryDeficit = StrategicHpDeficit(root, policy, primary);
        int maximumSmartPotionUses = policy.PotionPolicy == SolverPotionPolicy.Smart
            ? MaximumSmartPotionUses(root, policy, potionFreeWon: true, primaryDeficit)
            : Math.Max(1, primary.PotionCount);
        if (HasReachedProvablePrimaryQualityLowerBound(root, policy, primary)
            || policy.PotionPolicy == SolverPotionPolicy.RequireAtLeastOne
                && battleDamage.PotionsUsedSoFar == 0)
            return primary;

        IReadOnlyList<PlanAction> openingPowers = new CombatBeamSolver(
                root,
                displayNames,
                battleDamage,
                policy,
                cancellationToken,
                progressCallback,
                profile)
            .BuildOpeningPowerActions();
        IReadOnlyList<PlanAction> openingPotions = policy.PotionPolicy == SolverPotionPolicy.Disabled
            || maximumSmartPotionUses == 0
            ? []
            : new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses)
                .BuildOpeningPotionActions();
        IReadOnlyList<PlanAction> generatedResourcePotions = openingPotions.Count == 0
            ? []
            : new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses)
                .SelectGeneratedResourcePotionActions(openingPotions);
        IReadOnlyList<PlanAction> openingResources = new CombatBeamSolver(
                root,
                displayNames,
                battleDamage,
                policy,
                cancellationToken,
                progressCallback,
                profile)
            .BuildOpeningResourceActions();
        List<(PlanAction Potion, PlanAction Power)> potionPowerPairs = [];
        foreach (PlanAction openingPotion in openingPotions)
        {
            IReadOnlyList<PlanAction> powers = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses)
                .BuildPowerActionsAfterPrefix([openingPotion]);
            foreach (PlanAction power in powers)
            {
                potionPowerPairs.Add((openingPotion, power));
                if (potionPowerPairs.Count == 4)
                    break;
            }
            if (potionPowerPairs.Count == 4)
                break;
        }
        if (openingPowers.Count == 0
            && potionPowerPairs.Count == 0
            && generatedResourcePotions.Count == 0
            && openingResources.Count == 0)
            return primary;

        List<SolverResult> searches = [primary];
        SolverResult selected = primary;
        foreach (PlanAction openingPower in openingPowers)
        {
            SolverResult posterior = new CombatBeamSolver(
                root,
                displayNames,
                battleDamage,
                policy,
                cancellationToken,
                progressCallback,
                profile,
                fixedPrefixActions: [openingPower]).Solve();
            if (posterior.ResultScope != SolverResultScope.SearchCompletion)
                return posterior;

            posterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(posterior);
            searches.Add(posterior);
            if (HasReachedAcceptableBattleHpLoss(policy, posterior))
            {
                MergeAuditTotals(posterior, searches.ToArray());
                return posterior;
            }

            bool posteriorWon = posterior.Snapshot.AllEnemiesDead
                && !posterior.Snapshot.PlayerDead
                && posterior.Snapshot.ProjectedPlayerHp > 0;
            int posteriorDeficit = StrategicHpDeficit(root, policy, posterior);
            if (IsBetterCompletedResult(root, policy, posterior, selected))
            {
                selected = posterior;
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] OPENING_POWER_POSTERIOR card={openingPower.CardId} " +
                $"won={posteriorWon} hp_deficit={posteriorDeficit} " +
                $"selected={ReferenceEquals(selected, posterior)}");

            PlanAction? offensiveFollowUp = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile)
                .BuildOpeningPowerOffensiveFollowUp(openingPower);
            if (offensiveFollowUp == null)
                continue;

            SolverResult linkedPosterior = new CombatBeamSolver(
                root,
                displayNames,
                battleDamage,
                policy,
                cancellationToken,
                progressCallback,
                profile,
                fixedPrefixActions: [openingPower, offensiveFollowUp]).Solve();
            if (linkedPosterior.ResultScope != SolverResultScope.SearchCompletion)
                return linkedPosterior;

            linkedPosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(linkedPosterior);
            searches.Add(linkedPosterior);
            if (HasReachedAcceptableBattleHpLoss(policy, linkedPosterior))
            {
                MergeAuditTotals(linkedPosterior, searches.ToArray());
                return linkedPosterior;
            }

            bool linkedWon = linkedPosterior.Snapshot.AllEnemiesDead
                && !linkedPosterior.Snapshot.PlayerDead
                && linkedPosterior.Snapshot.ProjectedPlayerHp > 0;
            int linkedDeficit = StrategicHpDeficit(root, policy, linkedPosterior);
            if (IsBetterCompletedResult(root, policy, linkedPosterior, selected))
            {
                selected = linkedPosterior;
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] OPENING_POWER_LINK_POSTERIOR " +
                $"cards={openingPower.CardId}+{offensiveFollowUp.CardId} " +
                $"won={linkedWon} hp_deficit={linkedDeficit} " +
                $"selected={ReferenceEquals(selected, linkedPosterior)}");
        }

        foreach (PlanAction openingResource in openingResources)
        {
            PlanAction? defensiveFollowUp = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile)
                .BuildOpeningDefensiveFollowUp([openingResource]);
            if (defensiveFollowUp == null)
                continue;

            SolverResult resourceDefensePosterior = new CombatBeamSolver(
                root,
                displayNames,
                battleDamage,
                policy,
                cancellationToken,
                progressCallback,
                profile,
                fixedPrefixActions: [openingResource, defensiveFollowUp]).Solve();
            if (resourceDefensePosterior.ResultScope != SolverResultScope.SearchCompletion)
                return resourceDefensePosterior;

            resourceDefensePosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(resourceDefensePosterior);
            searches.Add(resourceDefensePosterior);
            if (HasReachedAcceptableBattleHpLoss(policy, resourceDefensePosterior))
            {
                MergeAuditTotals(resourceDefensePosterior, searches.ToArray());
                return resourceDefensePosterior;
            }

            if (IsBetterCompletedResult(root, policy, resourceDefensePosterior, selected))
                selected = resourceDefensePosterior;
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] OPENING_RESOURCE_DEFENSE_POSTERIOR " +
                $"cards={openingResource.CardId}+{defensiveFollowUp.CardId} " +
                $"won={resourceDefensePosterior.Snapshot.AllEnemiesDead && !resourceDefensePosterior.Snapshot.PlayerDead} " +
                $"hp_deficit={StrategicHpDeficit(root, policy, resourceDefensePosterior)} " +
                $"selected={ReferenceEquals(selected, resourceDefensePosterior)}");
        }

        foreach (PlanAction openingPotion in generatedResourcePotions)
        {
            SolverResult? resourcePosterior = SolveOptionalPotionPosterior(
                new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: 1,
                    fixedPrefixActions: [openingPotion]),
                policy,
                $"POTION_RESOURCE_POSTERIOR potion={openingPotion.PotionId}");
            if (resourcePosterior == null)
                continue;
            if (resourcePosterior.ResultScope != SolverResultScope.SearchCompletion)
                return resourcePosterior;

            resourcePosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(resourcePosterior);
            searches.Add(resourcePosterior);
            if (HasReachedAcceptableBattleHpLoss(policy, resourcePosterior))
            {
                MergeAuditTotals(resourcePosterior, searches.ToArray());
                return resourcePosterior;
            }

            bool resourceWon = resourcePosterior.Snapshot.AllEnemiesDead
                && !resourcePosterior.Snapshot.PlayerDead
                && resourcePosterior.Snapshot.ProjectedPlayerHp > 0;
            int resourceDeficit = StrategicHpDeficit(root, policy, resourcePosterior);
            if (IsBetterCompletedResult(root, policy, resourcePosterior, selected))
            {
                selected = resourcePosterior;
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] POTION_RESOURCE_POSTERIOR " +
                $"potion={openingPotion.PotionId} card={openingPotion.Choice!.Cards[0].CardId} " +
                $"won={resourceWon} hp_deficit={resourceDeficit} " +
                $"selected={ReferenceEquals(selected, resourcePosterior)}");
        }

        if (HasReachedProvablePrimaryQualityLowerBound(root, policy, selected)
            && selected.PotionCount <= 1)
        {
            MergeAuditTotals(selected, searches.ToArray());
            return selected;
        }

        foreach ((PlanAction openingPotion, PlanAction postPotionPower) in potionPowerPairs)
        {
            PlanAction[] jointPrefix = [openingPotion, postPotionPower];
            SolverResult? jointPosterior = SolveOptionalPotionPosterior(
                new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses,
                    fixedPrefixActions: jointPrefix),
                policy,
                $"POTION_POWER_POSTERIOR potion={openingPotion.PotionId} power={postPotionPower.CardId}");
            if (jointPosterior == null)
                continue;
            if (jointPosterior.ResultScope != SolverResultScope.SearchCompletion)
                return jointPosterior;

            jointPosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(jointPosterior);
            searches.Add(jointPosterior);
            if (HasReachedAcceptableBattleHpLoss(policy, jointPosterior))
            {
                MergeAuditTotals(jointPosterior, searches.ToArray());
                return jointPosterior;
            }

            bool jointWon = jointPosterior.Snapshot.AllEnemiesDead
                && !jointPosterior.Snapshot.PlayerDead
                && jointPosterior.Snapshot.ProjectedPlayerHp > 0;
            int jointDeficit = StrategicHpDeficit(root, policy, jointPosterior);
            int comparisonDeficit = StrategicHpDeficit(root, policy, selected);
            if (IsBetterCompletedResult(root, policy, jointPosterior, selected))
            {
                selected = jointPosterior;
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] POTION_POWER_POSTERIOR " +
                $"potion={openingPotion.PotionId} power={postPotionPower.CardId} " +
                $"won={jointWon} hp_deficit={jointDeficit} " +
                $"selected={ReferenceEquals(selected, jointPosterior)}");

            if (!jointWon
                || HasReachedProvablePrimaryQualityLowerBound(root, policy, jointPosterior)
                || jointDeficit > comparisonDeficit + 1)
            {
                continue;
            }

            PlanAction? defensiveFollowUp = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses)
                .BuildOpeningDefensiveFollowUp(jointPrefix);
            if (defensiveFollowUp == null)
                continue;

            SolverResult? defensivePosterior = SolveOptionalPotionPosterior(
                new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: maximumSmartPotionUses,
                    fixedPrefixActions: [openingPotion, postPotionPower, defensiveFollowUp]),
                policy,
                $"POTION_POWER_DEFENSIVE_POSTERIOR potion={openingPotion.PotionId} " +
                $"power={postPotionPower.CardId} follow_up={defensiveFollowUp.CardId}");
            if (defensivePosterior == null)
                continue;
            if (defensivePosterior.ResultScope != SolverResultScope.SearchCompletion)
                return defensivePosterior;

            defensivePosterior.SingleSessionSearch = true;
            PopulateSingleSessionTotals(defensivePosterior);
            searches.Add(defensivePosterior);
            if (HasReachedAcceptableBattleHpLoss(policy, defensivePosterior))
            {
                MergeAuditTotals(defensivePosterior, searches.ToArray());
                return defensivePosterior;
            }

            bool defensiveWon = defensivePosterior.Snapshot.AllEnemiesDead
                && !defensivePosterior.Snapshot.PlayerDead
                && defensivePosterior.Snapshot.ProjectedPlayerHp > 0;
            int defensiveDeficit = StrategicHpDeficit(root, policy, defensivePosterior);
            if (IsBetterCompletedResult(root, policy, defensivePosterior, selected))
            {
                selected = defensivePosterior;
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] POTION_POWER_DEFENSIVE_POSTERIOR " +
                $"potion={openingPotion.PotionId} power={postPotionPower.CardId} " +
                $"follow_up={defensiveFollowUp.CardId} won={defensiveWon} " +
                $"hp_deficit={defensiveDeficit} selected={ReferenceEquals(selected, defensivePosterior)}");

            if (HasReachedProvablePrimaryQualityLowerBound(root, policy, defensivePosterior))
                break;
        }

        MergeAuditTotals(selected, searches.ToArray());
        return selected;
    }

    private static SolverResult AuditRequiredPotionUse(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback,
        SolverSearchProfile profile,
        SolverResult primary)
    {
        if (policy.PotionPolicy != SolverPotionPolicy.RequireAtLeastOne
            || battleDamage.PotionsUsedSoFar > 0
            || primary.PotionCount <= 1)
        {
            return primary;
        }

        policy.Diagnostics.Info(
            $"[CombatSolver/Test] REQUIRED_POTION_AUDIT start potion_count={primary.PotionCount} " +
            $"reported_saved={primary.PotionHpSaved} required={primary.PotionHpRequired}");
        SolverResult potionFree = new CombatBeamSolver(
            root,
            displayNames,
            battleDamage,
            policy,
            cancellationToken,
            progressCallback,
            profile,
            SolverPotionPolicy.Disabled).Solve();
        if (potionFree.ResultScope != SolverResultScope.SearchCompletion)
            return potionFree;

        potionFree.SingleSessionSearch = true;
        PopulateSingleSessionTotals(potionFree);

        bool potionFreeWon = IsCompleteVictory(potionFree);
        if (!potionFreeWon)
        {
            List<SolverResult> searches = [primary, potionFree];
            SolverResult selected = primary;
            IReadOnlyList<PlanAction> openingPotions = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: primary.PotionCount)
                .BuildPreferredOpeningPotionActions();
            foreach (PlanAction openingPotion in openingPotions)
            {
                SolverResult posterior = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    cancellationToken,
                    progressCallback,
                    profile,
                    SolverPotionPolicy.RequireAtLeastOne,
                    maximumPotionUses: primary.PotionCount,
                    fixedPrefixActions: [openingPotion]).Solve();
                if (posterior.ResultScope != SolverResultScope.SearchCompletion)
                    return posterior;

                posterior.SingleSessionSearch = true;
                PopulateSingleSessionTotals(posterior);
                searches.Add(posterior);
                if (HasReachedAcceptableBattleHpLoss(policy, posterior))
                {
                    MergeAuditTotals(posterior, searches.ToArray());
                    return posterior;
                }

                bool posteriorWon = posterior.Snapshot.AllEnemiesDead
                    && !posterior.Snapshot.PlayerDead
                    && posterior.Snapshot.ProjectedPlayerHp > 0;
                int posteriorDeficit = StrategicHpDeficit(root, policy, posterior);
                if (IsBetterCompletedResult(root, policy, posterior, selected))
                {
                    selected = posterior;
                }
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] REQUIRED_MULTI_POTION_POSTERIOR " +
                    $"potion={openingPotion.PotionId} target={openingPotion.TargetCombatId?.ToString() ?? "-"} " +
                    $"won={posteriorWon} hp_deficit={posteriorDeficit} " +
                    $"selected={ReferenceEquals(selected, posterior)}");

                if (primary.PotionCount != 2)
                    continue;

                IReadOnlyList<PlanAction> secondPotions = new CombatBeamSolver(
                        root,
                        displayNames,
                        battleDamage,
                        policy,
                        cancellationToken,
                        progressCallback,
                        profile,
                        SolverPotionPolicy.RequireAtLeastOne,
                        maximumPotionUses: primary.PotionCount)
                    .BuildPreferredPotionActionsAfterPrefix([openingPotion]);
                foreach (PlanAction secondPotion in secondPotions)
                {
                    SolverResult pairPosterior = new CombatBeamSolver(
                        root,
                        displayNames,
                        battleDamage,
                        policy,
                        cancellationToken,
                        progressCallback,
                        profile,
                        SolverPotionPolicy.RequireAtLeastOne,
                        maximumPotionUses: primary.PotionCount,
                        fixedPrefixActions: [openingPotion, secondPotion]).Solve();
                    if (pairPosterior.ResultScope != SolverResultScope.SearchCompletion)
                        return pairPosterior;

                    pairPosterior.SingleSessionSearch = true;
                    PopulateSingleSessionTotals(pairPosterior);
                    searches.Add(pairPosterior);
                    if (HasReachedAcceptableBattleHpLoss(policy, pairPosterior))
                    {
                        MergeAuditTotals(pairPosterior, searches.ToArray());
                        return pairPosterior;
                    }

                    bool pairWon = pairPosterior.Snapshot.AllEnemiesDead
                        && !pairPosterior.Snapshot.PlayerDead
                        && pairPosterior.Snapshot.ProjectedPlayerHp > 0;
                    int pairDeficit = StrategicHpDeficit(root, policy, pairPosterior);
                    if (IsBetterCompletedResult(root, policy, pairPosterior, selected))
                    {
                        selected = pairPosterior;
                    }
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] REQUIRED_POTION_PAIR_POSTERIOR " +
                        $"first={openingPotion.PotionId}:{openingPotion.TargetCombatId?.ToString() ?? "-"} " +
                        $"second={secondPotion.PotionId}:{secondPotion.TargetCombatId?.ToString() ?? "-"} " +
                        $"won={pairWon} hp_deficit={pairDeficit} " +
                        $"selected={ReferenceEquals(selected, pairPosterior)}");

                    int selectedDeficit = StrategicHpDeficit(root, policy, selected);
                    if (!pairWon || pairDeficit > selectedDeficit + 1)
                        continue;

                    PlanAction[] pairPrefix = [openingPotion, secondPotion];
                    PlanAction? defensiveFollowUp = new CombatBeamSolver(
                            root,
                            displayNames,
                            battleDamage,
                            policy,
                            cancellationToken,
                            progressCallback,
                            profile,
                            SolverPotionPolicy.RequireAtLeastOne,
                            maximumPotionUses: primary.PotionCount)
                        .BuildOpeningDefensiveFollowUp(pairPrefix);
                    if (defensiveFollowUp == null)
                        continue;

                    SolverResult defensivePosterior = new CombatBeamSolver(
                        root,
                        displayNames,
                        battleDamage,
                        policy,
                        cancellationToken,
                        progressCallback,
                        profile,
                        SolverPotionPolicy.RequireAtLeastOne,
                        maximumPotionUses: primary.PotionCount,
                        fixedPrefixActions: [openingPotion, secondPotion, defensiveFollowUp]).Solve();
                    if (defensivePosterior.ResultScope != SolverResultScope.SearchCompletion)
                        return defensivePosterior;

                    defensivePosterior.SingleSessionSearch = true;
                    PopulateSingleSessionTotals(defensivePosterior);
                    searches.Add(defensivePosterior);
                    if (HasReachedAcceptableBattleHpLoss(policy, defensivePosterior))
                    {
                        MergeAuditTotals(defensivePosterior, searches.ToArray());
                        return defensivePosterior;
                    }

                    bool defensiveWon = defensivePosterior.Snapshot.AllEnemiesDead
                        && !defensivePosterior.Snapshot.PlayerDead
                        && defensivePosterior.Snapshot.ProjectedPlayerHp > 0;
                    int defensiveDeficit = StrategicHpDeficit(root, policy, defensivePosterior);
                    if (IsBetterCompletedResult(root, policy, defensivePosterior, selected))
                    {
                        selected = defensivePosterior;
                    }
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] REQUIRED_POTION_PAIR_DEFENSIVE_POSTERIOR " +
                        $"first={openingPotion.PotionId}:{openingPotion.TargetCombatId?.ToString() ?? "-"} " +
                        $"second={secondPotion.PotionId}:{secondPotion.TargetCombatId?.ToString() ?? "-"} " +
                        $"follow_up={defensiveFollowUp.CardId} won={defensiveWon} " +
                        $"hp_deficit={defensiveDeficit} " +
                        $"selected={ReferenceEquals(selected, defensivePosterior)}");
                }
            }

            MergeAuditTotals(selected, searches.ToArray());
            policy.Diagnostics.Info(
                "[CombatSolver/Test] REQUIRED_POTION_AUDIT result potion_free_won=False " +
                $"selected={(ReferenceEquals(selected, primary) ? "multi_potion_rescue" : "opening_potion_posterior")}");
            return selected;
        }

        // The candidates this baseline is compared against are ranked on the strategic axis, so the
        // baseline has to be measured on it too; the raw sum here predated healing counting at all.
        PotionFreePolicyBaseline baseline = new(
            Won: true,
            HpDeficit: StrategicHpDeficit(root, policy, potionFree),
            PlayerHp: potionFree.Snapshot.PlayerHp,
            CombatEndedTurn: potionFree.CombatEndedTurn)
        {
            DeathSaveUseCount = potionFree.Snapshot.ProjectedDeathSaveUseCount,
        };
        SolverResult audited = new CombatBeamSolver(
            root,
            displayNames,
            battleDamage,
            policy,
            cancellationToken,
            progressCallback,
            profile,
            SolverPotionPolicy.RequireAtLeastOne,
            baseline,
            maximumPotionUses: 1).Solve();
        if (audited.ResultScope != SolverResultScope.SearchCompletion)
            return audited;

        audited.SingleSessionSearch = true;
        PopulateSingleSessionTotals(audited);
        SolverResult auditedSelection = IsBetterPotionPolicyResult(
            root,
            policy,
            audited,
            primary)
                ? audited
                : primary;
        MergeAuditTotals(auditedSelection, primary, potionFree, audited);
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] REQUIRED_POTION_AUDIT result potion_free_won=True " +
            $"baseline_hp_deficit={baseline.HpDeficit} " +
            $"selected={(ReferenceEquals(auditedSelection, audited) ? "single_potion_audit" : "primary")} " +
            $"selected_potion_count={auditedSelection.PotionCount} " +
            $"selected_saved={auditedSelection.PotionHpSaved} " +
            $"selected_required={auditedSelection.PotionHpRequired}");
        return auditedSelection;
    }

    private static SolverResult? SearchEarlySmartPotionScout(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback,
        SolverSearchProfile profile,
        SolverResult provisionalPotionFree,
        Action<SolverResult>? interimResultCallback)
    {
        if (policy.PotionPolicy != SolverPotionPolicy.Smart
            || policy.PotionStrategy.HasForcedDirectives
            || !policy.UseE3FixedPortfolioScheduling
            || provisionalPotionFree.ResultScope != SolverResultScope.SearchCompletion)
        {
            return null;
        }

        bool potionFreeWon = IsCompleteVictory(provisionalPotionFree);
        int potionFreeDeficit = StrategicHpDeficit(root, policy, provisionalPotionFree);
        if (MaximumSmartPotionUses(root, policy, potionFreeWon, potionFreeDeficit) == 0)
            return null;

        PotionFreePolicyBaseline baseline = new(
            potionFreeWon,
            potionFreeDeficit,
            provisionalPotionFree.Snapshot.PlayerHp,
            provisionalPotionFree.CombatEndedTurn)
        {
            DeathSaveUseCount = provisionalPotionFree.Snapshot.ProjectedDeathSaveUseCount,
        };
        SolverResult? scout = SolveOptionalPotionPosterior(
            new CombatBeamSolver(
                root,
                displayNames,
                battleDamage,
                policy,
                cancellationToken,
                progressCallback,
                profile,
                SolverPotionPolicy.RequireAtLeastOne,
                baseline,
                maximumPotionUses: 1,
                minimumPotionUses: 1,
                primaryIncumbent: BuildPrimarySearchIncumbent(
                    root,
                    policy,
                    provisionalPotionFree)),
            policy,
            "E3_CROSS_FAMILY_SCOUT");
        if (scout == null || scout.ResultScope != SolverResultScope.SearchCompletion)
            return scout;

        scout.SingleSessionSearch = true;
        PopulateSingleSessionTotals(scout);
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] E3_CROSS_FAMILY_SCOUT result " +
            $"boundary={scout.BoundaryReason} won={IsCompleteVictory(scout)} " +
            $"potions={scout.ExplicitPotionCount} expanded={scout.ExpandedNodes} " +
            $"transitions={scout.TransitionCount} elapsed_ms={scout.Elapsed.TotalMilliseconds:F1}");
        if (IsCompleteVictory(scout))
            interimResultCallback?.Invoke(scout);
        return scout;
    }

    private static bool TryReuseEarlySmartPotionScout(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult finalPotionFree,
        SolverResult? provisionalPotionFree,
        SolverResult? scout,
        out SolverResult? selected)
    {
        selected = null;
        if (policy.PotionPolicy != SolverPotionPolicy.Smart
            || policy.PotionStrategy.HasForcedDirectives
            || provisionalPotionFree == null
            || scout == null
            || scout.ResultScope != SolverResultScope.SearchCompletion
            || scout.BoundaryReason != SearchBoundaryReason.None
            || scout.ExplicitPotionCount != 1)
        {
            return false;
        }

        // The early member used the provisional no-potion incumbent. Reuse it only
        // when the later no-potion result is at least as strong; otherwise rerun the
        // ordinary Smart audit rather than assuming the early search stayed complete.
        if (CompareCompletedResultPrimaryQuality(
                root,
                policy,
                finalPotionFree,
                provisionalPotionFree) > 0)
        {
            policy.Diagnostics.Info(
                "[CombatSolver/Test] E3_CROSS_FAMILY_REUSE reused=false reason=baseline_regressed");
            return false;
        }

        bool potionFreeWon = IsCompleteVictory(finalPotionFree);
        bool candidateWon = IsCompleteVictory(scout);
        int potionFreeDeficit = StrategicHpDeficit(root, policy, finalPotionFree);
        int candidateDeficit = StrategicHpDeficit(root, policy, scout);
        int hpSaved = potionFreeWon
            ? Math.Max(0, potionFreeDeficit - candidateDeficit)
            : candidateWon
                ? Math.Max(0, scout.Snapshot.PlayerHp - finalPotionFree.Snapshot.PlayerHp)
                : 0;
        int hpRequired = SmartPotionHpRequired(root, policy, scout);
        bool protectsLoot = policy.TheftPolicy == SolverTheftPolicy.PreserveResources
            && scout.OutstandingStolenResource < finalPotionFree.OutstandingStolenResource;
        bool acceptable = IsSmartPotionGradientCandidateAcceptable(
            potionFreeWon,
            candidateWon,
            hpSaved,
            hpRequired,
            protectsLoot);
        bool improvesSelection = acceptable
            && (policy.TheftPolicy != SolverTheftPolicy.PreserveResources
                || IsBetterCompletedResult(root, policy, scout, finalPotionFree));
        if (!improvesSelection)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] E3_CROSS_FAMILY_REUSE reused=false reason=revalidation " +
                $"won={candidateWon} saved={hpSaved} required={hpRequired} protects_loot={protectsLoot}");
            return false;
        }

        scout.PotionHpSaved = hpSaved;
        scout.PotionHpRequired = hpRequired;
        selected = scout;
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] E3_CROSS_FAMILY_REUSE reused=true saved={hpSaved} " +
            $"required={hpRequired} turn={scout.CombatEndedTurn?.ToString() ?? "-"}");
        return true;
    }

    private static SolverResult AuditSmartPotionUse(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken searchCancellationToken,
        CancellationToken callerCancellationToken,
        Action<SolverProgress>? progressCallback,
        SolverSearchProfile profile,
        SolverResult primary,
        SmartLayerMemoryForecast memoryForecast,
        Action<SolverResult>? interimResultCallback)
    {
        if (policy.PotionPolicy != SolverPotionPolicy.Smart)
            return primary;
        try
        {
            return policy.UseE3AdaptivePortfolioScheduling
                ? SearchSmartPotionGradientScheduled(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    searchCancellationToken,
                    callerCancellationToken,
                    profile,
                    primary,
                    memoryForecast,
                    interimResultCallback,
                    adaptive: true)
                : policy.UseE3FixedPortfolioScheduling
                    ? SearchSmartPotionGradientScheduled(
                        root,
                        displayNames,
                        battleDamage,
                        policy,
                        searchCancellationToken,
                        callerCancellationToken,
                        profile,
                        primary,
                        memoryForecast,
                        interimResultCallback,
                        adaptive: false)
                    : SearchSmartPotionGradient(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    searchCancellationToken,
                    callerCancellationToken,
                    progressCallback,
                    profile,
                    primary,
                    memoryForecast,
                    interimResultCallback);
        }
        catch (PotionPolicyUnsatisfiedException)
            when (policy.PotionPolicy == SolverPotionPolicy.Smart
                && !policy.PotionStrategy.HasForcedDirectives)
        {
            policy.Diagnostics.Info(
                "[CombatSolver/Test] SMART_POTION_AUDIT result optional_route_missing=true selected=primary");
            return primary;
        }
    }

    private static SolverResult SearchSmartPotionGradientScheduled(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken searchCancellationToken,
        CancellationToken callerCancellationToken,
        SolverSearchProfile profile,
        SolverResult potionFree,
        SmartLayerMemoryForecast memoryForecast,
        Action<SolverResult>? interimResultCallback,
        bool adaptive)
    {
        int forcedPotionCount = policy.PotionStrategy.ForcedDirectiveCount;
        if (potionFree.ExplicitPotionCount != forcedPotionCount)
            throw new InvalidOperationException("Smart 梯度搜索必须从仅满足强制用药的结果开始。");

        string scheduler = adaptive ? "e3_adaptive" : "e3_fixed";
        bool potionFreeWon = potionFree.Snapshot.AllEnemiesDead
            && !potionFree.Snapshot.PlayerDead
            && potionFree.Snapshot.ProjectedPlayerHp > 0;
        int potionFreeDeficit = StrategicHpDeficit(root, policy, potionFree);
        int maximumOptionalPotionUses = MaximumSmartPotionUses(
            root,
            policy,
            potionFreeWon,
            potionFreeDeficit);
        if (maximumOptionalPotionUses == 0)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SMART_POTION_GRADIENT result " +
                $"stop=no_potion_acceptable hp_deficit={potionFreeDeficit} maximum=0 scheduler={scheduler}");
            return potionFree;
        }

        PotionFreePolicyBaseline baseline = new(
            potionFreeWon,
            potionFreeDeficit,
            potionFree.Snapshot.PlayerHp,
            potionFree.CombatEndedTurn)
        {
            DeathSaveUseCount = potionFree.Snapshot.ProjectedDeathSaveUseCount,
        };
        List<SolverResult> searches = [potionFree];
        SolverResult selected = potionFree;
        bool deadlineExpired = false;
        bool acceptablePotionLayerFound = false;
        int firstPotionCount = forcedPotionCount + 1;
        int lastPotionCount = forcedPotionCount + maximumOptionalPotionUses;
        int nextPotionCountToStart = firstPotionCount;
        int nextPotionCountToCommit = firstPotionCount;
        int residentLimit = policy.MemoryPressureSignal.ConservativeParallelismRequired
            ? 1
            : Math.Min(3, maximumOptionalPotionUses);
        PrimarySearchIncumbent? primaryIncumbent = BuildPrimarySearchIncumbent(
            root,
            policy,
            potionFree);

        Dictionary<int, CombatBeamSolver.SearchMemberExecutionSession> active = [];
        Dictionary<int, SolverResult> completed = [];
        HashSet<int> missing = [];
        Dictionary<int, int> slices = [];
        Dictionary<int, double> marginalRates = [];
        Dictionary<int, SolverInterimResult> observedIncumbents = [];
        Dictionary<int, bool> importantCandidates = [];
        Dictionary<int, E3AdaptivePriority> pendingPriorities = [];
        int epoch = 0;

        void StartLayer(int potionCount)
        {
            CombatBeamSolver solver = new(
                root,
                displayNames,
                battleDamage,
                policy,
                searchCancellationToken,
                progressCallback: null,
                profile,
                SolverPotionPolicy.RequireAtLeastOne,
                baseline,
                maximumPotionUses: potionCount,
                minimumPotionUses: potionCount,
                primaryIncumbent: primaryIncumbent);
            active.Add(potionCount, solver.CreateExecutionSession());
            slices[potionCount] = 0;
            marginalRates[potionCount] = 0d;
            importantCandidates[potionCount] = false;
            pendingPriorities[potionCount] = E3AdaptivePriority.None;
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] E3_PORTFOLIO_MEMBER_START kind=smart_potion " +
                $"potion_count={potionCount} resident={active.Count}/{residentLimit} " +
                $"slice_parents={E3FixedMemberAllowance.MaxParentCommits} " +
                $"slice_transitions={E3FixedMemberAllowance.MaxTransitions} scheduler={scheduler}");
        }

        void FillResidentSet()
        {
            while (active.Count < residentLimit && nextPotionCountToStart <= lastPotionCount)
            {
                if (searchCancellationToken.IsCancellationRequested)
                    return;
                StartLayer(nextPotionCountToStart++);
            }
        }

        void ObserveCompletedLayer(
            int potionCount,
            CombatBeamSolver.SearchMemberExecutionSession session,
            SolverResult? result)
        {
            ObserveSmartLayerMemorySample(
                policy,
                memoryForecast,
                result,
                profile,
                potionCount,
                session.AllocatedBytesForScheduling,
                session.TransitionCountForScheduling);
        }

        void ObserveAdaptiveSignal(
            int potionCount,
            CombatBeamSolver.SearchMemberExecutionSession session,
            double exclusiveMilliseconds)
        {
            if (!adaptive)
                return;

            observedIncumbents.TryGetValue(potionCount, out SolverInterimResult? previous);
            SolverInterimResult? current = session.CurrentBestResultForScheduling;
            E3AdaptiveObservation observation = E3AdaptiveBudgetPolicy.Observe(
                previous,
                current,
                marginalRates.GetValueOrDefault(potionCount),
                exclusiveMilliseconds);
            marginalRates[potionCount] = observation.MarginalImprovementRate;
            if (current != null)
                observedIncumbents[potionCount] = current;
            if (observation.ImportantImprovement)
                importantCandidates[potionCount] = true;
            if (observation.Priority > pendingPriorities.GetValueOrDefault(potionCount))
                pendingPriorities[potionCount] = observation.Priority;
        }

        void ObserveSchedulingBoundary()
        {
            if (policy.MemoryPressureSignal.ConservativeParallelismRequired && residentLimit > 1)
            {
                residentLimit = 1;
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] E3_PORTFOLIO_MEMORY " +
                    $"scheduler={scheduler} resident_limit=1 active={active.Count} " +
                    $"remaining_bytes={policy.MemoryPressureSignal.RemainingBytes}");
            }
        }

        SolverResult? StepLayer(int potionCount)
        {
            if (!active.TryGetValue(
                    potionCount,
                    out CombatBeamSolver.SearchMemberExecutionSession? session))
            {
                return null;
            }

            double exclusiveBefore = session.ExclusiveElapsedForScheduling.TotalMilliseconds;
            SearchStepResult step;
            try
            {
                step = session.Step(E3FixedMemberAllowance, searchCancellationToken);
            }
            catch (PotionPolicyUnsatisfiedException)
            {
                ObserveCompletedLayer(potionCount, session, result: null);
                missing.Add(potionCount);
                active.Remove(potionCount);
                session.Dispose();
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] SMART_POTION_GRADIENT layer={potionCount} " +
                    $"route_missing=true scheduler={scheduler}");
                return null;
            }

            double exclusiveDelta = Math.Max(
                0d,
                session.ExclusiveElapsedForScheduling.TotalMilliseconds - exclusiveBefore);
            slices[potionCount] = checked(slices.GetValueOrDefault(potionCount) + 1);
            ObserveAdaptiveSignal(potionCount, session, exclusiveDelta);
            ObserveSchedulingBoundary();
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] E3_PORTFOLIO_SLICE kind=smart_potion " +
                $"potion_count={potionCount} status={step.Status} " +
                $"committed_parents={step.CommittedParents} total_parents={step.TotalCommittedParents} " +
                $"expanded={session.ExpandedNodesForScheduling} " +
                $"transitions={session.TransitionCountForScheduling} " +
                $"exclusive_ms={session.ExclusiveElapsedForScheduling.TotalMilliseconds:F3} " +
                $"slice_exclusive_ms={exclusiveDelta:F3} " +
                $"marginal_rate={marginalRates.GetValueOrDefault(potionCount):F6} " +
                $"important={importantCandidates.GetValueOrDefault(potionCount).ToString().ToLowerInvariant()} " +
                $"priority={pendingPriorities.GetValueOrDefault(potionCount)} " +
                $"slices={slices[potionCount]} resident={active.Count}/{residentLimit} scheduler={scheduler}");

            if (step.Status == SearchStepStatus.Canceled)
            {
                callerCancellationToken.ThrowIfCancellationRequested();
                ObserveCompletedLayer(potionCount, session, result: null);
                deadlineExpired = true;
                return null;
            }
            if (step.Status != SearchStepStatus.Completed)
                return null;

            SolverResult candidate = session.Result
                ?? throw new InvalidOperationException("E3 用药成员完成但没有结果。");
            ObserveCompletedLayer(potionCount, session, candidate);
            active.Remove(potionCount);
            session.Dispose();
            if (candidate.ResultScope != SolverResultScope.SearchCompletion)
                return candidate;
            candidate.SingleSessionSearch = true;
            PopulateSingleSessionTotals(candidate);
            completed.Add(potionCount, candidate);
            return null;
        }

        SolverResult? CommitCompletedInOrder()
        {
            while (nextPotionCountToCommit <= lastPotionCount
                && (completed.ContainsKey(nextPotionCountToCommit)
                    || missing.Contains(nextPotionCountToCommit)))
            {
                int potionCount = nextPotionCountToCommit++;
                if (missing.Remove(potionCount))
                    continue;

                SolverResult candidate = completed[potionCount];
                completed.Remove(potionCount);
                searches.Add(candidate);
                interimResultCallback?.Invoke(candidate);

                bool candidateWon = IsCompleteVictory(candidate);
                int candidateDeficit = StrategicHpDeficit(root, policy, candidate);
                int hpSaved = potionFreeWon
                    ? Math.Max(0, potionFreeDeficit - candidateDeficit)
                    : candidateWon
                        ? Math.Max(0, candidate.Snapshot.PlayerHp - potionFree.Snapshot.PlayerHp)
                        : 0;
                int hpRequired = SmartPotionHpRequired(root, policy, candidate);
                bool protectsLoot = policy.TheftPolicy == SolverTheftPolicy.PreserveResources
                    && candidate.OutstandingStolenResource < potionFree.OutstandingStolenResource;
                bool acceptable = IsSmartPotionGradientCandidateAcceptable(
                    potionFreeWon,
                    candidateWon,
                    hpSaved,
                    hpRequired,
                    protectsLoot);
                bool improvesSelection = acceptable
                    && (policy.TheftPolicy != SolverTheftPolicy.PreserveResources
                        || IsBetterCompletedResult(root, policy, candidate, selected));
                if (improvesSelection)
                {
                    candidate.PotionHpSaved = hpSaved;
                    candidate.PotionHpRequired = hpRequired;
                    selected = candidate;
                    acceptablePotionLayerFound = true;
                }
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] SMART_POTION_GRADIENT layer={potionCount} " +
                    $"won={candidateWon} hp_deficit={candidateDeficit} saved={hpSaved} " +
                    $"required={hpRequired} protects_loot={protectsLoot} acceptable={acceptable} " +
                    $"selected={improvesSelection} expanded={candidate.ExpandedNodes} " +
                    $"transitions={candidate.TransitionCount} choice_branches={candidate.ChoiceBranchesEvaluated} " +
                    $"elapsed_ms={candidate.Elapsed.TotalMilliseconds:F1} allocated_bytes={candidate.WorkerAllocatedBytes} " +
                    $"incumbent_deficit={primaryIncumbent?.StrategicHpDeficit.ToString() ?? "-"} " +
                    $"incumbent_turn={primaryIncumbent?.CombatEndedTurn.ToString() ?? "-"} " +
                    $"incumbent_pruned={candidate.PrimaryIncumbentBranchesPruned} " +
                    $"incumbent_updates={candidate.PrimaryIncumbentUpdates} scheduler={scheduler}");

                if (acceptable
                    && TheftEncounterStrategy.RecoverySatisfied(
                        policy.TheftPolicy,
                        selected.OutstandingStolenResource))
                {
                    foreach (CombatBeamSolver.SearchMemberExecutionSession activeSession in active.Values)
                        activeSession.Dispose();
                    active.Clear();
                    MergeAuditTotals(selected, [.. searches]);
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] SMART_POTION_GRADIENT result " +
                        $"stop=threshold_met maximum={lastPotionCount} " +
                        $"selected_potions={selected.PotionCount} scheduler={scheduler}");
                    return selected;
                }
            }
            return null;
        }

        FillResidentSet();
        try
        {
            while (active.Count > 0)
            {
                epoch++;
                // Hard exploration floor: every resident member gets one transition-bounded slice.
                foreach (int potionCount in active.Keys.OrderBy(value => value).ToArray())
                {
                    if (searchCancellationToken.IsCancellationRequested)
                    {
                        callerCancellationToken.ThrowIfCancellationRequested();
                        deadlineExpired = true;
                        break;
                    }
                    if (StepLayer(potionCount) is { } takeover)
                        return takeover;
                    if (deadlineExpired)
                        break;
                }

                if (deadlineExpired)
                    break;
                if (CommitCompletedInOrder() is { } settled)
                    return settled;

                if (adaptive && active.Count > 0)
                {
                    E3AdaptiveMemberSignal[] signals = active.Keys
                        .OrderBy(value => value)
                        .Select((potionCount, index) => new E3AdaptiveMemberSignal(
                            potionCount,
                            slices.GetValueOrDefault(potionCount),
                            ExplorationDue: false,
                            ImportantCandidate: importantCandidates.GetValueOrDefault(potionCount),
                            Priority: pendingPriorities.GetValueOrDefault(potionCount),
                            MarginalImprovementRate: marginalRates.GetValueOrDefault(potionCount),
                            TieOrder: index))
                        .ToArray();
                    int? bonusPotionCount = E3AdaptiveBudgetPolicy.SelectBonusMember(signals);
                    if (bonusPotionCount is { } bonus && active.ContainsKey(bonus))
                    {
                        policy.PortfolioTelemetry?.RecordE3AdaptiveBonusSlice();
                        policy.Diagnostics.Info(
                            $"[CombatSolver/Test] E3_ADAPTIVE_BUDGET epoch={epoch} bonus_member={bonus} " +
                            $"priority={pendingPriorities.GetValueOrDefault(bonus)} " +
                            $"important={importantCandidates.GetValueOrDefault(bonus).ToString().ToLowerInvariant()} " +
                            $"marginal_rate={marginalRates.GetValueOrDefault(bonus):F6} " +
                            $"slices={slices.GetValueOrDefault(bonus)} active={active.Count}");
                        importantCandidates[bonus] = false;
                        pendingPriorities[bonus] = E3AdaptivePriority.None;
                        if (StepLayer(bonus) is { } takeover)
                            return takeover;
                        if (deadlineExpired)
                            break;
                        if (CommitCompletedInOrder() is { } settledAfterBonus)
                            return settledAfterBonus;
                    }
                }

                FillResidentSet();
            }
        }
        finally
        {
            foreach (CombatBeamSolver.SearchMemberExecutionSession session in active.Values)
                session.Dispose();
            active.Clear();
        }

        callerCancellationToken.ThrowIfCancellationRequested();
        MergeAuditTotals(selected, [.. searches]);
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] SMART_POTION_GRADIENT result " +
            $"stop={(deadlineExpired ? "deadline" : acceptablePotionLayerFound ? "threshold_met" : "complete")} " +
            $"maximum={lastPotionCount} selected_potions={selected.PotionCount} scheduler={scheduler}");
        return selected;
    }

    private static SolverResult SearchSmartPotionGradient(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken searchCancellationToken,
        CancellationToken callerCancellationToken,
        Action<SolverProgress>? progressCallback,
        SolverSearchProfile profile,
        SolverResult potionFree,
        SmartLayerMemoryForecast memoryForecast,
        Action<SolverResult>? interimResultCallback)
    {
        int forcedPotionCount = policy.PotionStrategy.ForcedDirectiveCount;
        if (potionFree.ExplicitPotionCount != forcedPotionCount)
            throw new InvalidOperationException("Smart 梯度搜索必须从仅满足强制用药的结果开始。");

        bool potionFreeWon = potionFree.Snapshot.AllEnemiesDead
            && !potionFree.Snapshot.PlayerDead
            && potionFree.Snapshot.ProjectedPlayerHp > 0;
        int potionFreeDeficit = StrategicHpDeficit(root, policy, potionFree);
        int maximumOptionalPotionUses = MaximumSmartPotionUses(
            root,
            policy,
            potionFreeWon,
            potionFreeDeficit);
        if (maximumOptionalPotionUses == 0)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SMART_POTION_GRADIENT result " +
                $"stop=no_potion_acceptable hp_deficit={potionFreeDeficit} maximum=0");
            return potionFree;
        }

        PotionFreePolicyBaseline baseline = new(
            potionFreeWon,
            potionFreeDeficit,
            potionFree.Snapshot.PlayerHp,
            potionFree.CombatEndedTurn)
        {
            DeathSaveUseCount = potionFree.Snapshot.ProjectedDeathSaveUseCount,
        };
        List<SolverResult> searches = [potionFree];
        SolverResult selected = potionFree;
        bool deadlineExpired = false;
        bool acceptablePotionLayerFound = false;
        for (int optionalPotionCount = 1;
             optionalPotionCount <= maximumOptionalPotionUses;
             optionalPotionCount++)
        {
            int potionCount = forcedPotionCount + optionalPotionCount;
            if (searchCancellationToken.IsCancellationRequested)
            {
                callerCancellationToken.ThrowIfCancellationRequested();
                deadlineExpired = true;
                break;
            }
            try
            {
                ReclaimAtPotionGradientBoundary(
                    policy,
                    searchCancellationToken,
                    progressCallback,
                    profile,
                    potionFree,
                    memoryForecast,
                    potionCount - 1,
                    potionCount);
            }
            catch (OperationCanceledException)
                when (searchCancellationToken.IsCancellationRequested
                    && !callerCancellationToken.IsCancellationRequested)
            {
                deadlineExpired = true;
                break;
            }
            PrimarySearchIncumbent? primaryIncumbent = BuildPrimarySearchIncumbent(
                root,
                policy,
                selected);
            long layerAllocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
            long layerTransitionsAtStart = policy.RequestWorkTotals?.Snapshot().TransitionCount ?? 0;
            SolverResult? observedLayerResult = null;
            SolverResult candidate;
            try
            {
                candidate = new CombatBeamSolver(
                    root,
                    displayNames,
                    battleDamage,
                    policy,
                    searchCancellationToken,
                    progressCallback,
                    profile,
                    SolverPotionPolicy.RequireAtLeastOne,
                    baseline,
                    maximumPotionUses: potionCount,
                    minimumPotionUses: potionCount,
                    primaryIncumbent: primaryIncumbent).Solve();
                observedLayerResult = candidate;
            }
            catch (PotionPolicyUnsatisfiedException)
            {
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] SMART_POTION_GRADIENT layer={potionCount} route_missing=true");
                continue;
            }
            catch (OperationCanceledException)
                when (searchCancellationToken.IsCancellationRequested
                    && !callerCancellationToken.IsCancellationRequested)
            {
                deadlineExpired = true;
                break;
            }
            finally
            {
                // Request totals include a solver that failed or was canceled. Use its actual
                // interval, never the selected route's work paired with another layer's bytes.
                ObserveSmartLayerMemory(
                    policy, memoryForecast, layerAllocatedAtStart, layerTransitionsAtStart,
                    observedLayerResult, profile, potionCount);
            }
            if (candidate.ResultScope != SolverResultScope.SearchCompletion)
                return candidate;

            candidate.SingleSessionSearch = true;
            PopulateSingleSessionTotals(candidate);
            searches.Add(candidate);
            interimResultCallback?.Invoke(candidate);

            bool candidateWon = IsCompleteVictory(candidate);
            int candidateDeficit = StrategicHpDeficit(root, policy, candidate);
            int hpSaved = potionFreeWon
                ? Math.Max(0, potionFreeDeficit - candidateDeficit)
                : candidateWon
                    ? Math.Max(0, candidate.Snapshot.PlayerHp - potionFree.Snapshot.PlayerHp)
                    : 0;
            int hpRequired = SmartPotionHpRequired(root, policy, candidate);
            bool protectsLoot = policy.TheftPolicy == SolverTheftPolicy.PreserveResources
                && candidate.OutstandingStolenResource < potionFree.OutstandingStolenResource;
            bool acceptable = IsSmartPotionGradientCandidateAcceptable(
                potionFreeWon,
                candidateWon,
                hpSaved,
                hpRequired,
                protectsLoot);
            bool improvesSelection = acceptable && (policy.TheftPolicy != SolverTheftPolicy.PreserveResources
                || IsBetterCompletedResult(root, policy, candidate, selected));
            if (improvesSelection)
            {
                candidate.PotionHpSaved = hpSaved;
                candidate.PotionHpRequired = hpRequired;
                selected = candidate;
                acceptablePotionLayerFound = true;
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SMART_POTION_GRADIENT layer={potionCount} " +
                $"won={candidateWon} hp_deficit={candidateDeficit} saved={hpSaved} " +
                $"required={hpRequired} protects_loot={protectsLoot} acceptable={acceptable} " +
                $"selected={improvesSelection} " +
                $"expanded={candidate.ExpandedNodes} transitions={candidate.TransitionCount} " +
                $"choice_branches={candidate.ChoiceBranchesEvaluated} " +
                $"elapsed_ms={candidate.Elapsed.TotalMilliseconds:F1} " +
                $"allocated_bytes={candidate.WorkerAllocatedBytes} " +
                $"incumbent_deficit={primaryIncumbent?.StrategicHpDeficit.ToString() ?? "-"} " +
                $"incumbent_turn={primaryIncumbent?.CombatEndedTurn.ToString() ?? "-"} " +
                $"incumbent_pruned={candidate.PrimaryIncumbentBranchesPruned} " +
                $"incumbent_updates={candidate.PrimaryIncumbentUpdates}");
            if (acceptable && TheftEncounterStrategy.RecoverySatisfied(policy.TheftPolicy, selected.OutstandingStolenResource))
                break;
        }

        callerCancellationToken.ThrowIfCancellationRequested();
        MergeAuditTotals(selected, [.. searches]);
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] SMART_POTION_GRADIENT result " +
            $"stop={(deadlineExpired ? "deadline" : acceptablePotionLayerFound ? "threshold_met" : "complete")} " +
            $"maximum={forcedPotionCount + maximumOptionalPotionUses} " +
            $"selected_potions={selected.PotionCount}");
        return selected;
    }

    internal static bool IsSmartPotionGradientCandidateAcceptable(
        bool potionFreeWon,
        bool candidateWon,
        int hpSaved,
        int hpRequired,
        bool protectsLoot)
        => candidateWon
            && (!potionFreeWon || hpSaved >= hpRequired || protectsLoot);

    private static void ObserveSmartLayerMemorySample(
        SearchPolicySnapshot policy,
        SmartLayerMemoryForecast forecast,
        SolverResult? result,
        SolverSearchProfile profile,
        int completedPotionCount,
        long processAllocated,
        long transitions)
    {
        if (policy.PotionPolicy != SolverPotionPolicy.Smart)
            return;
        processAllocated = Math.Max(0, processAllocated);
        transitions = Math.Max(0, transitions);
        bool usableSample = result is { ResultScope: SolverResultScope.SearchCompletion }
            && result.BoundaryReason != SearchBoundaryReason.TimeLimit
            && result.Elapsed.TotalMilliseconds < profile.SoftTimeBudgetMilliseconds;
        forecast.Observe(processAllocated, transitions, usableSample);
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] SMART_LAYER_MEMORY_SAMPLE layer={completedPotionCount} " +
            $"process_allocated_bytes={processAllocated} transitions={transitions} " +
            $"sample_usable={usableSample.ToString().ToLowerInvariant()} " +
            $"boundary={result?.BoundaryReason.ToString() ?? "incomplete"} " +
            $"bytes_per_transition_high_water={forecast.BytesPerTransitionHighWater:F1} " +
            $"prediction_error_high_water={forecast.UnderpredictionHighWater:F3}");
    }

    private static void ObserveSmartLayerMemory(
        SearchPolicySnapshot policy,
        SmartLayerMemoryForecast forecast,
        long processAllocatedAtStart,
        long transitionsAtStart,
        SolverResult? result,
        SolverSearchProfile profile,
        int completedPotionCount)
    {
        if (policy.PotionPolicy != SolverPotionPolicy.Smart)
            return;
        long processAllocated = Math.Max(
            0,
            GC.GetTotalAllocatedBytes(precise: false) - processAllocatedAtStart);
        long transitions = Math.Max(
            0,
            (policy.RequestWorkTotals?.Snapshot().TransitionCount ?? 0) - transitionsAtStart);
        // A fixed node budget is a comparable work window for the next layer using this same
        // profile. A timed-out or interrupted layer can understate that window, so keep the
        // optional reset conservative until a complete observation is available again.
        ObserveSmartLayerMemorySample(
            policy,
            forecast,
            result,
            profile,
            completedPotionCount,
            processAllocated,
            transitions);
    }

    private static void ReclaimAtPotionGradientBoundary(
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback,
        SolverSearchProfile profile,
        SolverResult totalsCarrier,
        SmartLayerMemoryForecast forecast,
        int completedPotionCount,
        int nextPotionCount)
    {
        SearchMemoryPressureSignal signal = policy.MemoryPressureSignal;
        cancellationToken.ThrowIfCancellationRequested();
        SmartLayerMemoryDecision decision = forecast.Decide(
            signal.IsEnabled,
            signal.HasUnexpectedNoGcLoss(),
            signal.AllocatedBytes,
            signal.RemainingBytes,
            signal.AllocationLimitBytes);
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] POTION_GRADIENT_MEMORY_DECISION " +
            $"completed_layer={completedPotionCount} next_layer={nextPotionCount} " +
            $"reclaim={decision.ShouldReclaim.ToString().ToLowerInvariant()} reason={decision.Reason} " +
            $"forecast_bytes={decision.ForecastBytes} remaining_bytes={decision.RemainingBytes} " +
            $"observations={forecast.ObservationCount} minimum_transition_growth=2 allocation_safety_factor=1.5");
        if (!decision.ShouldReclaim)
            return;

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        TimeSpan pauseBefore = GC.GetTotalPauseDuration();
        SearchGcLifecycleSnapshot lifecycleBefore = signal.CaptureGcLifecycle();
        Stopwatch stopwatch = Stopwatch.StartNew();
        progressCallback?.Invoke(new SolverProgress(
            totalsCarrier.StartTurnNumber,
            totalsCarrier.StartTurnNumber + Math.Max(0, totalsCarrier.SearchedTurns - 1),
            totalsCarrier.SearchedTurns,
            PlayDepth: 0,
            // A memory reset is a coordinator-owned interval between solvers. Publish a
            // zero-based interval so the request progress accumulator closes the preceding
            // solver exactly once and does not count potionFree again before every layer.
            ExpandedNodes: 0,
            ReviewedWorldlines: 0,
            MaxNodes: profile.MaxExpandedNodes,
            FrontierNodes: 0,
            EndedNodes: 1,
            ElapsedMilliseconds: 0,
            Phase: "切换用药路线，正在整理内存"));
        long pressureBefore = signal.AllocatedBytes;
        long limitBefore = signal.AllocationLimitBytes;
        try
        {
            signal.ReclaimAndContinue(cancellationToken, "smart_potion_layer");
        }
        finally
        {
            // ReclaimWithinSearch can observe a deadline after completing its blocking Gen2.
            // Retain that completed work in request totals even when cancellation then unwinds.
            stopwatch.Stop();
            TimeSpan gcPause = GC.GetTotalPauseDuration() - pauseBefore;
            TimeSpan maxObservedGcPause = signal.LastReclaimMaxObservedGcPause;
            long allocatedBytes = Math.Max(
                0,
                GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
            int gen0Collections = GC.CollectionCount(0) - gen0Before;
            int gen1Collections = GC.CollectionCount(1) - gen1Before;
            int gen2Collections = GC.CollectionCount(2) - gen2Before;
            totalsCarrier.TotalWorkerAllocatedBytes = checked(
                totalsCarrier.TotalWorkerAllocatedBytes
                + allocatedBytes);
            totalsCarrier.TotalGen0Collections += gen0Collections;
            totalsCarrier.TotalGen1Collections += gen1Collections;
            totalsCarrier.TotalGen2Collections += gen2Collections;
            totalsCarrier.TotalGcPauseDuration += gcPause;
            if (maxObservedGcPause > totalsCarrier.TotalMaxObservedGcPause)
                totalsCarrier.TotalMaxObservedGcPause = maxObservedGcPause;
            totalsCarrier.TotalSearchElapsed += stopwatch.Elapsed;
            policy.RequestWorkTotals?.RecordCoordinatorOverhead(
                stopwatch.Elapsed,
                allocatedBytes,
                gen0Collections,
                gen1Collections,
                gen2Collections,
                gcPause,
                maxObservedGcPause);

            policy.Diagnostics.Info(
                $"[CombatSolver/Test] POTION_GRADIENT_MEMORY_RESET " +
                $"completed_layer={completedPotionCount} next_layer={nextPotionCount} " +
                $"allocated_before={pressureBefore} limit_before={limitBefore} " +
                $"allocated_after={signal.AllocatedBytes} limit_after={signal.AllocationLimitBytes} " +
                $"gc_pause_ms={gcPause.TotalMilliseconds:F1} " +
                $"max_observed_gc_pause_ms={maxObservedGcPause.TotalMilliseconds:F1} " +
                signal.CaptureGcLifecycle().DeltaFrom(lifecycleBefore).ToDiagnosticString() + " " +
                $"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1} " +
                $"canceled={cancellationToken.IsCancellationRequested.ToString().ToLowerInvariant()}");
        }
    }

    private static SolverInterimResult BuildInterimResult(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
        => new(
            Won: IsCompleteVictory(result),
            OutstandingStolenResource: result.OutstandingStolenResource,
            ProjectedBattleHpLost: result.ProjectedBattleHpLost,
            StrategicHpDeficit: StrategicHpDeficit(root, policy, result),
            PotionStrategicCost: SmartPotionHpRequired(root, policy, result),
            ProjectedBattlePotionCount: result.ProjectedBattlePotionCount,
            CombatEndedTurn: result.CombatEndedTurn,
            EnemyHp: result.Snapshot.EnemyHp,
            Score: result.BestNode.Score)
        {
            GrowthHpCredit = result.Snapshot.StrategyGoalHpCredit,
            TheftPolicy = policy.TheftPolicy,
            GrowthRewardCount = result.Snapshot.StrategyGoalCount,
            Survives = !result.Snapshot.PlayerDead && result.Snapshot.ProjectedPlayerHp > 0,
            DeathSaveUseCount = result.Snapshot.ProjectedDeathSaveUseCount,
        };


    private static bool IsBetterCompletedResult(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult candidate,
        SolverResult current)
    {
        int primaryQuality = CompareCompletedResultPrimaryQuality(root, policy, candidate, current);
        if (primaryQuality != 0)
            return primaryQuality < 0;
        if (candidate.PotionCount != current.PotionCount)
            return candidate.PotionCount < current.PotionCount;
        if (PreferPlayableCurrentTurnRoute(root, policy, candidate, current))
            return true;
        if (PreferPlayableCurrentTurnRoute(root, policy, current, candidate))
            return false;
        return candidate.BestNode.Score > current.BestNode.Score;
    }

    private static bool IsBetterPotionPolicyResult(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult candidate,
        SolverResult current)
    {
        SolverInterimResult candidateInterim = BuildInterimResult(root, policy, candidate);
        SolverInterimResult currentInterim = BuildInterimResult(root, policy, current);
        int quality = ComparePotionPolicyQuality(
            policy.TheftPolicy,
            candidateInterim,
            currentInterim);
        if (quality != 0)
            return quality < 0;
        if (PreferPlayableCurrentTurnRoute(root, policy, candidate, current))
            return true;
        if (PreferPlayableCurrentTurnRoute(root, policy, current, candidate))
            return false;
        return candidate.BestNode.Score > current.BestNode.Score;
    }

    internal static bool IsBetterPotionPolicyResult(
        SolverTheftPolicy? theftPolicy,
        SolverInterimResult candidate,
        SolverInterimResult current)
    {
        int quality = ComparePotionPolicyQuality(theftPolicy, candidate, current);
        if (quality != 0)
            return quality < 0;
        return candidate.Score > current.Score;
    }

    private static int ComparePotionPolicyQuality(
        SolverTheftPolicy? theftPolicy,
        SolverInterimResult candidate,
        SolverInterimResult current)
    {
        int victoryComparison = current.Won.CompareTo(candidate.Won);
        if (victoryComparison != 0)
            return victoryComparison;
        int survivalComparison = current.Survives.CompareTo(candidate.Survives);
        if (survivalComparison != 0)
            return survivalComparison;
        if (candidate.DeathSaveUseCount != current.DeathSaveUseCount)
            return candidate.DeathSaveUseCount.CompareTo(current.DeathSaveUseCount);
        int recovery = TheftEncounterStrategy.CompareRecovery(theftPolicy,
            candidate.Won, candidate.OutstandingStolenResource, current.Won, current.OutstandingStolenResource);
        if (recovery != 0)
            return recovery;
        int primaryQuality = SolverInterimResultOrdering.ComparePrimaryQuality(
            candidate.Won,
            candidate.StrategicHpDeficit,
            candidate.CombatEndedTurn,
            current.Won,
            current.StrategicHpDeficit,
            current.CombatEndedTurn,
            candidate.GrowthHpCredit,
            current.GrowthHpCredit,
            candidate.GrowthRewardCount,
            current.GrowthRewardCount,
            candidate.DeathSaveUseCount,
            current.DeathSaveUseCount);
        if (primaryQuality != 0)
            return primaryQuality;
        if (theftPolicy == SolverTheftPolicy.PreserveResources
            && candidate.OutstandingStolenResource != current.OutstandingStolenResource)
        {
            return candidate.OutstandingStolenResource.CompareTo(current.OutstandingStolenResource);
        }
        if (candidate.ProjectedBattlePotionCount != current.ProjectedBattlePotionCount)
        {
            return candidate.ProjectedBattlePotionCount.CompareTo(current.ProjectedBattlePotionCount);
        }
        return 0;
    }

    private static bool PreferPlayableCurrentTurnRoute(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult candidate,
        SolverResult current)
        => MultiplayerLocalCrossTurnContracts.PreferCurrentTurnPlayableRoute(
            MultiplayerLocalCrossTurnContracts.HasActiveMultiplayerRouteSemantics(
                policy.RoutePolicy,
                root.PlayerCount),
            HasCurrentTurnCardAction(candidate),
            HasCurrentTurnCardAction(current));

    private static bool HasCurrentTurnCardAction(SolverResult result)
        => result.BestNode.Actions.Any(action =>
            action.Turn == result.StartTurnNumber
            && action.Kind == PlanActionKind.PlayCard);

    private static int CompareCompletedResultPrimaryQuality(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult candidate,
        SolverResult current)
    {
        bool candidateWon = IsCompleteVictory(candidate);
        bool currentWon = IsCompleteVictory(current);
        int victoryComparison = currentWon.CompareTo(candidateWon);
        if (victoryComparison != 0)
            return victoryComparison;
        bool candidateSurvives = !candidate.Snapshot.PlayerDead
            && candidate.Snapshot.ProjectedPlayerHp > 0;
        bool currentSurvives = !current.Snapshot.PlayerDead
            && current.Snapshot.ProjectedPlayerHp > 0;
        int survivalComparison = currentSurvives.CompareTo(candidateSurvives);
        if (survivalComparison != 0)
            return survivalComparison;
        int deathSaveComparison = candidate.Snapshot.ProjectedDeathSaveUseCount.CompareTo(
            current.Snapshot.ProjectedDeathSaveUseCount);
        if (deathSaveComparison != 0)
            return deathSaveComparison;
        int recovery = TheftEncounterStrategy.CompareRecovery(policy.TheftPolicy,
            candidateWon, candidate.OutstandingStolenResource,
            currentWon, current.OutstandingStolenResource);
        if (recovery != 0)
            return recovery;
        return SolverInterimResultOrdering.ComparePrimaryQuality(
            candidateWon,
            StrategicHpDeficit(root, policy, candidate),
            candidate.CombatEndedTurn,
            currentWon,
            StrategicHpDeficit(root, policy, current),
            current.CombatEndedTurn,
            candidate.Snapshot.StrategyGoalHpCredit,
            current.Snapshot.StrategyGoalHpCredit,
            candidate.Snapshot.StrategyGoalCount,
            current.Snapshot.StrategyGoalCount,
            candidate.Snapshot.ProjectedDeathSaveUseCount,
            current.Snapshot.ProjectedDeathSaveUseCount);
    }

    private static bool IsCompleteVictory(SolverResult result)
        => SolverInterimResultOrdering.IsCompleteVictory(
            result.BestNode.ActionCount,
            result.Snapshot.AllEnemiesDead,
            result.Snapshot.PlayerDead,
            result.Snapshot.ProjectedPlayerHp);

    internal static bool HasReachedAcceptableBattleHpLoss(
        SearchPolicySnapshot policy,
        SolverResult result)
        => policy.GrowthTargetSatisfied(result.Snapshot.GrowthRewards)
            && policy.RelicTargetsSatisfied(result.Snapshot.RelicCounters)
            && TheftEncounterStrategy.RecoverySatisfied(policy.TheftPolicy, result.OutstandingStolenResource)
            && result.Snapshot.ProjectedDeathSaveUseCount == 0
            && result.PotionCount == policy.MinimumRequiredPotionUses(result.BattlePotionsUsedSoFar)
            && policy.PotionStrategy.EvaluateForcedUses(result.BestNode.Actions, renewablePotionShapedRock: false).AllForcedUsesSatisfied
            && HasReachedAcceptableBattleHpLoss(
            IsCompleteVictory(result),
            result.ProjectedBattleHpLost,
            policy.AcceptableBattleHpLoss);

    internal static bool HasReachedAcceptableBattleHpLoss(
        bool completeVictory,
        int projectedBattleHpLost,
        int acceptableBattleHpLoss)
        => completeVictory && projectedBattleHpLost <= acceptableBattleHpLoss;

    private static bool HasReachedProvablePrimaryQualityLowerBound(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
        => !policy.EffectiveHasGrowthTargets
            && policy.RelicTargets.Count == 0
            && result.Snapshot.ProjectedDeathSaveUseCount == 0
            && TheftEncounterStrategy.RecoverySatisfied(policy.TheftPolicy, result.OutstandingStolenResource)
            && HasReachedProvablePrimaryQualityLowerBound(
            IsCompleteVictory(result),
            StrategicHpDeficit(root, policy, result),
            result.CombatEndedTurn,
            root.StartTurnNumber,
            ProvableStrategicHpFloor(root, policy));

    internal static bool HasReachedProvablePrimaryQualityLowerBound(
        bool completeVictory,
        int strategicHpDeficit,
        int? combatEndedTurn,
        int? earliestPossibleCombatEndedTurn,
        int provableStrategicHpFloor)
    {
        if (earliestPossibleCombatEndedTurn is not { } earliestTurn)
            return false;
        return SolverInterimResultOrdering.ComparePrimaryQuality(
            completeVictory,
            strategicHpDeficit,
            combatEndedTurn,
            currentCompleteVictory: true,
            currentStrategicHpDeficit: provableStrategicHpFloor,
            currentCombatEndedTurn: earliestTurn) <= 0;
    }

    private static PrimarySearchIncumbent? BuildPrimarySearchIncumbent(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
    {
        if (policy.EffectiveHasGrowthTargets
            || policy.RelicTargets.Count > 0
            || result.Snapshot.ProjectedDeathSaveUseCount > 0
            || !IsCompleteVictory(result)
            || result.CombatEndedTurn is not { } combatEndedTurn)
            return null;
        return new PrimarySearchIncumbent(
            StrategicHpDeficit(root, policy, result),
            combatEndedTurn);
    }

    private static SolverResult? SolveOptionalPotionPosterior(
        CombatBeamSolver solver,
        SearchPolicySnapshot policy,
        string diagnostic)
    {
        try
        {
            return solver.Solve();
        }
        catch (PotionPolicyUnsatisfiedException)
        {
            policy.Diagnostics.Info($"[CombatSolver/Test] {diagnostic} qualified=false");
            return null;
        }
    }

    private static int SmartPotionHpRequired(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
    {
        ForcedPotionUseEvaluation forced = policy.PotionStrategy.EvaluateForcedUses(
            result.BestNode.Actions,
            root.HasRenewablePotionShapedRock);
        int ambergrisCount = result.BestNode.Actions.Count(action =>
            action.Kind == PlanActionKind.UsePotion
            && string.Equals(action.PotionId, "AMBERGRIS", StringComparison.Ordinal))
            - forced.ForcedAmbergrisCount;
        int strategicHpCost = PotionUsePolicy.EffectiveStrategicHpCost(
            Math.Max(0, result.PotionStrategicCostByTurn.Values.Sum() - forced.ForcedStrategicHpCost),
            ambergrisCount,
            root.InitialPlayerMaxHp);
        return PotionUsePolicy.SmartRequiredHpSaved(
            strategicHpCost,
            StrategicBossHpRelief(root, policy));
    }

    private static int StrategicHpDeficit(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
        => ActEndingBossPolicy.StrategicHpDeficit(
            result.Snapshot.CumulativePlayerHpLost,
            Math.Max(0, root.InitialPlayerMaxHp - result.Snapshot.PlayerMaxHp),
            result.Snapshot.RecoveredPlayerHp
                + ActEndingBossPolicy.RankedPostCombatRelicHeal(
                    root.PostCombatRelicHeal,
                    SolverInterimResultOrdering.IsCompleteVictory(
                        result.BestNode.ActionCount,
                        result.Snapshot.AllEnemiesDead,
                        result.Snapshot.PlayerDead,
                        result.Snapshot.ProjectedPlayerHp),
                    result.Snapshot.PlayerHp,
                    result.Snapshot.PlayerMaxHp),
            StrategicBossHpRelief(root, policy),
            result.Snapshot.DeathSaveHpRestored) - result.Snapshot.StrategicHpCredit;

    /// <summary>
    /// Best strategic HP result any route could still reach from this root.
    /// </summary>
    /// <remarks>
    /// Once healing counts, zero is no longer the floor. Current HP is capped by max HP, so a route can at most
    /// heal back to full, which puts the floor at the HP the player was already missing when the fight started.
    /// Treating zero as the floor while a wounded player holds a heal would declare a route provably optimal
    /// when a strictly better one exists, and stop the extra searches that would have found it.
    /// </remarks>
    private static int ProvableStrategicHpFloor(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy)
        => -ActEndingBossPolicy.PersistentValueOfRecoveredHp(
            Math.Max(0, root.InitialPlayerMaxHp - root.InitialPlayerHp),
            StrategicBossHpRelief(root, policy));

    internal static bool CanAnySmartPotionQualify(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        bool potionFreeWon,
        int potionFreeHpDeficit)
        => MaximumSmartPotionUses(root, policy, potionFreeWon, potionFreeHpDeficit) > 0;

    internal static int MaximumSmartPotionUses(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        bool potionFreeWon,
        int potionFreeHpDeficit)
    {
        SearchablePotionSlotSnapshot[] allowedPotions = root.SearchablePotions
            .Where(potion => policy.PotionStrategy.AllowsExplicitUse(
                potion.Slot,
                potion.PotionId,
                SolverPotionPolicy.Smart,
                forceAllDisabled: false)
                && policy.PotionStrategy.Resolve(potion.Slot, potion.PotionId)
                    != SolverPotionDirective.Force)
            .ToArray();
        if (!potionFreeWon || policy.TheftPolicy == SolverTheftPolicy.PreserveResources)
            return allowedPotions.Length;
        int paidPotionHpRequired = PotionUsePolicy.SmartRequiredHpSaved(
            SolverWeights.PotionMinimumHpSaved,
            StrategicBossHpRelief(root, policy));
        int paidPotionCapacity = paidPotionHpRequired >= int.MaxValue / 4
            ? 0
            : Math.Max(0, potionFreeHpDeficit) / paidPotionHpRequired;
        return Math.Min(
            allowedPotions.Length,
            allowedPotions.Count(potion => potion.StrategicHpCost == 0) + paidPotionCapacity);
    }

    private static BossHpRelief StrategicBossHpRelief(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy)
        => ActEndingBossPolicy.ResolveStrategicHpRelief(
            root.BossHpRelief,
            policy.ActTransitionBossHpStrategy,
            policy.FinalBossHpStrategy);

    private static void MergeAuditTotals(
        SolverResult selected,
        params SolverResult[] searches)
    {
        if (searches.Length == 0)
            throw new ArgumentException("审计总量至少需要一个搜索结果。", nameof(searches));

        SearchRequestWorkSnapshot totals = AggregateAuditWork(
            searches.Select(AuditWorkContribution).ToArray());
        PopulateRequestWorkTotals(selected, totals);
        // This result spans an audit even when a future caller supplies one layer.
        // Preserve the historical coordinator-session classification.
        selected.SingleSessionSearch = false;
    }

    private static SearchSolverWorkContribution AuditWorkContribution(SolverResult result)
        => new(
            result.ExpandedNodes,
            result.TransitionCount,
            result.ChoiceBranchesEvaluated,
            result.TotalSearchElapsed,
            result.TotalWorkerAllocatedBytes,
            result.TotalGen0Collections,
            result.TotalGen1Collections,
            result.TotalGen2Collections,
            result.TotalGcPauseDuration,
            result.TotalMaxObservedGcPause);

    internal static SearchRequestWorkSnapshot AggregateAuditWork(
        params SearchSolverWorkContribution[] searches)
    {
        SearchRequestWorkTotals totals = new();
        foreach (SearchSolverWorkContribution search in searches)
            totals.Record(search);
        return totals.Snapshot();
    }

    private static void PopulateRequestWorkTotals(
        SolverResult result,
        SearchRequestWorkTotals requestWorkTotals)
        => PopulateRequestWorkTotals(result, requestWorkTotals.Snapshot());

    private static void PopulateRequestWorkTotals(
        SolverResult result,
        SearchRequestWorkSnapshot totals)
    {
        result.SingleSessionSearch = totals.RecordedSolverCount == 1;
        result.TotalSearchElapsed = totals.Elapsed;
        result.TotalWorkerAllocatedBytes = totals.WorkerAllocatedBytes;
        result.TotalGen0Collections = SaturatingInt(totals.Gen0Collections);
        result.TotalGen1Collections = SaturatingInt(totals.Gen1Collections);
        result.TotalGen2Collections = SaturatingInt(totals.Gen2Collections);
        result.TotalGcPauseDuration = totals.GcPauseDuration;
        result.TotalMaxObservedGcPause = totals.MaxObservedGcPause;
        result.TotalExpandedNodes = totals.ExpandedNodes;
        result.TotalTransitionCount = totals.TransitionCount;
        result.TotalChoiceBranchesEvaluated = totals.ChoiceBranchesEvaluated;
    }

    private static int SaturatingInt(long value)
        => value >= int.MaxValue ? int.MaxValue : (int)value;

    private static void PopulateSingleSessionTotals(
        SolverResult result)
    {
        result.TotalSearchElapsed = result.Elapsed;
        result.TotalWorkerAllocatedBytes = result.WorkerAllocatedBytes;
        result.TotalGen0Collections = result.Gen0Collections;
        result.TotalGen1Collections = result.Gen1Collections;
        result.TotalGen2Collections = result.Gen2Collections;
        result.TotalGcPauseDuration = result.GcPauseDuration;
        result.TotalMaxObservedGcPause = result.MaxObservedGcPause;
        result.TotalExpandedNodes = result.ExpandedNodes;
        result.TotalTransitionCount = result.TransitionCount;
        result.TotalChoiceBranchesEvaluated = result.ChoiceBranchesEvaluated;
    }
}
