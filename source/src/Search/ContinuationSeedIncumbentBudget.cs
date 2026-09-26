namespace CombatSolver;

// P2 continuation repair is bounded work from the same request, not an extra budget.
internal static class ContinuationSeedIncumbentBudget
{
    internal const int WorkDivisor = 20;

    public static SolverSearchProfile? Probe(SolverSearchProfile profile)
    {
        if (profile.MaxExpandedNodes < WorkDivisor
            || profile.SoftTimeBudgetMilliseconds < WorkDivisor)
            return null;

        return profile with
        {
            MaxExpandedNodes = profile.MaxExpandedNodes / WorkDivisor,
            SoftTimeBudgetMilliseconds =
                profile.SoftTimeBudgetMilliseconds / WorkDivisor,
        };
    }

    public static SolverSearchProfile? Remaining(
        SolverSearchProfile profile,
        long elapsedMilliseconds,
        long expandedNodes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(expandedNodes);
        if (elapsedMilliseconds >= profile.SoftTimeBudgetMilliseconds
            || expandedNodes >= profile.MaxExpandedNodes)
            return null;

        return profile with
        {
            SoftTimeBudgetMilliseconds =
                profile.SoftTimeBudgetMilliseconds - (int)elapsedMilliseconds,
            MaxExpandedNodes = profile.MaxExpandedNodes - (int)expandedNodes,
        };
    }
}
