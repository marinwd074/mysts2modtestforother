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

    internal static bool ShouldRunLocalCoreCurrentTurnQualityScout(
        SearchRoutePolicy routePolicy,
        bool includeTurnSetup)
        => routePolicy == SearchRoutePolicy.MultiplayerSinglePlayerCore
            && !includeTurnSetup;

    /// <summary>
    /// The complete local-only projection is advisory for future turns because teammates can
    /// change that future before it is executed. If the retained current-turn incumbent is
    /// strictly better than the selected full-route first-turn boundary, deploy the incumbent
    /// and re-root next turn instead of sacrificing the immediate decision for distant quality.
    /// </summary>
    internal static bool ShouldPreferLocalCoreCurrentTurnResult(
        SearchRoutePolicy routePolicy,
        int playerCount,
        bool currentTurnStrictlyBetter)
        => routePolicy == SearchRoutePolicy.MultiplayerSinglePlayerCore
            && playerCount > 1
            && currentTurnStrictlyBetter;

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

    /// <summary>
    /// Default local-core multiplayer treats remote-only shared counters as advisory at a
    /// cross-turn continuation boundary. Shuffle may advance remotely, and the global
    /// finished-card-play count HC[0] may advance while the local hand/piles stay unchanged.
    /// HC[0] remains strict when a local Gold Axe is present because that card reads it.
    /// Every other continuation field remains exact.
    /// </summary>
    internal static bool IsLocalCoreContinuationStateCompatible(
        string expectedStateText,
        string actualStateText,
        out bool sharedShuffleRngDrift,
        out bool sharedFinishedCardPlayDrift)
    {
        sharedShuffleRngDrift = false;
        sharedFinishedCardPlayDrift = false;
        if (string.Equals(expectedStateText, actualStateText, StringComparison.Ordinal))
            return true;

        string[] expectedFields = expectedStateText.Split(';');
        string[] actualFields = actualStateText.Split(';');
        if (expectedFields.Length != actualFields.Length)
            return false;

        bool requiresGlobalFinishedCardPlays =
            StateContainsGlobalFinishedCardPlayDependentLocalCard(expectedFields)
            || StateContainsGlobalFinishedCardPlayDependentLocalCard(actualFields);

        for (int index = 0; index < expectedFields.Length; index++)
        {
            string expectedField = expectedFields[index];
            string actualField = actualFields[index];
            if (string.Equals(expectedField, actualField, StringComparison.Ordinal))
                continue;

            int expectedSeparator = expectedField.IndexOf('=');
            int actualSeparator = actualField.IndexOf('=');
            if (expectedSeparator <= 0
                || actualSeparator <= 0
                || !string.Equals(
                    expectedField[..expectedSeparator],
                    actualField[..actualSeparator],
                    StringComparison.Ordinal))
            {
                return false;
            }

            string name = expectedField[..expectedSeparator];
            if (string.Equals(name, "R", StringComparison.Ordinal))
            {
                string[] expectedRng = expectedField[(expectedSeparator + 1)..].Split('/');
                string[] actualRng = actualField[(actualSeparator + 1)..].Split('/');
                if (expectedRng.Length != actualRng.Length || expectedRng.Length == 0)
                    return false;
                for (int rngIndex = 1; rngIndex < expectedRng.Length; rngIndex++)
                {
                    if (!string.Equals(expectedRng[rngIndex], actualRng[rngIndex], StringComparison.Ordinal))
                        return false;
                }
                if (string.Equals(expectedRng[0], actualRng[0], StringComparison.Ordinal))
                    return false;
                sharedShuffleRngDrift = true;
                continue;
            }

            if (string.Equals(name, "HC", StringComparison.Ordinal)
                && !requiresGlobalFinishedCardPlays
                && HistoryCountersMatchExceptFinishedCardPlays(
                    expectedField[(expectedSeparator + 1)..],
                    actualField[(actualSeparator + 1)..]))
            {
                sharedFinishedCardPlayDrift = true;
                continue;
            }

            return false;
        }

        return sharedShuffleRngDrift || sharedFinishedCardPlayDrift;
    }

    private static bool StateContainsGlobalFinishedCardPlayDependentLocalCard(
        IReadOnlyList<string> fields)
        => fields.Any(field =>
            (field.StartsWith("H=", StringComparison.Ordinal)
                || field.StartsWith("D=", StringComparison.Ordinal)
                || field.StartsWith("C=", StringComparison.Ordinal)
                || field.StartsWith("X=", StringComparison.Ordinal))
            && field.Contains("GOLD_AXE", StringComparison.Ordinal));

    private static bool HistoryCountersMatchExceptFinishedCardPlays(
        string expected,
        string actual)
    {
        string[] expectedCounters = expected.Split('/');
        string[] actualCounters = actual.Split('/');
        if (expectedCounters.Length != actualCounters.Length
            || expectedCounters.Length == 0
            || string.Equals(expectedCounters[0], actualCounters[0], StringComparison.Ordinal))
        {
            return false;
        }

        for (int index = 1; index < expectedCounters.Length; index++)
        {
            if (!string.Equals(
                    expectedCounters[index],
                    actualCounters[index],
                    StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

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
