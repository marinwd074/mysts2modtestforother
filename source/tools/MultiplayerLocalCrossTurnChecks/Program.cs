using CombatSolver;

int checks = 0;

void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
    Console.WriteLine($"PASS {++checks}: {message}");
}

StateFingerprint Fingerprint(ulong first, ulong second = 0)
    => new(first, second);

MultiplayerContinuationMatchInput Match(
    StateFingerprint? remote = null,
    string combatIdentity = "combat-a",
    long actualWorldVersion = 12)
    => new(
        ExpectedCombatIdentity: "combat-a",
        ActualCombatIdentity: combatIdentity,
        ExpectedLocalNetId: "local-1",
        ActualLocalNetId: "local-1",
        ExpectedRemotePublicFingerprint: Fingerprint(1),
        ActualRemotePublicFingerprint: remote ?? Fingerprint(1),
        ExpectedMultiplayerScalingHooks: true,
        ActualMultiplayerScalingHooks: true,
        ExpectedCardMultiplayerConstraint: "Shared",
        ActualCardMultiplayerConstraint: "Shared",
        ExpectedSourceWorldVersion: 10,
        MinimumWorldVersion: 10,
        ActualWorldVersion: actualWorldVersion);

Check(
    !MultiplayerLocalCrossTurnContracts.IsCurrentTurnOnly(SearchRoutePolicy.SinglePlayerFullRoute)
        && MultiplayerLocalCrossTurnContracts.IsCurrentTurnOnly(SearchRoutePolicy.MultiplayerCurrentTurnOnly)
        && !MultiplayerLocalCrossTurnContracts.IsCurrentTurnOnly(SearchRoutePolicy.MultiplayerLocalCrossTurn)
        && MultiplayerLocalCrossTurnContracts.CanUseFullSearchHeuristics(SearchRoutePolicy.SinglePlayerFullRoute)
        && !MultiplayerLocalCrossTurnContracts.CanUseFullSearchHeuristics(SearchRoutePolicy.MultiplayerCurrentTurnOnly)
        && MultiplayerLocalCrossTurnContracts.CanUseFullSearchHeuristics(SearchRoutePolicy.MultiplayerLocalCrossTurn)
        && MultiplayerLocalCrossTurnContracts.CanUsePersistentRouteCache(SearchRoutePolicy.SinglePlayerFullRoute)
        && !MultiplayerLocalCrossTurnContracts.CanUsePersistentRouteCache(SearchRoutePolicy.MultiplayerLocalCrossTurn),
    "Singleplayer and multiplayer local-cross-turn share full search heuristics while current-turn-only remains reduced; persistent route cache stays singleplayer-only.");

Check(
    !MultiplayerLocalCrossTurnContracts.ShouldExcludeMultiplayerOnlyCard(
        SearchRoutePolicy.SinglePlayerFullRoute,
        isMultiplayerOnly: true)
        && MultiplayerLocalCrossTurnContracts.ShouldExcludeMultiplayerOnlyCard(
            SearchRoutePolicy.MultiplayerCurrentTurnOnly,
            isMultiplayerOnly: true)
        && MultiplayerLocalCrossTurnContracts.ShouldExcludeMultiplayerOnlyCard(
            SearchRoutePolicy.MultiplayerLocalCrossTurn,
            isMultiplayerOnly: true)
        && !MultiplayerLocalCrossTurnContracts.ShouldExcludeMultiplayerOnlyCard(
            SearchRoutePolicy.MultiplayerLocalCrossTurn,
            isMultiplayerOnly: false),
    "Multiplayer-only cards remain in card state but never become multiplayer search actions; singleplayer policy is unchanged.");

Check(
    !MultiplayerLocalCrossTurnContracts.DelayAngerCopyPreferenceUntilAfterEnemyHp(
        SearchRoutePolicy.SinglePlayerFullRoute,
        completeVictory: false)
        && !MultiplayerLocalCrossTurnContracts.DelayAngerCopyPreferenceUntilAfterEnemyHp(
            SearchRoutePolicy.MultiplayerLocalCrossTurn,
            completeVictory: true)
        && MultiplayerLocalCrossTurnContracts.DelayAngerCopyPreferenceUntilAfterEnemyHp(
            SearchRoutePolicy.MultiplayerLocalCrossTurn,
            completeVictory: false),
    "Incomplete multiplayer local-cross-turn routes rank deterministic enemy HP before Anger copy count, while singleplayer and complete victories keep the original long-term ordering.");

