namespace CombatSolver;

internal static class MultiplayerSearchCompletionContracts
{
    internal static bool IsStale(
        bool routeScopedCompletion,
        long searchWorldVersion,
        long currentWorldVersion,
        long searchRouteInvalidationVersion,
        long currentRouteInvalidationVersion,
        bool fullStampMatches,
        bool localStampMatches)
    {
        if (routeScopedCompletion)
            return searchRouteInvalidationVersion != currentRouteInvalidationVersion || !localStampMatches;

        return searchWorldVersion != 0
            && currentWorldVersion != searchWorldVersion
            || !fullStampMatches;
    }
}
