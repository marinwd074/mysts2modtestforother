using System.Diagnostics;
using System.Runtime;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace CombatSolver;

internal static partial class SolverController
{

    public static void RequestSearch(NGame host, CombatState state, SearchReason reason, bool deployWhenReady = false)
    {
        AssertMainThread();
        SolverSessionCapabilitySet capabilities = SolverSessionCapabilities.Capture(state);
        if (!capabilities.CanSearch)
        {
            Entry.Logger.Info($"[CombatSolver/MultiplayerProbe] SEARCH_REJECT reason={capabilities.SearchRejection}");
            return;
        }
        if (_combat.ShowcaseMode && reason != SearchReason.AutoTurnStart)
        {
            StopShowcaseRoute(host, "战斗状态与录像路线不一致，已停止执行。");
            return;
        }
        int? searchTurn = LocalContext.GetMe(state)?.PlayerCombatState?.TurnNumber;
        // Turn setup and TurnStarted can complete in either order. The first accepted
        // request owns this turn; a late automatic callback must keep its active plan.
        if (reason == SearchReason.AutoTurnStart
            && (ReferenceEquals(_combat.State, state)
                    && (_combat.LatestResult?.StartTurnNumber == searchTurn || _combat.LastSolverDeployedTurn == searchTurn)
                || _search is { } active && ReferenceEquals(active.State, state) && active.StartTurnNumber == searchTurn
                || _deployment is { } deploying && ReferenceEquals(deploying.State, state) && deploying.StartTurnNumber == searchTurn))
        {
            Entry.Logger.Info($"[CombatSolver/Test] AUTO_SEARCH_ALREADY_OWNED turn={searchTurn}");
            return;
        }
        _combat.StoppedSearch = null;
        SolverDispatcher.Ensure(host);
        if (_combat.DeployAfterTurnSetupTurn == searchTurn)
        {
            deployWhenReady = true;
            _combat.DeployAfterTurnSetupTurn = null;
            Entry.Logger.Info("[CombatSolver/Test] DEPLOY_RESUME reason=turn_setup_completed");
        }
        else if (_combat.DeployAfterTurnSetupTurn != null)
        {
            _combat.DeployAfterTurnSetupTurn = null;
        }
        Task rootCaptureBarrier = SearchGcPolicy.CaptureRootSnapshotBarrier();
        if (!rootCaptureBarrier.IsCompleted)
        {
            DeferSearchUntilRootCaptureBarrier(
                host,
                state,
                reason,
                deployWhenReady,
                rootCaptureBarrier);
            return;
        }
        // A direct request can arrive after the barrier opened but before the deferred
        // callback reached the dispatcher. The newest request owns the search slot.
        CancelDeferredSearch();
        if (reason == SearchReason.Manual)
        {
            if (_combat.AutomaticSearchPaused)
                Entry.Logger.Info("[CombatSolver/Test] AUTOMATIC_SEARCH_RESUMED reason=manual_recalculate");
            _combat.AutomaticSearchPaused = false;
            if (PlayerTurnSetupCoordinator.TryRecalculatePendingChoice(host, state))
            {
                _combat.PendingCompleteProjectionBaseline = null;
                _combat.PendingManualProjectionBaseline = null;
                Entry.Logger.Info(
                    "[CombatSolver/Test] SEARCH_RESTARTED reason=manual_recalculate_pending_turn_setup");
                return;
            }
            bool queuedAfterTurnSetup = PlayerTurnSetupCoordinator.TryQueueManualRecalculation(state);
            if (!queuedAfterTurnSetup && ReferenceEquals(_combat.TurnSetupResumeState, state))
            {
                QueueManualSearchAfterTurnSetup();
                queuedAfterTurnSetup = true;
                Entry.Logger.Info(
                    "[CombatSolver/Test] TURN_SETUP_MANUAL_RECALCULATE_QUEUED phase=resuming_play");
            }
            if (queuedAfterTurnSetup)
            {
                _combat.PendingCompleteProjectionBaseline = null;
                _combat.PendingManualProjectionBaseline = null;
                SolverOverlay.Show(
                    host,
                    "[b]战斗路线求解器[/b]\n等待当前回合开始选择完成后重新计算。");
                Entry.Logger.Info(
                    "[CombatSolver/Test] SEARCH_DEFERRED reason=manual_recalculate_after_turn_setup");
                return;
            }
        }
        else if (_combat.AutomaticSearchPaused)
        {
            _combat.FullAutoEnabled = false;
            Entry.Logger.Info($"[CombatSolver/Test] SEARCH_REJECT reason=user_stopped request={reason}");
            SolverOverlay.ShowSearchStopped(host);
            return;
        }
        // Queue completion includes post-action victory checks; a paused choice also keeps
        // its action completion pending even when the queue temporarily has no ready work.
        ActionExecutor actionExecutor = RunManager.Instance.ActionExecutor;
        Task nativeActionBarrier = actionExecutor.CurrentlyRunningAction is { } runningAction
            ? Task.WhenAll(actionExecutor.FinishedExecutingActions(), runningAction.CompletionTask)
            : actionExecutor.FinishedExecutingActions();
        if (!nativeActionBarrier.IsCompleted)
        {
            DeferSearchUntilRootCaptureBarrier(host, state, reason, deployWhenReady, nativeActionBarrier);
            return;
        }
        nativeActionBarrier.GetAwaiter().GetResult();
        ReplanCause replanCause = reason switch
        {
            SearchReason.AutoTurnStart => ReplanCause.InitialSearch,
            SearchReason.DeploymentDrift => ReplanCause.DeploymentDrift,
            SearchReason.PlanExhausted => ReplanCause.PlanExhausted,
            _ => ReplanCause.ExplicitRequest,
        };
        SearchBoundaryReason? previousBoundary = _combat.ContinuationSource?.BoundaryReason;
        if (reason != SearchReason.AutoTurnStart)
        {
            _combat.PendingCompleteProjectionBaseline = null;
            _combat.PendingManualProjectionBaseline = null;
        }
        if (!CanSolve(state, out string rejection))
        {
            SolverOverlay.Show(host, $"[b]战斗路线求解器[/b]\n{rejection}");
            Entry.Logger.Info($"[CombatSolver/Test] SEARCH_REJECT reason={rejection}");
            return;
        }
        CombatShowcaseCollector.TryCaptureInitialRoot(state, reason);
        CombatBugReportExporter.RecordCheckpoint(
            state,
            $"search_request_{reason}",
            CurrentResultForBugReport,
            DescribeReplanAudit());

        string setupStage = "battle_damage";
        try
        {
            BattleDamageSnapshot battleDamage = BattleDamageTracker.Observe(state);
            setupStage = "live_stamp";
            LiveCombatStamp stamp = LiveCombatStamp.Capture(state);
            if (reason != SearchReason.AutoTurnStart
                && _combat.LatestResult is { } previousResult
                && _combat.LatestStamp is { } previousStamp
                && previousStamp != stamp)
            {
                replanCause = ReplanCause.ManualDivergence;
                _combat.PendingManualProjectionBaseline = new ManualProjectionBaseline(
                    previousResult.StartTurnNumber,
                    previousResult.ProjectedBattleHpLost,
                    "field=live_combat_stamp expected={solver_result} actual={manual_state_change}",
                    CombatBugReportExporter.LastCompletedSearchRootId);
            }
            setupStage = "continuation";
            ContinuationStamp? continuationStamp = reason == SearchReason.AutoTurnStart
                && capabilities.CanCrossTurnReuse
                && _combat.ContinuationSource != null
                ? ContinuationStamp.CaptureLive(state)
                : null;
            SolverResult? continuationSource = continuationStamp == null
                ? null
                : _combat.ContinuationSource;
            int? continuationTurn = continuationStamp == null
                ? null
                : LocalContext.GetMe(state)?.PlayerCombatState?.TurnNumber
                    ?? throw new InvalidOperationException("续接校验时找不到本地回合。");
            bool freshProbeChanged = false;
            if (continuationStamp != null && capabilities.IsMultiplayer)
            {
                freshProbeChanged = MultiplayerClientProbe.ObserveActionBoundary(
                    state,
                    "continuation_validation");
                Entry.Logger.Info(
                    $"[CombatSolver/MultiplayerProbe] MP_LOCAL_CROSS_TURN_FRESH_PROBE " +
                    $"reason=continuation_validation changed={freshProbeChanged.ToString().ToLowerInvariant()} " +
                    $"world_version={MultiplayerWorldTracker.WorldVersion} fresh_probe=true");
            }
            MultiplayerContinuationValidation? multiplayerValidation = continuationStamp != null
                && capabilities.IsMultiplayer
                    ? MultiplayerClientProbe.CaptureContinuationValidation(
                        state,
                        _combat.LastSafeEndTurnWorldVersion ?? 0)
                    : null;
            CachedContinuation? expectedContinuation = continuationTurn is { } validationTurn
                ? continuationSource?.Continuations
                    .FirstOrDefault(item => item.StartTurnNumber == validationTurn)
                : null;
            MultiplayerContinuationExpectation? expectedMultiplayer =
                expectedContinuation?.MultiplayerExpectation;
            string continuationRejectReason = "none";
            if (continuationStamp != null && capabilities.IsMultiplayer)
            {
                Entry.Logger.Info(
                    $"[CombatSolver/Test] MP_LOCAL_XTURN_CONTINUATION_VALIDATE " +
                    $"turn={continuationTurn} route_identity={_combat.ContinuationSource?.RouteIdentity ?? "-"} " +
                    $"source_world_version={expectedMultiplayer?.SourceWorldVersion.ToString() ?? "-"} " +
                    $"minimum_world_version={multiplayerValidation?.MinimumWorldVersion.ToString() ?? "-"} " +
                    $"actual_world_version={multiplayerValidation?.CurrentWorldVersion.ToString() ?? "-"} " +
                    $"fresh_probe_changed={freshProbeChanged.ToString().ToLowerInvariant()}");
            }
            if (continuationStamp != null
                && continuationSource!.TryCreateContinuation(
                    continuationStamp,
                    continuationTurn!.Value,
                    LocalContext.GetMe(state)!.Creature.CurrentHp,
                    battleDamage,
                    multiplayerValidation,
                    out SolverResult? reused,
                    out continuationRejectReason))
            {
                CancelSearch();
                _combat.State = state;
                _combat.LatestResult = reused;
                _combat.LatestStamp = stamp;
                _combat.ContinuationSource = reused;
                _combat.AwaitingMultiplayerContinuation = false;
                _combat.LastSafeEndTurnWorldVersion = null;
                _combat.LastSafeEndTurnRequestId = null;
                _combat.LastSafeEndTurnNumber = null;
                _combat.ContinuationsReused++;
                if (UnattendedTestRunner.IsActive)
                {
                    LastCompletedResultForTesting = reused;
                    LastReusedTurnForTesting = reused!.StartTurnNumber;
                    LastReusedProjectedBattleHpLostForTesting = reused.ProjectedBattleHpLost;
                }
                BattleDamageTracker.RegisterPlan(state, reused!);
                CombatBugReportExporter.RecordCheckpoint(
                    state,
                    "search_reused",
                    reused,
                    DescribeReplanAudit());
                SolverOverlay.ShowResult(
                    host,
                    SolverOverlaySnapshot.CaptureWithReviewedWorldlines(
                        reused!,
                        UnexpectedReplanCount > 0,
                        _combat.ReviewedWorldlinesTotal));
                Entry.Logger.Info(
                    $"[CombatSolver/Test] SEARCH_REUSED from_turn={reused!.ReusedFromTurn} " +
                    $"turn={reused.StartTurnNumber} validation=exact_state_text " +
                    $"remaining_turns={reused.SearchedTurns} route_identity={reused.RouteIdentity} " +
                    $"old_authorization_dead={capabilities.IsMultiplayer.ToString().ToLowerInvariant()} " +
                    $"new_authorization_pending={(deployWhenReady && capabilities.CanDeploySimpleLocalActions).ToString().ToLowerInvariant()}");
                if (capabilities.IsMultiplayer)
                {
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] MP_LOCAL_XTURN_CONTINUATION_REUSED " +
                        $"turn={reused.StartTurnNumber} route_identity={reused.RouteIdentity} " +
                        $"source_world_version={expectedMultiplayer?.SourceWorldVersion.ToString() ?? "-"} " +
                        $"minimum_world_version={multiplayerValidation?.MinimumWorldVersion.ToString() ?? "-"} " +
                        $"actual_world_version={multiplayerValidation?.CurrentWorldVersion.ToString() ?? "-"} " +
                        "local_state_exact=true reason=exact");
                }
                Entry.Logger.Info(SolverDiagnostics.DescribeResult(reused));
                if (_combat.FullAutoEnabled)
                    StartFullAutoDeployment(host, state, reused);
                else if (deployWhenReady)
                    StartDeployment(host, state, reused);
                return;
            }

