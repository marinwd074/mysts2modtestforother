namespace CombatSolver;

internal readonly record struct ShadowBehaviorActionObservation(
    bool CompleteVictory,
    int EnemyDurabilityReduction,
    int TeamEffectiveHpGain,
    int EnergyCost,
    int StarCost,
    bool IsPowerCard);

/// <summary>
/// Generic, deliberately weak prior for teammate action choice. This is not a route-quality
/// evaluator: it estimates which legal action a human teammate is more likely to choose from
/// immediate, visible consequences. Player-specific calibration can replace these priors later.
/// </summary>
internal static class ShadowTeammateBehaviorModel
{
    private const double LethalBonus = 2.00d;
    private const double OffensiveProgressWeight = 1.40d;
    private const double SafetyProgressWeight = 0.90d;
    private const double PowerSetupBonus = 0.35d;
    private const double ResourceCostWeight = 0.10d;
    private const double NoVisibleProgressPenalty = 0.40d;
    private const double StopBaseUtility = 0.15d;
    private const double StopBestActionSlope = 0.35d;

    internal static double ActionUtility(ShadowBehaviorActionObservation observation)
    {
        double offensiveProgress = SaturatingProgress(observation.EnemyDurabilityReduction);
        double safetyProgress = SaturatingProgress(observation.TeamEffectiveHpGain);
        bool hasVisibleProgress = observation.CompleteVictory
            || observation.EnemyDurabilityReduction > 0
            || observation.TeamEffectiveHpGain > 0
            || observation.IsPowerCard;

        return (observation.CompleteVictory ? LethalBonus : 0d)
            + OffensiveProgressWeight * offensiveProgress
            + SafetyProgressWeight * safetyProgress
            + (observation.IsPowerCard ? PowerSetupBonus : 0d)
            - ResourceCostWeight * Math.Max(0, observation.EnergyCost + observation.StarCost)
            - (hasVisibleProgress ? 0d : NoVisibleProgressPenalty);
    }

    internal static double StopUtility(IReadOnlyList<ShadowBehaviorActionObservation> actions)
    {
        if (actions.Count == 0)
            return 0d;

        double bestActionUtility = double.NegativeInfinity;
        for (int index = 0; index < actions.Count; index++)
            bestActionUtility = Math.Max(bestActionUtility, ActionUtility(actions[index]));

        return StopBaseUtility - StopBestActionSlope * Math.Max(0d, bestActionUtility);
    }

    /// <summary>
    /// Returns log probabilities in action order, followed by the stop-playing option.
    /// Log space keeps long shadow sequences numerically stable.
    /// </summary>
    internal static double[] DecisionLogProbabilities(
        IReadOnlyList<ShadowBehaviorActionObservation> actions)
    {
        double[] logits = new double[actions.Count + 1];
        double maximum = double.NegativeInfinity;
        for (int index = 0; index < actions.Count; index++)
        {
            logits[index] = ActionUtility(actions[index]);
            maximum = Math.Max(maximum, logits[index]);
        }

        logits[^1] = StopUtility(actions);
        maximum = Math.Max(maximum, logits[^1]);

        double exponentialSum = 0d;
        for (int index = 0; index < logits.Length; index++)
            exponentialSum += Math.Exp(logits[index] - maximum);
        double logDenominator = maximum + Math.Log(exponentialSum);

        for (int index = 0; index < logits.Length; index++)
            logits[index] -= logDenominator;
        return logits;
    }

    internal static double MeanLogProbability(double logProbability, int decisionCount)
        => decisionCount <= 0 ? 0d : logProbability / decisionCount;

    private static double SaturatingProgress(int amount)
    {
        if (amount <= 0)
            return 0d;
        // A logarithm keeps large attacks/heals from making the prior overconfident.
        return Math.Min(1d, Math.Log2(1d + amount) / 5d);
    }
}
