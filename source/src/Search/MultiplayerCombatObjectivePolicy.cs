using MegaCrit.Sts2.Core.Entities.Creatures;

namespace CombatSolver;

internal enum MultiplayerCombatObjectiveStrategy
{
    MinimizeTeamLoss,
    AdaptiveLethalTempo,
}

internal static class MultiplayerCombatObjectivePolicy
{
    internal static double ComputeEnemyDurabilityRatio(IEnumerable<Creature> enemies)
    {
        long maximumHp = 0;
        long durability = 0;
        foreach (Creature enemy in enemies)
        {
            maximumHp += Math.Max(0, enemy.MaxHp);
            durability += Math.Max(0, enemy.CurrentHp) + Math.Max(0, enemy.Block);
        }

        if (maximumHp <= 0)
            return 0d;
        return Math.Clamp(durability / (double)maximumHp, 0d, 1d);
    }

    internal static bool UsesAdaptiveLethalTempo(
        SearchRoutePolicy routePolicy,
        MultiplayerCombatObjectiveStrategy strategy)
        => routePolicy == SearchRoutePolicy.MultiplayerLocalCrossTurn
            && strategy == MultiplayerCombatObjectiveStrategy.AdaptiveLethalTempo;
}
