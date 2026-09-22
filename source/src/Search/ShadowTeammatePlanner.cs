using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal readonly record struct ShadowTeammateActionCandidate(
    string PlayerNetId,
    int HandIndex,
    string CardId,
    int UpgradeLevel,
    string SemanticKey,
    int EnergyCost,
    int StarCost,
    uint? TargetCombatId);

/// <summary>
/// Generates exact legal current-state teammate actions from a detached prediction fork.
/// It never creates deployment actions and never widens RootActionPlayers.
/// </summary>
internal static class ShadowTeammatePlanner
{
    internal static IReadOnlyList<ShadowTeammateActionCandidate> EnumerateLegalActions(
        CombatPredictionSimulator simulator,
        Player teammate)
    {
        if (!simulator.State.RootCapturedPlayers.Any(player => ReferenceEquals(player, teammate)))
            throw new InvalidOperationException("Shadow teammate is outside the captured prediction root.");
        if (simulator.State.RootActionPlayers.Any(player => ReferenceEquals(player, teammate)))
            throw new InvalidOperationException("Shadow teammate unexpectedly belongs to the deployment action scope.");

        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(teammate);
        List<ShadowTeammateActionCandidate> candidates = [];
        for (int handIndex = 0; handIndex < playerState.Hand.Cards.Count; handIndex++)
        {
            PredictedCard card = playerState.Hand.Cards[handIndex];
            if (!simulator.CanPlay(card, out int energyCost, out int starCost))
                continue;

            string semanticKey = CardChoiceSupport.ChoiceCardKey(card);
            foreach (Creature? target in EnumerateTargets(simulator, card))
            {
                candidates.Add(new ShadowTeammateActionCandidate(
                    teammate.NetId.ToString(),
                    handIndex,
                    card.Preview.Id.Entry,
                    card.Preview.CurrentUpgradeLevel,
                    semanticKey,
                    energyCost,
                    starCost,
                    target?.CombatId));
            }
        }

        return candidates;
    }

    private static IEnumerable<Creature?> EnumerateTargets(
        CombatPredictionSimulator simulator,
        PredictedCard card)
    {
        TargetType targetType = simulator.GetTargetType(card);
        if (targetType == TargetType.AnyEnemy)
        {
            foreach (Creature enemy in simulator.State.Enemies)
            {
                if (simulator.State.IsHittable(enemy))
                    yield return enemy;
            }
            yield break;
        }

        if (targetType is TargetType.AnyPlayer or TargetType.AnyAlly)
        {
            foreach (Creature target in simulator.State.GetValidManualTargets(
                         card.Preview.Owner.Creature,
                         targetType))
            {
                yield return target;
            }
            yield break;
        }

        yield return null;
    }
}
