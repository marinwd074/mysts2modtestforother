using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal readonly record struct SafeLocalActionDecision(bool IsSafe, string Reason)
{
    public static SafeLocalActionDecision Allow { get; } = new(true, "safe_local_play_card");
}

/// <summary>
/// Conservative deployment boundary for the future multiplayer Safe Execute tier.
/// PlanAction deliberately has no actor field: every accepted action is implicitly owned
/// by the local player, while every unknown target or interaction is rejected closed.
/// </summary>
internal static class MultiplayerSafeLocalActionClassifier
{
    public static SafeLocalActionDecision Classify(
        CombatState state,
        PlanAction action)
    {
        if (action.Kind != PlanActionKind.PlayCard)
            return new(false, $"kind_{action.Kind.ToString().ToLowerInvariant()}");
        if (string.IsNullOrWhiteSpace(action.CardId))
            return new(false, "card_identity_missing");
        if (action.EndsPlayerTurn)
            return new(false, "ends_player_turn");
        if (action.ReplayCount != 0)
            return new(false, "replay_semantics");
        if (action.Choice != null
            || action.NestedChoices is { Count: > 0 }
            || action.NestedChoicesBeforePrimary != 0
            || action.TurnStartChoices is { Count: > 0 })
        {
            return new(false, "choice_required");
        }

        Player? localPlayer = LocalContext.GetMe(state);
        if (localPlayer?.PlayerCombatState == null)
            return new(false, "local_player_missing");

        CardModel? card = localPlayer.PlayerCombatState.Hand.Cards
            .Where(candidate => string.Equals(
                candidate.Id.Entry,
                action.CardId,
                StringComparison.Ordinal))
            .Skip(Math.Max(0, action.CardOccurrence))
            .FirstOrDefault();
        if (card == null)
            return new(false, "local_card_missing");
        if (card.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly)
            return new(false, "multiplayer_only_card");

        if (action.TargetCombatId is { } targetId)
        {
            Creature? target = state.GetCreature(targetId);
            if (target == null)
                return new(false, "target_missing");
            bool isLocalTarget = targetId == localPlayer.Creature.CombatId;
            bool isEnemyTarget = state.Enemies.Any(enemy => enemy.CombatId == targetId);
            if (!isLocalTarget && !isEnemyTarget)
                return new(false, "remote_player_or_unknown_target");
        }
        else if (action.TargetIndex != -1 || action.TargetName.Length > 0)
        {
            return new(false, "target_identity_incomplete");
        }

        return SafeLocalActionDecision.Allow;
    }

    /// <summary>
    /// Returns the contiguous safe prefix. The first unsafe action is the hard stop;
    /// later actions are intentionally not considered for execution.
    /// </summary>
    public static IReadOnlyList<PlanAction> TakeSafePrefix(
        CombatState state,
        IReadOnlyList<PlanAction> actions,
        out SafeLocalActionDecision stop)
    {
        List<PlanAction> safe = [];
        foreach (PlanAction action in actions)
        {
            SafeLocalActionDecision decision = Classify(state, action);
            if (!decision.IsSafe)
            {
                stop = decision;
                return safe;
            }
            safe.Add(action);
        }

        stop = SafeLocalActionDecision.Allow;
        return safe;
    }
}