Check(
    MultiplayerLocalCrossTurnContracts.ShouldStopBeforeSharedRngShuffle(
        SearchRoutePolicy.MultiplayerLocalCrossTurn,
        rootSetup: false,
        sharedShuffleForecastTrusted: false,
        willShuffle: true)
        && !MultiplayerLocalCrossTurnContracts.ShouldStopBeforeSharedRngShuffle(
            SearchRoutePolicy.MultiplayerLocalCrossTurn,
            rootSetup: false,
            sharedShuffleForecastTrusted: true,
            willShuffle: true)
        && !MultiplayerLocalCrossTurnContracts.ShouldStopBeforeSharedRngShuffle(
            SearchRoutePolicy.MultiplayerLocalCrossTurn,
            rootSetup: true,
            sharedShuffleForecastTrusted: false,
            willShuffle: true)
        && !MultiplayerLocalCrossTurnContracts.ShouldStopBeforeSharedRngShuffle(
            SearchRoutePolicy.MultiplayerLocalCrossTurn,
            rootSetup: false,
            sharedShuffleForecastTrusted: false,
            willShuffle: false)
        && !MultiplayerLocalCrossTurnContracts.ShouldStopBeforeSharedRngShuffle(
            SearchRoutePolicy.SinglePlayerFullRoute,
            rootSetup: false,
            sharedShuffleForecastTrusted: false,
            willShuffle: true)
        && !MultiplayerLocalCrossTurnContracts.ShouldStopBeforeSharedRngShuffle(
            SearchRoutePolicy.MultiplayerCurrentTurnOnly,
            rootSetup: false,
            sharedShuffleForecastTrusted: false,
            willShuffle: true),
    "Shuffle is a trust boundary, not a count boundary: a Joint/Shadow worldline may cross repeated shuffles while an untrusted multiplayer fallback stops at the first future shuffle.");

Check(
    MultiplayerLocalCrossTurnContracts.ValidateLocalOnlyProjection(
        [
            new(1, IsLocalAction: true, IsEndTurn: false),
            new(1, IsLocalAction: true, IsEndTurn: true),
            new(2, IsLocalAction: true, IsEndTurn: false),
        ],
        startTurnNumber: 1,
        out string t2Failure)
        && t2Failure.Length == 0,
    "A local T1 to T2 projection crosses one EndTurn without adding a remote action.");

Check(
    MultiplayerLocalCrossTurnContracts.ValidateLocalOnlyProjection(
        [
            new(1, IsLocalAction: true, IsEndTurn: false),
            new(1, IsLocalAction: true, IsEndTurn: true),
            new(2, IsLocalAction: true, IsEndTurn: false),
            new(2, IsLocalAction: true, IsEndTurn: true),
            new(3, IsLocalAction: true, IsEndTurn: false),
        ],
        startTurnNumber: 1,
        out string t3Failure)
        && t3Failure.Length == 0,
    "A local T1 to T2 to T3 projection remains valid across two local turn boundaries.");

Check(
    !MultiplayerLocalCrossTurnContracts.ValidateLocalOnlyProjection(
        [new(1, IsLocalAction: false, IsEndTurn: true)],
        startTurnNumber: 1,
        out string remoteActionFailure)
        && remoteActionFailure == "remote_action_present",
    "A projected teammate action is rejected instead of being fabricated into the local route.");

Check(
    MultiplayerLocalCrossTurnContracts.IsExactContinuation(Match())
        && MultiplayerLocalCrossTurnContracts.DescribeContinuationMismatch(Match()) is null,
    "An exact local continuation requires a strictly advanced WorldVersion and matching public inputs.");

Check(
    !MultiplayerLocalCrossTurnContracts.IsExactContinuation(Match(remote: Fingerprint(2)))
        && MultiplayerLocalCrossTurnContracts.DescribeContinuationMismatch(Match(remote: Fingerprint(2)))
            == "remote_public_mismatch"
        && !MultiplayerLocalCrossTurnContracts.IsExactContinuation(Match(actualWorldVersion: 10))
        && MultiplayerLocalCrossTurnContracts.DescribeContinuationMismatch(Match(actualWorldVersion: 10))
            == "world_version_not_advanced",
    "A remote public delta or a non-advanced WorldVersion rejects future-route reuse with a precise reason.");

Check(
    !MultiplayerLocalCrossTurnContracts.CanSoftReuseRemotePublicDelta(
        Match(remote: Fingerprint(2)))
        && !MultiplayerLocalCrossTurnContracts.CanSoftReuseRemotePublicDelta(
            Match(remote: Fingerprint(1)))
        && !MultiplayerLocalCrossTurnContracts.CanSoftReuseRemotePublicDelta(
            Match(remote: Fingerprint(2), actualWorldVersion: 10))
        && !MultiplayerLocalCrossTurnContracts.CanSoftReuseRemotePublicDelta(
            Match(remote: Fingerprint(2), combatIdentity: "combat-b")),
    "A locally readable teammate-state delta always rejects soft reuse and requires a fresh search.");

