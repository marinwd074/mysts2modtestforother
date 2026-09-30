namespace CombatSolver;

internal static class SolverInterimResultOrdering
{
    public static bool IsCompleteVictory(
        int actionCount,
        bool allEnemiesDead,
        bool playerDead,
        int projectedPlayerHp)
        => actionCount > 0
            && allEnemiesDead
            && !playerDead
            && projectedPlayerHp > 0;

    /// <summary>
    /// Compares the result-quality prefix shared by in-session selection, final candidate
    /// retention, and cross-session audits. A negative value means candidate is better.
    /// Rolling horizon compares raw battle loss, victory and earlier victory before strategic
    /// growth credit. Other modes retain their strategic-loss/growth ordering.
    /// </summary>
    public static int ComparePrimaryQuality(
        bool candidateCompleteVictory,
        int candidateStrategicHpDeficit,
        int? candidateCombatEndedTurn,
        bool currentCompleteVictory,
        int currentStrategicHpDeficit,
        int? currentCombatEndedTurn,
        int candidateGrowthHpCredit = 0,
        int currentGrowthHpCredit = 0,
        int candidateGrowthRewardCount = 0,
        int currentGrowthRewardCount = 0,
        int candidateDeathSaveUseCount = 0,
        int currentDeathSaveUseCount = 0,
        bool rollingHorizonLossFirst = false,
        int? candidateBattleHpLost = null,
        int? currentBattleHpLost = null)
    {
        if (!rollingHorizonLossFirst)
        {
            return CompareStrategicPrimaryQuality(
                candidateCompleteVictory,
                candidateStrategicHpDeficit,
                candidateCombatEndedTurn,
                currentCompleteVictory,
                currentStrategicHpDeficit,
                currentCombatEndedTurn,
                candidateGrowthHpCredit,
                currentGrowthHpCredit,
                candidateGrowthRewardCount,
                currentGrowthRewardCount,
                candidateDeathSaveUseCount,
                currentDeathSaveUseCount);
        }

        if (candidateBattleHpLost == null || currentBattleHpLost == null)
            throw new ArgumentException("Rolling-horizon quality requires raw battle HP loss.");

        int comparison = candidateDeathSaveUseCount.CompareTo(currentDeathSaveUseCount);
        if (comparison != 0)
            return comparison;
        comparison = CompareRollingHorizonQuality(
            candidateCompleteVictory,
            candidateBattleHpLost.Value,
            candidateCombatEndedTurn,
            currentCompleteVictory,
            currentBattleHpLost.Value,
            currentCombatEndedTurn);
        if (comparison != 0)
            return comparison;
        comparison = candidateStrategicHpDeficit.CompareTo(currentStrategicHpDeficit);
        if (comparison != 0)
            return comparison;
        comparison = currentGrowthHpCredit.CompareTo(candidateGrowthHpCredit);
        if (comparison != 0)
            return comparison;
        comparison = currentGrowthRewardCount.CompareTo(candidateGrowthRewardCount);
        if (comparison != 0)
            return comparison;
        comparison = currentCompleteVictory.CompareTo(candidateCompleteVictory);
        if (comparison != 0)
            return comparison;
        return (candidateCombatEndedTurn ?? int.MaxValue)
            .CompareTo(currentCombatEndedTurn ?? int.MaxValue);
    }

    private static int CompareStrategicPrimaryQuality(
        bool candidateCompleteVictory,
        int candidateStrategicHpDeficit,
        int? candidateCombatEndedTurn,
        bool currentCompleteVictory,
        int currentStrategicHpDeficit,
        int? currentCombatEndedTurn,
        int candidateGrowthHpCredit,
        int currentGrowthHpCredit,
        int candidateGrowthRewardCount,
        int currentGrowthRewardCount,
        int candidateDeathSaveUseCount,
        int currentDeathSaveUseCount)
    {
        int comparison = currentCompleteVictory.CompareTo(candidateCompleteVictory);
        if (comparison != 0)
            return comparison;
        comparison = candidateDeathSaveUseCount.CompareTo(currentDeathSaveUseCount);
        if (comparison != 0)
            return comparison;
        comparison = candidateStrategicHpDeficit.CompareTo(currentStrategicHpDeficit);
        if (comparison != 0)
            return comparison;
        comparison = currentGrowthHpCredit.CompareTo(candidateGrowthHpCredit);
        if (comparison != 0)
            return comparison;
        comparison = currentGrowthRewardCount.CompareTo(candidateGrowthRewardCount);
        if (comparison != 0)
            return comparison;
        return (candidateCombatEndedTurn ?? int.MaxValue)
            .CompareTo(currentCombatEndedTurn ?? int.MaxValue);
    }

    private static int CompareRollingHorizonQuality(
        bool candidateWon, int candidateBattleHpLost, int? candidateEndedTurn,
        bool currentWon, int currentBattleHpLost, int? currentEndedTurn)
    {
        int comparison = candidateBattleHpLost.CompareTo(currentBattleHpLost);
        if (comparison != 0)
            return comparison;
        comparison = currentWon.CompareTo(candidateWon);
        if (comparison != 0 || !candidateWon)
            return comparison;
        return (candidateEndedTurn ?? int.MaxValue).CompareTo(currentEndedTurn ?? int.MaxValue);
    }

    /// <summary>
    /// Secondary value for otherwise-equal completed routes. Persistent setup is worth remembering
    /// only when it can affect a later turn; a route that already wins on the current root turn
    /// must never add a Power merely to improve this tie-break.
    /// </summary>
    public static int CompletedRouteSetupTieBreakValue(
        bool completeVictory,
        int? combatEndedTurn,
        int startTurnNumber,
        int peakPersistentBuffValue)
        => completeVictory
            && combatEndedTurn is int endTurn
            && endTurn > startTurnNumber
                ? Math.Max(0, peakPersistentBuffValue)
                : 0;

