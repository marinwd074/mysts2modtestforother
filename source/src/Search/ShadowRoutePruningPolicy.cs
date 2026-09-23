namespace CombatSolver;

internal readonly record struct ShadowApproximateQuality(
    bool CompleteVictory,
    bool AllPlayersAlive,
    int EnemyDurability,
    int TeamEffectiveHp,
    double WorstPlayerEffectiveHpRatio,
    int TeamEnergy,
    int TeamStars,
    int ActionCount);

/// <summary>
/// Pure contracts for Shadow pruning. Exact dominance is handled by future-state equality.
/// This type contains only the explicitly approximate quality relation used after beam overflow.
/// </summary>
internal static class ShadowRoutePruningPolicy
{
    internal static bool HeuristicQualityDominates(
        ShadowApproximateQuality left,
        ShadowApproximateQuality right)
    {
        bool noWorse = (left.CompleteVictory || !right.CompleteVictory)
            && (left.AllPlayersAlive || !right.AllPlayersAlive)
            && left.EnemyDurability <= right.EnemyDurability
            && left.TeamEffectiveHp >= right.TeamEffectiveHp
            && left.WorstPlayerEffectiveHpRatio >= right.WorstPlayerEffectiveHpRatio
            && left.TeamEnergy >= right.TeamEnergy
            && left.TeamStars >= right.TeamStars
            && left.ActionCount <= right.ActionCount;
        if (!noWorse)
            return false;

        return left.CompleteVictory != right.CompleteVictory
            || left.AllPlayersAlive != right.AllPlayersAlive
            || left.EnemyDurability != right.EnemyDurability
            || left.TeamEffectiveHp != right.TeamEffectiveHp
            || !left.WorstPlayerEffectiveHpRatio.Equals(right.WorstPlayerEffectiveHpRatio)
            || left.TeamEnergy != right.TeamEnergy
            || left.TeamStars != right.TeamStars
            || left.ActionCount != right.ActionCount;
    }

    internal static bool MayUseApproximateBeamPruning(
        int exactSurvivorCount,
        int beamLimit)
    {
        if (beamLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(beamLimit));
        return exactSurvivorCount > beamLimit;
    }
}
