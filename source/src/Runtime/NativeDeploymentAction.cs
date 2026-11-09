using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal enum NativeActionOutcome
{
    Applied,
    NotApplied,
    Unconfirmed,
}

internal readonly record struct NativeActionConfirmation(
    NativeActionOutcome Outcome, string Reason, GameAction? NativeAction = null);

internal static class NativeDeploymentAction
{
    private static readonly TimeSpan QueueObservationTimeout = TimeSpan.FromSeconds(10);

    internal static async Task<NativeActionConfirmation> UsePotionAsync(PotionModel potion, int slot, Creature? target,
        CancellationToken token, NativeChoiceSession? choiceSession = null, Action<GameAction>? onStarted = null)
    {
        Player player = potion.Owner;
        // EnqueueManualUse supplies the owner as the native target for self-use.
        Creature? nativeTarget = target ?? (potion.IsValidTarget(player.Creature) ? player.Creature : null);
        var observation = new Observation(
            action => action is UsePotionAction use && ReferenceEquals(use.Player, player)
                && use.PotionIndex == (uint)slot && use.TargetId == nativeTarget?.CombatId,
            knownSkipAtStart: null,
            unchangedAfterCancellation: () => ReferenceEquals(player.GetPotionAtSlotIndex(slot), potion)
                && !potion.HasBeenRemovedFromState);

        void EffectFinished(AbstractModel _) => observation.ObserveEffect();
        potion.ExecutionFinished += EffectFinished;
        try
        {
            return await observation.RunAsync(() =>
            {
                if (!ReferenceEquals(player.GetPotionAtSlotIndex(slot), potion))
                    return false;
                potion.EnqueueManualUse(target);
                return true;
            }, token, choiceSession, onStarted, drainQueue: true);
        }
        finally { potion.ExecutionFinished -= EffectFinished; }
    }

    internal static async Task<NativeActionConfirmation> PlayCardAsync(CardModel card, Creature? target,
        CancellationToken token, NativeChoiceSession? choiceSession = null, Action<GameAction>? onStarted = null)
    {
        Player player = card.Owner;
        int energyBefore = 0;
        int starsBefore = 0;
        bool WasOutOfHandAtStart()
        {
            energyBefore = player.PlayerCombatState?.Energy ?? 0;
            starsBefore = player.PlayerCombatState?.Stars ?? 0;
            return card.Pile?.Type != PileType.Hand;
        }
        var observation = new Observation(
            action => action is PlayCardAction play && ReferenceEquals(play.Player, player)
                && play.TargetId == target?.CombatId && ReferenceEquals(play.NetCombatCard.ToCardModelOrNull(), card),
            knownSkipAtStart: WasOutOfHandAtStart,
            unchangedAfterCancellation: () => card.Pile?.Type == PileType.Hand
                && player.PlayerCombatState?.Energy == energyBefore && player.PlayerCombatState?.Stars == starsBefore);
        void Played() => observation.ObserveEffect();
        card.Played += Played;
        try
        {
            return await observation.RunAsync(() => card.TryManualPlay(target), token, choiceSession, onStarted,
                drainQueue: true);
        }
        finally { card.Played -= Played; }
    }

    internal static async Task<NativeActionConfirmation> EndTurnAsync(Player player, int turn, CancellationToken token,
        bool drainQueue, Action<GameAction>? onStarted = null, Action? onAccepted = null)
    {
        CombatManager combat = CombatManager.Instance;
        CombatState? state = combat.DebugOnlyGetState();
        bool IsCurrentTurn() => combat.IsInProgress && ReferenceEquals(combat.DebugOnlyGetState(), state)
            && state?.CurrentSide == CombatSide.Player && player.PlayerCombatState?.TurnNumber == turn;

        var observation = new Observation(
            action => action is EndPlayerTurnAction && action.OwnerId == player.NetId
                && action.ToNetAction() is NetEndPlayerTurnAction end && end.turnNumber == turn,
            knownSkipAtStart: () => !IsCurrentTurn() || combat.IsPlayerReadyToEndTurn(player),
            unchangedAfterCancellation: () => !combat.IsPlayerReadyToEndTurn(player));
        void PlayerEndedTurn(Player endedPlayer, bool _)
        {
            if (ReferenceEquals(endedPlayer, player) && IsCurrentTurn() && observation.ObserveEffect())
                onAccepted?.Invoke();
        }
        bool disabledLocally = false;
        combat.PlayerEndedTurn += PlayerEndedTurn;
        try
        {
            NativeActionConfirmation result = await observation.RunAsync(() =>
            {
                if (!IsCurrentTurn() || combat.IsPlayerReadyToEndTurn(player) || combat.PlayerActionsDisabled)
                    return false;
                combat.OnEndedTurnLocally();
                disabledLocally = true;
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
                return true;
            }, token, choiceSession: null, onStarted, drainQueue);
            // A rejected native action did not consume the local End Turn input lock.
            if (result.Outcome == NativeActionOutcome.NotApplied && disabledLocally && IsCurrentTurn()
                && !combat.IsPlayerReadyToEndTurn(player))
                combat.UndoReadyToEndTurn(player);
            return result;
        }
        finally { combat.PlayerEndedTurn -= PlayerEndedTurn; }
    }

