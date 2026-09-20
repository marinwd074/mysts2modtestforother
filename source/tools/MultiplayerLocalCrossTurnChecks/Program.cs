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
        && MultiplayerLocalCrossTurnContracts.CanUsePersistentRouteCache(SearchRoutePolicy.SinglePlayerFullRoute)
        && !MultiplayerLocalCrossTurnContracts.CanUsePersistentRouteCache(SearchRoutePolicy.MultiplayerLocalCrossTurn),
    "Singleplayer, Probe/current-turn, and multiplayer local-cross-turn route policies stay distinct; persistent route cache remains singleplayer-only.");

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
    !MultiplayerLocalCrossTurnContracts.IsExactContinuation(Match(combatIdentity: "combat-without-target"))
        && MultiplayerLocalCrossTurnContracts.DescribeContinuationMismatch(
            Match(combatIdentity: "combat-without-target")) == "combat_identity_mismatch",
    "A combat identity change such as teammate removal of the planned target rejects the old route.");

Check(
    MultiplayerLocalCrossTurnContracts.IsCurrentTurnAction(1, 1)
        && !MultiplayerLocalCrossTurnContracts.IsCurrentTurnAction(2, 1)
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

Console.WriteLine($"PASS: {checks} multiplayer local-cross-turn contract checks");
