namespace CombatSolver;

internal static class MultiplayerSearchCompletionContracts
{
    internal static bool IsStale(
        bool routeScopedCompletion,
        long searchWorldVersion,
        long currentWorldVersion,
        long searchRouteVersion,
        long currentRouteVersion,
        bool fullStampMatches,
        bool localStampMatches)
    {
        if (routeScopedCompletion)
            return searchRouteVersion != currentRouteVersion || !localStampMatches;

        return searchWorldVersion != 0
            && currentWorldVersion != searchWorldVersion
            || !fullStampMatches;
    }
}
