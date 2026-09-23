namespace CombatSolver;

internal enum MultiplayerInterleaveOrderRelation
{
    ReverseUnavailable,
    OrderSensitive,
    ExactEquivalent,
}

/// <summary>
/// U5 scheduling contract. The production search may forecast one teammate observation between
/// local cards, but it never invents an unbounded "wait until teammate helps" action. Exact order
/// collapse is legal only after both orders have been replayed and their conservative modeled
/// future-state fingerprints are identical.
/// </summary>
internal static class MultiplayerInterleaveOrderPolicy
{
    internal const int MaximumForecastObservationsPerTurn = 1;
    internal const int MaximumSingleObservationRoutes = 4;
    internal const string ForecastBoundaryReason = "kind_teammateforecast";

    internal static bool CanCollapseOrder(MultiplayerInterleaveOrderRelation relation)
        => relation == MultiplayerInterleaveOrderRelation.ExactEquivalent;

    internal static bool AllowsProactiveWaitForTeammate => false;

    internal static bool IsForecastBoundaryReason(string? reason)
        => string.Equals(reason, ForecastBoundaryReason, StringComparison.Ordinal);
}
