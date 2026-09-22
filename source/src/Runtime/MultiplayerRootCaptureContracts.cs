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
            _ = simulator.State.GetPlayerCombatState(player);

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
}
