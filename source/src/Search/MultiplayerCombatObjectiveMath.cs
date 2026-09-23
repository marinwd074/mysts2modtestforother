namespace CombatSolver;

internal enum MultiplayerCombatObjectiveStrategy
{
    MinimizeTeamLoss,
    AdaptiveLethalTempo,
}

internal readonly record struct MultiplayerCombatObjectiveRank(
    bool CompleteVictory,
    bool AllPlayersAlive,
    double LossEquivalent,
    double WorstPlayerLossRatio,
    double TeamLossRatio,
    double EnemyDurabilityRatio,
    int CombatEndedTurn);

/// <summary>
/// Pure math for the multiplayer loss/tempo objective. Terminal selection and intermediate
/// retention use the same normalized loss-equivalent scale. The terminal form prices each
/// additional turn; the intermediate form grants a bounded progress credit so Beam pruning
/// does not optimize a different objective from the final selector.
/// </summary>
internal static class MultiplayerCombatObjectiveMath
{
    internal const double MaximumExtraLossRatioPerTurn = 0.05d;

    internal static double ComputeLethalUrgency(double enemyDurabilityRatio)
    {
        double remaining = Math.Clamp(enemyDurabilityRatio, 0d, 1d);
        double progress = 1d - remaining;
        // Smoothstep: zero pressure at full durability, full pressure at zero durability,
        // with no discontinuity or endpoint kink.
        return progress * progress * (3d - 2d * progress);
    }

    internal static double ComputeTeamRiskFactor(double worstPlayerLossRatio)
    {
        double accumulatedRisk = Math.Clamp(worstPlayerLossRatio, 0d, 1d);
        // Team loss itself is already charged directly by the objective. This factor only
        // prices the danger of allowing another enemy turn: healthy=0.5, heavily damaged=1.
        // Increasing risk must never make the same route look cheaper.
        return 0.5d * (1d + accumulatedRisk);
    }

    internal static double LossRatioPerTurn(
        double enemyDurabilityRatio,
        double worstPlayerLossRatio)
        => MaximumExtraLossRatioPerTurn
            * ComputeLethalUrgency(enemyDurabilityRatio)
            * ComputeTeamRiskFactor(worstPlayerLossRatio);

    internal static double ContinuousTempoScore(
        double teamLossRatio,
        double worstPlayerLossRatio,
        double enemyDurabilityRatio,
        int combatEndedTurn,
        int startTurnNumber)
    {
        int turnsToEnd = Math.Max(0, combatEndedTurn - startTurnNumber);
        return Math.Max(0d, teamLossRatio)
            + turnsToEnd * LossRatioPerTurn(
                enemyDurabilityRatio,
                worstPlayerLossRatio);
    }

    internal static double InterimLossEquivalent(
        double teamLossRatio,
        double worstPlayerLossRatio,
        double enemyDurabilityRatio)
    {
        double remaining = Math.Clamp(enemyDurabilityRatio, 0d, 1d);
        double progress = 1d - remaining;
        double progressCredit = progress * LossRatioPerTurn(
            remaining,
            worstPlayerLossRatio);
        return Math.Max(0d, teamLossRatio) - progressCredit;
    }

    internal static MultiplayerCombatObjectiveRank BuildRank(
        MultiplayerCombatObjectiveStrategy strategy,
        bool completeVictory,
        bool allPlayersAlive,
        double teamLossRatio,
        double worstPlayerLossRatio,
        double enemyDurabilityRatio,
        double terminalTempoReferenceEnemyDurabilityRatio,
        int? combatEndedTurn,
        int startTurnNumber)
    {
        int endedTurn = completeVictory
            ? combatEndedTurn
                ?? throw new ArgumentException(
                    "A complete multiplayer victory requires a combat-ended turn.",
                    nameof(combatEndedTurn))
            : int.MaxValue;
        double lossEquivalent = strategy == MultiplayerCombatObjectiveStrategy.AdaptiveLethalTempo
            ? completeVictory
                ? ContinuousTempoScore(
                    teamLossRatio,
                    worstPlayerLossRatio,
                    terminalTempoReferenceEnemyDurabilityRatio,
                    endedTurn,
                    startTurnNumber)
                : InterimLossEquivalent(
                    teamLossRatio,
                    worstPlayerLossRatio,
                    enemyDurabilityRatio)
            : Math.Max(0d, teamLossRatio);
        return new MultiplayerCombatObjectiveRank(
            completeVictory,
            allPlayersAlive,
            lossEquivalent,
            Math.Max(0d, worstPlayerLossRatio),
            Math.Max(0d, teamLossRatio),
            Math.Clamp(enemyDurabilityRatio, 0d, 1d),
            endedTurn);
    }

    /// <summary>
    /// Negative means left is preferred. This ordering is the shared P1 team objective used by
    /// final candidate preselection, final policy ordering, ordinary Beam ranking and same-state
    /// representative selection.
    /// </summary>
    internal static int Compare(
        MultiplayerCombatObjectiveRank left,
        MultiplayerCombatObjectiveRank right)
    {
        int comparison = right.CompleteVictory.CompareTo(left.CompleteVictory);
        if (comparison != 0)
            return comparison;
        comparison = right.AllPlayersAlive.CompareTo(left.AllPlayersAlive);
        if (comparison != 0)
            return comparison;
        comparison = left.LossEquivalent.CompareTo(right.LossEquivalent);
        if (comparison != 0)
            return comparison;
        comparison = left.WorstPlayerLossRatio.CompareTo(right.WorstPlayerLossRatio);
        if (comparison != 0)
            return comparison;
        comparison = left.TeamLossRatio.CompareTo(right.TeamLossRatio);
        if (comparison != 0)
            return comparison;
        return left.CompleteVictory && right.CompleteVictory
            ? left.CombatEndedTurn.CompareTo(right.CombatEndedTurn)
            : left.EnemyDurabilityRatio.CompareTo(right.EnemyDurabilityRatio);
    }
}
