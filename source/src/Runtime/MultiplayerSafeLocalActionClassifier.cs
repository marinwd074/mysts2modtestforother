using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

/// <summary>
/// Conservative deployment boundary for the future multiplayer Safe Execute tier.
/// PlanAction deliberately has no actor field: every accepted action is implicitly owned
/// by the local player, while every unknown target or interaction is rejected closed.
/// </summary>
internal static class MultiplayerSafeLocalActionClassifier
{
    public static SafeLocalActionDecision Classify(CombatState state, PlanAction action)
    {
        SafeLocalActionDecision structural = MultiplayerSafeExecutePolicy.ClassifyStructural(
            new(
                KindToken: action.Kind.ToString().ToLowerInvariant(),
                IsPlayCard: action.Kind == PlanActionKind.PlayCard,
                HasCardIdentity: !string.IsNullOrWhiteSpace(action.CardId),
                EndsPlayerTurn: action.EndsPlayerTurn,
                HasReplaySemantics: action.ReplayCount != 0,
                RequiresChoice: action.Choice != null
                    || action.NestedChoices is { Count: > 0 }
                    || action.NestedChoicesBeforePrimary != 0
                    || action.TurnStartChoices is { Count: > 0 }));
        if (!structural.IsSafe)
        {
            LogLiftClassification(action, structural);
            return structural;
        }

        Player? localPlayer = LocalContext.GetMe(state);
        CardModel? card = null;
        if (localPlayer?.PlayerCombatState is { } localCombat)
        {
            card = localCombat.Hand.Cards
                .Where(candidate => string.Equals(candidate.Id.Entry, action.CardId, StringComparison.Ordinal))
                .Skip(Math.Max(0, action.CardOccurrence))
                .FirstOrDefault();
        }

        bool isPromotedMultiplayerOnlyCard = card is not null
            && card.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly
            && string.Equals(card.Id.Entry, "LIFT", StringComparison.Ordinal);

        bool hasTarget = action.TargetCombatId is not null;
        bool targetExists = false;
        bool allowedTarget = false;
        if (action.TargetCombatId is { } targetId)
        {
            Creature? target = state.GetCreature(targetId);
            targetExists = target != null;
            if (localPlayer != null && target != null)
            {
                bool isLocalTarget = targetId == localPlayer.Creature.CombatId;
                bool isEnemyTarget = state.Enemies.Any(enemy => enemy.CombatId == targetId);
                bool isLivingTeammateTarget = state.Players.Any(candidate =>
                    candidate.NetId != localPlayer.NetId
                    && candidate.Creature.CombatId == targetId
                    && !candidate.Creature.IsDead);
                allowedTarget = isPromotedMultiplayerOnlyCard
                    ? isLivingTeammateTarget
                    : isLocalTarget || isEnemyTarget;
            }
        }

        SafeLocalActionDecision decision = MultiplayerSafeExecutePolicy.ClassifyResolved(
            new(
                HasLocalPlayer: localPlayer?.PlayerCombatState != null,
                HasLocalCard: card != null,
                IsMultiplayerOnlyCard: card?.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly,
                HasTarget: hasTarget,
                TargetExists: targetExists,
                IsAllowedTarget: allowedTarget,
                HasIncompleteTargetIdentity: !hasTarget
                    && (action.TargetIndex != -1 || !string.IsNullOrEmpty(action.TargetName)),
                IsPromotedMultiplayerOnlyCard: isPromotedMultiplayerOnlyCard));
        LogLiftClassification(action, decision);
        return decision;
    }

    private static void LogLiftClassification(PlanAction action, SafeLocalActionDecision decision)
    {
        if (!string.Equals(action.CardId, "LIFT", StringComparison.Ordinal))
            return;
        Entry.Logger.Info(
            $"[LIFT-DIAG] CLASSIFY safe={decision.IsSafe.ToString().ToLowerInvariant()} " +
            $"reason={decision.Reason} ends_continuation={decision.EndsContinuation.ToString().ToLowerInvariant()} " +
            $"target_combat_id={action.TargetCombatId?.ToString() ?? "-"}");
    }

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

    /// <summary>
    /// Multiplayer Safe Execute takes a bounded prefix only. Each returned action is classified again against
    /// the live state immediately before execution; the post-action session revalidation
    /// is the authority for admitting the next action.
    /// </summary>
    public static IReadOnlyList<PlanAction> TakeBoundedDeploymentSlice(
        CombatState state,
        IReadOnlyList<PlanAction> actions,
        out SafeLocalActionDecision stop)
        => MultiplayerSafeExecutePolicy.TakeBoundedSafePrefix(
            actions,
            action => Classify(state, action),
            out stop);

    /// <summary>
    /// Retained for the MP-2A validator's historical contract. New runtime deployments
    /// use <see cref="TakeBoundedDeploymentSlice"/> and the explicit execution session.
    /// </summary>
    public static IReadOnlyList<PlanAction> TakeMp2ADeploymentSlice(
        CombatState state,
        IReadOnlyList<PlanAction> actions,
        out SafeLocalActionDecision stop)
    {
        if (actions.Count == 0)
        {
            stop = SafeLocalActionDecision.Allow;
            return [];
        }

        PlanAction first = actions[0];
        SafeLocalActionDecision decision = Classify(state, first);
        if (!decision.IsSafe)
        {
            stop = decision;
            return [];
        }

        stop = actions.Count > 1
            ? new(false, MultiplayerSafeExecutePolicy.SingleActionLimitReason)
            : SafeLocalActionDecision.Allow;
        return [first];
    }
}
