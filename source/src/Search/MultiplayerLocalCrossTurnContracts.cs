namespace CombatSolver;

internal enum SearchRoutePolicy
{
    SinglePlayerFullRoute,
    MultiplayerCurrentTurnOnly,
    MultiplayerLocalCrossTurn,
}

internal enum MultiplayerSearchResultScope
{
    NotMultiplayer,
    CompleteLocalBattleProjection,
    PartialLocalCrossTurnProjection,
    CurrentTurnOnly,
}

internal readonly record struct MultiplayerContinuationScheduleDecision(
    bool HoldPendingRoute,
    bool FreshSearchMissingCurrentTurn,
    bool ClearAwaitingContinuation,
    bool ClearContinuationSource);

internal readonly record struct MultiplayerContinuationMatchInput(
    string ExpectedCombatIdentity,
    string ActualCombatIdentity,
    string ExpectedLocalNetId,
    string ActualLocalNetId,
    StateFingerprint ExpectedRemotePublicFingerprint,
    StateFingerprint ActualRemotePublicFingerprint,
    bool? ExpectedMultiplayerScalingHooks,
    bool? ActualMultiplayerScalingHooks,
    string ExpectedCardMultiplayerConstraint,
    string ActualCardMultiplayerConstraint,
    long ExpectedSourceWorldVersion,
    long MinimumWorldVersion,
    long ActualWorldVersion);

/// <summary>
/// Small pure contracts for the multiplayer local-cross-turn boundary. These predicates are
/// shared by runtime admission and offline checks so route prediction cannot silently become
/// remote-action prediction or a cross-turn authorization.
/// </summary>
internal static class MultiplayerLocalCrossTurnContracts
{
    internal static bool IsCurrentTurnOnly(SearchRoutePolicy policy)
        => policy == SearchRoutePolicy.MultiplayerCurrentTurnOnly;

    internal static bool CanUsePersistentRouteCache(SearchRoutePolicy policy)
        => policy == SearchRoutePolicy.SinglePlayerFullRoute;

    internal static bool CanPreserveFutureRoute(
        bool canReuse,
        bool awaitingContinuation,
        int continuationCount,
        MultiplayerSearchResultScope scope)
        => canReuse
            && awaitingContinuation
            && continuationCount > 0
            && scope != MultiplayerSearchResultScope.CurrentTurnOnly;

    internal static bool HasLocalCrossTurnContinuation(
        MultiplayerSearchResultScope scope,
        int continuationCount)
        => continuationCount > 0
            && scope is MultiplayerSearchResultScope.CompleteLocalBattleProjection
                or MultiplayerSearchResultScope.PartialLocalCrossTurnProjection;

    internal static bool IsCurrentTurnAction(int actionTurn, int currentTurn)
        => actionTurn == currentTurn;

    internal static bool HasCurrentTurnPlayableAction(
        IReadOnlyList<MultiplayerProjectedAction> actions,
        int currentTurn)
        => actions.Any(action =>
            action.IsLocalAction
            && action.Turn == currentTurn
            && !action.IsEndTurn);

    internal static bool PreferCurrentTurnPlayableRoute(
        SearchRoutePolicy routePolicy,
        bool candidateHasCurrentTurnCard,
        bool currentHasCurrentTurnCard)
        => routePolicy == SearchRoutePolicy.MultiplayerLocalCrossTurn
            && candidateHasCurrentTurnCard
            && !currentHasCurrentTurnCard;

    internal static MultiplayerContinuationScheduleDecision DecidePendingContinuationScheduling(
        bool awaitingContinuation,
        bool hasContinuationSource,
        bool hasCurrentTurnContinuation,
        bool localTurnPlayable)
    {
        if (!awaitingContinuation
            || !hasContinuationSource
            || hasCurrentTurnContinuation)
        {
            return default;
        }

        if (!localTurnPlayable)
        {
            return new MultiplayerContinuationScheduleDecision(
                HoldPendingRoute: true,
                FreshSearchMissingCurrentTurn: false,
                ClearAwaitingContinuation: false,
                ClearContinuationSource: false);
        }

        return new MultiplayerContinuationScheduleDecision(
            HoldPendingRoute: false,
            FreshSearchMissingCurrentTurn: true,
            ClearAwaitingContinuation: true,
            ClearContinuationSource: true);
    }

    /// <summary>
    /// A future local hand must not be projected through the shared Shuffle RNG after
    /// yielding the local turn. Teammate actions are intentionally not simulated and may
    /// advance that RNG, so the post-shuffle order is not locally knowable. Root setup is
    /// exempt because it starts from the freshly captured live state.
    /// </summary>
    internal static bool ShouldStopBeforeSharedRngShuffle(
        SearchRoutePolicy routePolicy,
        bool rootSetup,
        bool willShuffle)
        => routePolicy == SearchRoutePolicy.MultiplayerLocalCrossTurn
            && !rootSetup
            && willShuffle;

    internal static bool IsExactContinuation(MultiplayerContinuationMatchInput input)
        => DescribeContinuationMismatch(input) is null;

    /// <summary>
    /// Legacy compatibility hook. The remote fingerprint now includes every teammate
    /// combat field already readable in the local process (cards/resources/potions in
    /// addition to public HP/block/powers), so a mismatch can no longer be proven to be
    /// harmless public drift. Fail closed and force a fresh search.
    /// </summary>
    internal static bool CanSoftReuseRemotePublicDelta(
        MultiplayerContinuationMatchInput input)
        => false;

    internal static string? DescribeContinuationMismatch(
        MultiplayerContinuationMatchInput input)
    {
        if (input.ActualWorldVersion <= Math.Max(
                input.ExpectedSourceWorldVersion,
                input.MinimumWorldVersion))
        {
            return "world_version_not_advanced";
        }
        if (!string.Equals(
                input.ExpectedCombatIdentity,
                input.ActualCombatIdentity,
                StringComparison.Ordinal))
        {
            return "combat_identity_mismatch";
        }
        if (!string.Equals(
                input.ExpectedLocalNetId,
                input.ActualLocalNetId,
                StringComparison.Ordinal))
        {
            return "local_net_id_mismatch";
        }
        if (input.ExpectedRemotePublicFingerprint != input.ActualRemotePublicFingerprint)
            return "remote_public_mismatch";
        if (input.ExpectedMultiplayerScalingHooks != input.ActualMultiplayerScalingHooks)
            return "scaling_mismatch";
        if (!string.Equals(
                input.ExpectedCardMultiplayerConstraint,
                input.ActualCardMultiplayerConstraint,
                StringComparison.Ordinal))
        {
            return "card_constraint_mismatch";
        }
        return null;
    }

    internal static bool ValidateLocalOnlyProjection(
        IReadOnlyList<MultiplayerProjectedAction> actions,
        int startTurnNumber,
        out string failure)
    {
        int previousTurn = startTurnNumber;
        foreach (MultiplayerProjectedAction action in actions)
        {
            if (!action.IsLocalAction)
            {
                failure = "remote_action_present";
                return false;
            }
            if (action.Turn < previousTurn)
            {
                failure = "turn_order_reversed";
                return false;
            }
            previousTurn = action.Turn;
        }
        failure = string.Empty;
        return true;
    }
}

internal readonly record struct MultiplayerProjectedAction(
    int Turn,
    bool IsLocalAction,
    bool IsEndTurn);