    private sealed class Observation(Func<GameAction, bool> matches, Func<bool>? knownSkipAtStart,
        Func<bool> unchangedAfterCancellation)
    {
        private readonly RunManager _run = RunManager.Instance;
        private readonly TaskCompletionSource<GameAction> _enqueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _startedOrCanceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancelSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private GameAction? _action;
        private bool _started;
        private bool _canceled;
        private bool _enteredChoice;
        private bool _knownSkip;
        private bool _effect;

        internal bool ObserveEffect()
        {
            if (!_started || !ReferenceEquals(_run.ActionExecutor.CurrentlyRunningAction, _action))
                return false;
            _effect = true;
            return true;
        }

        private void OnEnqueued(GameAction action)
        {
            if (_action != null || !matches(action))
                return;
            // The native queue raises this before it can reject the action or run it synchronously.
            _action = action;
            action.BeforeExecuted += OnStarted;
            action.BeforeCancelled += OnCanceled;
            action.BeforePausedForPlayerChoice += OnChoice;
            _enqueued.TrySetResult(action);
        }

        private void OnStarted(GameAction _)
        {
            _knownSkip = knownSkipAtStart?.Invoke() == true;
            _started = true;
            _startedOrCanceled.TrySetResult();
        }

        private void OnCanceled(GameAction _)
        {
            _canceled = true;
            _startedOrCanceled.TrySetResult();
            _cancelSignal.TrySetResult();
        }

        private void OnChoice(GameAction _) => _enteredChoice = true;

        internal async Task<NativeActionConfirmation> RunAsync(Func<bool> submit, CancellationToken token,
            NativeChoiceSession? choiceSession, Action<GameAction>? onStarted, bool drainQueue)
        {
            NativeActionConfirmation Result(NativeActionOutcome outcome, string reason) => new(outcome, reason, _action);

            token.ThrowIfCancellationRequested();
            _run.ActionQueueSet.ActionEnqueued += OnEnqueued;
            try
            {
                if (!submit())
                    return Result(NativeActionOutcome.NotApplied, "submission_rejected");
                GameAction action;
                try
                {
                    action = await _enqueued.Task.WaitAsync(QueueObservationTimeout, token);
                    await _startedOrCanceled.Task.WaitAsync(QueueObservationTimeout, token);
                }
                catch (TimeoutException)
                {
                    return Result(NativeActionOutcome.Unconfirmed, "queue_observation_timeout");
                }
                if (!_started)
                    return Result(NativeActionOutcome.NotApplied, "canceled_before_execution");

                onStarted?.Invoke(action);
                // The choice driver must be observed while the producer is still waiting for it.
                Task completion = choiceSession?.AwaitPhaseAsync(action.CompletionTask) ?? action.CompletionTask;
                if (ReferenceEquals(await Task.WhenAny(completion, _cancelSignal.Task).WaitAsync(token), _cancelSignal.Task))
                {
                    try { await completion.WaitAsync(QueueObservationTimeout, token); }
                    catch (TimeoutException)
                    {
                        return Result(NativeActionOutcome.Unconfirmed, "canceled_execution_not_settled");
                    }
                }
                else
                {
                    await completion;
                }

                if (action.Exception != null)
                    throw new InvalidOperationException("原生部署动作执行失败。", action.Exception);
                token.ThrowIfCancellationRequested();
                if (!_canceled && _effect)
                {
                    if (choiceSession != null)
                        await choiceSession.CompleteAsync();
                    if (drainQueue)
                        await _run.ActionExecutor.FinishedExecutingActions().WaitAsync(token);
                    token.ThrowIfCancellationRequested();
                    return Result(NativeActionOutcome.Applied, "native_effect_confirmed");
                }
                if (!_effect && !_enteredChoice && (_knownSkip || _canceled && unchangedAfterCancellation()))
                    return Result(NativeActionOutcome.NotApplied, _canceled ? "native_canceled" : "native_skipped");
                return Result(NativeActionOutcome.Unconfirmed, "native_effect_missing_or_partial");
            }
            finally
            {
                _run.ActionQueueSet.ActionEnqueued -= OnEnqueued;
                if (_action is { } action)
                {
                    action.BeforeExecuted -= OnStarted;
                    action.BeforeCancelled -= OnCanceled;
                    action.BeforePausedForPlayerChoice -= OnChoice;
                }
            }
        }
    }
}
