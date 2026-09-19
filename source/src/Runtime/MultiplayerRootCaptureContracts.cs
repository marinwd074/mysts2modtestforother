using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

/// <summary>
/// Small fail-closed contracts for the first local-player-only multiplayer root.
/// These checks make an unsupported public/private boundary explicit instead of
/// allowing a search to fall back to a live teammate object.
/// </summary>
internal static class MultiplayerRootCaptureContracts
{
    internal static void Verify(
        CombatState live,
        CombatPredictionSimulator simulator,
        Player localPlayer)
    {
        IReadOnlyList<Player> captured = simulator.State.RootCapturedPlayers;
        if (captured.Count != 1 || !ReferenceEquals(captured[0], localPlayer))
        {
            throw new InvalidOperationException(
                "Multiplayer local-player root must capture exactly the local player.");
        }
        _ = simulator.State.GetPlayerCombatState(localPlayer);

        foreach (Player remote in live.Players.Where(player => !ReferenceEquals(player, localPlayer)))
        {
            bool remotePrivateWasCaptured = false;
            try
            {
                _ = simulator.State.GetPlayerCombatState(remote);
                remotePrivateWasCaptured = true;
            }
            catch (InvalidOperationException)
            {
                // Expected: remote private combat state is outside the root.
            }

            if (remotePrivateWasCaptured)
            {
                throw new InvalidOperationException(
                    $"Remote player {remote.NetId} private combat state escaped the root boundary.");
            }

            // Creature state is public combat context and must be materialized in the
            // detached root even though the player's private piles are not captured.
            SimCreatureState frozen = simulator.State.GetCreature(remote.Creature);
            if (ReferenceEquals(frozen, remote.Creature))
            {
                throw new InvalidOperationException(
                    $"Remote player {remote.NetId} creature state retained the live object.");
            }
        }

        if (simulator.State.CombatState is SimulatedCombatState simulated
            && !simulated.RootMultiplayerScalingIsDetached)
        {
            throw new InvalidOperationException(
                "Multiplayer scaling still retains a live RunState or CombatState reference.");
        }

        if (simulator.State.CombatState is SimulatedCombatState withRemoteRelics
            && withRemoteRelics.RootRemotePublicRelicListenerCount != 0)
        {
            throw new PredictionUnsupportedException(
                "Remote public relic hooks require a targeted multiplayer semantic capture contract.");
        }
    }
}
