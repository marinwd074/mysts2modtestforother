using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver.Engine.Common;

/// <summary>
/// Small, allocation-free predicates shared by multiplayer Advisor simulation paths
/// and their regression contracts. They describe admission boundaries only; they do
/// not provide a fallback for unknown semantics.
/// </summary>
internal static class MultiplayerAdvisorBoundaryContracts
{
    internal static bool IsCapturedPlayer(IReadOnlyList<Player> capturedPlayers, Player player)
    {
        for (int index = 0; index < capturedPlayers.Count; index++)
        {
            if (ReferenceEquals(capturedPlayers[index], player))
                return true;
        }
        return false;
    }

    internal static void RequireCapturedPlayer(
        IReadOnlyList<Player> capturedPlayers,
        Player player)
    {
        if (!IsCapturedPlayer(capturedPlayers, player))
        {
            throw new PredictionUnsupportedException(
                $"Side-turn relic hooks require captured inventory for player {player.NetId}.");
        }
    }

    /// <summary>
    /// EndTurn must consume the captured root list, never expand it to the public roster.
    /// The subset check catches a malformed root before a private combat state is touched.
    /// </summary>
    internal static IReadOnlyList<Player> SelectEndTurnPlayers(
        IReadOnlyList<Player> publicPlayers,
        IReadOnlyList<Player> rootCapturedPlayers)
    {
        for (int index = 0; index < rootCapturedPlayers.Count; index++)
        {
            if (!IsCapturedPlayer(publicPlayers, rootCapturedPlayers[index]))
            {
                throw new InvalidOperationException(
                    "EndTurn root contains a player outside the public combat roster.");
            }
        }
        return rootCapturedPlayers;
    }

    internal static bool ShouldApplyEnemyBlockScaling(
        bool isPrimaryEnemy,
        bool isSecondaryEnemy,
        bool isPoweredCardOrMonsterMoveBlock)
        => (isPrimaryEnemy || isSecondaryEnemy) && isPoweredCardOrMonsterMoveBlock;
}
