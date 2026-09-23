namespace CombatSolver;

internal readonly record struct ShadowScenarioProbabilitySet(
    double RetainedProbabilityMass,
    IReadOnlyList<double> ConditionalProbabilities);

/// <summary>
/// Probability-mass helpers for retained Shadow scenarios. BehaviorLogProbability describes one
/// representative action history; BehaviorLogMass may aggregate several exact-equivalent histories.
/// </summary>
internal static class ShadowScenarioChanceMath
{
    internal static double LogAddExp(double left, double right)
    {
        if (double.IsNegativeInfinity(left))
            return right;
        if (double.IsNegativeInfinity(right))
            return left;

        double maximum = Math.Max(left, right);
        double minimum = Math.Min(left, right);
        return maximum + Math.Log(1d + Math.Exp(minimum - maximum));
    }

    internal static ShadowScenarioProbabilitySet NormalizeRetainedLogMasses(
        IReadOnlyList<double> logMasses)
    {
        if (logMasses.Count == 0)
            return new ShadowScenarioProbabilitySet(0d, Array.Empty<double>());

        double totalLogMass = double.NegativeInfinity;
        for (int index = 0; index < logMasses.Count; index++)
        {
            double logMass = logMasses[index];
            if (double.IsNaN(logMass) || logMass > 1e-12d)
                throw new ArgumentOutOfRangeException(
                    nameof(logMasses),
                    "Shadow log probability mass must be finite-or-negative and cannot exceed log(1).");
            totalLogMass = LogAddExp(totalLogMass, logMass);
        }

        if (totalLogMass > 1e-9d)
        {
            throw new InvalidOperationException(
                "Mutually exclusive retained Shadow scenarios carry probability mass greater than one.");
        }

        double retainedMass = double.IsNegativeInfinity(totalLogMass)
            ? 0d
            : Math.Clamp(Math.Exp(Math.Min(0d, totalLogMass)), 0d, 1d);
        double[] conditional = new double[logMasses.Count];
        if (double.IsNegativeInfinity(totalLogMass))
            return new ShadowScenarioProbabilitySet(retainedMass, conditional);

        double conditionalSum = 0d;
        for (int index = 0; index < logMasses.Count; index++)
        {
            conditional[index] = Math.Exp(logMasses[index] - totalLogMass);
            conditionalSum += conditional[index];
        }

        if (conditionalSum > 0d)
        {
            for (int index = 0; index < conditional.Length; index++)
                conditional[index] /= conditionalSum;
        }
        return new ShadowScenarioProbabilitySet(retainedMass, conditional);
    }
}

internal readonly record struct MultiplayerChanceOutcome(
    double ProbabilityMass,
    bool CompleteVictory,
    bool AllPlayersAlive,
    double LossEquivalent,
    double WorstPlayerLossRatio,
    double TeamLossRatio,
    double EnemyDurabilityRatio);

internal readonly record struct MultiplayerChanceDecisionRank(
    double RetainedProbabilityMass,
    bool GuaranteedVictory,
    double VictoryProbabilityLower,
    double ConservativeTeamDeathProbability,
    double ExpectedLossEquivalentUpper,
    double ExpectedWorstPlayerLossRatioUpper,
    double ExpectedTeamLossRatioUpper,
    double ExpectedEnemyDurabilityRatioUpper);

