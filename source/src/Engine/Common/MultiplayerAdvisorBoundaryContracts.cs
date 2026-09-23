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
    /// EndTurn must consume the explicit action-owner list, never expand it to the readable
    /// player roster. The subset check catches malformed ownership before turn state mutates.
    /// </summary>
    internal static IReadOnlyList<Player> SelectEndTurnPlayers(
        IReadOnlyList<Player> publicPlayers,
        IReadOnlyList<Player> actionPlayers)
    {
        for (int index = 0; index < actionPlayers.Count; index++)
        {
            if (!IsCapturedPlayer(publicPlayers, actionPlayers[index]))
            {
                throw new InvalidOperationException(
                    "EndTurn root contains a player outside the public combat roster.");
            }
        }
        return actionPlayers;
    }

    /// <summary>
    /// Joint forecast may advance captured remote players without granting them action/deployment
    /// ownership. This validates only prediction-state membership; RootActionPlayers is unchanged.
    /// </summary>
    internal static IReadOnlyList<Player> SelectForecastEndTurnPlayers(
        IReadOnlyList<Player> capturedPlayers,
        IReadOnlyList<Player> forecastPlayers)
    {
        for (int index = 0; index < forecastPlayers.Count; index++)
        {
            if (!IsCapturedPlayer(capturedPlayers, forecastPlayers[index]))
            {
                throw new InvalidOperationException(
                    "Forecast EndTurn contains a player outside the captured combat roster.");
            }
        }
        return forecastPlayers;
    }

    internal static bool ShouldApplyEnemyBlockScaling(
        bool isPrimaryEnemy,
        bool isSecondaryEnemy,
        bool isPoweredCardOrMonsterMoveBlock)
        => (isPrimaryEnemy || isSecondaryEnemy) && isPoweredCardOrMonsterMoveBlock;
}
