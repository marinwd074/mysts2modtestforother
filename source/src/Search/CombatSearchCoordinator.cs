using System.Diagnostics;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    private static readonly SearchWorkAllowance ResumableMemberAllowance = new(256);
    private static readonly SearchWorkAllowance E3FixedMemberAllowance = new(
        maxParentCommits: 256,
        maxTransitions: 1_024);
    private const int E4SupplementalMinimumGraceMilliseconds = 20_000;
    private const int E4SupplementalFreshImprovementWindowMilliseconds = 30_000;
    private const int E4SmartPotionDegradedBudgetMilliseconds = 10_000;
    private readonly record struct E4SupplementalBudgetContext(
        long RequestElapsedMilliseconds,
        long LastImprovementMilliseconds,
        bool HasApprovedForeground);

    internal static int ComputeE4ActiveClockDeadlineMilliseconds(
        int configuredActiveBudgetMilliseconds,
        long activeClockElapsedMilliseconds,
        long requestStartMilliseconds,
        int requestDeadlineMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(configuredActiveBudgetMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(activeClockElapsedMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(requestStartMilliseconds);
        long remainingRequestWindow = Math.Max(
            1L,
            requestDeadlineMilliseconds - requestStartMilliseconds);
        long activeDeadline = activeClockElapsedMilliseconds + remainingRequestWindow;
        return (int)Math.Clamp(
            activeDeadline,
            1L,
            configuredActiveBudgetMilliseconds);
    }

    internal static int ComputeE4SupplementalDeadlineMilliseconds(
        SearchRoutePolicy routePolicy,
        int configuredBudgetMilliseconds,
        long supplementalStartMilliseconds,
        long lastImprovementMilliseconds,
        bool hasApprovedForeground,
        bool hasForcedPotionDirectives,
        bool isPrimaryPass)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(configuredBudgetMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(supplementalStartMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(lastImprovementMilliseconds);
        if (routePolicy != SearchRoutePolicy.MultiplayerSinglePlayerCore
            || !hasApprovedForeground
            || hasForcedPotionDirectives
            || !isPrimaryPass
            || supplementalStartMilliseconds >= configuredBudgetMilliseconds)
        {
            return configuredBudgetMilliseconds;
        }

        long boundedImprovement = Math.Min(
            lastImprovementMilliseconds,
            supplementalStartMilliseconds);
        long minimumDeadline = supplementalStartMilliseconds
            + E4SupplementalMinimumGraceMilliseconds;
        long freshnessDeadline = boundedImprovement
            + E4SupplementalFreshImprovementWindowMilliseconds;
        long deadline = Math.Max(minimumDeadline, freshnessDeadline);
        return (int)Math.Min(configuredBudgetMilliseconds, deadline);
    }

    internal static int ComputeE4SmartPotionStageDeadlineMilliseconds(
        int supplementalBudgetMilliseconds,
        long stageStartMilliseconds,
        bool e4Active)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(supplementalBudgetMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(stageStartMilliseconds);
        if (!e4Active || stageStartMilliseconds >= supplementalBudgetMilliseconds)
            return supplementalBudgetMilliseconds;

        return (int)Math.Min(
            supplementalBudgetMilliseconds,
            stageStartMilliseconds + E4SmartPotionDegradedBudgetMilliseconds);
    }

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
        bool localRecoveryPolicy = policy.RoutePolicy == SearchRoutePolicy.MultiplayerSinglePlayerCore
            && !policy.CurrentTurnOnly && !policy.IncludeTurnSetup
            && !policy.UseMultiplayerTeamObjective && !policy.UseMultiplayerTeammateForecast
            && !policy.UseMultiplayerScenarioReevaluation;
        if (policy.MultiplayerPlanRefresh != null && !localRecoveryPolicy)
        {
            // Forecast/scenario branches have separate work accounting. This path
            // is for the production local-core policy, not an experimental budget bypass.
            policy.Diagnostics.Info("[CombatSolver/MultiplayerPlanRefresh] MP_PLAN_REFRESH_BYPASS reason=unsupported_policy");
            policy = policy with { MultiplayerPlanRefresh = null };
        }
        R1TransitionHydrationCache? cache = policy.UseRequestTransitionHydration
            && policy.RoutePolicy == SearchRoutePolicy.MultiplayerSinglePlayerCore
            && !policy.VerifyIncrementalSearch && !policy.IncludeTurnSetup && !policy.CurrentTurnOnly
            && !policy.UseMultiplayerTeamObjective && !policy.UseMultiplayerTeammateForecast
            && !policy.UseMultiplayerScenarioReevaluation
                ? new(learnFromSearch: true, measurePerformance: policy.MeasurePhasePerformance) : null;
        SearchRequestWorkTotals totals = new(measurePhases: policy.MeasurePhasePerformance);
        SearchOptimizationSession? optimizations = policy.UseUpstreamSearchOptimizations ? new(root) : null;
        if (localRecoveryPolicy && (policy.MultiplayerPlanRefresh != null
            || policy.ContinuationSeedActions.Count > 0
            || policy.LocalCoreTurnLayers != SearchEvaluationPolicy.RollingHorizonTurnLayers
            || policy.ContinuationRouteReplayActions.Count > 0 && !policy.VerifyIncrementalSearch))
        {
            SolverSearchProfile profile = policy.Profile with
            {
                SoftTimeBudgetMilliseconds = policy.BudgetOverrideMilliseconds
                    ?? policy.Profile.SoftTimeBudgetMilliseconds,
            };
            policy = policy with { Profile = profile, BudgetOverrideMilliseconds = null,
                RecoveryBudget = new(profile.SoftTimeBudgetMilliseconds, profile.MaxExpandedNodes) };
        }
        using CancellationTokenRegistration recoveryCancellation = policy.RecoveryBudget is { } recoveryBudget
            ? cancellationToken.Register(recoveryBudget.ObserveCancellation) : default;
        string recoveryOutcome = "Faulted";
        try
        {
            SolverResult result = SolveRequest(root, displayNames, battleDamage,
                policy with { RequestTransitionHydrationCache = cache, RequestWorkTotals = totals,
                    OptimizationSession = optimizations }, cancellationToken, progressCallback,
                out MultiplayerPlanRefreshReport? refreshReport);
            if (result.ResultScope == SolverResultScope.SearchCompletion
                && optimizations?.PotionFreeWitness is { } witness
                && IsBetterPotionPolicyResult(root, policy, witness, result))
            {
                result = witness;
                PopulateRequestWorkTotals(result, totals.Snapshot());
            }
            result.OptimizationMetrics = optimizations?.Capture();
            result.PlanRefresh = refreshReport;
            result.RequestPhaseMetrics = totals.CapturePhases();
            if (cache != null)
                result.RequestTransitionHydration = cache.Capture();
            recoveryOutcome = "Completed";
            result.RecoveryBudgetMetrics = policy.RecoveryBudget?.Capture(recoveryOutcome);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            recoveryOutcome = "Canceled";
            throw;
        }
        catch (SearchRecoveryBudgetExhaustedException)
        {
            recoveryOutcome = "BudgetExhausted";
            throw;
        }
        finally
        {
            try
            {
                if (policy.RecoveryBudget is { } recovery)
                    policy.Diagnostics.Info("[CombatSolver/Test] SEARCH_RECOVERY_BUDGET "
                        + System.Text.Json.JsonSerializer.Serialize(recovery.Capture(recoveryOutcome)));
                if (totals.CapturePhases() is { } phases)
                    policy.Diagnostics.Info("[CombatSolver/Test] SEARCH_REQUEST_PHASE "
                        + System.Text.Json.JsonSerializer.Serialize(phases));
                if (cache != null)
                {
                    R1TransitionHydrationSnapshot stats = cache.Capture();
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] REQUEST_TRANSITION_HYDRATION entries={stats.Entries}/{stats.EntryLimit} " +
                        $"stores={stats.Stores} first_validations={stats.FirstValidations} " +
                        $"validated_keys={stats.ValidatedKeys} hydration_hits={stats.HydrationHits} " +
                        $"output_mismatches={stats.OutputMismatches} rejected_keys={stats.RejectedKeys}");
                }
                if (optimizations != null)
                    policy.Diagnostics.Info("[CombatSolver/Test] SEARCH_OPTIMIZATIONS "
                        + System.Text.Json.JsonSerializer.Serialize(optimizations.Capture()));
            }
            finally
            {
                try { cache?.Release(); }
                finally { policy.RequestWorkObserver?.Invoke(totals.Snapshot()); }
            }
        }
    }

    private static SolverResult SolveRequest(
        CombatRootSnapshot root, SolverDisplayNames displayNames, BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy, CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback, out MultiplayerPlanRefreshReport? refreshReport)
    {
        refreshReport = null;
        SearchRequestWorkTotals requestWorkTotals = policy.RequestWorkTotals
            ?? new(measurePhases: policy.MeasurePhasePerformance);
        BeamWidthPortfolioTelemetry portfolioTelemetry = new();
        policy = policy with
        {
            RequestWorkTotals = requestWorkTotals,
            PortfolioTelemetry = portfolioTelemetry,
        };
        if (policy.MultiplayerPlanRefresh != null)
        {
            var recovery = TryRefreshPlan(root, displayNames, battleDamage, policy, cancellationToken);
            refreshReport = recovery.Report;
            policy = recovery.Policy;
            if (recovery.Result is { } restored)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PopulateRequestWorkTotals(restored, requestWorkTotals);
                return restored;
            }
        }
        else if (TryReplayContinuationRoute(root, displayNames, battleDamage, policy,
                cancellationToken) is { } replayedContinuation)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PopulateRequestWorkTotals(replayedContinuation, requestWorkTotals);
            return replayedContinuation;
        }
        if (policy.RecoveryBudget is { } replayBudget
            && policy.ContinuationRouteReplayActions.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SolverSearchProfile? remaining = replayBudget.RemainingProfile(policy.Profile);
            policy.Diagnostics.Info($"[CombatSolver/Test] MP_LOCAL_XTURN_REPLAY_BUDGET " +
                $"elapsed_ms={replayBudget.ElapsedMilliseconds} expanded={replayBudget.ExpandedNodes} " +
                $"remaining_ms={remaining?.SoftTimeBudgetMilliseconds ?? 0} remaining_nodes={remaining?.MaxExpandedNodes ?? 0}");
            if (remaining == null) throw new SearchRecoveryBudgetExhaustedException();
            policy = policy with { Profile = remaining };
        }
        policy = policy with
        {
            ContinuationRouteReplayActions = [],
            ContinuationRouteReplayBaseline = null,
        };
        SearchInteractionState? interaction = policy.Interaction;
        SolverResult? currentCompleteAdoptableResult = null;
        SolverInterimResult? currentDisplayedResult = null;
        SolverProgress? lastProgress = null;
        int currentTurnPreviewVersion = 0;
        int speculativeRouteVersion = 0;
        SolverCurrentTurnPreview? currentTurnPreview = null;
        SolverSpeculativeRoutePreview? speculativeRoutePreview = null;
        SolverRouteAdoptionSeed? currentTurnAdoptionSeed = null;
        SolverRouteAdoptionSeed? currentRouteAdoptionSeed = null;
        SolverResult? currentMaterializedForegroundResult = null;
        long? firstForegroundPublishedTicks = null;
        long? lastForegroundImprovementTicks = null;
        int foregroundInitialCount = 0;
        int foregroundReplaceCount = 0;
        int foregroundKeepCount = 0;
        int foregroundRefreshCount = 0;

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
            SolverInterimResult summary = BuildInterimResult(root, policy, result);
            if (currentDisplayedResult == summary
                && RouteMatchesPreview(result.BestNode.Actions, speculativeRoutePreview))
            {
                currentMaterializedForegroundResult = result;
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] SEARCH_E3_FOREGROUND_BACKING_CAPTURED " +
                    $"hp_loss={summary.ProjectedBattleHpLost} enemy_hp={summary.EnemyHp} " +
                    $"won={summary.Won.ToString().ToLowerInvariant()}");
            }

            if (result.OnlyDeathRoutesFound
                || !SolverInterimResultOrdering.IsCompleteVictory(
                    result.BestNode.ActionCount,
                    result.Snapshot.AllEnemiesDead,
                    result.Snapshot.PlayerDead,
                    result.Snapshot.ProjectedPlayerHp))
            {
                return;
            }

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
            currentTurnAdoptionSeed = new SolverRouteAdoptionSeed(
                currentTurnPreview.CandidateVersion,
                currentTurnPreview.Actions,
                () =>
                {
                    result.ResultScope = SolverResultScope.CurrentTurnAdoption;
                    return result;
                });
            currentRouteAdoptionSeed = new SolverRouteAdoptionSeed(
                speculativeRoutePreview.CandidateVersion,
                result.BestNode.Actions,
                () => result);
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
                    CurrentTurnAdoptionSeed = currentTurnAdoptionSeed,
                };
                progressCallback(lastProgress);
            }
        }

        Action<SolverProgress>? enrichedProgressCallback = progressCallback == null
            ? null
            : progress =>
            {
                lastProgress = progress;
                // Keep the visible current-turn line and speculative future attached to the
                // same globally promoted Beam result. A shallow early boundary such as Strike
                // must not independently lock the preview after a better multi-turn route appears.
                SolverInterimResult? previousDisplayedResult = currentDisplayedResult;
                SolverSpeculativeRoutePreview? previousRoutePreview = speculativeRoutePreview;
                SolverInterimResult? evaluatedCandidate = progress.CurrentBestResult;
                bool acceptsRouteUpdate = currentDisplayedResult == null;
                if (evaluatedCandidate is { } candidate)
                {
                    acceptsRouteUpdate = TryPromoteDisplayedResult(candidate);
                }
                else if (currentDisplayedResult != null)
                {
                    acceptsRouteUpdate = false;
                }

                if (progress.OfficialPublishedOrigin is { } e2Origin
                    && !string.IsNullOrWhiteSpace(progress.OfficialPublishedEvaluationContextId)
                    && evaluatedCandidate is { } e2Candidate)
                {
                    long decisionTicks = Stopwatch.GetTimestamp();
                    string decision = previousDisplayedResult == null
                        ? "initial"
                        : e2Candidate == previousDisplayedResult
                            ? "refresh"
                            : acceptsRouteUpdate ? "replace" : "keep";
                    switch (decision)
                    {
                        case "initial":
                            foregroundInitialCount++;
                            break;
                        case "replace":
                            foregroundReplaceCount++;
                            lastForegroundImprovementTicks = decisionTicks;
                            break;
                        case "keep":
                            foregroundKeepCount++;
                            break;
                        case "refresh":
                            foregroundRefreshCount++;
                            break;
                    }
                    if (acceptsRouteUpdate)
                        firstForegroundPublishedTicks ??= decisionTicks;

                    SearchEfficiencyMemberReport? e3Member =
                        portfolioTelemetry.FindSearchMember(e2Origin.SearchMemberId);
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] SEARCH_E2_FOREGROUND_DECISION " +
                        $"decision={decision} candidate_id={e2Origin.CandidateId} " +
                        $"member_id={e2Origin.SearchMemberId} " +
                        $"member_kind={e3Member?.Kind ?? "unknown"} " +
                        $"decision_elapsed_ms={portfolioTelemetry.ToRequestMilliseconds(decisionTicks).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                        $"previous_version={previousRoutePreview?.CandidateVersion.ToString() ?? "-"} " +
                        $"candidate_version={progress.SpeculativeRoutePreview?.CandidateVersion.ToString() ?? "-"} " +
                        $"previous_hp_loss={previousDisplayedResult?.ProjectedBattleHpLost.ToString() ?? "-"} " +
                        $"candidate_hp_loss={e2Candidate.ProjectedBattleHpLost} " +
                        $"previous_enemy_hp={previousDisplayedResult?.EnemyHp.ToString() ?? "-"} " +
                        $"candidate_enemy_hp={e2Candidate.EnemyHp}");
                }

                if (acceptsRouteUpdate)
                {
                    currentMaterializedForegroundResult = null;
                    // Current-turn and future-route previews are one displayed candidate bundle.
                    // Replace or clear them together so the UI can never combine a newer
                    // current-turn line with an older speculative future.
                    currentTurnPreview = progress.CurrentTurnPreview;
                    currentTurnAdoptionSeed = progress.CurrentTurnAdoptionSeed;
                    speculativeRoutePreview = progress.SpeculativeRoutePreview;
                    currentRouteAdoptionSeed = progress.RouteAdoptionSeed;
                    if (currentTurnPreview != null)
                    {
                        currentTurnPreviewVersion = Math.Max(
                            currentTurnPreviewVersion,
                            currentTurnPreview.CandidateVersion);
                    }
                    if (speculativeRoutePreview != null)
                    {
                        speculativeRouteVersion = Math.Max(
                            speculativeRouteVersion,
                            speculativeRoutePreview.CandidateVersion);
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

                CandidateOrigin? approvedForegroundOrigin =
                    acceptsRouteUpdate ? progress.OfficialPublishedOrigin : null;
                string? approvedForegroundContextId =
                    acceptsRouteUpdate ? progress.OfficialPublishedEvaluationContextId : null;
                progressCallback(progress with
                {
                    CurrentBestResult = currentDisplayedResult,
                    CurrentTurnPreview = currentTurnPreview,
                    SpeculativeRoutePreview = speculativeRoutePreview,
                    RouteAdoptionSeed = currentRouteAdoptionSeed,
                    CurrentTurnAdoptionSeed = currentTurnAdoptionSeed,
                    OfficialPublishedOrigin = approvedForegroundOrigin,
                    OfficialPublishedEvaluationContextId = approvedForegroundContextId,
                });
            };

        void LogFinalVsForeground(SolverResult selected)
        {
            if (currentDisplayedResult is not { } foreground)
                return;

            SolverInterimResult final = BuildInterimResult(root, policy, selected);
            bool finalWouldReplaceForeground =
                SolverInterimResultOrdering.CanPromoteDisplayedResult(final, foreground);
            bool foregroundWouldReplaceFinal =
                SolverInterimResultOrdering.CanPromoteDisplayedResult(foreground, final);
            string relation = finalWouldReplaceForeground
                ? "final_better"
                : foregroundWouldReplaceFinal
                    ? "foreground_better"
                    : "equivalent_or_incomparable";
            CandidateOrigin? finalOrigin = selected.SearchEfficiencyOrigin;
            SearchEfficiencyMemberReport? finalMember = finalOrigin == null
                ? null
                : portfolioTelemetry.FindSearchMember(finalOrigin.SearchMemberId);
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SEARCH_E3_FINAL_VS_FOREGROUND " +
                $"relation={relation} " +
                $"final_member_id={finalOrigin?.SearchMemberId.ToString() ?? "-"} " +
                $"final_member_kind={finalMember?.Kind ?? "unknown"} " +
                $"final_won={final.Won.ToString().ToLowerInvariant()} " +
                $"foreground_won={foreground.Won.ToString().ToLowerInvariant()} " +
                $"final_hp_loss={final.ProjectedBattleHpLost} " +
                $"foreground_hp_loss={foreground.ProjectedBattleHpLost} " +
                $"final_strategic_deficit={final.StrategicHpDeficit} " +
                $"foreground_strategic_deficit={foreground.StrategicHpDeficit} " +
                $"final_enemy_hp={final.EnemyHp} foreground_enemy_hp={foreground.EnemyHp} " +
                $"final_potions={final.ProjectedBattlePotionCount} " +
                $"foreground_potions={foreground.ProjectedBattlePotionCount}");
        }

        void LogBackgroundValueSummary()
        {
            if (firstForegroundPublishedTicks is not long firstTicks)
                return;

            long endTicks = Stopwatch.GetTimestamp();
            SearchEfficiencyMemberReport[] postForegroundStartedMembers =
                portfolioTelemetry.SearchMembers
                    .Where(member => member.StartedTicks >= firstTicks)
                    .ToArray();
            long postForegroundExpanded = postForegroundStartedMembers.Sum(member => member.ExpandedNodes);
            long postForegroundTransitions =
                postForegroundStartedMembers.Sum(member => member.TransitionCount);
            double postForegroundMemberElapsedMs = postForegroundStartedMembers.Sum(member =>
                member.CompletedTicks.HasValue
                    ? BeamWidthPortfolioTelemetry.DurationMilliseconds(
                        Math.Max(0, member.CompletedTicks.Value - member.StartedTicks))
                    : 0d);
            double firstForegroundMs = portfolioTelemetry.ToRequestMilliseconds(firstTicks);
            double requestEndMs = portfolioTelemetry.ToRequestMilliseconds(endTicks);
            double lastImprovementMs = lastForegroundImprovementTicks.HasValue
                ? portfolioTelemetry.ToRequestMilliseconds(lastForegroundImprovementTicks.Value)
                : firstForegroundMs;

            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SEARCH_E3_BACKGROUND_VALUE " +
                $"first_foreground_ms={firstForegroundMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"request_end_ms={requestEndMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"background_wall_ms={Math.Max(0d, requestEndMs - firstForegroundMs).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"last_improvement_ms={lastImprovementMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"initial={foregroundInitialCount} replace={foregroundReplaceCount} " +
                $"keep={foregroundKeepCount} refresh={foregroundRefreshCount} " +
                $"post_foreground_started_members={postForegroundStartedMembers.Length} " +
                $"post_foreground_started_member_ms={postForegroundMemberElapsedMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"post_foreground_started_expanded={postForegroundExpanded} " +
                $"post_foreground_started_transitions={postForegroundTransitions} " +
                $"work_scope=members_started_after_foreground_lower_bound");
        }

        try
        {
            SolverResult result = SolveCore(
                root,
                displayNames,
                battleDamage,
                policy,
                cancellationToken,
                enrichedProgressCallback,
                interaction == null ? null : PublishAdoptableResult,
                () =>
                {
                    long nowTicks = Stopwatch.GetTimestamp();
                    long nowMs = (long)Math.Round(
                        portfolioTelemetry.ToRequestMilliseconds(nowTicks));
                    long qualityAnchorTicks =
                        lastForegroundImprovementTicks
                        ?? firstForegroundPublishedTicks
                        ?? nowTicks;
                    long qualityAnchorMs = (long)Math.Round(
                        portfolioTelemetry.ToRequestMilliseconds(qualityAnchorTicks));
                    return new E4SupplementalBudgetContext(
                        nowMs,
                        qualityAnchorMs,
                        firstForegroundPublishedTicks.HasValue
                            && currentDisplayedResult?.Survives == true);
                });
            SolverResult selected = ResolveTakeoverResult(result, interaction) ?? result;
            if (interaction?.CurrentTakeoverRequest?.Kind == SearchTakeoverKind.ApplyCurrentTurn
                && selected.ResultScope == SolverResultScope.SearchCompletion
                && currentCompleteAdoptableResult != null)
            {
                selected = currentCompleteAdoptableResult;
            }

            if (policy.RoutePolicy == SearchRoutePolicy.MultiplayerSinglePlayerCore
                && selected.ResultScope == SolverResultScope.SearchCompletion
                && currentDisplayedResult is { } foreground)
            {
                SolverInterimResult finalSummary = BuildInterimResult(root, policy, selected);
                if (ShouldPreferForegroundAtCompletion(finalSummary, foreground))
                {
                    SolverResult? exactForeground = null;
                    string materializationSource = "none";
                    int candidateVersion = -1;
                    if (currentRouteAdoptionSeed is { } foregroundSeed)
                    {
                        exactForeground = foregroundSeed.Materialize();
                        materializationSource = "route_seed";
                        candidateVersion = foregroundSeed.CandidateVersion;
                    }
                    else if (currentMaterializedForegroundResult is { } materializedForeground
                             && RouteMatchesPreview(
                                 materializedForeground.BestNode.Actions,
                                 speculativeRoutePreview))
                    {
                        exactForeground = materializedForeground;
                        materializationSource = "completed_member";
                    }
                    else if (currentTurnAdoptionSeed is { } turnSeed
                             && CurrentTurnSeedMatchesPreview(
                                 turnSeed,
                                 currentTurnPreview))
                    {
                        exactForeground = turnSeed.Materialize();
                        materializationSource = "current_turn_seed";
                        candidateVersion = turnSeed.CandidateVersion;
                    }

                    if (exactForeground != null)
                    {
                        bool completeVictory = IsCompleteVictory(exactForeground);
                        if (!CompletionForegroundCoversRoute(
                                exactForeground.BestNode.Actions, speculativeRoutePreview,
                                exactForeground.SearchedTurns, selected.SearchedTurns,
                                completeVictory,
                                exactForeground.ResultScope == SolverResultScope.CurrentTurnAdoption))
                        {
                            policy.Diagnostics.Info(
                                $"[CombatSolver/Test] SEARCH_E3_COMPLETION_FOREGROUND_REJECTED " +
                                $"source={materializationSource} reason=evaluation_scope_mismatch " +
                                $"foreground_turns={exactForeground.SearchedTurns} final_turns={selected.SearchedTurns}");
                            exactForeground = null;
                        }
                    }
                    if (exactForeground != null)
                    {
                        SolverInterimResult exactForegroundSummary =
                            BuildInterimResult(root, policy, exactForeground);
                        if (ShouldPreferForegroundAtCompletion(
                                finalSummary,
                                exactForegroundSummary))
                        {
                            policy.Diagnostics.Info(
                                $"[CombatSolver/Test] SEARCH_E3_COMPLETION_FOREGROUND_GUARD " +
                                $"source={materializationSource} candidate_version={candidateVersion} " +
                                $"final_hp_loss={finalSummary.ProjectedBattleHpLost} " +
                                $"foreground_hp_loss={exactForegroundSummary.ProjectedBattleHpLost} " +
                                $"final_strategic_deficit={finalSummary.StrategicHpDeficit} " +
                                $"foreground_strategic_deficit={exactForegroundSummary.StrategicHpDeficit} " +
                                $"final_enemy_hp={finalSummary.EnemyHp} " +
                                $"foreground_enemy_hp={exactForegroundSummary.EnemyHp} " +
                                $"scope={exactForeground.ResultScope}");
                            selected = exactForeground;
                            // Automatic completion retains the evaluated route. Current-turn
                            // adoption is reserved for the explicit takeover branch above.
                            selected.ResultScope = SolverResultScope.SearchCompletion;
                        }
                    }
                }
            }

            portfolioTelemetry.RecordCandidatePublished(
                selected.SearchEfficiencyOrigin,
                selected.SearchEfficiencyEvaluationContextId ?? string.Empty);
            PopulateRequestWorkTotals(selected, requestWorkTotals);
            selected.PortfolioTelemetry = portfolioTelemetry;
            LogFinalVsForeground(selected);
            LogBackgroundValueSummary();
            LogSearchEfficiencySummary(root, policy.Diagnostics, selected, portfolioTelemetry);
            return selected;
        }
        catch (OperationCanceledException)
            when (interaction?.CurrentTakeoverRequest?.Kind == SearchTakeoverKind.ApplyCurrentTurn
                  && (interaction.CurrentTakeoverRequest.CurrentTurnAdoptionSeed != null
                      || currentCompleteAdoptableResult != null))
        {
            SolverResult adopted =
                interaction.CurrentTakeoverRequest.CurrentTurnAdoptionSeed?.Materialize()
                ?? currentCompleteAdoptableResult!;
            adopted.ResultScope = SolverResultScope.CurrentTurnAdoption;
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SEARCH_INTERIM_ADOPTED " +
                $"candidate_version=" +
                $"{interaction.CurrentTakeoverRequest.CurrentTurnAdoptionSeed?.CandidateVersion.ToString() ?? "-"} " +
                $"potions={adopted.ProjectedBattlePotionCount} " +
                $"projected_battle_hp_lost={adopted.ProjectedBattleHpLost}");
            portfolioTelemetry.RecordCandidatePublished(
                adopted.SearchEfficiencyOrigin,
                adopted.SearchEfficiencyEvaluationContextId ?? string.Empty);
            PopulateRequestWorkTotals(adopted, requestWorkTotals);
            adopted.PortfolioTelemetry = portfolioTelemetry;
            LogFinalVsForeground(adopted);
            LogBackgroundValueSummary();
            LogSearchEfficiencySummary(
                root,
                policy.Diagnostics,
                adopted,
                portfolioTelemetry);
            return adopted;
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

    internal static bool ShouldPreferForegroundAtCompletion(
        SolverInterimResult final,
        SolverInterimResult foreground)
        => SolverInterimResultOrdering.CanPromoteDisplayedResult(foreground, final)
            && !SolverInterimResultOrdering.CanPromoteDisplayedResult(final, foreground);

    internal static string ClassifyE4SupplementalRelation(
        SolverInterimResult before,
        SolverInterimResult after)
    {
        bool afterPromotesBefore =
            SolverInterimResultOrdering.CanPromoteDisplayedResult(after, before);
        bool beforePromotesAfter =
            SolverInterimResultOrdering.CanPromoteDisplayedResult(before, after);
        if (afterPromotesBefore && !beforePromotesAfter)
            return "improved";
        if (beforePromotesAfter && !afterPromotesBefore)
            return "regressed";
        return "equivalent_or_incomparable";
    }

    internal static bool ShouldAdmitE4SupplementalResult(
        bool e4Active,
        SolverInterimResult before,
        SolverInterimResult after)
        => !e4Active
            || ClassifyE4SupplementalRelation(before, after) != "regressed";

    internal static bool RouteMatchesPreview(
        IReadOnlyList<PlanAction> actions,
        SolverSpeculativeRoutePreview? preview)
        => preview != null
            && actions.SequenceEqual(preview.Turns.SelectMany(static turn => turn.Actions));

    internal static bool CurrentTurnSeedMatchesPreview(
        SolverRouteAdoptionSeed seed,
        SolverCurrentTurnPreview? preview)
        => preview != null
            && seed.Actions.SequenceEqual(preview.Actions);

    internal static bool CompletionForegroundCoversRoute(
        IReadOnlyList<PlanAction> actions, SolverSpeculativeRoutePreview? preview,
        int foregroundTurns, int finalTurns, bool completeVictory, bool currentTurnAdoption)
        => actions.Count > 0 && RouteMatchesPreview(actions, preview)
            && (completeVictory || foregroundTurns >= finalTurns)
            && (!currentTurnAdoption || completeVictory
                || actions.Any(action => action.Turn > actions[0].Turn));

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
        {
            if (request.RouteAdoptionSeed is not { } seed)
                return null;
            SolverResult exactDisplayed = seed.Materialize();
            exactDisplayed.ResultScope = SolverResultScope.RouteAdoption;
            return exactDisplayed;
        }
        if (request.Kind == SearchTakeoverKind.ApplyCurrentTurn
            && request.CurrentTurnAdoptionSeed != null)
        {
            SolverResult exactDisplayed = request.CurrentTurnAdoptionSeed.Materialize();
            exactDisplayed.ResultScope = SolverResultScope.CurrentTurnAdoption;
            return exactDisplayed;
        }
        return IsAdoptionResult(result) ? result : null;
    }

    private static SolverResult SolveCore(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback,
        Action<SolverResult>? interimResultCallback,
        Func<E4SupplementalBudgetContext>? e4BudgetContextProvider)
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
                    ReviewedWorldlines = policy.RecoveryBudget?.ExpandedNodes
                        ?? completedSearches + progress.ExpandedNodes,
                    ElapsedMilliseconds = policy.RecoveryBudget?.ElapsedMilliseconds
                        ?? completedElapsed + progress.ElapsedMilliseconds,
                    RequestBudgetMilliseconds = policy.RecoveryBudget?.BudgetMilliseconds
                        ?? profile.SoftTimeBudgetMilliseconds,
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
            PrimarySearchIncumbent? r1RecoveryIncumbent = null;
            R1EvaluationShadowCache? r1EvaluationShadowCache = null;
            R1TransitionHydrationCache? r1TransitionHydrationCache = null;
            R1FrontierShadowCache? r1FrontierShadowCache = null;
            IReadOnlyList<PlanAction> r1ValidatedEnumerationHints = [];
            SolverResult? earlySmartPotionBaseline = null;
            SolverResult? earlySmartPotionScout = null;

            bool HasOptionalSmartPotionCandidate()
                => initialPotionPolicyOverride == SolverPotionPolicy.Disabled
                    && root.SearchablePotions.Any(potion =>
                        beamPolicy.PotionStrategy.AllowsExplicitUse(
                            potion.Slot,
                            potion.PotionId,
                            SolverPotionPolicy.Smart,
                            forceAllDisabled: false)
                        && beamPolicy.PotionStrategy.Resolve(
                            potion.Slot,
                            potion.PotionId) != SolverPotionDirective.Force);

            SolverResult SolveMember(SolverSearchProfile memberProfile, bool refinement)
            {
                Action<SolverProgress>? memberProgressCallback = refinement && progressCallback != null
                    ? progress => progressCallback(progress with { Phase = "正在精炼路线" })
                    : progressCallback;
                if (policy.UseP3CrossFamilyScheduling
                    && !refinement
                    && HasOptionalSmartPotionCandidate())
                {
                    SearchPolicySnapshot p3Policy = beamPolicy with
                    {
                        ContinuationEnumerationHintActions = [],
                        R1EvaluationShadowCache = null,
                        R1TransitionHydrationCache = null,
                        R1FrontierShadowCache = null,
                    };
                    SolverResult potionFree = RunP3CrossFamilyFixedPass(
                        root,
                        displayNames,
                        battleDamage,
                        p3Policy,
                        memberProfile,
                        cancellationToken,
                        memberProgressCallback,
                        out SolverResult? scout);
                    if (scout != null)
                    {
                        earlySmartPotionBaseline = potionFree;
                        earlySmartPotionScout = scout;
                    }
                    return potionFree;
                }

                SearchPolicySnapshot memberPolicy = refinement
                    ? beamPolicy with
                    {
                        ContinuationEnumerationHintActions = [],
                        R1EvaluationShadowCache = null,
                        R1TransitionHydrationCache = null,
                        R1FrontierShadowCache = null,
                    }
                    : beamPolicy;
                if (policy.MeasurePhasePerformance)
                {
                    memberPolicy = memberPolicy with
                    {
                        RequestMemberKind = refinement ? SearchRequestMemberKind.BeamRefinement
                            : SearchRequestMemberKind.Baseline,
                    };
                }
                CombatBeamSolver solver = new(
                    root,
                    displayNames,
                    battleDamage,
                    memberPolicy,
                    cancellationToken,
                    memberProgressCallback,
                    memberProfile,
                    potionPolicyOverride: initialPotionPolicyOverride,
                    primaryIncumbent: refinement ? null : r1RecoveryIncumbent);
                return RunResumableMemberToCompletion(
                    solver,
                    cancellationToken,
                    memberPolicy.Diagnostics);
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
                    scoutProfile,
                    provisionalPotionFree);
                return earlySmartPotionScout;
            }
            SolverResult RunPrimary()
                => policy.UseNoveltyPortfolio
                    ? RunNoveltyPortfolioPass(root, displayNames, battleDamage, passPolicy, activeProfile,
                        activeClock, initialPotionPolicyOverride, cancellationToken, progressCallback,
                        interimResultCallback,
                        MultiplayerLocalCrossTurnContracts.ShouldRunEarlySmartPotionScout(
                            policy.RoutePolicy)
                                ? RunCrossFamilyScout
                                : null,
                        RunBaseline)
                    : RunBaseline(activeProfile);

            try
            {
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
                    r1EvaluationShadowCache = new R1EvaluationShadowCache();
                    r1TransitionHydrationCache = new R1TransitionHydrationCache();
                    r1FrontierShadowCache = new R1FrontierShadowCache();
                    SearchPolicySnapshot seedPolicy = beamPolicy with
                    {
                        Interaction = null,
                        NoveltySearch = null,
                        UseNoveltyPortfolio = false,
                        ContinuationSeedActions = continuationSeedActions,
                        ContinuationEnumerationHintActions = [],
                        R1EvaluationShadowCache = r1EvaluationShadowCache,
                        R1TransitionHydrationCache = r1TransitionHydrationCache,
                        R1FrontierShadowCache = r1FrontierShadowCache,
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
                        bool reusablePartial =
                            seedResult.ResultScope == SolverResultScope.SearchCompletion
                            && !seedResult.Snapshot.PlayerDead
                            && seedResult.Snapshot.ProjectedPlayerHp > 0;
                        if (reusablePartial)
                        {
                            r1ValidatedEnumerationHints =
                                MultiplayerLocalCrossTurnContracts.CaptureR1ValidatedEnumerationHintActions(
                                    seedResult.BestNode.Actions,
                                    root.StartTurnNumber);
                            if (r1ValidatedEnumerationHints.Count > 0)
                            {
                                beamPolicy = beamPolicy with
                                {
                                    ContinuationEnumerationHintActions = r1ValidatedEnumerationHints,
                                };
                            }
                        }
                        if (admissible)
                        {
                            continuationSeedIncumbent = seedResult;
                            r1RecoveryIncumbent = BuildPrimarySearchIncumbent(root, beamPolicy, seedResult);
                            interimResultCallback?.Invoke(seedResult);
                        }
                        policy.Diagnostics.Info(
                            $"[CombatSolver/Test] P2_CONTINUATION_SEED_INCUMBENT " +
                            $"status={(admissible ? "admissible" : "incomplete")} " +
                            $"actions={continuationSeedActions.Count} " +
                            $"expanded={seedResult.ExpandedNodes} transitions={seedResult.TransitionCount} " +
                            $"elapsed_ms={seedResult.Elapsed.TotalMilliseconds:F1} " +
                            $"published={admissible.ToString().ToLowerInvariant()} " +
                            $"r1_bound={(r1RecoveryIncumbent.HasValue ? "established" : "none")}");
                        policy.Diagnostics.Info(
                            $"[CombatSolver/Test] R1_REROOT_RECOVERY " +
                            $"status={(r1RecoveryIncumbent.HasValue
                                ? "bound_established"
                                : r1ValidatedEnumerationHints.Count > 0
                                    ? "partial_hint"
                                    : "replayed_only")} " +
                            $"actions={continuationSeedActions.Count} " +
                            $"validated_hint_actions={r1ValidatedEnumerationHints.Count} " +
                            $"complete_victory={admissible.ToString().ToLowerInvariant()} " +
                            $"request_budget_shared={(policy.RecoveryBudget != null).ToString().ToLowerInvariant()} action_legality_unchanged=true " +
                            $"refinement_hint=false");
                    }
                    catch (ContinuationSeedRejectedException rejected)
                    {
                        policy.Diagnostics.Info(
                            $"[CombatSolver/Test] P2_CONTINUATION_SEED_INCUMBENT " +
                            $"status=rejected reason={rejected.Reason} " +
                            $"actions={continuationSeedActions.Count} published=false");
                        policy.Diagnostics.Info(
                            $"[CombatSolver/Test] R1_REROOT_RECOVERY " +
                            $"status=rejected reason={rejected.Reason} " +
                            $"actions={continuationSeedActions.Count} request_budget_shared={(policy.RecoveryBudget != null).ToString().ToLowerInvariant()}");
                    }

                    r1EvaluationShadowCache.Freeze();
                    r1TransitionHydrationCache.FreezeStores();
                    r1FrontierShadowCache.FreezeStores();
                    if (r1EvaluationShadowCache.EntryCount > 0
                        || r1TransitionHydrationCache.EntryCount > 0
                        || r1FrontierShadowCache.Capture().StoredSignatures > 0)
                    {
                        beamPolicy = beamPolicy with
                        {
                            R1EvaluationShadowCache =
                                r1EvaluationShadowCache.EntryCount > 0
                                    ? r1EvaluationShadowCache
                                    : null,
                            R1TransitionHydrationCache =
                                r1TransitionHydrationCache.EntryCount > 0
                                    ? r1TransitionHydrationCache
                                    : null,
                            R1FrontierShadowCache =
                                r1FrontierShadowCache.Capture().StoredSignatures > 0
                                    ? r1FrontierShadowCache
                                    : null,
                        };
                    }

                    SearchRequestWorkSnapshot afterSeed = totals.Snapshot();
                    long seedElapsedMs = Math.Max(0, passClock.ElapsedMilliseconds - seedStartedMs);
                    long seedExpanded = Math.Max(
                        0, afterSeed.ExpandedNodes - beforeSeed.ExpandedNodes);
                    SolverSearchProfile? remaining = policy.RecoveryBudget is { } sharedBudget
                        ? sharedBudget.RemainingProfile(passProfile) : passProfile;
                    if (remaining != null) activeProfile = remaining;
                    activeClock = Stopwatch.StartNew();
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] P2_CONTINUATION_SEED_BUDGET " +
                        $"probe_nodes={seedExpanded}/{seedProfile.MaxExpandedNodes} " +
                        $"probe_ms={seedElapsedMs}/{seedProfile.SoftTimeBudgetMilliseconds} " +
                        $"primary_nodes_remaining={remaining?.MaxExpandedNodes ?? 0} " +
                        $"primary_ms_remaining={remaining?.SoftTimeBudgetMilliseconds ?? 0} " +
                        $"request_budget_shared={(policy.RecoveryBudget != null).ToString().ToLowerInvariant()} " +
                        $"r1_bound={(r1RecoveryIncumbent.HasValue ? "established" : "none")} " +
                        $"r1_hint_actions={r1ValidatedEnumerationHints.Count}");
                    cancellationToken.ThrowIfCancellationRequested();
                    if (remaining == null)
                    {
                        if (continuationSeedIncumbent != null)
                            return continuationSeedIncumbent;
                        throw new SearchRecoveryBudgetExhaustedException();
                    }
                }

                void ReleaseR1TransitionHydration(string stage, bool emitTelemetry)
                {
                    if (r1TransitionHydrationCache == null)
                        return;
                    if (emitTelemetry)
                    {
                        R1TransitionHydrationSnapshot hydration =
                            r1TransitionHydrationCache.Capture();
                        policy.Diagnostics.Info(
                            $"[CombatSolver/Test] R1_TRANSITION_HYDRATION_TAIL " +
                            $"stage={stage} entries={hydration.Entries} " +
                            $"validated_keys={hydration.ValidatedKeys} " +
                            $"hydration_hits={hydration.HydrationHits} " +
                            $"output_mismatches={hydration.OutputMismatches} " +
                            $"rejected_keys={hydration.RejectedKeys} " +
                            $"reuse_disabled={hydration.ReuseDisabled.ToString().ToLowerInvariant()}");
                    }
                    r1TransitionHydrationCache.Release();
                    r1TransitionHydrationCache = null;
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
                if (r1EvaluationShadowCache != null)
                {
                    R1EvaluationShadowSnapshot r1Shadow = r1EvaluationShadowCache.Capture();
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] R1_EVAL_SHADOW " +
                        $"mode=exact_path_aware entry_limit={r1Shadow.EntryLimit} " +
                        $"entries={r1Shadow.Entries} store_attempts={r1Shadow.StoreAttempts} " +
                        $"duplicate_stores={r1Shadow.DuplicateStores} " +
                        $"store_conflicts={r1Shadow.StoreConflicts} " +
                        $"observations={r1Shadow.ValidationObservations} " +
                        $"state_key_misses={r1Shadow.StateKeyMisses} " +
                        $"full_key_misses={r1Shadow.FullKeyMisses} " +
                        $"validated_hits={r1Shadow.ValidatedHits} " +
                        $"output_mismatches={r1Shadow.OutputMismatches} " +
                        $"beam_rank_stores={r1Shadow.BeamRankStores} " +
                        $"beam_rank_first_validations={r1Shadow.BeamRankFirstValidations} " +
                        $"beam_rank_validated={r1Shadow.BeamRankValidated} " +
                        $"beam_rank_mismatches={r1Shadow.BeamRankMismatches} " +
                        $"beam_rank_reuses={r1Shadow.BeamRankReuses} " +
                        $"reuse_disabled={r1Shadow.ReuseDisabled.ToString().ToLowerInvariant()} " +
                        $"capped={r1Shadow.Capped.ToString().ToLowerInvariant()} " +
                        $"behavioral_reuse=beam_rank_after_first_validation");
                }
                if (r1TransitionHydrationCache != null)
                {
                    R1TransitionHydrationSnapshot hydration =
                        r1TransitionHydrationCache.Capture();
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] R1_TRANSITION_HYDRATION " +
                        $"stage=baseline mode=exact_request_local entry_limit={hydration.EntryLimit} " +
                        $"entries={hydration.Entries} stores={hydration.Stores} " +
                        $"duplicate_stores={hydration.DuplicateStores} " +
                        $"dropped_stores={hydration.DroppedStores} " +
                        $"store_conflicts={hydration.StoreConflicts} " +
                        $"first_validations={hydration.FirstValidations} " +
                        $"validated_keys={hydration.ValidatedKeys} " +
                        $"index_misses={hydration.IndexMisses} " +
                        $"collision_rejects={hydration.CollisionRejects} " +
                        $"hydration_hits={hydration.HydrationHits} " +
                        $"output_mismatches={hydration.OutputMismatches} " +
                        $"rejected_keys={hydration.RejectedKeys} " +
                        $"reuse_disabled={hydration.ReuseDisabled.ToString().ToLowerInvariant()} " +
                        $"behavioral_reuse=exact_nonterminal_transition");
                    foreach (R1TransitionHydrationRejectSample rejected in hydration.RejectSamples)
                    {
                        policy.Diagnostics.Info(
                            $"[CombatSolver/Test] R1_HYDRATION_REJECTED_KEY " +
                            $"action={rejected.ActionIdentity} reason={rejected.Reason}");
                    }
                    // D3.3C keeps validated exact hydration alive through request-tail audits.
                }
                if (r1FrontierShadowCache != null)
                {
                    R1FrontierShadowSnapshot frontierShadow = r1FrontierShadowCache.Capture();
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] R1_FRONTIER_SHADOW " +
                        $"mode=plain_exact_ordered signature_limit={frontierShadow.SignatureLimit} " +
                        $"stored_signatures={frontierShadow.StoredSignatures} " +
                        $"duplicate_stores={frontierShadow.DuplicateStores} " +
                        $"dropped_stores={frontierShadow.DroppedStores} " +
                        $"baseline_observations={frontierShadow.BaselineObservations} " +
                        $"key_misses={frontierShadow.KeyMisses} " +
                        $"turn_misses={frontierShadow.TurnMisses} " +
                        $"action_count_misses={frontierShadow.ActionCountMisses} " +
                        $"node_count_misses={frontierShadow.NodeCountMisses} " +
                        $"validated_hits={frontierShadow.ValidatedHits} " +
                        $"signature_mismatches={frontierShadow.SignatureMismatches} " +
                        $"skipped_frontiers={frontierShadow.SkippedFrontiers} " +
                        $"subset_observations={frontierShadow.SubsetObservations} " +
                        $"probe_subset_hits={frontierShadow.ProbeSubsetHits} " +
                        $"exact_set_hits={frontierShadow.ExactSetHits} " +
                        $"partial_overlap_hits={frontierShadow.PartialOverlapHits} " +
                        $"zero_overlap_hits={frontierShadow.ZeroOverlapHits} " +
                        $"behavioral_reuse=false");
                    foreach (R1FrontierSubsetSample subset in frontierShadow.SubsetSamples)
                    {
                        policy.Diagnostics.Info(
                            $"[CombatSolver/Test] R1_FRONTIER_SUBSET " +
                            $"turn={subset.Depth.Turn} action_count={subset.Depth.ActionCount} " +
                            $"probe_nodes={subset.ProbeNodes} baseline_nodes={subset.BaselineNodes} " +
                            $"intersection_nodes={subset.IntersectionNodes} " +
                            $"probe_subset={subset.ProbeSubsetOfBaseline.ToString().ToLowerInvariant()} " +
                            $"exact_set={subset.ExactSetMatch.ToString().ToLowerInvariant()}");
                    }
                    foreach (R1FrontierMissSample miss in frontierShadow.MissSamples)
                    {
                        string missKind = miss.Kind switch
                        {
                            R1FrontierMissKind.Turn => "turn_miss",
                            R1FrontierMissKind.ActionCount => "action_count_miss",
                            R1FrontierMissKind.NodeCount => "node_count_miss",
                            _ => "unknown",
                        };
                        policy.Diagnostics.Info(
                            $"[CombatSolver/Test] R1_FRONTIER_MISS " +
                            $"kind={missKind} " +
                            $"baseline_turn={miss.Baseline.Turn} " +
                            $"baseline_action_count={miss.Baseline.ActionCount} " +
                            $"baseline_nodes={miss.Baseline.NodeCount} " +
                            $"nearest_probe_turn={miss.NearestProbe.Turn} " +
                            $"nearest_probe_action_count={miss.NearestProbe.ActionCount} " +
                            $"nearest_probe_nodes={miss.NearestProbe.NodeCount} " +
                            $"turn_delta={Math.Abs(miss.NearestProbe.Turn - miss.Baseline.Turn)} " +
                            $"action_count_delta={Math.Abs(miss.NearestProbe.ActionCount - miss.Baseline.ActionCount)} " +
                            $"node_count_delta={Math.Abs(miss.NearestProbe.NodeCount - miss.Baseline.NodeCount)}");
                    }
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
                    ReleaseR1TransitionHydration("takeover", emitTelemetry: false);
                    return passResult;
                }
                if (passResult.DeterministicBlockPotionInserted)
                {
                    passSettled = true;
                    ReleaseR1TransitionHydration("deterministic_block_potion", emitTelemetry: false);
                    return SelectContinuationSeedIncumbent(passResult);
                }
                if (!policy.PotionStrategy.HasForcedDirectives || hasForcedBaseline)
                {
                    if (!hasForcedBaseline && HasReachedAcceptableBattleHpLoss(policy, passResult))
                    {
                        passSettled = true;
                        ReleaseR1TransitionHydration("acceptable_loss", emitTelemetry: false);
                        return SelectContinuationSeedIncumbent(passResult);
                    }
                    SolverSearchProfile supplementalProfile = activeProfile;
                    bool e4SmartPotionBudgetActive = false;
                    if (policy.RoutePolicy == SearchRoutePolicy.MultiplayerSinglePlayerCore)
                    {
                        E4SupplementalBudgetContext e4Context =
                            e4BudgetContextProvider?.Invoke()
                            ?? new E4SupplementalBudgetContext(
                                activeClock.ElapsedMilliseconds,
                                activeClock.ElapsedMilliseconds,
                                HasApprovedForeground: false);
                        long supplementalStartMs = e4Context.RequestElapsedMilliseconds;
                        long qualityAnchorMs = e4Context.LastImprovementMilliseconds;
                        bool hasApprovedForeground = e4Context.HasApprovedForeground;
                        bool isPrimaryPass = ReferenceEquals(passClock, requestClock);
                        int e4DeadlineMs = ComputeE4SupplementalDeadlineMilliseconds(
                            policy.RoutePolicy,
                            activeProfile.SoftTimeBudgetMilliseconds,
                            supplementalStartMs,
                            qualityAnchorMs,
                            hasApprovedForeground,
                            policy.PotionStrategy.HasForcedDirectives,
                            isPrimaryPass);
                        bool e4Active =
                            e4DeadlineMs < activeProfile.SoftTimeBudgetMilliseconds;
                        e4SmartPotionBudgetActive = e4Active;
                        int e4ActiveDeadlineMs = e4Active
                            ? ComputeE4ActiveClockDeadlineMilliseconds(
                                activeProfile.SoftTimeBudgetMilliseconds,
                                activeClock.ElapsedMilliseconds,
                                supplementalStartMs,
                                e4DeadlineMs)
                            : activeProfile.SoftTimeBudgetMilliseconds;
                        if (e4ActiveDeadlineMs < activeProfile.SoftTimeBudgetMilliseconds)
                        {
                            supplementalProfile = activeProfile with
                            {
                                SoftTimeBudgetMilliseconds = e4ActiveDeadlineMs,
                            };
                        }
                        policy.Diagnostics.Info(
                            $"[CombatSolver/Test] SEARCH_E4_SUPPLEMENTAL_BUDGET " +
                            $"active={e4Active.ToString().ToLowerInvariant()} " +
                            $"primary_pass={isPrimaryPass.ToString().ToLowerInvariant()} " +
                            $"approved_foreground={hasApprovedForeground.ToString().ToLowerInvariant()} " +
                            $"forced_potions={policy.PotionStrategy.HasForcedDirectives.ToString().ToLowerInvariant()} " +
                            $"start_ms={supplementalStartMs} last_improvement_ms={qualityAnchorMs} " +
                            $"active_clock_ms={activeClock.ElapsedMilliseconds} " +
                            $"original_deadline_ms={activeProfile.SoftTimeBudgetMilliseconds} " +
                            $"e4_request_deadline_ms={e4DeadlineMs} " +
                            $"e4_active_deadline_ms={e4ActiveDeadlineMs}");
                    }

                    passResult = RunSupplementalAudits(
                        root,
                        displayNames,
                        battleDamage,
                        (policy.NoveltySearch == null
                            ? policy
                            : policy with { NoveltySearch = null }) with
                        {
                            R1TransitionHydrationCache = r1TransitionHydrationCache,
                        },
                        cancellationToken,
                        progressCallback,
                        supplementalProfile,
                        activeClock,
                        passResult,
                        memoryForecast,
                        interimResultCallback,
                        earlySmartPotionBaseline,
                        earlySmartPotionScout,
                        e4SmartPotionBudgetActive);
                    // The final potion audit may return another result object. Keep the
                    // primary-pass observations alongside the request's final outcome.
                    passResult.NoveltyPortfolio = noveltyPass;
                }
                ReleaseR1TransitionHydration("request_tail", emitTelemetry: true);
                return SelectContinuationSeedIncumbent(passResult);
            }
            finally
            {
                // A canceled/failed probe can exit before FreezeStores or the
                // ordinary tail. Cached simulators still belong to this request.
                if (r1TransitionHydrationCache != null)
                {
                    int entriesBeforeRelease = r1TransitionHydrationCache.EntryCount;
                    r1TransitionHydrationCache.Release();
                    policy.Diagnostics.Info("[CombatSolver/Test] R1_TRANSITION_HYDRATION_RELEASE "
                        + $"stage=early_exit entries_before_release={entriesBeforeRelease} "
                        + $"entries_after_release={r1TransitionHydrationCache.EntryCount}");
                }
            }
        }

        SolverResult result = RunSearchPass(profile, requestClock);
        if (takeoverResult != null)
            return takeoverResult;
        // 打到可接受战损就收手那一条和改动之前一样直接返回，连 SEARCH_SESSION 都不打。
        if (passSettled || policy.FixedBudget || policy.RecoveryBudget != null)
            return result;
        if (result.BoundaryReason == SearchBoundaryReason.TurnLimit
            && SearchEvaluationPolicy.HasReachedTurnLayerLimit(
                policy.RoutePolicy,
                result.SearchedTurns, policy.LocalCoreTurnLayers))
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Multiplayer] MP_LOCAL_CORE_HORIZON_COMPLETE " +
                $"searched_turns={result.SearchedTurns} escalation=false");
            return result;
        }
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
                    policy.MemoryPressureSignal.RemainingBytes,
                    policy.UseUpstreamSearchOptimizations && policy.MemoryPressureSignal.HasReclaimCheckpoint)
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

    internal static bool IsAcceptableContinuationRouteResult(
        SearchPolicySnapshot policy, SolverResult result, out string quality)
    {
        bool equivalentPartial = !result.Snapshot.AllEnemiesDead && !result.Snapshot.PlayerDead
            && result.Snapshot.ProjectedPlayerHp > 0
            && result.PotionCount == 0
            && policy.MinimumRequiredPotionUses(result.BattlePotionsUsedSoFar) == 0
            && policy.PotionStrategy.EvaluateForcedUses(result.PotionUses).AllForcedUsesSatisfied
            && result.BoundaryReason is SearchBoundaryReason.None or SearchBoundaryReason.TurnLimit
            && policy.ContinuationRouteReplayBaseline is { } baseline
            && result.PredictedFinalState is { } finalState
            && result.ProjectedBattleHpLost <= baseline.ProjectedBattleHpLost
            && result.Snapshot.ProjectedDeathSaveUseCount == baseline.DeathSaveUses
            && result.Snapshot.GrowthRewards == baseline.GrowthRewards
            && result.Snapshot.RelicCounters == baseline.RelicCounters
            && MultiplayerLocalCrossTurnContracts.IsReplayedRouteFinalStateCompatible(
                baseline.FinalState.StateText, finalState.StateText);
        bool explicitVictory = policy.ContinuationRouteReplayBaseline is { ExplicitRouteAdoption: true } adopted
            && IsCompleteVictory(result)
            && result.ProjectedBattleHpLost <= adopted.ProjectedBattleHpLost
            && result.Snapshot.ProjectedDeathSaveUseCount == adopted.DeathSaveUses
            && result.Snapshot.GrowthRewards == adopted.GrowthRewards
            && result.Snapshot.RelicCounters == adopted.RelicCounters
            && result.PotionCount == 0
            && policy.MinimumRequiredPotionUses(result.BattlePotionsUsedSoFar) == 0
            && policy.PotionStrategy.EvaluateForcedUses(result.PotionUses).AllForcedUsesSatisfied
            && TheftEncounterStrategy.RecoverySatisfied(policy.TheftPolicy, result.OutstandingStolenResource);
        bool accepted = policy.ContinuationRouteReplayBaseline?.ExplicitRouteAdoption == true
            ? equivalentPartial || explicitVictory
            : equivalentPartial || HasReachedAcceptableBattleHpLoss(policy, result);
        quality = equivalentPartial ? "equivalent_partial" : explicitVictory ? "explicit_adoption_victory"
            : accepted ? "acceptable_victory" : "not_proven";
        return accepted;
    }

    internal static SolverResult? TryReplayContinuationRoute(
        CombatRootSnapshot root, SolverDisplayNames displayNames, BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy, CancellationToken cancellationToken)
        => TryReplayContinuationRoute(root, displayNames, battleDamage, policy, cancellationToken, out _);

    private static SolverResult? TryReplayContinuationRoute(
        CombatRootSnapshot root, SolverDisplayNames displayNames, BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy, CancellationToken cancellationToken, out string reason)
    {
        reason = "replay_not_admitted";
        if (policy.ContinuationRouteReplayActions.Count == 0
            || policy.RoutePolicy != SearchRoutePolicy.MultiplayerSinglePlayerCore
            || policy.IncludeTurnSetup || policy.CurrentTurnOnly || policy.VerifyIncrementalSearch)
            return null;
        try
        {
            SolverSearchProfile replayProfile = policy.Profile with
            {
                SoftTimeBudgetMilliseconds = Math.Min(policy.Profile.SoftTimeBudgetMilliseconds, 600),
            };
            SolverResult result = new CombatBeamSolver(root, displayNames, battleDamage,
                policy with { VerifyIncrementalSearch = false, RequestMemberKind = SearchRequestMemberKind.RouteReplay }, cancellationToken,
                searchProfile: replayProfile,
                continuationRouteReplayActions: policy.ContinuationRouteReplayActions).Solve();
            bool accepted = IsAcceptableContinuationRouteResult(policy, result, out string quality);
            reason = accepted ? quality : "quality_rejected";
            if (accepted && policy.ContinuationRouteReplayBaseline?.ExplicitRouteAdoption == true)
                result.ResultScope = SolverResultScope.RouteAdoption;
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] MP_LOCAL_XTURN_ROUTE_REPLAY status={(accepted ? "accepted" : "quality_rejected")} " +
                $"fresh_root=true expanded={result.ExpandedNodes} actions={result.BestNode.ActionCount} " +
                $"hp_loss={result.ProjectedBattleHpLost} elapsed_ms={result.Elapsed.TotalMilliseconds:F1} " +
                $"quality={quality} " +
                $"full_search_skipped={accepted.ToString().ToLowerInvariant()}");
            return accepted ? result : null;
        }
        catch (ContinuationSeedRejectedException ex)
        {
            reason = ex.Reason;
            policy.Diagnostics.Info($"[CombatSolver/Test] MP_LOCAL_XTURN_ROUTE_REPLAY status=rejected reason={ex.Reason}");
            return null;
        }
        catch (PredictionUnsupportedException ex)
        {
            reason = ex.GetType().Name;
            policy.Diagnostics.Info($"[CombatSolver/Test] MP_LOCAL_XTURN_ROUTE_REPLAY status=rejected reason={ex.GetType().Name}");
            return null;
        }
        catch (NativeChoicePlanMismatchException ex)
        {
            reason = ex.GetType().Name;
            policy.Diagnostics.Info($"[CombatSolver/Test] MP_LOCAL_XTURN_ROUTE_REPLAY status=rejected reason={ex.GetType().Name}");
            return null;
        }
        catch (InvalidPlannedChoiceBranchException ex)
        {
            reason = ex.GetType().Name;
            policy.Diagnostics.Info($"[CombatSolver/Test] MP_LOCAL_XTURN_ROUTE_REPLAY status=rejected reason={ex.GetType().Name}");
            return null;
        }
    }

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