internal static class MultiplayerChanceDecisionMath
{
    internal static MultiplayerChanceDecisionRank Aggregate(
        IReadOnlyList<MultiplayerChanceOutcome> outcomes)
    {
        if (outcomes.Count == 0)
        {
            return new MultiplayerChanceDecisionRank(
                0d,
                GuaranteedVictory: false,
                VictoryProbabilityLower: 0d,
                ConservativeTeamDeathProbability: 1d,
                double.PositiveInfinity,
                double.PositiveInfinity,
                double.PositiveInfinity,
                double.PositiveInfinity);
        }

        double retainedMass = 0d;
        double successMass = 0d;
        double aliveMass = 0d;
        double weightedLoss = 0d;
        double weightedWorstLoss = 0d;
        double weightedTeamLoss = 0d;
        double weightedEnemyDurability = 0d;
        double worstLoss = double.NegativeInfinity;
        double worstWorstLoss = double.NegativeInfinity;
        double worstTeamLoss = double.NegativeInfinity;
        double worstEnemyDurability = double.NegativeInfinity;

        for (int index = 0; index < outcomes.Count; index++)
        {
            MultiplayerChanceOutcome outcome = outcomes[index];
            if (!double.IsFinite(outcome.ProbabilityMass)
                || outcome.ProbabilityMass < 0d
                || outcome.ProbabilityMass > 1d + 1e-12d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(outcomes),
                    "Chance outcome probability mass must be between zero and one.");
            }

            double probability = Math.Clamp(outcome.ProbabilityMass, 0d, 1d);
            retainedMass += probability;
            if (outcome.CompleteVictory)
                successMass += probability;
            if (outcome.AllPlayersAlive)
                aliveMass += probability;
            weightedLoss += probability * outcome.LossEquivalent;
            weightedWorstLoss += probability * outcome.WorstPlayerLossRatio;
            weightedTeamLoss += probability * outcome.TeamLossRatio;
            weightedEnemyDurability += probability * outcome.EnemyDurabilityRatio;
            worstLoss = Math.Max(worstLoss, outcome.LossEquivalent);
            worstWorstLoss = Math.Max(worstWorstLoss, outcome.WorstPlayerLossRatio);
            worstTeamLoss = Math.Max(worstTeamLoss, outcome.TeamLossRatio);
            worstEnemyDurability = Math.Max(worstEnemyDurability, outcome.EnemyDurabilityRatio);
        }

        if (retainedMass > 1d + 1e-9d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcomes),
                "Chance outcomes overlap or carry more than total probability mass one.");
        }

        retainedMass = Math.Clamp(retainedMass, 0d, 1d);
        double omittedMass = 1d - retainedMass;
        double victoryProbabilityLower = Math.Clamp(successMass, 0d, 1d);
        return new MultiplayerChanceDecisionRank(
            retainedMass,
            GuaranteedVictory: victoryProbabilityLower >= 1d - 1e-9d,
            VictoryProbabilityLower: victoryProbabilityLower,
            ConservativeTeamDeathProbability:
                Math.Clamp(1d - Math.Min(1d, aliveMass), 0d, 1d),
            weightedLoss + omittedMass * worstLoss,
            weightedWorstLoss + omittedMass * worstWorstLoss,
            weightedTeamLoss + omittedMass * worstTeamLoss,
            weightedEnemyDurability + omittedMass * worstEnemyDurability);
    }

    /// <summary>Negative means left is preferred.</summary>
    internal static int Compare(
        MultiplayerChanceDecisionRank left,
        MultiplayerChanceDecisionRank right)
    {
        int comparison = right.GuaranteedVictory.CompareTo(left.GuaranteedVictory);
        if (comparison != 0)
            return comparison;
        comparison = left.ConservativeTeamDeathProbability.CompareTo(
            right.ConservativeTeamDeathProbability);
        if (comparison != 0)
            return comparison;
        comparison = left.ExpectedLossEquivalentUpper.CompareTo(
            right.ExpectedLossEquivalentUpper);
        if (comparison != 0)
            return comparison;
        comparison = left.ExpectedWorstPlayerLossRatioUpper.CompareTo(
            right.ExpectedWorstPlayerLossRatioUpper);
        if (comparison != 0)
            return comparison;
        comparison = left.ExpectedTeamLossRatioUpper.CompareTo(
            right.ExpectedTeamLossRatioUpper);
        if (comparison != 0)
            return comparison;
        comparison = right.VictoryProbabilityLower.CompareTo(
            left.VictoryProbabilityLower);
        if (comparison != 0)
            return comparison;
        comparison = left.ExpectedEnemyDurabilityRatioUpper.CompareTo(
            right.ExpectedEnemyDurabilityRatioUpper);
        if (comparison != 0)
            return comparison;
        return right.RetainedProbabilityMass.CompareTo(left.RetainedProbabilityMass);
    }
}
