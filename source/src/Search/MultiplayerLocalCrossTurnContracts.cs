namespace CombatSolver;

internal enum SearchRoutePolicy
{
    SinglePlayerFullRoute,
    MultiplayerCurrentTurnOnly,
    // Multiplayer runtime boundary with the proven single-player search core.
    // This keeps local-only deployment/continuation semantics without enabling
    // teammate prediction, team objectives, robust scenario reranking, or other
    // multiplayer-specific search behavior.
    MultiplayerSinglePlayerCore,
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

    internal static bool CanUseFullSearchHeuristics(SearchRoutePolicy policy)
        => policy is SearchRoutePolicy.SinglePlayerFullRoute
            or SearchRoutePolicy.MultiplayerSinglePlayerCore
            or SearchRoutePolicy.MultiplayerLocalCrossTurn;

    internal static bool HasLocalCrossTurnProjection(SearchRoutePolicy policy)
        => policy is SearchRoutePolicy.MultiplayerSinglePlayerCore
            or SearchRoutePolicy.MultiplayerLocalCrossTurn;

    /// <summary>
    /// Quality-first default: local-core multiplayer keeps the exact single-player
    /// exploration order. P1/P2 continuation seeds and P3 cross-family scheduling stay
    /// available as offline experiments but do not influence production search.
    /// </summary>
    internal static bool LocalCoreSearchAcceleratorsEnabled => false;

    internal static bool ShouldUseP3CrossFamilyScheduling(
        SearchRoutePolicy routePolicy,
        bool includeTurnSetup,
        bool smartPotionPolicy,
        bool hasForcedPotionDirectives,
        bool useNoveltyPortfolio)
        => LocalCoreSearchAcceleratorsEnabled
            && routePolicy == SearchRoutePolicy.MultiplayerSinglePlayerCore
            && !includeTurnSetup
            && smartPotionPolicy
            && !hasForcedPotionDirectives
            && !useNoveltyPortfolio;

    internal static bool CanReplayContinuationSeedAction(
        int actionTurn,
        int currentTurn,
        bool isPlayCard,
        bool endsPlayerTurn,
        bool hasChoice,
        bool hasNestedChoices,
        bool hasTurnStartChoices,
        bool hasShadowForecast,
        bool hasCardStateKey)
        => actionTurn == currentTurn
            && isPlayCard
            && !endsPlayerTurn
            && !hasChoice
            && !hasNestedChoices
            && !hasTurnStartChoices
            && !hasShadowForecast
            && hasCardStateKey;

    internal static bool HasActiveMultiplayerRouteSemantics(
        SearchRoutePolicy policy,
        int playerCount)
        => policy == SearchRoutePolicy.MultiplayerLocalCrossTurn
            && playerCount > 1;

    /// <summary>
    /// Local-core multiplayer deliberately does not predict teammate actions. If every full
    /// local-only projection eventually dies but the current turn has a legal surviving boundary,
    /// prefer that current-turn decision and re-root next turn instead of ranking fabricated
    /// "solo the whole multiplayer encounter" death routes.
    /// </summary>
    internal static bool ShouldUseLocalCoreDeathHorizonFallback(
        SearchRoutePolicy policy,
        int playerCount,
        bool onlyDeathRoutesFound,
        bool hasSurvivingCurrentTurnCandidate)
        => policy == SearchRoutePolicy.MultiplayerSinglePlayerCore
            && playerCount > 1
            && onlyDeathRoutesFound
            && hasSurvivingCurrentTurnCandidate;

    internal static bool DelayAngerCopyPreferenceUntilAfterEnemyHp(
        bool multiplayerRouteSemanticsActive,
        bool completeVictory)
        => multiplayerRouteSemanticsActive
            && !completeVictory;

    internal static bool CanUsePersistentRouteCache(SearchRoutePolicy policy)
        => policy is SearchRoutePolicy.SinglePlayerFullRoute
            or SearchRoutePolicy.MultiplayerSinglePlayerCore;

    internal static bool ShouldExcludeMultiplayerOnlyCard(
        SearchRoutePolicy policy,
        bool isMultiplayerOnly)
        => policy != SearchRoutePolicy.SinglePlayerFullRoute
            && isMultiplayerOnly;

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
        bool multiplayerRouteSemanticsActive,
        bool candidateHasCurrentTurnCard,
        bool currentHasCurrentTurnCard)
        => multiplayerRouteSemanticsActive
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
    /// Shuffle count is not a correctness boundary. A multiplayer forecast may cross any
    /// number of shuffles while the selected Joint/Shadow worldline owns the shared Shuffle
    /// RNG state. Fallback local-only prediction stops at the first future shuffle whose RNG
    /// state is not justified by that worldline. Root setup starts from live captured state.
    /// </summary>
    internal static bool ShouldStopBeforeSharedRngShuffle(
        bool multiplayerRouteSemanticsActive,
        bool rootSetup,
        bool sharedShuffleForecastTrusted,
        bool willShuffle)
        => multiplayerRouteSemanticsActive
            && !rootSetup
            && !sharedShuffleForecastTrusted
            && willShuffle;

    internal static bool IsExactContinuation(MultiplayerContinuationMatchInput input)
        => DescribeContinuationMismatch(input) is null;

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
