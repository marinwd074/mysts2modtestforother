namespace CombatSolver;

/// <summary>
/// Pure math for the multiplayer loss/tempo objective. The tempo term is continuous in both
/// enemy durability and team fragility; no HP threshold changes the ordering regime.
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

    internal static double ComputeTeamSafetyFactor(double worstPlayerLossRatio)
    {
        double fragility = Math.Clamp(worstPlayerLossRatio, 0d, 1d);
        // A damaged team should be less willing to buy tempo with additional HP.
        // This stays continuous and parameter-free: healthy=1, fully depleted=0.5.
        return 1d / (1d + fragility);
    }

    internal static double LossRatioPerTurn(
        double enemyDurabilityRatio,
        double worstPlayerLossRatio)
        => MaximumExtraLossRatioPerTurn
            * ComputeLethalUrgency(enemyDurabilityRatio)
            * ComputeTeamSafetyFactor(worstPlayerLossRatio);

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
}
