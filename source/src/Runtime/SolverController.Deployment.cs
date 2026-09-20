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
            action.Turn == result.StartTurnNumber
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
                    MultiplayerSafeExecutePolicy.MaxActionsPerDeployment)
                : null,
        };
        _deployment = deployment;
        IReadOnlyList<PlanAction> plannedTurnActions = result.BestNode.Actions
            .Where(action => action.Turn == result.StartTurnNumber)
            .ToArray();
        int actionCount;
        if (capabilities.Kind == SolverSessionKind.MultiplayerSafeExecute)
        {
            IReadOnlyList<PlanAction> safeActions =
                MultiplayerSafeLocalActionClassifier.TakeMp2BDeploymentSlice(
                    state,
                    plannedTurnActions,
                    out SafeLocalActionDecision stop);
            if (safeActions.Count == 0 && plannedTurnActions.Count > 0)
            {
                _combat.MultiplayerSafeExecuteDeploymentRequested = false;
                deployment.SafeExecutionSession?.Abort(stop.Reason);
                Entry.Logger.Info(
                    $"[CombatSolver/MultiplayerSafeExecute] MP2B_DEPLOY_STOP " +
                    $"turn={result.StartTurnNumber} reason={stop.Reason}");
                CompleteDeployment(deployment);
                return;
            }
            actionCount = safeActions.Count;
        }
        else
        {
            actionCount = plannedTurnActions.Count(action => action.IsExecutable);
        }
        SolverSettingsSnapshot deploymentSettings = SolverSettings.Capture();
        SolverOverlay.ShowDeploying(host, result.StartTurnNumber, actionCount);
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
            .Where(action => action.Turn == turn)
            .ToArray();
        List<PlanAction> actions;
        SafeLocalActionDecision safeStop = SafeLocalActionDecision.Allow;
        MultiplayerSafeExecutionSession? safeSession = deployment.SafeExecutionSession;
        if (safeExecute)
        {
            actions = [.. MultiplayerSafeLocalActionClassifier.TakeMp2BDeploymentSlice(
                state,
                plannedTurnActions,
                out safeStop)];
            if (!safeStop.IsSafe)
            {
                Entry.Logger.Info(
                    $"[CombatSolver/MultiplayerSafeExecute] DEPLOY_PREFIX_STOP " +
                    $"turn={turn} action={safeStop.Reason}");
            }
        }
        else
        {
            actions = plannedTurnActions.Where(action => action.IsExecutable).ToList();
        }
        PlanAction? plannedEndTurn = safeExecute || !capabilities.CanEndTurnAutomatically
            ? null
            : plannedTurnActions.FirstOrDefault(action => action.Kind == PlanActionKind.EndTurn);
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
                        beforeBoundary!.WorldVersion,
                        out string sessionStartReason))
                {
                    AbortSafeExecution(host, deployment, turn, actionIndex, sessionStartReason);
                    return;
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
                // Safe Execute only admits no-choice local actions. Creating a native choice
                // driver for those actions is both unnecessary and forbidden in multiplayer.
                using NativeChoiceSession? choiceSession =
                    safeExecute && actionChoices.Count == 0
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
                        AbortSafeExecution(host, deployment, turn, actionIndex, decisionReason);
                        return;
                    }
                    if (!safeSession.AcceptAction(afterBoundary.WorldVersion, hasNextAction))
                    {
                        AbortSafeExecution(host, deployment, turn, actionIndex, "session_accept_failed");
                        return;
                    }
                    if (!hasNextAction || safeSession.State == MultiplayerSafeExecutionState.Completed)
                    {
                        CompleteDeployment(deployment);
                        SolverOverlay.ShowDeploymentComplete(
                            host,
                            turn,
                            actionIndex + 1,
                            endedTurn: false);
                        _combat.LastSolverDeployedTurn = turn;
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
                if (CombatManager.Instance.IsInProgress && IsSamePlayableTurn(state, turn))
                {
                    CompleteDeployment(deployment);
                    SolverOverlay.ShowDeploymentComplete(host, turn, actions.Count, endedTurn: false);
                    _combat.LastSolverDeployedTurn = turn;
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
            EnemyStateMatchesExpectedTarget: EnemyStateMatchesExpectedTarget(
                before.Enemies,
                after.Enemies,
                action.TargetCombatId),
            WorldVersionAdvanced: after.WorldVersion > before.WorldVersion,
            WorldVersionStable: MultiplayerWorldTracker.TryReadStable(out long stableVersion)
                && stableVersion == after.WorldVersion,
            HasNextAction: hasNextAction);
    }

    private static bool EnemyStateMatchesExpectedTarget(
        IReadOnlyList<string> before,
        IReadOnlyList<string> after,
        uint? targetCombatId)
    {
        if (targetCombatId is null)
            return before.SequenceEqual(after, StringComparer.Ordinal);

        if (!TryBuildEnemyTokensById(before, out Dictionary<string, string> beforeById)
            || !TryBuildEnemyTokensById(after, out Dictionary<string, string> afterById))
        {
            return false;
        }
        if (!beforeById.Keys.OrderBy(key => key, StringComparer.Ordinal)
                .SequenceEqual(
                    afterById.Keys.OrderBy(key => key, StringComparer.Ordinal),
                    StringComparer.Ordinal))
        {
            return false;
        }

        string targetId = targetCombatId.Value.ToString();
        foreach ((string id, string token) in beforeById)
        {
            if (string.Equals(id, targetId, StringComparison.Ordinal))
                continue;
            if (!string.Equals(afterById[id], token, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static bool TryBuildEnemyTokensById(
        IEnumerable<string> tokens,
        out Dictionary<string, string> byId)
    {
        byId = new(StringComparer.Ordinal);
        foreach (string token in tokens)
        {
            int separator = token.IndexOf(':');
            if (separator <= 0
                || !byId.TryAdd(token[..separator], token))
            {
                byId.Clear();
                return false;
            }
        }
        return true;
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
        SolverOverlay.ShowDeploymentComplete(host, turn, completedActions, endedTurn: false);
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