Check(
    !MultiplayerLocalCrossTurnContracts.IsExactContinuation(Match(combatIdentity: "combat-without-target"))
        && MultiplayerLocalCrossTurnContracts.DescribeContinuationMismatch(
            Match(combatIdentity: "combat-without-target")) == "combat_identity_mismatch",
    "A combat identity change such as teammate removal of the planned target rejects the old route.");

Check(
    MultiplayerLocalCrossTurnContracts.IsCurrentTurnAction(1, 1)
        && !MultiplayerLocalCrossTurnContracts.IsCurrentTurnAction(2, 1)
        && !MultiplayerLocalCrossTurnContracts.HasCurrentTurnPlayableAction(
            [new(1, IsLocalAction: true, IsEndTurn: true)],
            currentTurn: 1)
        && MultiplayerLocalCrossTurnContracts.HasCurrentTurnPlayableAction(
            [new(1, IsLocalAction: true, IsEndTurn: false), new(1, IsLocalAction: true, IsEndTurn: true)],
            currentTurn: 1)
        && MultiplayerLocalCrossTurnContracts.CanPreserveFutureRoute(
            canReuse: true,
            awaitingContinuation: true,
            continuationCount: 2,
            scope: MultiplayerSearchResultScope.PartialLocalCrossTurnProjection)
        && !MultiplayerLocalCrossTurnContracts.CanPreserveFutureRoute(
            canReuse: true,
            awaitingContinuation: true,
            continuationCount: 2,
            scope: MultiplayerSearchResultScope.CurrentTurnOnly),
    "Deployment admits only the current local turn, while a non-current-turn route is preserved only as future data.");

MultiplayerContinuationScheduleDecision heldContinuation =
    MultiplayerLocalCrossTurnContracts.DecidePendingContinuationScheduling(
        awaitingContinuation: true,
        hasContinuationSource: true,
        hasCurrentTurnContinuation: false,
        localTurnPlayable: false);
MultiplayerContinuationScheduleDecision missingPlayableContinuation =
    MultiplayerLocalCrossTurnContracts.DecidePendingContinuationScheduling(
        awaitingContinuation: true,
        hasContinuationSource: true,
        hasCurrentTurnContinuation: false,
        localTurnPlayable: true);
MultiplayerContinuationScheduleDecision currentTurnContinuation =
    MultiplayerLocalCrossTurnContracts.DecidePendingContinuationScheduling(
        awaitingContinuation: true,
        hasContinuationSource: true,
        hasCurrentTurnContinuation: true,
        localTurnPlayable: true);
MultiplayerContinuationScheduleDecision noContinuationSource =
    MultiplayerLocalCrossTurnContracts.DecidePendingContinuationScheduling(
        awaitingContinuation: true,
        hasContinuationSource: false,
        hasCurrentTurnContinuation: false,
        localTurnPlayable: true);
Check(
    heldContinuation.HoldPendingRoute
        && !heldContinuation.FreshSearchMissingCurrentTurn
        && !heldContinuation.ClearAwaitingContinuation
        && !heldContinuation.ClearContinuationSource
        && !missingPlayableContinuation.HoldPendingRoute
        && missingPlayableContinuation.FreshSearchMissingCurrentTurn
        && missingPlayableContinuation.ClearAwaitingContinuation
        && missingPlayableContinuation.ClearContinuationSource
        && currentTurnContinuation == default
        && noContinuationSource == default,
    "The scheduler holds a missing future turn only while local play is unavailable; once playable it fresh-searches and clears both pending-route ownership fields.");

Check(
    MultiplayerLocalCrossTurnContracts.PreferCurrentTurnPlayableRoute(
        SearchRoutePolicy.MultiplayerLocalCrossTurn,
        candidateHasCurrentTurnCard: true,
        currentHasCurrentTurnCard: false)
        && !MultiplayerLocalCrossTurnContracts.PreferCurrentTurnPlayableRoute(
            SearchRoutePolicy.MultiplayerLocalCrossTurn,
            candidateHasCurrentTurnCard: false,
            currentHasCurrentTurnCard: true)
        && !MultiplayerLocalCrossTurnContracts.PreferCurrentTurnPlayableRoute(
            SearchRoutePolicy.SinglePlayerFullRoute,
            candidateHasCurrentTurnCard: true,
            currentHasCurrentTurnCard: false)
        && !MultiplayerLocalCrossTurnContracts.PreferCurrentTurnPlayableRoute(
            SearchRoutePolicy.MultiplayerCurrentTurnOnly,
            candidateHasCurrentTurnCard: true,
            currentHasCurrentTurnCard: false),
    "Local cross-turn tie-breaking prefers a current-turn card over an EndTurn-only route without changing single-player or current-turn-only policies.");

Console.WriteLine($"PASS: {checks} multiplayer local-cross-turn contract checks");
