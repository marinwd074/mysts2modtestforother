using MegaCrit.Sts2.Core.Entities.Creatures;

namespace CombatSolver;

internal static class MultiplayerCombatObjectivePolicy
{
    // The project historically used 35% remaining durability as the lethal boundary.
    // AdaptiveLethalTempo replaced the discontinuous objective jump with a smooth urgency
    // curve; runtime replan gating can still reuse that established boundary without
    // changing route ranking.
    internal const double LegacyLethalDurabilityBoundary = 0.35d;
    internal static readonly double LegacyLethalUrgencyBoundary =
        MultiplayerCombatObjectiveMath.ComputeLethalUrgency(
            LegacyLethalDurabilityBoundary);

    internal static bool IsInLethalRecalculationWindow(IEnumerable<Creature> enemies)
    {
        double durabilityRatio = ComputeEnemyDurabilityRatio(enemies);
        return MultiplayerCombatObjectiveMath.ComputeLethalUrgency(durabilityRatio)
            >= LegacyLethalUrgencyBoundary;
    }

    internal static double ComputeEnemyDurabilityRatio(IEnumerable<Creature> enemies)
    {
        long maximumHp = 0;
        long durability = 0;
        foreach (Creature enemy in enemies)
        {
            maximumHp += Math.Max(0, enemy.MaxHp);
            durability += Math.Max(0, enemy.CurrentHp) + Math.Max(0, enemy.Block);
        }

        return ComputeEnemyDurabilityRatio(durability, maximumHp);
    }

    internal static double ComputeEnemyDurabilityRatio(
        EnemyDurabilityVector enemies,
        int initialEnemyMaximumHp)
    {
        long durability = 0;
        for (int index = 0; index < enemies.Count; index++)
            durability += Math.Max(0, enemies[index].Durability);
        return ComputeEnemyDurabilityRatio(durability, initialEnemyMaximumHp);
    }

    private static double ComputeEnemyDurabilityRatio(
        long durability,
        long maximumHp)
    {
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
