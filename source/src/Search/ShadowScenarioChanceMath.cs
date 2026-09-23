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
        return maximum + Math.Log1p(Math.Exp(minimum - maximum));
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
