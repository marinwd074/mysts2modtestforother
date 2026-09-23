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

    private static void StartFullAutoDeployment(NGame host, CombatState state, SolverResult result)
    {
        if (!SolverSessionCapabilities.Capture(state).CanFullAuto)
        {
            _combat.FullAutoEnabled = false;
            Entry.Logger.Info("[CombatSolver/MultiplayerProbe] FULL_AUTO_DEPLOY_REJECT reason=session_capability");
            return;
        }
        if (_stopFullAutoOnWorseRecalculation
            && !result.WasReused
            && result.ProjectedBattleHpLossIncrease > 0)
        {
            _combat.BugReportIssues.Record(
                CombatBugReportIssueKind.FullAutoStoppedAfterWorseRecalculation,
                $"第 {result.StartTurnNumber} 回合，预计战损 {result.PreviousProjectedBattleHpLost} → {result.ProjectedBattleHpLost}");
            _combat.FullAutoEnabled = false;
            LastFullAutoStoppedForWorseRecalculationForTesting = true;
            SolverOverlay.RefreshControls();
            SolverOverlay.ShowFullAutoStoppedAfterWorseRecalculation(
                result.StartTurnNumber,
                result.PreviousProjectedBattleHpLost,
                result.ProjectedBattleHpLost);
            Entry.Logger.Info(
                $"[CombatSolver/Test] FULL_AUTO_STOP reason=worse_recalculation " +
                $"turn={result.StartTurnNumber} increase={result.ProjectedBattleHpLossIncrease}");
            return;
        }
        if (_stopFullAutoOnCombatEnd && result.CombatEndedTurn == result.StartTurnNumber)
        {
            _combat.FullAutoEnabled = false;
            SolverOverlay.RefreshControls();
            SolverOverlay.ShowFullAutoStoppedAtCombatEnd(result.StartTurnNumber);
            Entry.Logger.Info($"[CombatSolver/Test] FULL_AUTO_STOP reason=combat_end_turn turn={result.StartTurnNumber}");
            return;
        }
        if (_stopFullAutoOnDeathTurn && result.DeathTurn == result.StartTurnNumber)
        {
            _combat.BugReportIssues.Record(
                CombatBugReportIssueKind.FullAutoStoppedAtDeathTurn,
                $"第 {result.StartTurnNumber} 回合");
            _combat.FullAutoEnabled = false;
            SolverOverlay.RefreshControls();
            SolverOverlay.ShowFullAutoStoppedAtDeathTurn(result.StartTurnNumber);
            Entry.Logger.Info($"[CombatSolver/Test] FULL_AUTO_STOP reason=death_turn turn={result.StartTurnNumber}");
            return;
        }

        Entry.Logger.Info($"[CombatSolver/Test] FULL_AUTO_DEPLOY turn={result.StartTurnNumber}");
        StartDeployment(host, state, result);
    }

    private static void StartDeployment(NGame host, CombatState state, SolverResult result)
    {
        SolverSessionCapabilitySet capabilities = SolverSessionCapabilities.Capture(state);
        if (!capabilities.CanDeploySimpleLocalActions)
        {
            Entry.Logger.Info("[CombatSolver/MultiplayerProbe] DEPLOY_START_REJECT reason=session_capability");
            return;
        }
        bool hasCurrentTurnPlan = result.BestNode.Actions.Any(action =>
            MultiplayerLocalCrossTurnContracts.IsCurrentTurnAction(action.Turn, result.StartTurnNumber)
            && (action.IsExecutable || action.Kind == PlanActionKind.EndTurn));
        if (!hasCurrentTurnPlan)
        {
            _combat.ContinuationSource = null;
            Entry.Logger.Warn(
                $"[CombatSolver/Test] DEPLOY_REPLAN turn={result.StartTurnNumber} reason=turn_plan_exhausted");
            RequestSearch(
                host,
                state,
                SearchReason.PlanExhausted,
                deployWhenReady: !_combat.FullAutoEnabled);
            return;
        }

        _combat.LatestResult = null;
        _combat.LatestStamp = null;
        CancelDeployment();
        IReadOnlyList<PlanAction> plannedTurnActions = result.BestNode.Actions
            .Where(action => MultiplayerLocalCrossTurnContracts.IsCurrentTurnAction(
                action.Turn,
                result.StartTurnNumber))
            .ToArray();
        int safeSessionActionCapacity = capabilities.Kind == SolverSessionKind.MultiplayerSafeExecute
            ? MultiplayerSafeLocalActionClassifier.TakeBoundedDeploymentSlice(
                state,
                plannedTurnActions,
                out _).Count
            : 0;
        SolverSettingsSnapshot deploymentSettings = SolverSettings.Capture();
        SearchPolicySnapshot? safeReplayPolicy =
            capabilities.Kind == SolverSessionKind.MultiplayerSafeExecute
                ? CaptureSearchPolicy(
                    deploymentSettings,
                    state,
                    includeTurnSetup: false,
                    theftPolicy: _combat.TheftPolicy) with
                {
                    Interaction = null,
                    Diagnostics = new SearchDiagnosticsSink(
                        static _ => { },
                        static _ => { }),
                }
                : null;
        SolverDeploymentSession deployment = new()
        {
            State = state,
            StartTurnNumber = result.StartTurnNumber,
            WorldVersion = capabilities.IsMultiplayer
                ? MultiplayerWorldTracker.WorldVersion
                : 0,
            RouteGeneration = _combat.SearchesStarted,
            CombatLifecycleGeneration = Volatile.Read(ref _combatLifecycleGeneration),
            SafeExecutionSession = capabilities.Kind == SolverSessionKind.MultiplayerSafeExecute
                ? new MultiplayerSafeExecutionSession(
                    result.StartTurnNumber,
                     _combat.SearchesStarted,
                     capabilities.IsMultiplayer ? MultiplayerWorldTracker.WorldVersion : 0,
                     safeSessionActionCapacity)
                : null,
            SafeReplayPolicy = safeReplayPolicy,
        };
        _deployment = deployment;
        int actionCount;
        PlanAction? safeEndTurnAction = null;
        if (capabilities.Kind == SolverSessionKind.MultiplayerSafeExecute)
        {
            IReadOnlyList<PlanAction> safeActions =
                MultiplayerSafeLocalActionClassifier.TakeBoundedDeploymentSlice(
                    state,
                    plannedTurnActions,
                    out SafeLocalActionDecision stop);
            safeEndTurnAction = FindSafeEndTurnAction(plannedTurnActions, safeActions.Count);
            if (safeActions.Count == 0 && plannedTurnActions.Count > 0 && safeEndTurnAction == null)
            {
                _combat.MultiplayerSafeExecuteDeploymentRequested = false;
                StopMultiplayerSafeAutoAtUnsupportedBoundary(stop, result.StartTurnNumber);
                deployment.SafeExecutionSession?.Abort(stop.Reason);
                Entry.Logger.Info(
                    $"[CombatSolver/MultiplayerSafeExecute] MP2B_DEPLOY_STOP " +
                    $"turn={result.StartTurnNumber} reason={stop.Reason} " +
                    DescribeSafeExecutionStopAction(plannedTurnActions, safeActions.Count));
                CompleteDeployment(deployment);
                return;
            }
            actionCount = safeActions.Count;
        }
        else
        {
            actionCount = plannedTurnActions.Count(action => action.IsExecutable);
        }
        deployment.SafeEndTurnAction = safeEndTurnAction;
        SolverOverlay.ShowDeploying(
            host,
            result.StartTurnNumber,
            actionCount,
            willEndTurn: safeEndTurnAction != null
                || (capabilities.Kind != SolverSessionKind.MultiplayerSafeExecute
                    && plannedTurnActions.Any(action => action.Kind == PlanActionKind.EndTurn)));
        Task deploymentTask = DeployCurrentTurn(
            host,
            state,
            result,
            deploymentSettings,
            deployment,
            deployment.Cancellation.Token);
        deployment.Operation = deploymentTask;
        if (UnattendedAsyncActivityTracker.IsRequestActive)
            deploymentTask = UnattendedAsyncActivityTracker.Track(deploymentTask);
        TaskHelper.RunSafely(deploymentTask);
    }

    private static async Task DeployCurrentTurn(
        NGame host,
        CombatState state,
        SolverResult result,
        SolverSettingsSnapshot deploymentSettings,
        SolverDeploymentSession deployment,
        CancellationToken token)
    {
        AssertMainThread();
        bool measureDeploymentTiming = UnattendedTestRunner.IsActive;
        long deploymentStartedAt = measureDeploymentTiming
            ? Stopwatch.GetTimestamp()
            : 0;
        int turn = result.StartTurnNumber;
        SolverSessionCapabilitySet capabilities = SolverSessionCapabilities.Capture(state);
        bool safeExecute = capabilities.Kind == SolverSessionKind.MultiplayerSafeExecute;
        IReadOnlyList<PlanAction> plannedTurnActions = result.BestNode.Actions
            .Where(action => MultiplayerLocalCrossTurnContracts.IsCurrentTurnAction(
                action.Turn,
                turn))
            .ToArray();
        List<PlanAction> actions;
        SafeLocalActionDecision safeStop = SafeLocalActionDecision.Allow;
        MultiplayerSafeExecutionSession? safeSession = deployment.SafeExecutionSession;
        if (safeExecute)
        {
            actions = [.. MultiplayerSafeLocalActionClassifier.TakeBoundedDeploymentSlice(
                state,
                plannedTurnActions,
                out safeStop)];
            if (!safeStop.IsSafe)
            {
                Entry.Logger.Info(
                    $"[CombatSolver/MultiplayerSafeExecute] DEPLOY_PREFIX_STOP " +
                    $"turn={turn} reason={safeStop.Reason} " +
                    DescribeSafeExecutionStopAction(plannedTurnActions, actions.Count));
            }
        }
        else
        {
            actions = plannedTurnActions.Where(action => action.IsExecutable).ToList();
        }
        PlanAction? plannedEndTurn = safeExecute
            ? deployment.SafeEndTurnAction
            : !capabilities.CanEndTurnAutomatically
                ? null
                : plannedTurnActions.FirstOrDefault(action => action.Kind == PlanActionKind.EndTurn);
        MultiplayerSafeExecutionBoundary? lastAcceptedBoundary = null;
        FastModeType originalFastMode = SaveManager.Instance.PrefsSave.FastMode;
        SolverDeploymentFastMode allowedFastMode = capabilities.CanUseFastDeployment
            ? deploymentSettings.DeploymentFastMode
            : SolverDeploymentFastMode.FollowGame;
        FastModeType? overrideFastMode = ResolveDeploymentFastMode(allowedFastMode);
        try
        {
            if (overrideFastMode is { } requestedFastMode)
                SaveManager.Instance.PrefsSave.FastMode = requestedFastMode;
            Entry.Logger.Info(
                $"[CombatSolver/Test] DEPLOY_START turn={turn} action_count={actions.Count} " +
                $"fast_mode={allowedFastMode} " +
                $"inter_action_delay_seconds={deploymentSettings.DeploymentInterActionDelaySeconds:0.###}");
            if (safeExecute)
            {
                Entry.Logger.Info(
                    $"[CombatSolver/MultiplayerSafeExecute] MP2B_DEPLOY_START turn={turn} " +
                    $"request_id={safeSession?.RequestId ?? 0} route_generation={deployment.RouteGeneration} " +
                    $"route_identity={result.RouteIdentity} new_authorization=true " +
                    $"action_count={actions.Count} max_actions={safeSession?.MaxActions ?? 0} " +
                    $"search_world_version={deployment.WorldVersion} stop_reason={safeStop.Reason}");
            }
            for (int actionIndex = 0; actionIndex < actions.Count; actionIndex++)
            {
                PlanAction action = actions[actionIndex];
                token.ThrowIfCancellationRequested();
                if (safeExecute && safeSession is null)
                    throw new InvalidOperationException("多人 Safe Execute 缺少活动执行会话。");
                if (safeExecute
                    && !IsCurrentCombatLifecycle(state, deployment.CombatLifecycleGeneration))
                {
                    AbortSafeExecution(
                        host,
                        deployment,
                        turn,
                        actionIndex,
                        "combat_lifecycle_changed");
                    return;
                }
                if (safeExecute
                    && _combat.SearchesStarted != safeSession!.RouteGeneration)
                {
                    AbortSafeExecution(host, deployment, turn, actionIndex, "route_generation_changed");
                    return;
                }
                if (!IsSamePlayableTurn(state, turn))
                    throw new InvalidOperationException("部署途中已不再是原玩家回合。");

                Player player = LocalContext.GetMe(state)!;
                Creature? target = state.GetCreature(action.TargetCombatId);
                if (safeExecute)
                {
                    bool preActionChanged = MultiplayerClientProbe.ObserveActionBoundary(
                        state,
                        "safe_execute_pre_action");
                    Entry.Logger.Info(
                        $"[CombatSolver/MultiplayerSafeExecute] U1_PRE_ACTION_PROBE " +
                        $"request_id={safeSession!.RequestId} turn={turn} action_index={actionIndex} " +
                        $"changed={preActionChanged.ToString().ToLowerInvariant()} " +
                        $"world_version={MultiplayerWorldTracker.WorldVersion} " +
                        $"last_accepted_world_version={safeSession.LastAcceptedWorldVersion}");
                }
                MultiplayerSafeExecutionBoundary? beforeBoundary = safeExecute
                    ? MultiplayerClientProbe.CaptureSafeExecutionBoundary(state)
                    : null;
                CardModel? playedCard = null;
                GameAction? capturedAction = null;
                int energyBefore = player.PlayerCombatState?.Energy ?? 0;
                int starsBefore = player.PlayerCombatState?.Stars ?? 0;
                SafeLocalActionDecision liveSafety = safeExecute
                    ? MultiplayerSafeLocalActionClassifier.Classify(state, action)
                    : SafeLocalActionDecision.Allow;
                if (safeExecute && !liveSafety.IsSafe)
                {
                    Entry.Logger.Warn(
                        $"[CombatSolver/MultiplayerSafeExecute] MP2B_LIVE_GATE_STOP " +
                        $"turn={turn} reason={liveSafety.Reason} " +
                        DescribeSafeExecutionStopAction(actions, actionIndex));
                    AbortSafeExecution(
                        host,
                        deployment,
                        turn,
                        actionIndex,
                        liveSafety.Reason);
                    return;
                }
                if (safeExecute
                    && !safeSession!.TryBeginAction(
                         actionIndex,
                         DescribeSafeExecutionAction(action),
                         turn,
                         deployment.RouteGeneration,
                         beforeBoundary!.WorldVersion,
                         out string sessionStartReason))
                {
                    AbortSafeExecution(host, deployment, turn, actionIndex, sessionStartReason);
                    return;
                }

                SafeExecutionExpectedPostAction? expectedPostAction = null;
                if (safeExecute)
                {
                    try
                    {
                        expectedPostAction =
                            await CaptureExpectedSafeExecutionPostActionAsync(
                                state,
                                deployment,
                                action,
                                token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Entry.Logger.Warn(
                            $"[CombatSolver/MultiplayerSafeExecute] U1_EXPECTED_POST_STATE_UNAVAILABLE " +
                            $"request_id={safeSession!.RequestId} turn={turn} action_index={actionIndex} " +
                            $"card={action.CardId ?? "-"} exception={ex.GetType().Name} message={ex.Message}");
                        StopMultiplayerSafeAutoAtUnsupportedBoundary(
                            new SafeLocalActionDecision(false, "expected_post_state_unavailable"),
                            turn);
                        AbortSafeExecution(
                            host,
                            deployment,
                            turn,
                            actionIndex,
                            "expected_post_state_unavailable");
                        return;
                    }
                }

                string actionTitle = action.Kind == PlanActionKind.UsePotion
                    ? SolverUiModelNames.Potion(action.PotionId, action.PotionTitle)
                    : SolverUiModelNames.Card(action.CardId, action.CardUpgradeLevel, action.CardTitle);
                SolverOverlay.ShowDeploymentStep(actionIndex, actions.Count, actionTitle);
                List<PlanCardChoice> actionChoices = [.. action.GetActionChoicesInExecutionOrder()];
                // A card can advance the turn directly or through a nested auto-play, so its
                // next-turn choices belong to this native UI session.
                if (action.EndsPlayerTurn && action.TurnStartChoices is { Count: > 0 })
                {
                    // Keep the session open through the enemy turn so Knowledge Demon's
                    // Choose A Card page is driven by the same planned sequence.
                    actionChoices.AddRange(action.TurnStartChoices);
                }
                if (actionChoices.Count > 0)
                {
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] DEPLOY_CHOICE_PLAN turn={turn} card={action.CardId} " +
                        $"count={actionChoices.Count} sources={string.Join(',', actionChoices.Select(choice =>
                            string.IsNullOrEmpty(choice.SourceId) ? choice.Effect.ToString() : choice.SourceId))} " +
                        $"cards={string.Join(';', actionChoices.Select(choice =>
                            $"{choice.Effect}:{string.Join(',', choice.Cards.Select(card =>
                                $"{card.CardId}+{card.UpgradeLevel}#src{card.SourceOccurrence}/opt{card.OptionOccurrence}"))}"))}");
                }
                // Local choices are deployed through the same native choice driver
                // in singleplayer and Multiplayer Safe Execute. Multiplayer-specific safety is
                // enforced by local ownership/target checks and post-action revalidation, not by
                // rejecting already planned local choices.
                using NativeChoiceSession? choiceSession =
                    actionChoices.Count == 0
                        ? null
                        : NativeChoiceRuntime.Begin(
                            state,
                            player,
                            $"deployment:{turn}:{actionIndex}:{action.CardId ?? action.PotionId}");
                choiceSession?.SetPlanAndStartDriving(host, actionChoices, token);
                long actionStartedAt = measureDeploymentTiming
                    ? Stopwatch.GetTimestamp()
                    : 0;
                Task actionCompletion;
                if (action.Kind == PlanActionKind.UsePotion)
                {
                    PotionModel? potion = player.GetPotionAtSlotIndex(action.PotionSlot);
                    if (potion is null)
                    {
                        if (safeExecute)
                        {
                            AbortSafeExecution(host, deployment, turn, actionIndex, "potion_missing");
                            return;
                        }
                        Entry.Logger.Warn(
                            $"[CombatSolver/Test] DEPLOY_REPLAN turn={turn} reason=potion_missing " +
                            $"potion={action.PotionId} slot={action.PotionSlot}");
                        _combat.ContinuationSource = null;
                        CompleteDeployment(deployment);
                        RequestSearch(
                            host,
                            state,
                            SearchReason.DeploymentDrift,
                            deployWhenReady: !_combat.FullAutoEnabled);
                        return;
                    }
                    if (!string.Equals(potion.Id.Entry, action.PotionId, StringComparison.Ordinal))
                    {
                        if (safeExecute)
                        {
                            AbortSafeExecution(host, deployment, turn, actionIndex, "potion_mismatch");
                            return;
                        }
                        Entry.Logger.Warn(
                            $"[CombatSolver/Test] DEPLOY_REPLAN turn={turn} reason=potion_mismatch " +
                            $"slot={action.PotionSlot} actual={potion.Id.Entry} expected={action.PotionId}");
                        _combat.ContinuationSource = null;
                        CompleteDeployment(deployment);
                        RequestSearch(
                            host,
                            state,
                            SearchReason.DeploymentDrift,
                            deployWhenReady: !_combat.FullAutoEnabled);
                        return;
                    }
                    GameAction queuedAction = await EnqueueAndCaptureActionAsync(
                        candidate => candidate is UsePotionAction usePotion
                            && ReferenceEquals(usePotion.Player, player)
                            && usePotion.PotionIndex == (uint)action.PotionSlot,
                        () => potion.EnqueueManualUse(target),
                        token);
                    actionCompletion = queuedAction.CompletionTask;
                    LastDeployedActionStartedAtMillisecondsForTesting = System.Environment.TickCount64;
                    DeployedPotionIdsForTesting.Add(action.PotionId);
                    Entry.Logger.Info($"[CombatSolver/Test] DEPLOY_ACTION turn={turn} potion={action.PotionId} slot={action.PotionSlot} target_index={action.TargetIndex} target_combat_id={action.TargetCombatId?.ToString() ?? "-"}");
                }
                else
                {
                    List<CardModel> hand = player.PlayerCombatState!.Hand.Cards.ToList();
                    CardModel card = FindCardForDeployment(hand, action);
                    playedCard = card;
                    if (!card.CanPlayTargeting(target))
                    {
                        bool targetValid = card.IsValidTarget(target);
                        bool cardPlayable = card.CanPlay(out UnplayableReason reason, out AbstractModel? preventer);
                        if (safeExecute)
                        {
                            AbortSafeExecution(host, deployment, turn, actionIndex, "card_unplayable");
                            return;
                        }
                        Entry.Logger.Warn(
                            $"[CombatSolver/Test] DEPLOY_REPLAN turn={turn} reason=card_unplayable " +
                            $"card={action.CardId} occurrence={action.CardOccurrence} target_valid={targetValid} " +
                            $"can_play={cardPlayable} unplayable_reason={reason} " +
                            $"preventer={preventer?.Id.Entry ?? "-"} energy={player.PlayerCombatState.Energy} " +
                            $"stars={player.PlayerCombatState.Stars} energy_cost={card.EnergyCost.GetAmountToSpend()} " +
                            $"star_cost={card.GetStarCostWithModifiers()}");
                        _combat.ContinuationSource = null;
                        CompleteDeployment(deployment);
                        RequestSearch(
                            host,
                            state,
                            SearchReason.DeploymentDrift,
                            deployWhenReady: !_combat.FullAutoEnabled);
                        return;
                    }
                    GameAction queuedAction = await EnqueueAndCaptureActionAsync(
                        candidate => candidate is PlayCardAction playCard
                            && ReferenceEquals(playCard.NetCombatCard.ToCardModelOrNull(), card),
                        () =>
                        {
                            if (!card.TryManualPlay(target))
                                throw new InvalidOperationException($"部署卡牌 {action.CardId} 在入队时失去可用状态。");
                        },
                        token);
                    capturedAction = queuedAction;
                    actionCompletion = queuedAction.CompletionTask;
                    LastDeployedActionStartedAtMillisecondsForTesting = System.Environment.TickCount64;
                    DeployedCardIdsForTesting.Add(card.Id.Entry);
                    Entry.Logger.Info($"[CombatSolver/Test] DEPLOY_ACTION turn={turn} card={action.CardId} target_index={action.TargetIndex} target_combat_id={action.TargetCombatId?.ToString() ?? "-"} choice={action.Choice?.Effect.ToString() ?? "-"}");
                    if (safeExecute)
                    {
                        Entry.Logger.Info(
                            $"[CombatSolver/MultiplayerSafeExecute] NATIVE_ACTION_CAPTURED " +
                            $"timestamp_ms={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} " +
                            $"request_id={safeSession?.RequestId ?? 0} action_index={actionIndex} " +
                            $"type={queuedAction.GetType().Name} turn={turn} card={action.CardId} " +
                            $"local_net_id={player.NetId} custom_network_api_used=false");
                    }
                }
                try
                {
                    if (choiceSession is { } activeChoiceSession)
                        await activeChoiceSession.AwaitProducerAndCompleteAsync(actionCompletion);
                    else
                        await actionCompletion;
                    // The root action can complete before nested card/potion actions settle;
                    // deploy the next planned action only after the native queue is idle.
                    await RunManager.Instance.ActionExecutor.FinishedExecutingActions().WaitAsync(token);
                    RunStatistics.Activity(state, execution: true, auto: _combat.FullAutoEnabled);
                    if (safeExecute && !safeSession!.MarkAwaitingWorldUpdate())
                        throw new InvalidOperationException("多人 Safe Execute 动作完成时会话状态不一致。");
                }
                catch (NativeChoicePlanMismatchException)
                {
                    choiceSession?.ReleaseVisibleSurface();
                    throw;
                }
                catch (NativeChoiceSurfaceMismatchException)
                {
                    choiceSession?.ReleaseVisibleSurface();
                    throw;
                }
                if (measureDeploymentTiming)
                {
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] DEPLOY_ACTION_COMPLETE turn={turn} action_index={actionIndex} " +
                        $"action={action.CardId ?? action.PotionId ?? action.Kind.ToString()} " +
                        $"elapsed_ms={Stopwatch.GetElapsedTime(actionStartedAt).TotalMilliseconds:F1}");
                }
                if (safeExecute)
                {
                    MultiplayerSafeExecutionBoundary? afterBoundary =
                        await WaitForStableSafeExecutionWorldAsync(
                            host,
                            state,
                            beforeBoundary!,
                            token);
                    if (afterBoundary is null)
                    {
                        AbortSafeExecution(host, deployment, turn, actionIndex, "world_unstable");
                        return;
                    }
                    if (!safeSession!.BeginRevalidation())
                    {
                        AbortSafeExecution(host, deployment, turn, actionIndex, "session_not_revalidating");
                        return;
                    }

                    bool hasNextAction = actionIndex + 1 < actions.Count;
                    MultiplayerSafeActionRevalidationFacts facts =
                        BuildSafeActionRevalidationFacts(
                            beforeBoundary!,
                            afterBoundary,
                            capturedAction,
                            playedCard,
                            player,
                            energyBefore,
                            starsBefore,
                            action,
                            target,
                            state,
                            hasNextAction);
                    MultiplayerSafeActionRevalidationDecision decision =
                        MultiplayerSafeExecutePolicy.RevalidateAction(facts);
                    string decisionReason = MultiplayerSafeExecutePolicy.RevalidationReason(decision);
                    Entry.Logger.Info(
                        $"[CombatSolver/MultiplayerSafeExecute] MP2B_ACTION_RECONCILED " +
                        $"request_id={safeSession!.RequestId} action_index={actionIndex} " +
                        $"card={action.CardId} decision={decision} reason={decisionReason} " +
                        $"before_world_version={beforeBoundary!.WorldVersion} " +
                        $"after_world_version={afterBoundary.WorldVersion} " +
                        $"observation_sequence={afterBoundary.ObservationSequence}");
                    if (decision is not MultiplayerSafeActionRevalidationDecision.SafeToContinue
                        and not MultiplayerSafeActionRevalidationDecision.ExpectedLocalChange)
                    {
                        LogSafeActionRevalidationDiagnostic(
                            deployment,
                            safeSession,
                            turn,
                            actionIndex,
                            action,
                            facts,
                            beforeBoundary,
                            afterBoundary,
                            decision,
                            decisionReason);
                        AbortSafeExecution(host, deployment, turn, actionIndex, decisionReason);
                        return;
                    }
                    if (!safeSession.AcceptAction(afterBoundary.WorldVersion, hasNextAction))
                    {
                        AbortSafeExecution(host, deployment, turn, actionIndex, "session_accept_failed");
                        return;
                    }
                    if (!MultiplayerWorldTracker.TryConfirmStable(afterBoundary.WorldVersion))
                    {
                        AbortSafeExecution(host, deployment, turn, actionIndex, "world_observation_pending");
                        return;
                    }
                    lastAcceptedBoundary = afterBoundary;
                    if ((!hasNextAction || safeSession.State == MultiplayerSafeExecutionState.Completed)
                        && plannedEndTurn == null)
                    {
                        CompleteDeployment(deployment);
                        SolverOverlay.ShowDeploymentComplete(
                            host,
                            turn,
                            actionIndex + 1,
                            endedTurn: false);
                        _combat.LastSolverDeployedTurn = turn;
                        StopMultiplayerSafeAutoAtUnsupportedBoundary(safeStop, turn);
                        Entry.Logger.Info(
                            $"[CombatSolver/MultiplayerSafeExecute] MP2B_DEPLOY_END " +
                            $"request_id={safeSession.RequestId} turn={turn} " +
                            $"action_count={actionIndex + 1} end_turn=false stop_reason={safeStop.Reason} " +
                            $"search_world_version={deployment.WorldVersion} " +
                            $"last_accepted_world_version={safeSession.LastAcceptedWorldVersion} " +
                            $"automatic_end_turn=false custom_network_api_used=false");
                        return;
                    }
                }
                {
                    PlayerCombatState liveState = player.PlayerCombatState!;
                    Entry.Logger.Info(
                        $"[CombatSolver/Debug] DEPLOY_STATE turn={turn} action_index={actionIndex} " +
                        $"action={action.CardId ?? action.PotionId ?? action.Kind.ToString()} " +
                        $"target={action.TargetCombatId} hp={player.Creature.CurrentHp} block={player.Creature.Block} " +
                        $"energy={liveState.Energy} hand={string.Join(',', liveState.Hand.Cards.Select(card => card.Id.Entry))} " +
                        $"draw={string.Join(',', liveState.DrawPile.Cards.Select(card => card.Id.Entry))} " +
                        $"discard={string.Join(',', liveState.DiscardPile.Cards.Select(card => card.Id.Entry))} " +
                        $"exhaust={string.Join(',', liveState.ExhaustPile.Cards.Select(card => card.Id.Entry))} " +
                        $"enemies={string.Join(',', state.Enemies.Select(enemy =>
                            $"{enemy.Monster?.Id.Entry ?? "null"}:{enemy.CurrentHp}/{enemy.Block}"))} " +
                        $"powers={string.Join(',', player.Creature.Powers.Select(power =>
                            $"{power.Id.Entry}:{power.Amount}/{power.AmountOnTurnStart}"))}");
                }
                SolverOverlay.ShowDeploymentStep(actionIndex + 1, actions.Count, null);
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                if (actionIndex + 1 < actions.Count
                    && deploymentSettings.DeploymentInterActionDelaySeconds > 0d)
                {
                    await WaitForDeploymentDelayAsync(
                        host,
                        deploymentSettings.DeploymentInterActionDelaySeconds,
                        token);
                }
            }

            if (safeExecute)
            {
                if (plannedEndTurn != null)
                {
                    await ExecuteSafeEndTurnAsync(
                        host,
                        state,
                        deployment,
                        safeSession!,
                        plannedEndTurn,
                        turn,
                        actions.Count,
                        lastAcceptedBoundary,
                        token);
                    return;
                }
                if (CombatManager.Instance.IsInProgress && IsSamePlayableTurn(state, turn))
                {
                    CompleteDeployment(deployment);
                    SolverOverlay.ShowDeploymentComplete(host, turn, actions.Count, endedTurn: false);
                    _combat.LastSolverDeployedTurn = turn;
                    StopMultiplayerSafeAutoAtUnsupportedBoundary(safeStop, turn);
                    Entry.Logger.Info(
                        $"[CombatSolver/MultiplayerSafeExecute] MP2B_DEPLOY_END " +
                        $"request_id={safeSession?.RequestId ?? 0} turn={turn} " +
                        $"action_count={actions.Count} end_turn=false stop_reason={safeStop.Reason} " +
                        $"search_world_version={deployment.WorldVersion} " +
                        $"last_accepted_world_version={safeSession?.LastAcceptedWorldVersion ?? 0} " +
                        "automatic_end_turn=false custom_network_api_used=false");
                }
                else
                {
                    SolverOverlay.ShowDeploymentComplete(host, turn, actions.Count, endedTurn: false);
                    _combat.LastSolverDeployedTurn = turn;
                    Entry.Logger.Info(
                        $"[CombatSolver/MultiplayerSafeExecute] MP2B_DEPLOY_END " +
                        $"request_id={safeSession?.RequestId ?? 0} turn={turn} " +
                        $"action_count={actions.Count} end_turn=false " +
                        "combat_or_turn_finished=true automatic_end_turn=false custom_network_api_used=false");
                }
                return;
            }

            if (CombatManager.Instance.IsInProgress && IsSamePlayableTurn(state, turn))
            {
                if (plannedEndTurn == null)
                {
                    Entry.Logger.Warn(
                        $"[CombatSolver/Test] DEPLOY_REPLAN turn={turn} reason=turn_plan_exhausted " +
                        $"executed_actions={actions.Count}");
                    _combat.ContinuationSource = null;
                    CompleteDeployment(deployment);
                    RequestSearch(
                        host,
                        state,
                        SearchReason.PlanExhausted,
                        deployWhenReady: !_combat.FullAutoEnabled);
                    return;
                }
                Player player = LocalContext.GetMe(state)!;
                if (_combat.FullAutoEnabled
                    && (_stopFullAutoOnDeathTurn || _stopFullAutoOnWorseRecalculation))
                {
                    await UnattendedTestRunner.ApplyScheduledPreEndTurnDriftAsync(state, turn);
                    LiveEndTurnRiskProjection liveRisk = LiveEndTurnRiskEvaluator.Evaluate(
                        state,
                        plannedEndTurn.TurnStartChoices);
                    int plannedHpLoss = result.HpLostByTurn.GetValueOrDefault(turn);
                    bool worsened = liveRisk.HpLost > plannedHpLoss;
                    if ((_stopFullAutoOnDeathTurn && liveRisk.PlayerDead)
                        || (_stopFullAutoOnWorseRecalculation && worsened))
                    {
                        _combat.BugReportIssues.Record(
                            liveRisk.PlayerDead
                                ? CombatBugReportIssueKind.FullAutoStoppedAtLiveRiskDeath
                                : CombatBugReportIssueKind.FullAutoStoppedAtLiveRiskWorsening,
                            $"第 {turn} 回合，路线预计 {plannedHpLoss} HP，实机复核 {liveRisk.HpLost} HP");
                        _combat.FullAutoEnabled = false;
                        LastFullAutoStoppedAtLiveRiskForTesting = true;
                        _combat.ContinuationSource = null;
                        SolverOverlay.RefreshControls();
                        SolverOverlay.ShowFullAutoStoppedAtLiveRisk(
                            turn,
                            plannedHpLoss,
                            liveRisk.HpLost,
                            liveRisk.PlayerDead);
                        Entry.Logger.Warn(
                            $"[CombatSolver/Test] FULL_AUTO_STOP reason=live_end_turn_risk turn={turn} " +
                            $"planned_hp_lost={plannedHpLoss} live_hp_lost={liveRisk.HpLost} " +
                            $"hp_before={liveRisk.HpBefore} hp_after={liveRisk.HpAfter} " +
                            $"player_dead={liveRisk.PlayerDead} moves={liveRisk.MonsterMoves}");
                        return;
                    }
                }
                _combat.LastSolverDeployedTurn = turn;
                SolverOverlay.ShowEndTurnDeploymentStep();
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                token.ThrowIfCancellationRequested();
                PlanCardChoice[] endTurnChoices = plannedEndTurn.TurnStartChoices?
                    .Where(choice => choice.Timing is PlanChoiceTiming.PlayerTurnEnd or PlanChoiceTiming.EnemyTurn)
                    .ToArray() ?? [];
                if (endTurnChoices.Length > 0)
                {
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] DEPLOY_END_TURN_CHOICE_PLAN turn={turn} " +
                        $"count={endTurnChoices.Length} sources={string.Join(',', endTurnChoices.Select(choice => choice.SourceId))}");
                    using NativeChoiceSession choiceSession = NativeChoiceRuntime.Begin(
                        state,
                        player,
                        $"deployment_end_turn:{turn}");
                    choiceSession.SetPlanAndStartDriving(host, endTurnChoices, token);
                    CombatManager.Instance.OnEndedTurnLocally();
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
                    try
                    {
                        await choiceSession.WaitForAllPlansConsumedAsync(token);
                    }
                    catch (NativeChoicePlanMismatchException)
                    {
                        choiceSession.ReleaseVisibleSurface();
                        throw;
                    }
                    catch (NativeChoiceSurfaceMismatchException)
                    {
                        choiceSession.ReleaseVisibleSurface();
                        throw;
                    }
                    await choiceSession.CompleteAndDetachAsync();
                }
                else
                {
                    CombatManager.Instance.OnEndedTurnLocally();
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
                }
                SolverOverlay.ShowDeploymentComplete(host, turn, actions.Count, endedTurn: true);
                string deploymentEndMessage =
                    $"[CombatSolver/Test] DEPLOY_END turn={turn} action_count={actions.Count} end_turn=true " +
                    $"forecast_turn_start_choices={plannedEndTurn.TurnStartChoices?.Count ?? 0} " +
                    $"end_turn_choices={endTurnChoices.Length}";
                if (measureDeploymentTiming)
                {
                    deploymentEndMessage +=
                        $" elapsed_ms={Stopwatch.GetElapsedTime(deploymentStartedAt).TotalMilliseconds:F1}";
                }
                Entry.Logger.Info(deploymentEndMessage);
                _combat.LastSolverDeployedTurn = turn;
            }
            else
            {
                SolverOverlay.ShowDeploymentComplete(host, turn, actions.Count, endedTurn: false);
                string deploymentEndMessage =
                    $"[CombatSolver/Test] DEPLOY_END turn={turn} action_count={actions.Count} " +
                    $"end_turn=false combat_or_turn_finished=true";
                if (measureDeploymentTiming)
                {
                    deploymentEndMessage +=
                        $" elapsed_ms={Stopwatch.GetElapsedTime(deploymentStartedAt).TotalMilliseconds:F1}";
                }
                Entry.Logger.Info(deploymentEndMessage);
                _combat.LastSolverDeployedTurn = turn;
            }
        }
        catch (OperationCanceledException)
        {
            Entry.Logger.Info($"[CombatSolver/Test] DEPLOY_CANCELED turn={turn}");
        }
        catch (InvalidOperationException ex) when (IsMissingDeploymentCard(ex))
        {
            if (safeExecute)
            {
                AbortSafeExecution(host, deployment, turn, safeSession?.CompletedActions ?? 0, "card_missing");
                return;
            }
            _combat.ContinuationSource = null;
            CompleteDeployment(deployment);
            Entry.Logger.Warn(
                $"[CombatSolver/Test] DEPLOY_REPLAN turn={turn} reason=card_missing " +
                $"message={ex.Message}");
            RequestSearch(
                host,
                state,
                SearchReason.DeploymentDrift,
                deployWhenReady: !_combat.FullAutoEnabled);
        }
        catch (InvalidOperationException ex) when (IsDeploymentTurnDrift(ex))
        {
            if (safeExecute)
            {
                AbortSafeExecution(host, deployment, turn, safeSession?.CompletedActions ?? 0, "turn_drift");
                return;
            }
            _combat.ContinuationSource = null;
            CompleteDeployment(deployment);
            Entry.Logger.Warn(
                $"[CombatSolver/Test] DEPLOY_REPLAN turn={turn} reason=turn_drift " +
                $"message={ex.Message}");
            RequestSearch(
                host,
                state,
                SearchReason.DeploymentDrift,
                deployWhenReady: !_combat.FullAutoEnabled);
        }
        catch (NativeChoicePlanMismatchException ex)
        {
            PauseAfterNativeChoiceFailure(host, deployment, turn, ex);
        }
        catch (NativeChoiceSurfaceMismatchException ex)
        {
            PauseAfterNativeChoiceFailure(host, deployment, turn, ex);
        }
        catch (Exception ex)
        {
            if (safeExecute)
            {
                AbortSafeExecution(host, deployment, turn, safeSession?.CompletedActions ?? 0, "deployment_exception");
                Entry.Logger.Error($"[CombatSolver/MultiplayerSafeExecute] MP2B_DEPLOY_EXCEPTION turn={turn} exception={ex}");
                return;
            }
            _combat.BugReportIssues.RecordFailure(CombatBugReportIssueKind.DeploymentFailure, ex);
            CombatBugReportExporter.RecordRuntimeException("deployment", ex);
            SolverOverlay.Show(host, FormatDeploymentFailure(ex));
            Entry.Logger.Error($"[CombatSolver/Test] DEPLOY_FAILURE turn={turn} exception={ex}");
        }
        finally
        {
            try
            {
                if (overrideFastMode.HasValue)
                {
                    SaveManager.Instance.PrefsSave.FastMode = originalFastMode;
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] DEPLOY_SPEED_RESTORED turn={turn} restored={originalFastMode}");
                }
                if (measureDeploymentTiming)
                {
                    Entry.Logger.Info(
                        $"[CombatSolver/Test] DEPLOY_FINISH turn={turn} " +
                        $"elapsed_ms={Stopwatch.GetElapsedTime(deploymentStartedAt).TotalMilliseconds:F1}");
                }
            }
            finally
            {
                if (safeSession is { State: not MultiplayerSafeExecutionState.Completed
                    and not MultiplayerSafeExecutionState.Aborted })
                {
                    safeSession.Abort("deployment_finally");
                }
                CompleteDeployment(deployment);
                DisposeDeploymentCancellationOnce(deployment);
                SolverOverlay.RefreshControls();
            }
        }
    }

    private static void StopMultiplayerSafeAutoAtUnsupportedBoundary(
        SafeLocalActionDecision stop,
        int turn)
    {
        if (!_combat.MultiplayerSafeAutoEnabled
            || MultiplayerSafeExecutePolicy.ShouldKeepSafeAutoAfterBoundary(stop))
        {
            return;
        }

        _combat.MultiplayerSafeAutoEnabled = false;
        _combat.MultiplayerSafeExecuteDeploymentRequested = false;
        SolverOverlay.RefreshControls();
        Entry.Logger.Warn(
            $"[CombatSolver/MultiplayerSafeExecute] MP_SAFE_AUTO_STOP " +
            $"reason={stop.Reason} turn={turn}");
    }

    private static PlanAction? FindSafeEndTurnAction(
        IReadOnlyList<PlanAction> plannedTurnActions,
        int safeActionCount)
    {
        if (safeActionCount < 0 || safeActionCount >= plannedTurnActions.Count)
            return null;

        PlanAction candidate = plannedTurnActions[safeActionCount];
        return candidate.Kind == PlanActionKind.EndTurn ? candidate : null;
    }

    private static async Task ExecuteSafeEndTurnAsync(
        NGame host,
        CombatState state,
        SolverDeploymentSession deployment,
        MultiplayerSafeExecutionSession safeSession,
        PlanAction plannedEndTurn,
        int turn,
        int actionCount,
        MultiplayerSafeExecutionBoundary? lastAcceptedBoundary,
        CancellationToken token)
    {
        ActionExecutor actionExecutor = RunManager.Instance.ActionExecutor;
        await actionExecutor.FinishedExecutingActions().WaitAsync(token);

        lastAcceptedBoundary ??= CaptureSafeExecutionBoundaryAfterObservation(
            state,
            "safe_execute_pre_end_turn_baseline");
        MultiplayerClientProbe.ObserveActionBoundary(state, "safe_execute_pre_end_turn");
        MultiplayerSafeExecutionBoundary current =
            MultiplayerClientProbe.CaptureSafeExecutionBoundary(state);
        bool currentLifecycle = IsCurrentCombatLifecycle(
            state,
            deployment.CombatLifecycleGeneration);
        bool actionQueueIdle = actionExecutor.CurrentlyRunningAction == null;
        bool localIdentityStable = SafeExecutionBoundaryHasSameLocalTurn(
            lastAcceptedBoundary,
            current);
        MultiplayerSafeEndTurnFacts facts = new(
            CurrentCombatLifecycle: currentLifecycle,
            LocalPlayableTurn: IsSamePlayableTurn(state, turn),
            RouteGenerationCurrent: _combat.SearchesStarted == deployment.RouteGeneration,
            RouteEndsWithEndTurn: plannedEndTurn.Kind == PlanActionKind.EndTurn,
            ActionQueueIdle: actionQueueIdle,
            NoPendingChoice: !PlayerTurnSetupCoordinator.IsManaging(state)
                && !PlayerTurnSetupCoordinator.HasPendingPlannedChoice(state),
            LocalTurnIdentityStable: localIdentityStable,
            WorldVersionMatchesAccepted: current.WorldVersion == safeSession.LastAcceptedWorldVersion,
            WorldVersionStable: MultiplayerWorldTracker.IsStable(current.WorldVersion),
            NoPendingWorldObservation: !MultiplayerWorldTracker.IsDirty);
        MultiplayerSafeEndTurnDecision decision =
            MultiplayerSafeExecutePolicy.ValidateSafeEndTurn(facts);
        string decisionReason = MultiplayerSafeExecutePolicy.SafeEndTurnReason(decision);
        Entry.Logger.Info(
            $"[CombatSolver/MultiplayerSafeExecute] MP2B_END_TURN_REVALIDATED " +
            $"request_id={safeSession.RequestId} turn={turn} action_count={actionCount} " +
            $"decision={decision} reason={decisionReason} " +
            $"world_version={current.WorldVersion} " +
            $"last_accepted_world_version={safeSession.LastAcceptedWorldVersion} " +
            $"route_generation={deployment.RouteGeneration}");
        if (decision != MultiplayerSafeEndTurnDecision.Safe)
        {
            AbortSafeExecution(host, deployment, turn, actionCount, decisionReason);
            return;
        }

        if (!safeSession.TryBeginEndTurn(
                turn,
                deployment.RouteGeneration,
                current.WorldVersion,
                out string sessionReason))
        {
            AbortSafeExecution(host, deployment, turn, actionCount, sessionReason);
            return;
        }

        SolverOverlay.ShowEndTurnDeploymentStep();
        GameAction queuedAction = await EnqueueAndCaptureActionAsync(
            candidate => candidate is EndPlayerTurnAction,
            () =>
            {
                CombatManager.Instance.OnEndedTurnLocally();
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                    new EndPlayerTurnAction(LocalContext.GetMe(state)!, turn));
            },
            token);
        Entry.Logger.Info(
            $"[CombatSolver/MultiplayerSafeExecute] NATIVE_ACTION_CAPTURED " +
            $"request_id={safeSession.RequestId} action_index={actionCount} " +
            $"type={queuedAction.GetType().Name} turn={turn} card=- " +
            $"local_net_id={LocalContext.GetMe(state)?.NetId} custom_network_api_used=false");
        await queuedAction.CompletionTask.WaitAsync(token);
        await actionExecutor.FinishedExecutingActions().WaitAsync(token);
        if (!safeSession.CompleteEndTurn())
        {
            AbortSafeExecution(host, deployment, turn, actionCount, "end_turn_completion_failed");
            return;
        }

        MultiplayerClientProbe.ObserveActionBoundary(state, "safe_execute_end_turn_complete");
        long afterWorldVersion = MultiplayerWorldTracker.WorldVersion;
        int nextTurn = LocalContext.GetMe(state)?.PlayerCombatState?.TurnNumber ?? 0;
        SolverResult? futureRoute = _combat.ContinuationSource;
        bool preserveFutureRoute = MultiplayerLocalCrossTurnContracts.CanPreserveFutureRoute(
            SolverSessionCapabilities.Capture(state).CanCrossTurnReuse,
            awaitingContinuation: true,
            futureRoute?.Continuations.Count ?? 0,
            futureRoute?.MultiplayerScope ?? MultiplayerSearchResultScope.CurrentTurnOnly);
        _combat.MultiplayerSafeExecuteDeploymentRequested = false;
        _combat.LatestResult = null;
        _combat.LatestStamp = null;
        _combat.LastSafeEndTurnRequestId = safeSession.RequestId;
        _combat.LastSafeEndTurnNumber = turn;
        _combat.LastSafeEndTurnWorldVersion = afterWorldVersion;
        _combat.AwaitingMultiplayerContinuation = preserveFutureRoute;
        if (!preserveFutureRoute)
            _combat.ContinuationSource = null;
        InvalidateRenderedRouteAdoptionSeed();
        CompleteDeployment(deployment);
        SolverOverlay.ShowDeploymentComplete(host, turn, actionCount, endedTurn: true);
        _combat.LastSolverDeployedTurn = turn;
        Entry.Logger.Info(
            $"[CombatSolver/MultiplayerSafeExecute] MP2B_SAFE_END_TURN_ACCEPTED " +
            $"request_id={safeSession.RequestId} turn={turn} action_count={actionCount} " +
            $"route_generation={deployment.RouteGeneration} " +
            $"before_world_version={current.WorldVersion} after_world_version={afterWorldVersion} " +
            $"next_local_turn={nextTurn} session_cleared=true authorization_cleared=true " +
            $"continuation_pending={preserveFutureRoute.ToString().ToLowerInvariant()} " +
            $"route_identity={futureRoute?.RouteIdentity ?? "-"} " +
            "automatic_end_turn=true custom_network_api_used=false");
    }

    private static MultiplayerSafeExecutionBoundary CaptureSafeExecutionBoundaryAfterObservation(
        CombatState state,
        string reason)
    {
        MultiplayerClientProbe.ObserveActionBoundary(state, reason);
        return MultiplayerClientProbe.CaptureSafeExecutionBoundary(state);
    }

    private static bool SafeExecutionBoundaryHasSameLocalTurn(
        MultiplayerSafeExecutionBoundary before,
        MultiplayerSafeExecutionBoundary after)
        => string.Equals(before.LocalNetId, after.LocalNetId, StringComparison.Ordinal)
           && before.RoundNumber == after.RoundNumber
           && string.Equals(before.CurrentSide, after.CurrentSide, StringComparison.Ordinal)
           && before.LocalTurn == after.LocalTurn
           && string.Equals(before.LocalPhase, after.LocalPhase, StringComparison.Ordinal)
           && before.LocalFingerprint == after.LocalFingerprint;

    private static async Task<MultiplayerSafeExecutionBoundary?>
        WaitForStableSafeExecutionWorldAsync(
            NGame host,
            CombatState state,
            MultiplayerSafeExecutionBoundary before,
            CancellationToken token)
    {
        long deadline = System.Environment.TickCount64 + 5_000;
        while (System.Environment.TickCount64 < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (!IsCurrentCombatLifecycle(
                    state,
                    Volatile.Read(ref _combatLifecycleGeneration)))
            {
                return null;
            }

            MultiplayerClientProbe.ObserveActionBoundary(state, "safe_execute_action_complete");
            MultiplayerSafeExecutionBoundary current =
                MultiplayerClientProbe.CaptureSafeExecutionBoundary(state);
            if (current.WorldVersion > before.WorldVersion
                && MultiplayerWorldTracker.TryReadStable(out long stableWorldVersion)
                && stableWorldVersion == current.WorldVersion)
            {
                return current;
            }

            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        return null;
    }

    private static MultiplayerSafeActionRevalidationFacts BuildSafeActionRevalidationFacts(
        MultiplayerSafeExecutionBoundary before,
        MultiplayerSafeExecutionBoundary after,
        GameAction? capturedAction,
        CardModel? playedCard,
        Player player,
        int energyBefore,
        int starsBefore,
        PlanAction action,
        Creature? expectedTarget,
        CombatState state,
        bool hasNextAction)
    {
        PlayerCombatState? liveCombat = player.PlayerCombatState;
        bool localCardRemoved = playedCard != null
            && liveCombat != null
            && liveCombat.Hand.Cards.All(card => !ReferenceEquals(card, playedCard));
        int energyCost = playedCard?.EnergyCost.GetAmountToSpend() ?? int.MaxValue;
        int starsCost = playedCard?.GetStarCostWithModifiers() ?? int.MaxValue;
        bool energyConsistent = after.LocalEnergy is { } energy
            && energy >= 0
            && energy >= energyBefore - Math.Max(0, energyCost);
        bool starsConsistent = after.LocalStars is { } stars
            && stars >= 0
            && stars >= starsBefore - Math.Max(0, starsCost);
        bool targetStable = action.TargetCombatId is null
            ? true
            : expectedTarget != null
                && ReferenceEquals(expectedTarget, state.GetCreature(action.TargetCombatId));
        bool localIdentityStable = before.LocalNetId != null
            && string.Equals(before.LocalNetId, after.LocalNetId, StringComparison.Ordinal)
            && before.RoundNumber == after.RoundNumber
            && before.CurrentSide == after.CurrentSide
            && before.LocalTurn == after.LocalTurn
            && before.LocalPhase == after.LocalPhase;

        return new(
            NativePlayCardCaptured: capturedAction is PlayCardAction,
            ActionQueueIdle: true,
            LocalCardRemovedFromHand: localCardRemoved,
            LocalPlayerIdentityStable: localIdentityStable,
            EnergyStateConsistent: energyConsistent && starsConsistent,
            TargetIdentityStable: targetStable,
            RemotePublicStateUnchanged: before.RemotePublicFingerprint == after.RemotePublicFingerprint,
            EnemyStateMatchesExpectedTarget:
                MultiplayerSafeExecutePolicy.EnemyStateMatchesExpectedLocalAction(
                    before.Enemies,
                    after.Enemies,
                    action.TargetCombatId),
            WorldVersionAdvanced: after.WorldVersion > before.WorldVersion,
            WorldVersionStable: MultiplayerWorldTracker.TryReadStable(out long stableVersion)
                && stableVersion == after.WorldVersion,
            HasNextAction: hasNextAction);
    }

    private static string DescribeSafeExecutionStopAction(
        IReadOnlyList<PlanAction> plannedActions,
        int actionIndex)
    {
        if (actionIndex < 0 || actionIndex >= plannedActions.Count)
        {
            return $"action_index={actionIndex} kind=- card=- target=- " +
                   "choice_type=none turn_start_choice_count=0 choice_sources=-";
        }

        PlanAction action = plannedActions[actionIndex];
        IReadOnlyList<PlanCardChoice>? turnStartChoices = action.TurnStartChoices;
        string choiceType;
        string choiceSources;
        if (turnStartChoices is { Count: > 0 })
        {
            choiceType = "turn_start:" + string.Join(
                ",",
                turnStartChoices
                    .Select(choice => choice.Timing.ToString())
                    .Distinct(StringComparer.Ordinal));
            choiceSources = string.Join(
                ",",
                turnStartChoices.Select(choice =>
                    string.IsNullOrEmpty(choice.SourceId)
                        ? choice.Effect.ToString()
                        : choice.SourceId));
        }
        else if (action.Choice != null
                 || action.NestedChoices is { Count: > 0 }
                 || action.NestedChoicesBeforePrimary != 0)
        {
            choiceType = "immediate_local";
            choiceSources = "-";
        }
        else
        {
            choiceType = "none";
            choiceSources = "-";
        }

        return $"action_index={actionIndex} kind={action.Kind} " +
               $"card={action.CardId ?? "-"} target={action.TargetCombatId?.ToString() ?? "-"} " +
               $"choice_type={choiceType} turn_start_choice_count={turnStartChoices?.Count ?? 0} " +
               $"choice_sources={choiceSources}";
    }

    private static void LogSafeActionRevalidationDiagnostic(
        SolverDeploymentSession deployment,
        MultiplayerSafeExecutionSession safeSession,
        int turn,
        int actionIndex,
        PlanAction action,
        MultiplayerSafeActionRevalidationFacts facts,
        MultiplayerSafeExecutionBoundary before,
        MultiplayerSafeExecutionBoundary after,
        MultiplayerSafeActionRevalidationDecision decision,
        string decisionReason)
    {
        List<string> failedChecks = [];
        if (!facts.NativePlayCardCaptured)
            failedChecks.Add("native_play_card");
        if (!facts.ActionQueueIdle)
            failedChecks.Add("action_queue_idle");
        if (!facts.LocalCardRemovedFromHand)
            failedChecks.Add("local_card_removed");
        if (!facts.LocalPlayerIdentityStable)
            failedChecks.Add("local_identity");
        if (!facts.EnergyStateConsistent)
            failedChecks.Add("energy_or_stars");
        if (!facts.TargetIdentityStable)
            failedChecks.Add("target_identity");
        if (!facts.RemotePublicStateUnchanged)
            failedChecks.Add("remote_public_state");
        if (!facts.EnemyStateMatchesExpectedTarget)
            failedChecks.Add("enemy_state");
        if (!facts.WorldVersionAdvanced)
            failedChecks.Add("world_version_advanced");
        if (!facts.WorldVersionStable)
            failedChecks.Add("world_version_stable");

        List<string> changedFields = [];
        if (before.LocalHp != after.LocalHp)
            changedFields.Add("hp");
        if (before.LocalBlock != after.LocalBlock)
            changedFields.Add("block");
        if (before.LocalEnergy != after.LocalEnergy)
            changedFields.Add("energy");
        if (before.LocalStars != after.LocalStars)
            changedFields.Add("stars");
        if (!before.LocalHand.SequenceEqual(after.LocalHand, StringComparer.Ordinal))
            changedFields.Add("hand");
        if (!before.LocalDrawPile.SequenceEqual(after.LocalDrawPile, StringComparer.Ordinal))
            changedFields.Add("draw");
        if (!before.LocalDiscard.SequenceEqual(after.LocalDiscard, StringComparer.Ordinal))
            changedFields.Add("discard");
        if (!before.LocalExhaust.SequenceEqual(after.LocalExhaust, StringComparer.Ordinal))
            changedFields.Add("exhaust");
        if (!before.LocalPowers.SequenceEqual(after.LocalPowers, StringComparer.Ordinal))
            changedFields.Add("powers");
        if (!before.RemotePlayers.SequenceEqual(after.RemotePlayers, StringComparer.Ordinal))
            changedFields.Add("remote_players");
        if (!before.Enemies.SequenceEqual(after.Enemies, StringComparer.Ordinal))
            changedFields.Add("enemies");
        if (!before.LocalFingerprint.Equals(after.LocalFingerprint))
            changedFields.Add("local_fingerprint");
        if (!before.RemotePublicFingerprint.Equals(after.RemotePublicFingerprint))
            changedFields.Add("remote_public_fingerprint");
        if (before.RoundNumber != after.RoundNumber)
            changedFields.Add("round");
        if (!string.Equals(before.CurrentSide, after.CurrentSide, StringComparison.Ordinal))
            changedFields.Add("side");
        if (before.LocalTurn != after.LocalTurn)
            changedFields.Add("turn");
        if (!string.Equals(before.LocalPhase, after.LocalPhase, StringComparison.Ordinal))
            changedFields.Add("phase");

        int currentLifecycleGeneration = Volatile.Read(ref _combatLifecycleGeneration);
        bool lifecycleCurrent = deployment.State != null
            && IsCurrentCombatLifecycle(
                deployment.State,
                deployment.CombatLifecycleGeneration);

        Entry.Logger.Warn(
            $"[CombatSolver/MultiplayerSafeExecute] MP2B_ACTION_REVALIDATION_DIAGNOSTIC " +
            $"request_id={safeSession.RequestId} turn={turn} action_index={actionIndex} " +
            $"card={action.CardId ?? "-"} target={action.TargetCombatId?.ToString() ?? "-"} " +
            $"decision={decision} reason={decisionReason} " +
            $"failed_checks={FormatSafeDiagnosticTokens(failedChecks)} " +
            $"changed_fields={FormatSafeDiagnosticTokens(changedFields)} " +
            $"before_world_version={before.WorldVersion} after_world_version={after.WorldVersion} " +
            $"before_observation_sequence={before.ObservationSequence} " +
            $"after_observation_sequence={after.ObservationSequence} " +
            $"lifecycle_current={lifecycleCurrent.ToString().ToLowerInvariant()} " +
            $"expected_lifecycle_generation={deployment.CombatLifecycleGeneration} " +
            $"current_lifecycle_generation={currentLifecycleGeneration} " +
            $"before_local_fingerprint={before.LocalFingerprint.First:X16}:{before.LocalFingerprint.Second:X16} " +
            $"after_local_fingerprint={after.LocalFingerprint.First:X16}:{after.LocalFingerprint.Second:X16} " +
            $"before_remote_fingerprint={before.RemotePublicFingerprint.First:X16}:{before.RemotePublicFingerprint.Second:X16} " +
            $"after_remote_fingerprint={after.RemotePublicFingerprint.First:X16}:{after.RemotePublicFingerprint.Second:X16}");

        Entry.Logger.Warn(
            $"[CombatSolver/MultiplayerSafeExecute] MP2B_ACTION_STATE_DIFF " +
            $"request_id={safeSession.RequestId} action_index={actionIndex} " +
            $"before_hp={before.LocalHp?.ToString() ?? "-"} after_hp={after.LocalHp?.ToString() ?? "-"} " +
            $"before_block={before.LocalBlock?.ToString() ?? "-"} after_block={after.LocalBlock?.ToString() ?? "-"} " +
            $"before_energy={before.LocalEnergy?.ToString() ?? "-"} after_energy={after.LocalEnergy?.ToString() ?? "-"} " +
            $"before_stars={before.LocalStars?.ToString() ?? "-"} after_stars={after.LocalStars?.ToString() ?? "-"} " +
            $"before_hand=[{FormatSafeDiagnosticTokens(before.LocalHand)}] " +
            $"after_hand=[{FormatSafeDiagnosticTokens(after.LocalHand)}] " +
            $"before_draw=[{FormatSafeDiagnosticTokens(before.LocalDrawPile)}] " +
            $"after_draw=[{FormatSafeDiagnosticTokens(after.LocalDrawPile)}] " +
            $"before_discard=[{FormatSafeDiagnosticTokens(before.LocalDiscard)}] " +
            $"after_discard=[{FormatSafeDiagnosticTokens(after.LocalDiscard)}] " +
            $"before_exhaust=[{FormatSafeDiagnosticTokens(before.LocalExhaust)}] " +
            $"after_exhaust=[{FormatSafeDiagnosticTokens(after.LocalExhaust)}] " +
            $"before_powers=[{FormatSafeDiagnosticTokens(before.LocalPowers)}] " +
            $"after_powers=[{FormatSafeDiagnosticTokens(after.LocalPowers)}] " +
            $"before_remote=[{FormatSafeDiagnosticTokens(before.RemotePlayers)}] " +
            $"after_remote=[{FormatSafeDiagnosticTokens(after.RemotePlayers)}] " +
            $"before_enemies=[{FormatSafeDiagnosticTokens(before.Enemies)}] " +
            $"after_enemies=[{FormatSafeDiagnosticTokens(after.Enemies)}]");
    }

    private static string FormatSafeDiagnosticTokens(IEnumerable<string> tokens)
    {
        string[] values = tokens as string[] ?? tokens.ToArray();
        return values.Length == 0 ? "-" : string.Join("|", values);
    }

    private static string DescribeSafeExecutionAction(PlanAction action)
        => $"{action.Kind}:{action.CardId}:{action.CardOccurrence}:" +
           $"target={action.TargetCombatId?.ToString() ?? "-"}";

    private static void AbortSafeExecution(
        NGame host,
        SolverDeploymentSession deployment,
        int turn,
        int completedActions,
        string reason)
    {
        MultiplayerSafeExecutionSession? safeSession = deployment.SafeExecutionSession;
        safeSession?.Abort(reason);
        _combat.MultiplayerSafeExecuteDeploymentRequested = false;
        _combat.ContinuationSource = null;
        _combat.LatestResult = null;
        _combat.LatestStamp = null;
        InvalidateRenderedRouteAdoptionSeed();
        CompleteDeployment(deployment);
        SolverOverlay.ShowDeploymentComplete(
            host,
            turn,
            completedActions,
            endedTurn: false,
            completionMessage: string.Equals(reason, "remote_or_unknown_change", StringComparison.Ordinal)
                ? "检测到多人状态变化，已停止后续执行并重新计算。"
                : null);
        string marker = string.Equals(reason, "remote_or_unknown_change", StringComparison.Ordinal)
            ? "MP2B_REMOTE_DELTA_ABORT"
            : "MP2B_DEPLOY_ABORT";
        Entry.Logger.Info(
            $"[CombatSolver/MultiplayerSafeExecute] {marker} " +
            $"request_id={safeSession?.RequestId ?? 0} turn={turn} " +
            $"completed_actions={completedActions} reason={reason} " +
            $"last_accepted_world_version={safeSession?.LastAcceptedWorldVersion ?? 0}");

        if (reason != "combat_lifecycle_changed"
            && CombatManager.Instance.IsInProgress
            && IsCurrentCombatLifecycle(
                deployment.State!,
                Volatile.Read(ref _combatLifecycleGeneration))
            && CanSolve(deployment.State!, out _))
        {
            RequestSearch(
                host,
                deployment.State!,
                SearchReason.DeploymentDrift,
                deployWhenReady: false);
        }
    }

    private static void PauseAfterNativeChoiceFailure(NGame host, SolverDeploymentSession deployment, int turn, Exception failure)
    {
        _combat.ContinuationSource = null;
        _combat.FullAutoEnabled = false;
        _combat.AutomaticSearchPaused = true;
        _combat.AutomaticSearchPausedTurn = turn;
        _combat.BugReportIssues.RecordFailure(CombatBugReportIssueKind.DeploymentFailure, failure);
        CombatBugReportExporter.RecordRuntimeException("deployment_choice", failure);
        CompleteDeployment(deployment);
        SolverOverlay.Show(host, SolverText.Get("自动选牌未完成，已暂停执行。当前选择交还手动操作，完成后点击“重新计算”。"));
        Entry.Logger.Error($"[CombatSolver/Test] DEPLOY_CHOICE_PAUSED turn={turn} exception={failure}");
    }

    internal static CardModel FindCardForDeployment(
        IReadOnlyList<CardModel> hand,
        PlanAction action)
    {
        if (!string.IsNullOrEmpty(action.CardStateKey))
        {
            CardModel? stateMatch = hand
                .Where(card => string.Equals(
                    CardChoiceSupport.ChoiceCardKey(card),
                    action.CardStateKey,
                    StringComparison.Ordinal))
                .Skip(action.CardStateOccurrence)
                .FirstOrDefault();
            if (stateMatch != null)
                return stateMatch;

            // Native card-cost hooks can change only the effective energy cost after an
            // earlier card is played. The physical card is still the planned card, so do
            // not turn this harmless mutable-state change into a deployment replan.
            string plannedWithoutEnergy = DeploymentCardKeyWithoutEnergy(action.CardStateKey);
            List<CardModel> mutableStateMatches = hand
                .Where(card => string.Equals(
                    DeploymentCardKeyWithoutEnergy(CardChoiceSupport.ChoiceCardKey(card)),
                    plannedWithoutEnergy,
                    StringComparison.Ordinal))
                .ToList();
            CardModel? occurrenceMatch = hand
                .Where(card => string.Equals(card.Id.Entry, action.CardId, StringComparison.Ordinal))
                .Skip(action.CardOccurrence)
                .FirstOrDefault();
            if (occurrenceMatch != null
                && mutableStateMatches.Contains(occurrenceMatch))
            {
                Entry.Logger.Info(
                    $"[CombatSolver/Test] DEPLOY_CARD_STATE_RECONCILED card={action.CardId} " +
                    $"occurrence={action.CardOccurrence} reason=effective_energy_cost_changed " +
                    $"planned={action.CardStateKey} actual={CardChoiceSupport.ChoiceCardKey(occurrenceMatch)}");
                return occurrenceMatch;
            }

            if (mutableStateMatches.Count == 1)
            {
                CardModel reconciled = mutableStateMatches[0];
                Entry.Logger.Info(
                    $"[CombatSolver/Test] DEPLOY_CARD_STATE_RECONCILED card={action.CardId} " +
                    $"occurrence={action.CardOccurrence} reason=effective_energy_cost_changed " +
                    $"planned={action.CardStateKey} actual={CardChoiceSupport.ChoiceCardKey(reconciled)}");
                return reconciled;
            }

            throw new InvalidOperationException(
                $"部署时找不到计划中的手牌状态 {action.CardId}@{action.CardStateOccurrence}；" +
                $"当前手牌={string.Join(',', hand.Select(card => CardChoiceSupport.ChoiceCardKey(card)))}。");
        }

        return hand
            .Where(card => string.Equals(card.Id.Entry, action.CardId, StringComparison.Ordinal))
            .Skip(action.CardOccurrence)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"部署时找不到手牌 {action.CardId}#{action.CardOccurrence}；" +
                $"当前手牌={string.Join(',', hand.Select(card => card.Id.Entry))}。");
    }

    private static string DeploymentCardKeyWithoutEnergy(string key)
    {
        const string marker = "|energy=";
        int start = key.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return key;
        int end = key.IndexOf('|', start + marker.Length);
        return end < 0
            ? key[..start]
            : string.Concat(key.AsSpan(0, start), key.AsSpan(end));
    }

    private static bool IsMissingDeploymentCard(InvalidOperationException exception)
        => exception.Message.StartsWith("部署时找不到计划中的手牌状态 ", StringComparison.Ordinal)
            || exception.Message.StartsWith("部署时找不到手牌 ", StringComparison.Ordinal);

    private static bool IsDeploymentTurnDrift(InvalidOperationException exception)
        => string.Equals(
            exception.Message,
            "部署途中已不再是原玩家回合。",
            StringComparison.Ordinal);

    internal static async Task<GameAction> EnqueueAndCaptureActionAsync(
        Func<GameAction, bool> matches,
        Action enqueue,
        CancellationToken token)
    {
        TaskCompletionSource<GameAction> captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ActionExecutor executor = RunManager.Instance.ActionExecutor;
        void OnBeforeActionExecuted(GameAction action)
        {
            if (matches(action))
                captured.TrySetResult(action);
        }

        executor.BeforeActionExecuted += OnBeforeActionExecuted;
        try
        {
            enqueue();
            return await captured.Task.WaitAsync(token);
        }
        finally
        {
            executor.BeforeActionExecuted -= OnBeforeActionExecuted;
        }
    }

    internal static async Task WaitForTurnStartDeploymentDelayAsync(
        NGame host,
        int turn,
        CancellationToken token = default)
    {
        double seconds = SolverSettings.Capture().DeploymentInterActionDelaySeconds;
        if (seconds <= 0d)
            return;
        long startedAt = System.Environment.TickCount64;
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_START_DEPLOY_DELAY turn={turn} seconds={seconds:0.###}");
        await WaitForDeploymentDelayAsync(host, seconds, token);
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_START_DEPLOY_DELAY_COMPLETE turn={turn} " +
            $"elapsed_ms={System.Environment.TickCount64 - startedAt}");
    }

    private static async Task WaitForDeploymentDelayAsync(
        NGame host,
        double seconds,
        CancellationToken token)
    {
        SceneTreeTimer delay = host.GetTree().CreateTimer(
            seconds,
            processAlways: false,
            processInPhysics: false,
            ignoreTimeScale: false);
        await AwaitSceneTreeTimerAsync(host, delay).WaitAsync(token);
    }

    private static async Task AwaitSceneTreeTimerAsync(NGame host, SceneTreeTimer delay)
        => await host.ToSignal(delay, SceneTreeTimer.SignalName.Timeout);

    private static string FormatDeploymentFailure(Exception exception)
        => exception.GetBaseException() is IncompatibleGameplayModException incompatible
           ? FormatIncompatibleModFailure(incompatible)
           : $"[color={SolverUiTokens.Palette.DangerHex}][b]{SolverText.Get("自动执行中止")}[/b]\n" +
           $"{EscapeRichText(exception.Message)}[/color]\n" +
           SolverUiTokens.BugReportUploadInstructionRichText;

    private static void CancelDeployment()
    {
        SolverDeploymentSession? deployment = _deployment;
        _deployment = null;
        if (deployment == null)
            return;
        deployment.SafeExecutionSession?.Abort("deployment_cancelled");
        deployment.Cancellation.Cancel();
        QueueDeploymentReferenceRelease(deployment);
    }

    private static async Task ReleaseDeploymentSessionAsync(SolverDeploymentSession deployment)
    {
        try
        {
            await deployment.Operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            deployment.Operation = Task.CompletedTask;
        }
        finally
        {
            DisposeDeploymentCancellationOnce(deployment);
            Interlocked.Increment(ref _deploymentReferenceReleaseCompletedCountForTesting);
        }
    }

    private static void QueueDeploymentReferenceRelease(SolverDeploymentSession deployment)
    {
        if (Interlocked.Exchange(ref deployment.ReferenceReleaseState, 1) != 0)
            return;
        PendingDeploymentReferenceReleases.RemoveAll(static task => task.IsCompleted);
        Interlocked.Increment(ref _deploymentReferenceReleaseScheduledCountForTesting);
        PendingDeploymentReferenceReleases.Add(ReleaseDeploymentSessionAsync(deployment));
    }

    private static Task DrainDeploymentReferenceReleases()
    {
        if (PendingDeploymentReferenceReleases.Count == 0)
            return Task.CompletedTask;
        Task[] releases = [.. PendingDeploymentReferenceReleases];
        PendingDeploymentReferenceReleases.Clear();
        return Task.WhenAll(releases);
    }

    private static void DisposeDeploymentCancellationOnce(SolverDeploymentSession deployment)
    {
        if (Interlocked.Exchange(ref deployment.CancellationDisposeState, 1) != 0)
            return;
        deployment.Cancellation.Dispose();
        Interlocked.Increment(ref _deploymentCtsDisposeCountForTesting);
    }

    private static void CompleteDeployment(SolverDeploymentSession deployment)
    {
        if (ReferenceEquals(_deployment, deployment))
            _deployment = null;
    }

    private static FastModeType? ResolveDeploymentFastMode(SolverDeploymentFastMode mode)
        => mode switch
        {
            SolverDeploymentFastMode.FollowGame => null,
            SolverDeploymentFastMode.Normal => FastModeType.Normal,
            SolverDeploymentFastMode.Fast => FastModeType.Fast,
            SolverDeploymentFastMode.Instant => null,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
}
