namespace CombatSolver;

internal readonly record struct MultiplayerScenarioOutcome(
    ShadowTeammateScenarioKind Kind,
    bool CompleteVictory,
    bool AllPlayersAlive,
    double LossEquivalent,
    double WorstPlayerLossRatio,
    double TeamLossRatio,
    double EnemyDurabilityRatio);

internal readonly record struct MultiplayerScenarioDecisionRank(
    int ScenarioCount,
    bool AllScenariosAlive,
    bool GuaranteedVictory,
    double WorstLossEquivalent,
    double MeanLossEquivalent,
    double WorstPlayerLossRatio,
    double WorstTeamLossRatio,
    double WorstEnemyDurabilityRatio);

/// <summary>
/// Robust P3 reranking. Scenario kinds are stress cases, not calibrated probabilities:
/// worst-case safety/loss is primary and the unweighted mean is only a secondary tie-break.
/// </summary>
internal static class MultiplayerScenarioReevaluationPolicy
{
    internal const int MaximumCurrentDecisions = 4;
    internal const int MaximumScenariosPerDecision = 4;
    internal const int MaximumCoverageCandidates =
        MaximumCurrentDecisions * MaximumScenariosPerDecision;

    internal static MultiplayerScenarioDecisionRank Aggregate(
        IReadOnlyList<MultiplayerScenarioOutcome> outcomes)
    {
        if (outcomes.Count == 0)
        {
            return new MultiplayerScenarioDecisionRank(
                0,
                AllScenariosAlive: false,
                GuaranteedVictory: false,
                double.PositiveInfinity,
                double.PositiveInfinity,
                double.PositiveInfinity,
                double.PositiveInfinity,
                double.PositiveInfinity);
        }

        double worstLoss = double.NegativeInfinity;
        double lossSum = 0d;
        double worstPlayerLoss = double.NegativeInfinity;
        double worstTeamLoss = double.NegativeInfinity;
        double worstEnemyDurability = double.NegativeInfinity;
        bool allAlive = true;
        bool allVictory = true;
        for (int index = 0; index < outcomes.Count; index++)
        {
            MultiplayerScenarioOutcome outcome = outcomes[index];
            allAlive &= outcome.AllPlayersAlive;
            allVictory &= outcome.CompleteVictory;
            worstLoss = Math.Max(worstLoss, outcome.LossEquivalent);
            lossSum += outcome.LossEquivalent;
            worstPlayerLoss = Math.Max(
                worstPlayerLoss,
                outcome.WorstPlayerLossRatio);
            worstTeamLoss = Math.Max(worstTeamLoss, outcome.TeamLossRatio);
            worstEnemyDurability = Math.Max(
                worstEnemyDurability,
                outcome.EnemyDurabilityRatio);
        }

        return new MultiplayerScenarioDecisionRank(
            outcomes.Count,
            allAlive,
            allVictory,
            worstLoss,
            lossSum / outcomes.Count,
            worstPlayerLoss,
            worstTeamLoss,
            worstEnemyDurability);
    }

    /// <summary>Negative means left is preferred.</summary>
    internal static int Compare(
        MultiplayerScenarioDecisionRank left,
        MultiplayerScenarioDecisionRank right)
    {
        int comparison = right.AllScenariosAlive.CompareTo(left.AllScenariosAlive);
        if (comparison != 0)
            return comparison;
        comparison = right.GuaranteedVictory.CompareTo(left.GuaranteedVictory);
        if (comparison != 0)
            return comparison;
        comparison = left.WorstLossEquivalent.CompareTo(right.WorstLossEquivalent);
        if (comparison != 0)
            return comparison;
        comparison = left.WorstPlayerLossRatio.CompareTo(right.WorstPlayerLossRatio);
        if (comparison != 0)
            return comparison;
        comparison = left.MeanLossEquivalent.CompareTo(right.MeanLossEquivalent);
        if (comparison != 0)
            return comparison;
        comparison = left.WorstTeamLossRatio.CompareTo(right.WorstTeamLossRatio);
        if (comparison != 0)
            return comparison;
        comparison = left.WorstEnemyDurabilityRatio.CompareTo(
            right.WorstEnemyDurabilityRatio);
        if (comparison != 0)
            return comparison;
        return right.ScenarioCount.CompareTo(left.ScenarioCount);
    }
}