    public static bool IsBetter(SolverInterimResult candidate, SolverInterimResult current)
    {
        bool rollingHorizonLossFirst =
            candidate.RollingHorizonLossFirst && current.RollingHorizonLossFirst;
        if (!rollingHorizonLossFirst)
            return IsBetterStrategic(candidate, current);

        int comparison = current.Survives.CompareTo(candidate.Survives);
        if (comparison != 0)
            return comparison < 0;
        if (candidate.DeathSaveUseCount != current.DeathSaveUseCount)
            return candidate.DeathSaveUseCount < current.DeathSaveUseCount;
        if (candidate.TheftPolicy == SolverTheftPolicy.PreserveResources
            && candidate.OutstandingStolenResource != current.OutstandingStolenResource)
            return candidate.OutstandingStolenResource < current.OutstandingStolenResource;
        comparison = CompareRollingHorizonQuality(
            candidate.Won,
            candidate.ProjectedBattleHpLost,
            candidate.CombatEndedTurn,
            current.Won,
            current.ProjectedBattleHpLost,
            current.CombatEndedTurn);
        if (comparison != 0)
            return comparison < 0;
        if (IsResourceTradeImprovement(candidate, current))
            return true;
        if (IsResourceTradeImprovement(current, candidate))
            return false;
        if (candidate.GrowthHpCredit != current.GrowthHpCredit)
            return candidate.GrowthHpCredit > current.GrowthHpCredit;
        if (candidate.GrowthRewardCount != current.GrowthRewardCount)
            return candidate.GrowthRewardCount > current.GrowthRewardCount;
        comparison = (candidate.CombatEndedTurn ?? int.MaxValue)
            .CompareTo(current.CombatEndedTurn ?? int.MaxValue);
        if (comparison != 0)
            return comparison < 0;
        if (candidate.ProjectedBattlePotionCount != current.ProjectedBattlePotionCount)
            return candidate.ProjectedBattlePotionCount < current.ProjectedBattlePotionCount;
        if (candidate.EnemyHp != current.EnemyHp)
            return candidate.EnemyHp < current.EnemyHp;
        return candidate.Score > current.Score;
    }

    private static bool IsBetterStrategic(
        SolverInterimResult candidate,
        SolverInterimResult current)
    {
        int comparison = current.Won.CompareTo(candidate.Won);
        if (comparison != 0)
            return comparison < 0;
        comparison = current.Survives.CompareTo(candidate.Survives);
        if (comparison != 0)
            return comparison < 0;
        if (candidate.DeathSaveUseCount != current.DeathSaveUseCount)
            return candidate.DeathSaveUseCount < current.DeathSaveUseCount;
        if (candidate.TheftPolicy == SolverTheftPolicy.PreserveResources
            && candidate.OutstandingStolenResource != current.OutstandingStolenResource)
            return candidate.OutstandingStolenResource < current.OutstandingStolenResource;
        if (IsResourceTradeImprovement(candidate, current))
            return true;
        if (IsResourceTradeImprovement(current, candidate))
            return false;
        if (candidate.GrowthHpCredit != current.GrowthHpCredit)
            return candidate.GrowthHpCredit > current.GrowthHpCredit;
        if (candidate.GrowthRewardCount != current.GrowthRewardCount)
            return candidate.GrowthRewardCount > current.GrowthRewardCount;
        comparison = (candidate.CombatEndedTurn ?? int.MaxValue)
            .CompareTo(current.CombatEndedTurn ?? int.MaxValue);
        if (comparison != 0)
            return comparison < 0;
        if (candidate.ProjectedBattlePotionCount != current.ProjectedBattlePotionCount)
            return candidate.ProjectedBattlePotionCount < current.ProjectedBattlePotionCount;
        if (candidate.EnemyHp != current.EnemyHp)
            return candidate.EnemyHp < current.EnemyHp;
        return candidate.Score > current.Score;
    }

    public static bool CanPromoteDisplayedResult(
        SolverInterimResult candidate,
        SolverInterimResult current)
    {
        bool rollingHorizonLossFirst =
            candidate.RollingHorizonLossFirst && current.RollingHorizonLossFirst;
        if (rollingHorizonLossFirst)
        {
            return IsBetter(candidate, current);
        }
        return (!candidate.Won
                || candidate.Survives && !current.Survives
                || candidate.DeathSaveUseCount < current.DeathSaveUseCount
                || candidate.TheftPolicy == SolverTheftPolicy.PreserveResources
                    && candidate.OutstandingStolenResource < current.OutstandingStolenResource
                || !current.Won
                || candidate.ProjectedBattleHpLost - candidate.GrowthHpCredit
                    <= current.ProjectedBattleHpLost - current.GrowthHpCredit)
            && IsBetter(candidate, current);
    }

    internal static bool IsResourceTradeImprovement(
        int candidateHpDeficit,
        int candidatePotionCost,
        int currentHpDeficit,
        int currentPotionCost)
    {
        int candidateBurden = checked(candidateHpDeficit + candidatePotionCost);
        int currentBurden = checked(currentHpDeficit + currentPotionCost);
        return candidateBurden < currentBurden
            || candidateBurden == currentBurden && candidateHpDeficit < currentHpDeficit;
    }

    private static bool IsResourceTradeImprovement(
        SolverInterimResult candidate,
        SolverInterimResult current)
        => IsResourceTradeImprovement(
            candidate.StrategicHpDeficit,
            candidate.PotionStrategicCost,
            current.StrategicHpDeficit,
            current.PotionStrategicCost);
}