            if (continuationStamp != null)
            {
                _combat.PendingCompleteProjectionBaseline = null;
                SolverResult source = continuationSource
                    ?? throw new InvalidOperationException("续接校验源路线已丢失。");
                int currentTurn = continuationTurn!.Value;
                CachedContinuation? expected = expectedContinuation;
                _combat.LastContinuationDifferences = expected == null
                    ? ["field=continuation expected={cached_turn_missing} actual={live_turn_present}"]
                    : expected.ExpectedState.DescribeDifferences(continuationStamp);
                bool localStateExact = expected != null
                    && _combat.LastContinuationDifferences.Count == 0;
                string difference = _combat.LastContinuationDifferences.FirstOrDefault()
                    ?? (expected == null
                        ? "field=continuation expected={cached_turn_missing} actual={live_turn_present}"
                        : $"field=multiplayer_validation reason={continuationRejectReason}");
                bool followedBySolver = _combat.LastSolverDeployedTurn == currentTurn - 1;
                replanCause = !followedBySolver
                    ? ReplanCause.ManualDivergence
                    : expected == null
                        ? ReplanCause.ContinuationMissing
                        : ReplanCause.StateMismatch;
                if (replanCause == ReplanCause.ManualDivergence)
                {
                    _combat.PendingManualProjectionBaseline = new ManualProjectionBaseline(
                        source.StartTurnNumber,
                        source.ProjectedBattleHpLost,
                        difference,
                        CombatBugReportExporter.LastCompletedSearchRootId);
                }
                if (followedBySolver
                    && source.BoundaryReason == SearchBoundaryReason.None
                    && source.CombatEndedTurn.HasValue)
                {
                    _combat.PendingCompleteProjectionBaseline = new CompleteProjectionBaseline(
                        source.StartTurnNumber,
                        source.ProjectedBattleHpLost,
                        difference);
                }
                Entry.Logger.Info(
                    $"[CombatSolver/Test] SEARCH_REUSE_MISS turn={currentTurn} " +
                    $"reason={CauseToken(replanCause)} cached_turns={source.Continuations.Count} " +
                    $"previous_boundary={source.BoundaryReason} " +
                    $"continuation_reject_reason={continuationRejectReason} " +
                    $"local_state_exact={localStateExact.ToString().ToLowerInvariant()} " +
                    $"diff_count={_combat.LastContinuationDifferences.Count} {difference}");
                if (_combat.LastContinuationDifferences.Count > 0)
                {
                    for (int index = 0; index < _combat.LastContinuationDifferences.Count; index++)
                    {
                        Entry.Logger.Info(
                            $"[CombatSolver/Debug] STATE_DIFF index={index} " +
                            _combat.LastContinuationDifferences[index]);
                    }
                }
                if (capabilities.IsMultiplayer)
                {
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] MP_LOCAL_XTURN_CONTINUATION_REJECTED " +
                        $"turn={currentTurn} route_identity={source.RouteIdentity} " +
                        $"source_world_version={expectedMultiplayer?.SourceWorldVersion.ToString() ?? "-"} " +
                        $"minimum_world_version={multiplayerValidation?.MinimumWorldVersion.ToString() ?? "-"} " +
                        $"actual_world_version={multiplayerValidation?.CurrentWorldVersion.ToString() ?? "-"} " +
                        $"local_state_exact={localStateExact.ToString().ToLowerInvariant()} " +
                        $"reason={continuationRejectReason}");
                }
            }

            if (_combat.ShowcaseMode)
            {
                StopShowcaseRoute(host, "新回合状态与预计算续接点不一致。");
                return;
            }
            _combat.ContinuationSource = null;
            _combat.AwaitingMultiplayerContinuation = false;
            CancelSearch();
            SolverSearchSession search = new(
                ++_nextSearchGeneration,
                state,
                stamp,
                deployWhenReady)
            {
                ReplanCause = replanCause,
                StartTurnNumber = searchTurn!.Value,
                WorldVersion = capabilities.IsMultiplayer
                    ? MultiplayerWorldTracker.WorldVersion
                    : 0,
            };
            _search = search;
            CancellationToken token = search.Cancellation.Token;
            int generation = search.Generation;
            if (capabilities.Kind == SolverSessionKind.MultiplayerAdvisor)
            {
                Entry.Logger.Info(
                    $"[CombatSolver/MultiplayerAdvisor] MP_ADVISOR_SEARCH_START " +
                    $"generation={generation} world_version={search.WorldVersion} " +
                    $"turn={search.StartTurnNumber} reason={reason}");
            }
            _combat.SearchesStarted++;
            _combat.ReplanCounts[replanCause] = _combat.ReplanCounts.GetValueOrDefault(replanCause) + 1;
            if (capabilities.Kind == SolverSessionKind.MultiplayerSafeExecute
                && reason == SearchReason.AutoTurnStart)
            {
                int? previousEndTurnRequestId = _combat.LastSafeEndTurnRequestId;
                int? previousEndTurnNumber = _combat.LastSafeEndTurnNumber;
                long? previousEndTurnWorldVersion = _combat.LastSafeEndTurnWorldVersion;
                Entry.Logger.Info(
                    $"[CombatSolver/MultiplayerSafeExecute] MP_REACTIVE_FRESH_SEARCH " +
                    $"generation={generation} route_generation={_combat.SearchesStarted} " +
                    $"world_version={search.WorldVersion} turn={search.StartTurnNumber} " +
                    $"reason={reason} fresh_probe=true fresh_capture=true " +
                    $"after_safe_end_turn={(previousEndTurnRequestId.HasValue).ToString().ToLowerInvariant()} " +
                    $"previous_end_turn_request_id={previousEndTurnRequestId?.ToString() ?? "-"} " +
                    $"previous_end_turn_turn={previousEndTurnNumber?.ToString() ?? "-"} " +
                    $"previous_end_turn_world_version={previousEndTurnWorldVersion?.ToString() ?? "-"} " +
                    "cross_turn_reuse=false");
                _combat.LastSafeEndTurnRequestId = null;
                _combat.LastSafeEndTurnNumber = null;
                _combat.LastSafeEndTurnWorldVersion = null;
            }
            if (replanCause == ReplanCause.ManualDivergence)
                MarkManualControlObserved("continuation_divergence");
            setupStage = "display_names";
            SolverDisplayNames displayNames = SolverDisplayNames.Capture(state);
            setupStage = "settings";
            SolverSettingsSnapshot settings = SolverSettings.Capture();
            SolverTheftPolicy? theftPolicy = ResolveTheftPolicy(state);
            SearchPolicySnapshot searchPolicy = CaptureSearchPolicy(
                settings,
                state,
                includeTurnSetup: false,
                theftPolicy: theftPolicy,
                interaction: search.Interaction);
            search.MaxDegreeOfParallelism = searchPolicy.MaxDegreeOfParallelism;
            search.MemoryPressureSignal = searchPolicy.MemoryPressureSignal;
            setupStage = "combat_root_snapshot";
            long rootCaptureAllocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
            CombatRootSnapshot rootSnapshot;
            try
            {
                if (capabilities.Kind == SolverSessionKind.MultiplayerAdvisor)
                {
                    Entry.Logger.Info(
                        $"[CombatSolver/MultiplayerAdvisor] MP_ADVISOR_ROOT_CAPTURE_BEGIN " +
                        $"generation={generation} world_version={search.WorldVersion} " +
                        $"turn={search.StartTurnNumber}");
                }
                rootSnapshot = CombatRootSnapshot.Capture(state);
            }
            catch (Exception ex) when (capabilities.Kind == SolverSessionKind.MultiplayerAdvisor)
            {
                Entry.Logger.Warn(
                    $"[CombatSolver/MultiplayerAdvisor] MP_ADVISOR_FAIL_CLOSED " +
                    $"stage=root_capture generation={generation} exception={ex.GetType().Name}");
                throw;
            }
            finally
            {
                SearchGcPolicy.ReportCombatLifecycleAllocation(
                    Math.Max(
                        0,
                        GC.GetTotalAllocatedBytes(precise: false) - rootCaptureAllocatedAtStart),
                    "combat_root_snapshot",
                    settings.EnableNoGcRegion);
            }
            Entry.Logger.Info(
                $"[CombatSolver/Test] COMBAT_ROOT_CAPTURE generation={generation} " +
                $"elapsed_ms={rootSnapshot.CaptureElapsedMilliseconds:F3} " +
                $"cards={rootSnapshot.CapturedCardCount} powers={rootSnapshot.CapturedPowerCount} " +
                $"listeners={rootSnapshot.CapturedHookListenerCount} " +
                $"run_mod_subscribers={rootSnapshot.CapturedRunModSubscriberCount} " +
                $"combat_mod_subscribers={rootSnapshot.CapturedCombatModSubscriberCount} " +
                $"base_lib_card_modifiers={rootSnapshot.CapturedBaseLibCardModifiers}");
            _combat.State = state;
            _combat.LatestResult = null;
            _combat.LatestStamp = null;
            LastCompletedResultForTesting = null;
            LastSearchFailureForTesting = null;
            LastFullAutoStoppedForWorseRecalculationForTesting = false;
            LastFullAutoStoppedAtLiveRiskForTesting = false;

            Player player = LocalContext.GetMe(state)!;
            int turn = player.PlayerCombatState!.TurnNumber;
            RunStatistics.Activity(state);
            SolverOverlay.ShowSearching(
                host,
                turn,
                deployWhenReady,
                _combat.ReviewedWorldlinesTotal);
            Entry.Logger.Info(
                $"[CombatSolver/Test] SEARCH_REQUEST generation={generation} reason={reason} " +
                $"cause={CauseToken(replanCause)} previous_boundary={previousBoundary?.ToString() ?? "-"} " +
                $"turn={turn} deploy_when_ready={deployWhenReady} " +
                $"theft_policy={theftPolicy?.ToString() ?? "-"} " +
                $"act_transition_boss_hp_strategy={searchPolicy.ActTransitionBossHpStrategy} " +
                $"final_boss_hp_strategy={searchPolicy.FinalBossHpStrategy} " +
                $"frame_baseline_samples={FramePressureSignal.BaselineSampleCount} " +
                $"frame_baseline_ms={FramePressureSignal.BaselineFrameGapMilliseconds:F1} " +
                $"frame_pressure_threshold_ms={FramePressureSignal.PressureFrameGapMilliseconds:F1} " +
                $"frame_recovery_enabled={FramePressureSignal.RecoveryEnabled} " +
                $"no_gc_enabled={settings.EnableNoGcRegion.ToString().ToLowerInvariant()} " +
                $"no_gc_budget_bytes={settings.NoGcRegionBudgetBytes} " +
                $"max_dop={searchPolicy.MaxDegreeOfParallelism}");
            Entry.Logger.Info(SolverDiagnostics.DescribeStart(
                state,
                settings.Profile));

            setupStage = "worker_schedule";
            SolvedRouteCache routeCache = SolvedRouteCache.Capture(state, rootSnapshot, searchPolicy, battleDamage);
            Task<SolverResult> solveTask = Task.Run(() =>
            {
                if (MultiplayerLocalCrossTurnContracts.CanUsePersistentRouteCache(searchPolicy.RoutePolicy)
                    && !searchPolicy.VerifyIncrementalSearch && !searchPolicy.MeasurePhasePerformance
                    && reason is SearchReason.AutoTurnStart or SearchReason.Deploy or SearchReason.FullAuto
                    && routeCache.Read(rootSnapshot.Forecast) is { } cached)
                {
                    token.ThrowIfCancellationRequested();
                    return cached;
                }
                Entry.Logger.Info($"[CombatSolver/Test] SEARCH_WORKER_START generation={generation} thread={System.Environment.CurrentManagedThreadId} main_thread={NGame.IsMainThread()}");
                Thread worker = Thread.CurrentThread;
                ThreadPriority previousPriority = worker.Priority;
                worker.Priority = ThreadPriority.BelowNormal;
                ISearchGcScope? gcPolicy = null;
                SolverResult? finalizedResult = null;
                try
                {
                    using ISearchGcScope admittedGcPolicy = gcPolicy = SearchGcPolicy.EnterSearchScope(
                        settings.EnableNoGcRegion,
                        settings.NoGcRegionBudgetBytes,
                        searchPolicy.MemoryPressureSignal,
                        token);
                    SolverResult result = CombatSearchCoordinator.Solve(
                        rootSnapshot,
                        displayNames,
                        battleDamage,
                        searchPolicy,
                        token,
                        progress => PublishSearchProgress(search, progress));
                    finalizedResult = search.Interaction.FinalizeWorkerResult(result);
                    token.ThrowIfCancellationRequested();
                    if (!search.Interaction.StopRequested
                        && MultiplayerLocalCrossTurnContracts.CanUsePersistentRouteCache(
                            searchPolicy.RoutePolicy))
                        routeCache.StoreFirst(finalizedResult);
                    return finalizedResult;
                }
                finally
                {
                    worker.Priority = previousPriority;
                    if (gcPolicy?.IsLifecycleCompleted == true)
                    {
                        SearchGcLifecycleSnapshot gcLifecycle = gcPolicy.Lifecycle;
                        if (finalizedResult != null)
                        {
                            finalizedResult.GcLifecycle = gcLifecycle;
                            finalizedResult.GcLifecycleAttribution = gcPolicy.LifecycleAttribution;
                        }
                        Entry.Logger.Info(
                            $"[CombatSolver/Test] SEARCH_GC_LIFECYCLE generation={generation} " +
                            $"completed={(finalizedResult != null).ToString().ToLowerInvariant()} " +
                            $"attribution={gcPolicy.LifecycleAttribution} " +
                            gcLifecycle.ToDiagnosticString());
                    }
                }
            }, token);
            search.WorkerCompletion = solveTask;
            if (!UnattendedAsyncActivityTracker.IsRequestActive)
            {
                // Preserve the production continuation path exactly. The extra lifecycle
                // ownership is needed only while a reusable unattended request is active.
                solveTask.ContinueWith(task =>
                {
                    SolverDispatcher.Post(() => CompleteSearch(host, search, task));
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                search.CallbackScheduled = true;
                return;
            }

            IDisposable? unattendedActivity = UnattendedAsyncActivityTracker.BeginActivity();
            solveTask.ContinueWith(task =>
            {
                try
                {
                    SolverDispatcher.Post(() =>
                    {
                        try
                        {
                            CompleteSearch(host, search, task);
                        }
                        finally
                        {
                            unattendedActivity?.Dispose();
                        }
                    });
                }
                catch
                {
                    unattendedActivity?.Dispose();
                    throw;
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            search.CallbackScheduled = true;
        }
        catch (Exception ex)
        {
            _combat.BugReportIssues.RecordFailure(CombatBugReportIssueKind.SearchSetupFailure, ex);
            CombatBugReportExporter.RecordRuntimeException("search_setup", ex);
            CancelSearch();
            _combat.State = null;
            _combat.LatestResult = null;
            _combat.LatestStamp = null;
            _combat.ContinuationSource = null;
            _combat.PendingCompleteProjectionBaseline = null;
            _combat.PendingManualProjectionBaseline = null;
            SolverOverlay.Show(
                host,
                FormatSearchSetupFailure(ex));
            SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Failed);
            string failure =
                $"[CombatSolver/Test] SEARCH_SETUP_FAILURE stage={setupStage} " +
                $"reason={reason} exception={ex}";
            // The asynchronous combat journal can be saturated by a large completed-route
            // record. Keep the setup stack in the native game log as a last-resort diagnostic
            // so a second-turn initialization failure remains actionable.
            GD.PrintErr(failure);
            Entry.Logger.Error(failure);
        }
    }

    private static async Task ReleaseSearchSessionAsync(SolverSearchSession? search)
    {
        if (search == null)
            return;
        try
        {
            await search.WorkerCompletion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await search.CallbackCompletion.Task.ConfigureAwait(false);
            search.WorkerCompletion = Task.CompletedTask;
        }
        finally
        {
            DisposeSearchCancellationOnce(search);
            Interlocked.Increment(ref _searchReferenceReleaseCompletedCountForTesting);
        }
    }

    private static void QueueSearchReferenceRelease(SolverSearchSession search)
    {
        if (Interlocked.Exchange(ref search.ReferenceReleaseState, 1) != 0)
            return;
        PendingSearchReferenceReleases.RemoveAll(static task => task.IsCompleted);
        Interlocked.Increment(ref _searchReferenceReleaseScheduledCountForTesting);
        PendingSearchReferenceReleases.Add(ReleaseSearchSessionAsync(search));
    }

    private static Task DrainSearchReferenceReleases()
    {
        if (PendingSearchReferenceReleases.Count == 0)
            return Task.CompletedTask;
        Task[] releases = [.. PendingSearchReferenceReleases];
        PendingSearchReferenceReleases.Clear();
        return Task.WhenAll(releases);
    }

    private static void DisposeSearchCancellationOnce(SolverSearchSession search)
    {
        if (Interlocked.Exchange(ref search.CancellationDisposeState, 1) != 0)
            return;
        search.Cancellation.Dispose();
        Interlocked.Increment(ref _searchCtsDisposeCountForTesting);
    }

    private static void PublishSearchProgress(SolverSearchSession search, SolverProgress progress)
    {
        if (ReferenceEquals(_search, search))
            search.Interaction.PublishProgress(progress);
    }

    private static void CompleteSearch(
        NGame host,
        SolverSearchSession search,
        Task<SolverResult> task)
    {
        try
        {
            CompleteSearchCore(host, search, task);
        }
        finally
        {
            search.CallbackCompletion.TrySetResult();
            DisposeSearchCancellationOnce(search);
        }
    }

    private static void CompleteSearchCore(
        NGame host,
        SolverSearchSession search,
        Task<SolverResult> task)
    {
        AssertMainThread();
        if (!ReferenceEquals(_search, search))
            return;
        _search = null;
        Volatile.Write(ref search.Interaction.Progress, null);
        int generation = search.Generation;
        Entry.Logger.Info($"[CombatSolver/Test] SEARCH_CALLBACK generation={generation} thread={System.Environment.CurrentManagedThreadId} main_thread={NGame.IsMainThread()}");
        Entry.Logger.Info(
            $"[CombatSolver/Test] MAIN_THREAD_FRAMES generation={generation} frames={search.FrameCount} " +
            $"p95_gap_ms={search.FramePercentile(0.95d):F1} p99_gap_ms={search.FramePercentile(0.99d):F1} " +
            $"max_gap_ms={search.MaxFrameGapMilliseconds:F1} over_33ms={search.FramesOver33Milliseconds} " +
            $"over_50ms={search.FramesOver50Milliseconds} over_100ms={search.FramesOver100Milliseconds}");

        if (task.IsCanceled)
        {
            _combat.PendingCompleteProjectionBaseline = null;
            _combat.PendingManualProjectionBaseline = null;
            SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Canceled);
            Entry.Logger.Info($"[CombatSolver/Test] SEARCH_CANCELED generation={generation}");
            return;
        }
        if (task.IsFaulted)
        {
            _combat.PendingCompleteProjectionBaseline = null;
            _combat.PendingManualProjectionBaseline = null;
            Exception ex = task.Exception?.GetBaseException() ?? new InvalidOperationException("后台搜索失败但没有异常对象。");
            _combat.BugReportIssues.RecordFailure(CombatBugReportIssueKind.SearchFailure, ex);
            CombatBugReportExporter.RecordRuntimeException("search", ex);
            LastSearchFailureForTesting = ex;
            if (ex is PotionPolicyUnsatisfiedException)
            {
                _combat.FullAutoEnabled = false;
                SolverOverlay.RefreshControls();
            }
            SolverOverlay.Show(
                host,
                FormatSearchFailure(ex, search.MaxDegreeOfParallelism > 1));
            SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Failed);
            if (SolverSessionCapabilities.Capture(search.State).Kind == SolverSessionKind.MultiplayerAdvisor)
            {
                Entry.Logger.Warn(
                    $"[CombatSolver/MultiplayerAdvisor] MP_ADVISOR_FAIL_CLOSED " +
                    $"stage=search generation={generation} exception={ex.GetType().Name}");
            }
            Entry.Logger.Error($"[CombatSolver/Test] SEARCH_FAILURE generation={generation} exception={ex}");
            return;
        }

        CombatState searchedState = search.State;
        LiveCombatStamp searchedStamp = search.Stamp;
        CombatState? currentState = CombatManager.Instance.DebugOnlyGetState();
        if (!ReferenceEquals(currentState, searchedState)
            || !CanSolve(searchedState, out _)
            || search.WorldVersion != 0
                && MultiplayerWorldTracker.WorldVersion != search.WorldVersion
            || LiveCombatStamp.Capture(searchedState) != searchedStamp)
        {
            _combat.BugReportIssues.Record(
                CombatBugReportIssueKind.SearchResultStale,
                $"第 {LocalContext.GetMe(searchedState)?.PlayerCombatState?.TurnNumber ?? 0} 回合");
            _combat.PendingCompleteProjectionBaseline = null;
            _combat.PendingManualProjectionBaseline = null;
            _combat.LatestResult = null;
            _combat.LatestStamp = null;
            SolverOverlay.Show(
                host,
                "[b]战斗路线求解器[/b]\n战斗状态在计算期间发生变化，已丢弃过期结果。\n" +
                SolverUiTokens.BugReportUploadInstructionRichText);
            SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Stale);
            if (SolverSessionCapabilities.Capture(searchedState).Kind == SolverSessionKind.MultiplayerAdvisor)
            {
                Entry.Logger.Info(
                    $"[CombatSolver/MultiplayerAdvisor] MP_ADVISOR_SEARCH_STALE " +
                    $"generation={generation} search_world_version={search.WorldVersion} " +
                    $"current_world_version={MultiplayerWorldTracker.WorldVersion}");
            }
            Entry.Logger.Info($"[CombatSolver/Test] SEARCH_STALE generation={generation}");
            return;
        }

        SolverResult result = task.Result;
        if (result.WasRestoredFromCache)
        {
            _combat.SearchesStarted--;
            _combat.RoutesRestored++;
            _combat.ReplanCounts[search.ReplanCause]--;
            Entry.Logger.Info($"[CombatSolver/Test] ROUTE_CACHE_HIT turn={result.StartTurnNumber} validation=exact_root");
        }
        bool stopped = search.Interaction.StopRequested;
        bool currentTurnAdopted = result.ResultScope == SolverResultScope.CurrentTurnAdoption;
        bool routeAdopted = result.ResultScope == SolverResultScope.RouteAdoption;
        if (stopped || currentTurnAdopted)
        {
            _combat.PendingCompleteProjectionBaseline = null;
            _combat.PendingManualProjectionBaseline = null;
        }
        else
        {
            ApplyProjectionBaselines(result);
        }
        ApplySearchFrameMetrics(result, search);
        RecordReviewedWorldlines(result);

        if (stopped)
        {
            result.ResultScope = SolverResultScope.RouteAdoption;
            search.Interaction.PreserveStoppedResult(result, searchedStamp);
            _combat.StoppedSearch = search.Interaction;
            SolverOverlay.ShowResult(
                host,
                SolverOverlaySnapshot.CaptureWithReviewedWorldlines(
                    result,
                    UnexpectedReplanCount > 0,
                    _combat.ReviewedWorldlinesTotal));
            SolverOverlay.ShowSearchStopped(host);
            SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Canceled);
            Entry.Logger.Info(
                $"[CombatSolver/Test] SEARCH_STOPPED_RESULT_PRESERVED generation={generation} " +
                $"actions={result.BestNode.Actions.Count}");
            return;
        }

        _combat.LatestResult = result;
        _combat.LatestStamp = searchedStamp;
        bool retainCurrentTurnRoute = currentTurnAdopted
            && MultiplayerLocalCrossTurnContracts.HasLocalCrossTurnContinuation(
                result.MultiplayerScope,
                result.Continuations.Count);
        _combat.ContinuationSource = !currentTurnAdopted || retainCurrentTurnRoute
            ? result
            : null;
        Entry.Logger.Info(
            $"[CombatSolver/Test] SEARCH_RESULT_ROUTE_CAPTURE generation={generation} " +
            $"deployment_scope={result.ResultScope} route_scope={result.MultiplayerScope} " +
            $"continuations={result.Continuations.Count} " +
            $"future_route_preserved={(_combat.ContinuationSource != null).ToString().ToLowerInvariant()} " +
            $"route_identity={_combat.ContinuationSource?.RouteIdentity ?? "-"}");
        if (UnattendedTestRunner.IsActive)
            LastCompletedResultForTesting = result;
        BattleDamageTracker.RegisterPlan(searchedState, result);
        if (!currentTurnAdopted && !routeAdopted)
            CombatShowcaseCollector.TryQueueCompletedRoute(searchedState, result);
        CombatBugReportExporter.RecordCheckpoint(
            searchedState,
            currentTurnAdopted
                ? "search_current_turn_adopted"
                : routeAdopted
                    ? "search_route_adopted"
                    : "search_completed",
            result,
            DescribeReplanAudit());
        SolverOverlaySnapshot completedSnapshot = currentTurnAdopted
            ? SolverOverlaySnapshot.CaptureCurrentTurn(SolverCurrentTurnPreview.FromResult(result))
            : SolverOverlaySnapshot.CaptureWithReviewedWorldlines(
                result,
                UnexpectedReplanCount > 0,
                _combat.ReviewedWorldlinesTotal);
        SolverOverlay.ShowResult(
            host,
            routeAdopted ? MarkRouteAdopted(completedSnapshot) : completedSnapshot);
        if (SolverSessionCapabilities.Capture(searchedState).Kind == SolverSessionKind.MultiplayerAdvisor)
        {
            Entry.Logger.Info(
                $"[CombatSolver/MultiplayerAdvisor] MP_ADVISOR_SEARCH_COMPLETE " +
                $"generation={generation} world_version={search.WorldVersion} " +
                $"turn={result.StartTurnNumber} actions={result.BestNode.Actions.Count} " +
                $"scope={result.ResultScope}");
        }
        SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Succeeded);
        if (currentTurnAdopted)
        {
            Entry.Logger.Info(
                $"[CombatSolver/Test] SEARCH_CURRENT_TURN_ADOPTED generation={generation} " +
                $"turn={result.StartTurnNumber} actions={result.BestNode.Actions.Count} " +
                $"continuations={result.Continuations.Count} " +
                $"future_route_preserved={retainCurrentTurnRoute.ToString().ToLowerInvariant()}");
        }
        else if (routeAdopted)
        {
            Entry.Logger.Info(
                $"[CombatSolver/Test] SEARCH_ROUTE_ADOPTED generation={generation} " +
                $"actions={result.BestNode.Actions.Count} continuations={result.Continuations.Count}");
        }
        Entry.Logger.Info(SolverDiagnostics.DescribeResult(result));
        SolverSessionCapabilitySet completionCapabilities = SolverSessionCapabilities.Capture(searchedState);
        bool deployWhenReady = currentTurnAdopted || search.DeployWhenReady;
        if (completionCapabilities.Kind == SolverSessionKind.MultiplayerSafeExecute)
        {
            // Safe Execute is advisor-first: an automatic current-turn result must never
            // drive a card. Only the explicit Execute button may arm this deployment.
            deployWhenReady = _combat.MultiplayerSafeExecuteDeploymentRequested;
            if (deployWhenReady)
                _combat.MultiplayerSafeExecuteDeploymentRequested = false;
        }
        if (deployWhenReady)
            StartDeployment(host, searchedState, result);
        else if (_combat.FullAutoEnabled)
            StartFullAutoDeployment(host, searchedState, result);
    }

    private static void ApplyProjectionBaselines(SolverResult result)
    {
        CompleteProjectionBaseline? recalculationBaseline = _combat.PendingCompleteProjectionBaseline;
        ManualProjectionBaseline? manualBaseline = _combat.PendingManualProjectionBaseline;
        _combat.PendingCompleteProjectionBaseline = null;
        _combat.PendingManualProjectionBaseline = null;
        if (recalculationBaseline != null
            && result.ProjectedBattleHpLost > recalculationBaseline.ProjectedBattleHpLost)
        {
            result.RecalculatedAfterCompleteProjection = true;
            result.PreviousProjectedBattleHpLost = recalculationBaseline.ProjectedBattleHpLost;
            result.RecalculationStateDifference = recalculationBaseline.StateDifference;
            Entry.Logger.Warn(
                $"[CombatSolver/Test] COMPLETE_ROUTE_RECALCULATION_WORSENED " +
                $"original_turn={recalculationBaseline.StartTurnNumber} current_turn={result.StartTurnNumber} " +
                $"previous_projected_battle_hp_lost={recalculationBaseline.ProjectedBattleHpLost} " +
                $"current_projected_battle_hp_lost={result.ProjectedBattleHpLost} " +
                $"increase={result.ProjectedBattleHpLossIncrease} {recalculationBaseline.StateDifference}");
            _combat.BugReportIssues.Record(
                CombatBugReportIssueKind.RecalculationHpLossIncreased,
                $"预计战损 {recalculationBaseline.ProjectedBattleHpLost} → {result.ProjectedBattleHpLost}，" +
                $"增加 {result.ProjectedBattleHpLossIncrease} HP");
        }
        if (manualBaseline != null)
        {
            RecordManualProjectionComparison(
                manualBaseline,
                result.StartTurnNumber,
                result.ProjectedBattleHpLost);
        }
    }

    private static void ApplySearchFrameMetrics(SolverResult result, SolverSearchSession search)
    {
        result.MainThreadFrameCount = search.FrameCount;
        result.MainThreadFramesOver33Milliseconds = search.FramesOver33Milliseconds;
        result.MaxMainThreadFrameGapMilliseconds = search.MaxFrameGapMilliseconds;
        result.P95MainThreadFrameGapMilliseconds = search.FramePercentile(0.95d);
        result.P99MainThreadFrameGapMilliseconds = search.FramePercentile(0.99d);
        result.MainThreadFramesOver50Milliseconds = search.FramesOver50Milliseconds;
        result.MainThreadFramesOver100Milliseconds = search.FramesOver100Milliseconds;
    }

    private static void DeferSearchUntilRootCaptureBarrier(
        NGame host,
        CombatState state,
        SearchReason reason,
        bool deployWhenReady,
        Task barrier)
    {
        CancelDeferredSearch();
        CancellationTokenSource cancellation = new();
        _deferredSearchCts = cancellation;
        int requestId = ++_deferredSearchId;
        int lifecycleGeneration = Volatile.Read(ref _combatLifecycleGeneration);
        Entry.Logger.Info(
            $"[CombatSolver/Test] SEARCH_ROOT_CAPTURE_DEFERRED reason={reason} " +
            $"lifecycle_epoch={lifecycleGeneration}");
        PendingDeferredSearchReleases.RemoveAll(static task => task.IsCompleted);
        Task operation = ResumeDeferredSearchAsync(
            host,
            state,
            reason,
            deployWhenReady,
            barrier,
            cancellation,
            requestId,
            lifecycleGeneration);
        PendingDeferredSearchReleases.Add(operation);
        TaskHelper.RunSafely(operation);
    }

    private static async Task ResumeDeferredSearchAsync(
        NGame host,
        CombatState state,
        SearchReason reason,
        bool deployWhenReady,
        Task barrier,
        CancellationTokenSource cancellation,
        int requestId,
        int lifecycleGeneration)
    {
        CancellationToken token = cancellation.Token;
        try
        {
            await barrier.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            TaskCompletionSource callbackCompleted = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            SolverDispatcher.Post(() =>
            {
                try
                {
                    bool ownsDeferredRequest = requestId == _deferredSearchId
                        && ReferenceEquals(_deferredSearchCts, cancellation);
                    if (token.IsCancellationRequested
                        || !ownsDeferredRequest
                        || Volatile.Read(ref _combatLifecycleGeneration) != lifecycleGeneration
                        || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state))
                    {
                        if (ownsDeferredRequest)
                            _deferredSearchCts = null;
                        return;
                    }
                    _deferredSearchCts = null;
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] SEARCH_ROOT_CAPTURE_RESUMED reason={reason} " +
                        $"lifecycle_epoch={lifecycleGeneration}");
                    RequestSearch(host, state, reason, deployWhenReady);
                }
                finally
                {
                    callbackCompleted.TrySetResult();
                }
            });
            // Even a canceled request waits for its queued callback. That callback owns the
            // final references to the old combat graph, so the combat-end barrier must not
            // declare quiescence merely because its token was canceled.
            await callbackCompleted.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Reset/newer request owns the deferred-search cancellation.
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static void CancelDeferredSearch()
    {
        CancellationTokenSource? cancellation = _deferredSearchCts;
        _deferredSearchCts = null;
        if (cancellation == null)
            return;
        _deferredSearchId++;
        cancellation.Cancel();
    }

    private static Task DrainDeferredSearchReleases()
    {
        if (PendingDeferredSearchReleases.Count == 0)
            return Task.CompletedTask;
        Task[] releases = PendingDeferredSearchReleases.ToArray();
        PendingDeferredSearchReleases.Clear();
        return Task.WhenAll(releases);
    }

    private static void CancelSearch()
    {
        SolverSearchSession? search = _search;
        _search = null;
        if (search == null)
            return;
        search.Cancellation.Cancel();
        if (!search.CallbackScheduled)
            search.CallbackCompletion.TrySetResult();
        QueueSearchReferenceRelease(search);
    }

    private static string FormatSearchFailure(
        Exception exception,
        bool parallelSearchWasEnabled)
        => exception.GetBaseException() is IncompatibleGameplayModException incompatible
           ? FormatIncompatibleModFailure(incompatible)
           : $"[color={SolverUiTokens.Palette.DangerHex}][b]{SolverText.Get("计算失败")}[/b]\n" +
           $"{EscapeRichText(exception.Message)}[/color]\n" +
           SolverUiTokens.SearchFailureInstructionRichText(parallelSearchWasEnabled);
}
