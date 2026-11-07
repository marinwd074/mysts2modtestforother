namespace CombatSolver;

internal enum SearchRoutePolicy
{
    SinglePlayerFullRoute,
    MultiplayerCurrentTurnOnly,
    MultiplayerSinglePlayerCore,
    MultiplayerLocalCrossTurn,
}

/// <summary>
/// Shared search configuration. Multiplayer observation and execution authority belong
/// to Runtime; the solver consumes a horizon and a quality priority, not a second engine.
/// </summary>
internal static class SearchEvaluationPolicy
{
    internal const int RollingHorizonTurnLayers = 3;

    // Preserve the existing defaults: singleplayer/team experiments search for battle
    // completion, while local-core uses the current turn and two future local turns.
    internal static int? TurnLayerLimit(SearchRoutePolicy policy, int? localCoreTurnLayers = RollingHorizonTurnLayers)
    {
        if (policy != SearchRoutePolicy.MultiplayerSinglePlayerCore)
            return null;
        if (localCoreTurnLayers is { } limit)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit, nameof(localCoreTurnLayers));
        return localCoreTurnLayers;
    }

    internal static bool UsesRawLossFirst(SearchRoutePolicy policy)
        => policy == SearchRoutePolicy.MultiplayerSinglePlayerCore;

    internal static bool UsesLocalEvaluationWindows(SearchRoutePolicy policy)
        => policy is SearchRoutePolicy.SinglePlayerFullRoute or SearchRoutePolicy.MultiplayerSinglePlayerCore;

    internal static bool HasReachedTurnLayerLimit(SearchRoutePolicy policy, int searchedTurnLayers,
        int? localCoreTurnLayers = RollingHorizonTurnLayers)
        => TurnLayerLimit(policy, localCoreTurnLayers) is { } limit && searchedTurnLayers >= limit;

    internal static RouteEvaluationWindow CaptureWindow(int? targetTurnLayers, int startTurn,
        int evaluatedTurn, int playerPhase, bool completeVictory, bool resolved, bool atPlayerDecisionPoint)
        => new(startTurn, targetTurnLayers is { } layers ? checked(startTurn + layers) : null,
            evaluatedTurn, playerPhase, completeVictory, resolved, atPlayerDecisionPoint);
}
