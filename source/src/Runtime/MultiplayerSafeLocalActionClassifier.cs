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
            return structural;

        Player? localPlayer = LocalContext.GetMe(state);
        CardModel? card = null;
        if (localPlayer?.PlayerCombatState is { } localCombat)
        {
            card = localCombat.Hand.Cards
                .Where(candidate => string.Equals(candidate.Id.Entry, action.CardId, StringComparison.Ordinal))
                .Skip(Math.Max(0, action.CardOccurrence))
                .FirstOrDefault();
        }

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
                allowedTarget = isLocalTarget || isEnemyTarget;
            }
        }

        return MultiplayerSafeExecutePolicy.ClassifyResolved(
            new(
                HasLocalPlayer: localPlayer?.PlayerCombatState != null,
                HasLocalCard: card != null,
                IsMultiplayerOnlyCard: card?.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly,
                HasTarget: hasTarget,
                TargetExists: targetExists,
                IsAllowedTarget: allowedTarget,
                HasIncompleteTargetIdentity: !hasTarget
                    && (action.TargetIndex != -1 || !string.IsNullOrEmpty(action.TargetName))));
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
    /// Dormant MP-2A executes at most one safe local card. A fresh observation/search
    /// is required before another deployment, avoiding ambiguous local-vs-remote
    /// WorldVersion changes during a multi-card sequence.
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

        stop = MultiplayerSafeExecutePolicy.DeploymentStopAfter(1, actions.Count);
        return [first];
    }
}
