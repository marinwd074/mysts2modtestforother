using MegaCrit.Sts2.Core.Entities.Creatures;

namespace CombatSolver;

internal enum MultiplayerCombatObjectiveStrategy
{
    MinimizeTeamLoss,
    AdaptiveLethalTempo,
}

internal static class MultiplayerCombatObjectivePolicy
{
    internal const double LethalDurabilityRatioThreshold = 0.35d;
    internal const double ExtraLossRatioPerTurnSaved = 0.05d;

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

    internal static bool UsesLethalTempoTradeoff(
        SearchRoutePolicy routePolicy,
        MultiplayerCombatObjectiveStrategy strategy,
        double enemyDurabilityRatio)
        => routePolicy == SearchRoutePolicy.MultiplayerLocalCrossTurn
            && strategy == MultiplayerCombatObjectiveStrategy.AdaptiveLethalTempo
            && enemyDurabilityRatio <= LethalDurabilityRatioThreshold;

    internal static double LethalTempoScore(
        int strategicHpDeficit,
        int playerMaxHp,
        int combatEndedTurn,
        int startTurnNumber)
    {
        double lossRatio = Math.Max(0, strategicHpDeficit) / (double)Math.Max(1, playerMaxHp);
        int turnsToEnd = Math.Max(0, combatEndedTurn - startTurnNumber);
        return lossRatio + turnsToEnd * ExtraLossRatioPerTurnSaved;
    }
}
