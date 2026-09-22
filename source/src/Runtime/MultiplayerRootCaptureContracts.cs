using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

/// <summary>
/// Fail-closed contracts for multiplayer roots that may read every player state already
/// materialized in the local game process. This widens read scope only; action ownership
/// and network capabilities remain governed by the multiplayer session/deployment policy.
/// </summary>
internal static class MultiplayerRootCaptureContracts
{
    internal static void Verify(
        CombatState live,
        CombatPredictionSimulator simulator,
        Player localPlayer)
    {
        IReadOnlyList<Player> captured = simulator.State.RootCapturedPlayers;
        if (captured.Count != live.Players.Count
            || live.Players.Any(player => !MultiplayerAdvisorBoundaryContracts.IsCapturedPlayer(captured, player)))
        {
            throw new InvalidOperationException(
                "Multiplayer readable-state root must capture the complete local combat roster.");
        }
        if (!MultiplayerAdvisorBoundaryContracts.IsCapturedPlayer(captured, localPlayer))
            throw new InvalidOperationException("Multiplayer root omitted the local player.");

        IReadOnlyList<Player> actionPlayers = simulator.State.RootActionPlayers;
        if (actionPlayers.Count != 1 || !ReferenceEquals(actionPlayers[0], localPlayer))
        {
            throw new InvalidOperationException(
                "Multiplayer prediction action scope must contain exactly the local player.");
        }
        if (actionPlayers.Any(player =>
                !MultiplayerAdvisorBoundaryContracts.IsCapturedPlayer(captured, player)))
        {
            throw new InvalidOperationException(
                "Multiplayer prediction action scope escaped the readable root.");
        }

        foreach (Player player in live.Players)
        {
            VerifyExactPlayerCombatState(player, simulator);

            SimCreatureState frozen = simulator.State.GetCreature(player.Creature);
            if (ReferenceEquals(frozen, player.Creature))
            {
                throw new InvalidOperationException(
                    $"Player {player.NetId} creature state retained the live object.");
            }
        }

        if (simulator.State.CombatState is SimulatedCombatState simulated
            && !simulated.RootMultiplayerScalingIsDetached)
        {
            throw new InvalidOperationException(
                "Multiplayer scaling still retains a live RunState or CombatState reference.");
        }

        if (simulator.State.CombatState is SimulatedCombatState withRemoteRelics
            && withRemoteRelics.RootUnsupportedRemotePublicRelicListenerCount != 0)
        {
            throw new PredictionUnsupportedException(
                "A locally readable player relic was not captured into the multiplayer root.");
        }
    }

    private static void VerifyExactPlayerCombatState(
        Player player,
        CombatPredictionSimulator simulator)
    {
        PlayerCombatState live = player.PlayerCombatState
            ?? throw new InvalidOperationException(
                $"Player {player.NetId} has no live combat state during root capture.");
        SimPlayerCombatState predicted = simulator.State.GetPlayerCombatState(player);

        if (predicted.Energy != live.Energy
            || predicted.Stars != live.Stars
            || predicted.Phase != live.Phase)
        {
            throw new InvalidOperationException(
                $"Player {player.NetId} resource state differs from the multiplayer prediction root.");
        }

        VerifyPile(player, "hand", live.Hand.Cards, predicted.Hand.Cards);
        VerifyPile(player, "draw", live.DrawPile.Cards, predicted.DrawPile.Cards);
        VerifyPile(player, "discard", live.DiscardPile.Cards, predicted.DiscardPile.Cards);
        VerifyPile(player, "exhaust", live.ExhaustPile.Cards, predicted.ExhaustPile.Cards);
        VerifyPile(player, "play", live.PlayPile.Cards, predicted.PlayPile.Cards);

        if (predicted.OrbQueue.Capacity != live.OrbQueue.Capacity
            || !predicted.OrbQueue.Orbs.Select(orb => orb.Id.Entry)
                .SequenceEqual(live.OrbQueue.Orbs.Select(orb => orb.Id.Entry), StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Player {player.NetId} orb state differs from the multiplayer prediction root.");
        }
    }

    private static void VerifyPile(
        Player player,
        string pileName,
        IReadOnlyList<MegaCrit.Sts2.Core.Models.CardModel> liveCards,
        IReadOnlyList<PredictedCard> predictedCards)
    {
        if (liveCards.Count != predictedCards.Count)
        {
            throw new InvalidOperationException(
                $"Player {player.NetId} {pileName} count differs from the multiplayer prediction root.");
        }

        for (int index = 0; index < liveCards.Count; index++)
        {
            MegaCrit.Sts2.Core.Models.CardModel liveCard = liveCards[index];
            PredictedCard predictedCard = predictedCards[index];
            if (!string.Equals(liveCard.Id.Entry, predictedCard.Preview.Id.Entry, StringComparison.Ordinal)
                || liveCard.CurrentUpgradeLevel != predictedCard.Preview.CurrentUpgradeLevel)
            {
                throw new InvalidOperationException(
                    $"Player {player.NetId} {pileName}[{index}] differs from the multiplayer prediction root.");
            }
        }
    }
}
