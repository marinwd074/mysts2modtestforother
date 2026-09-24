using CombatSolver;

int checks = 0;

void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
    Console.WriteLine($"PASS {++checks}: {message}");
}

SafeLocalActionDecision Structural(
    string kind = "playcard",
    bool isPlayCard = true,
    bool hasCardIdentity = true,
    bool endsTurn = false,
    bool replay = false,
    bool choice = false,
    bool isUsePotion = false,
    bool hasPotionIdentity = false,
    bool hasPotionSlot = false)
    => MultiplayerSafeExecutePolicy.ClassifyStructural(
        new(
            kind,
            isPlayCard,
            hasCardIdentity,
            endsTurn,
            replay,
            choice,
            isUsePotion,
            hasPotionIdentity,
            hasPotionSlot));

SafeLocalActionDecision Resolved(
    bool localPlayer = true,
    bool localCard = true,
    bool multiplayerOnly = false,
    bool hasTarget = false,
    bool targetExists = false,
    bool allowedTarget = false,
    bool incompleteTarget = false,
    bool isUsePotion = false,
    bool localPotion = false,
    bool potionIdentityMatches = false,
    bool potionTargetValid = true)
    => MultiplayerSafeExecutePolicy.ClassifyResolved(
        new(
            localPlayer,
            localCard,
            multiplayerOnly,
            hasTarget,
            targetExists,
            allowedTarget,
            incompleteTarget,
            isUsePotion,
            localPotion,
            potionIdentityMatches,
            potionTargetValid));

Check(Structural().IsSafe, "A normal local PlayCard shape passes the structural gate.");
Check(
    Structural(
        "usepotion",
        isPlayCard: false,
        hasCardIdentity: false,
        isUsePotion: true,
        hasPotionIdentity: true,
        hasPotionSlot: true).IsSafe,
    "A local potion shape with slot and identity passes the structural gate.");
Check(
    Structural(
        "usepotion",
        isPlayCard: false,
        hasCardIdentity: false,
        isUsePotion: true,
        hasPotionSlot: true).Reason == "potion_identity_missing",
    "A potion without identity fails closed.");
Check(
    Structural(
        "usepotion",
        isPlayCard: false,
        hasCardIdentity: false,
        isUsePotion: true,
        hasPotionIdentity: true).Reason == "potion_slot_missing",
    "A potion without a concrete slot fails closed.");
Check(Structural(hasCardIdentity: false).Reason == "card_identity_missing", "Missing card identity fails closed.");
Check(Structural(endsTurn: true).Reason == "ends_player_turn", "Cards that end the player turn fail closed.");
Check(Structural(replay: true).Reason == "replay_semantics", "Replay semantics fail closed.");
Check(Structural(choice: true).IsSafe, "Planned local choices use the same native choice driver as singleplayer.");
Check(
    MultiplayerSafeExecutePolicy.CanReplayPlannedTurnSetupChoice(
        canDriveChoices: true,
        turn: 3,
        hasPlannedChoices: true),
    "Safe Execute replays an already-planned local turn-start choice from the accepted cross-turn route.");
Check(
    !MultiplayerSafeExecutePolicy.CanReplayPlannedTurnSetupChoice(
        canDriveChoices: true,
        turn: 1,
        hasPlannedChoices: true)
        && !MultiplayerSafeExecutePolicy.CanReplayPlannedTurnSetupChoice(
            canDriveChoices: true,
            turn: 3,
            hasPlannedChoices: false)
        && !MultiplayerSafeExecutePolicy.CanReplayPlannedTurnSetupChoice(
            canDriveChoices: false,
            turn: 3,
            hasPlannedChoices: true),
    "Safe Execute turn-setup replay does not authorize first-turn search, missing choices, or a session that cannot drive native choices.");
Check(Resolved(localPlayer: false).Reason == "local_player_missing", "Missing local player fails closed.");
Check(Resolved(localCard: false).Reason == "local_card_missing", "A card outside the local hand fails closed.");
Check(
    Resolved(multiplayerOnly: true).Reason == MultiplayerSafeExecutePolicy.ManualMultiplayerCardReason,
    "Multiplayer-only cards stop automatic execution and remain available for manual play.");
Check(
    Resolved(multiplayerOnly: true, hasTarget: true, targetExists: true, allowedTarget: true).Reason
        == MultiplayerSafeExecutePolicy.ManualMultiplayerCardReason,
    "A multiplayer-only card stays manual even when its teammate target is fully resolved.");
Check(
    Resolved(hasTarget: true, targetExists: true, allowedTarget: false).Reason
        == "remote_player_or_unknown_target",
    "An unapproved remote-player target remains fail-closed.");
Check(Resolved(hasTarget: true, targetExists: false).Reason == "target_missing", "Missing target fails closed.");
Check(
    Resolved(hasTarget: true, targetExists: true, allowedTarget: false).Reason == "remote_player_or_unknown_target",
    "Remote-player or unknown targets fail closed.");
Check(
    Resolved(hasTarget: true, targetExists: true, allowedTarget: true).IsSafe,
    "A resolved local-player or enemy target is allowed.");
Check(Resolved(incompleteTarget: true).Reason == "target_identity_incomplete", "Incomplete target identity fails closed.");
Check(Resolved().IsSafe, "A targetless resolved local card is allowed.");
Check(
    Resolved(
        localCard: false,
        isUsePotion: true,
        localPotion: true,
        potionIdentityMatches: true).IsSafe,
    "A matching local potion is admitted without requiring a card in hand.");
Check(
    Resolved(
        localCard: false,
        isUsePotion: true,
        localPotion: false).Reason == "local_potion_missing",
    "A missing local potion slot fails closed.");
Check(
    Resolved(
        localCard: false,
        isUsePotion: true,
        localPotion: true,
        potionIdentityMatches: false).Reason == "local_potion_mismatch",
    "A changed potion identity fails closed.");
Check(
    Resolved(
        localCard: false,
        hasTarget: true,
        targetExists: true,
        allowedTarget: false,
        isUsePotion: true,
        localPotion: true,
        potionIdentityMatches: true).Reason == "remote_player_or_unknown_target",
    "A local potion cannot target a teammate through Safe Execute.");
Check(
    Resolved(
        localCard: false,
        isUsePotion: true,
        localPotion: true,
        potionIdentityMatches: true,
        potionTargetValid: false).Reason == "potion_target_invalid",
    "A potion whose live native target validation changed fails closed.");
IReadOnlyList<SafeLocalActionDecision> longSafeRoute =
    Enumerable.Repeat(SafeLocalActionDecision.Allow, 64).ToArray();
IReadOnlyList<SafeLocalActionDecision> longSafePrefix =
    MultiplayerSafeExecutePolicy.TakeBoundedSafePrefix(
        longSafeRoute,
        decision => decision,
        out SafeLocalActionDecision longSafeStop);
Check(
    longSafePrefix.Count == longSafeRoute.Count && longSafeStop.IsSafe,
    "Safe Execute preflight is bounded by the finite planned route, not a fixed action-count ceiling.");
IReadOnlyList<SafeLocalActionDecision> allSafe =
    [SafeLocalActionDecision.Allow, SafeLocalActionDecision.Allow,
     SafeLocalActionDecision.Allow, SafeLocalActionDecision.Allow];
IReadOnlyList<SafeLocalActionDecision> fullPrefix =
    MultiplayerSafeExecutePolicy.TakeBoundedSafePrefix(allSafe, decision => decision, out SafeLocalActionDecision fullStop);
Check(fullPrefix.Count == 4 && fullStop.IsSafe, "An all-safe route returns its complete bounded prefix.");
IReadOnlyList<SafeLocalActionDecision> safeThenUnsafe =
    [SafeLocalActionDecision.Allow, SafeLocalActionDecision.Allow,
     new(false, "replay_semantics"), SafeLocalActionDecision.Allow];
IReadOnlyList<SafeLocalActionDecision> truncatedPrefix =
    MultiplayerSafeExecutePolicy.TakeBoundedSafePrefix(
        safeThenUnsafe,
        decision => decision,
        out SafeLocalActionDecision truncatedStop);
Check(
    truncatedPrefix.Count == 2 && truncatedStop.Reason == "replay_semantics",
    "The first unsafe route action is a hard boundary; later safe actions are not skipped to.");
IReadOnlyList<SafeLocalActionDecision> unsafeFirst =
    [new(false, "ends_player_turn"), SafeLocalActionDecision.Allow];
IReadOnlyList<SafeLocalActionDecision> emptyPrefix =
    MultiplayerSafeExecutePolicy.TakeBoundedSafePrefix(
        unsafeFirst,
        decision => decision,
        out SafeLocalActionDecision emptyStop);
Check(emptyPrefix.Count == 0 && emptyStop.Reason == "ends_player_turn", "An unsafe first route action returns an empty prefix.");
Check(
    MultiplayerSafeExecutePolicy.CanGrantLabCapability(
        new(MultiplayerSafeExecutePolicy.LabModeToken, true, true)),
    "The Lab capability requires the exact lab token plus evidence and an owned client instance.");
Check(
    !MultiplayerSafeExecutePolicy.CanGrantLabCapability(new("safe-execute", true, true)),
    "The Lab gate rejects the formal safe-execute token.");
Check(
    !MultiplayerSafeExecutePolicy.CanGrantLabCapability(
        new(MultiplayerSafeExecutePolicy.LabModeToken, false, true)),
    "The Lab capability is rejected without Probe evidence.");
Check(
    !MultiplayerSafeExecutePolicy.CanGrantLabCapability(
        new(MultiplayerSafeExecutePolicy.LabModeToken, true, false)),
    "The Lab capability is rejected outside an owned client instance.");
Check(
    MultiplayerSafeExecutePolicy.CanGrantFormalCapability(MultiplayerSafeExecutePolicy.FormalModeToken),
    "The formal Safe Execute capability requires the exact explicit safe-execute token.");
Check(
    !MultiplayerSafeExecutePolicy.CanGrantFormalCapability(MultiplayerSafeExecutePolicy.LabModeToken),
    "The Lab token is not silently promoted to the formal capability.");

MultiplayerSafeExecutionSession session = new(
    startTurnNumber: 1,
    routeGeneration: 7,
    startWorldVersion: 4,
    maxActions: 5);
Check(session.State == MultiplayerSafeExecutionState.Authorized, "A Safe Execute request starts authorized with the planned route capacity.");
bool fiveActionRouteAccepted = true;
for (int actionIndex = 0; actionIndex < 5; actionIndex++)
{
    fiveActionRouteAccepted = fiveActionRouteAccepted
        && session.TryBeginAction(
             actionIndex,
             $"PlayCard:CARD_{actionIndex}:0:target=-",
             1,
             7,
             4 + actionIndex,
             out _)
        && session.MarkAwaitingWorldUpdate()
        && session.BeginRevalidation()
        && session.AcceptAction(5 + actionIndex, hasNextAction: actionIndex < 4);
}
Check(
    fiveActionRouteAccepted
        && session.CompletedActions == 5
        && session.State == MultiplayerSafeExecutionState.Completed,
    "A five-action route advances one session through every action boundary and completes.");
MultiplayerSafeExecutionSession indexSession = new(
    startTurnNumber: 1,
    routeGeneration: 7,
    startWorldVersion: 4,
    maxActions: 2);
Check(
    indexSession.TryBeginAction(0, "PlayCard:STRIKE:0:target=-", 1, 7, 4, out _)
        && indexSession.MarkAwaitingWorldUpdate()
        && indexSession.BeginRevalidation()
        && indexSession.AcceptAction(5, hasNextAction: true)
        && !indexSession.TryBeginAction(2, "PlayCard:DEFEND:0:target=-", 1, 7, 5, out string indexReason)
        && indexReason == "action_index_mismatch",
    "A stale or skipped action index is rejected after a successful earlier action.");
MultiplayerSafeExecutionSession ceilingSession = new(
    startTurnNumber: 1,
    routeGeneration: 7,
    startWorldVersion: 4,
    maxActions: 2);
for (int actionIndex = 0; actionIndex < 2; actionIndex++)
{
    _ = ceilingSession.TryBeginAction(
        actionIndex,
        $"PlayCard:CARD_{actionIndex}:0:target=-",
        1,
        7,
        4 + actionIndex,
        out _);
    _ = ceilingSession.MarkAwaitingWorldUpdate();
    _ = ceilingSession.BeginRevalidation();
    _ = ceilingSession.AcceptAction(5 + actionIndex, hasNextAction: actionIndex == 0);
}
Check(
    !ceilingSession.TryBeginAction(2, "PlayCard:EXTRA:0:target=-", 1, 7, 6, out string capReason)
        && capReason == "session_state_Completed",
    "A completed route-bounded session rejects an action beyond its planned action capacity.");
MultiplayerSafeExecutionSession conflictSession = new(
    startTurnNumber: 1,
    routeGeneration: 7,
    startWorldVersion: 4,
    maxActions: 1);
Check(
    !conflictSession.TryBeginAction(0, "PlayCard:STRIKE:0:target=-", 1, 7, 5, out string worldReason)
        && worldReason == "world_version_not_accepted",
    "A WorldVersion conflict cannot consume a new action authorization.");

MultiplayerSafeActionRevalidationFacts RevalidationFacts(bool hasNextAction = true)
    => new(
        NativeLocalActionCaptured: true,
        ActionQueueIdle: true,
        ExpectedContinuationStateMatched: true,
        ExpectedRemoteStateMatched: true,
        LocalCardRemovedFromHand: true,
        LocalPlayerIdentityStable: true,
        EnergyStateConsistent: true,
        TargetIdentityStable: true,
        RemotePublicStateUnchanged: true,
        EnemyStateMatchesExpectedTarget: true,
        WorldVersionAdvanced: true,
        WorldVersionStable: true,
        HasNextAction: hasNextAction);

Check(
    MultiplayerSafeExecutePolicy.RevalidateAction(RevalidationFacts())
        == MultiplayerSafeActionRevalidationDecision.SafeToContinue,
    "A fully matched first action is safe to continue.");
Check(
    MultiplayerSafeExecutePolicy.RevalidateAction(RevalidationFacts(hasNextAction: false))
        == MultiplayerSafeActionRevalidationDecision.ExpectedLocalChange,
    "A fully matched final action is recorded as an expected local change.");
Check(
    MultiplayerSafeExecutePolicy.RevalidateAction(
        RevalidationFacts() with
        {
            LocalCardRemovedFromHand = false,
            EnergyStateConsistent = false,
            RemotePublicStateUnchanged = false,
            EnemyStateMatchesExpectedTarget = false,
        })
        == MultiplayerSafeActionRevalidationDecision.SafeToContinue,
    "Modeled draw/generation/Choice or multi-target chains continue even when legacy heuristics disagree.");
Check(
    MultiplayerSafeExecutePolicy.RevalidateAction(
        RevalidationFacts() with { ExpectedRemoteStateMatched = false })
        == MultiplayerSafeActionRevalidationDecision.RemoteOrUnknownChange,
    "A teammate state that differs from the one-action prediction invalidates the old suffix.");
Check(
    MultiplayerSafeExecutePolicy.RevalidateAction(
        RevalidationFacts() with { ExpectedContinuationStateMatched = false })
        == MultiplayerSafeActionRevalidationDecision.ActionMismatch,
    "A settled local/enemy/RNG state that differs from production replay fails closed.");
Check(
    MultiplayerSafeExecutePolicy.RevalidateAction(
        RevalidationFacts() with { NativeLocalActionCaptured = false })
        == MultiplayerSafeActionRevalidationDecision.ActionMismatch,
    "A missing expected native local-action attribution fails closed.");

string[] oneEnemyBefore = ["7:VINE:54/100/0:MOVE_A:powers=-"];
string[] oneEnemyAfter = ["7:VINE:29/100/0:MOVE_A:powers=VULNERABLE:1"];
Check(
    MultiplayerSafeExecutePolicy.EnemyStateMatchesExpectedLocalAction(
        oneEnemyBefore,
        oneEnemyAfter,
        targetCombatId: null),
    "A targetless local card may legitimately mutate existing enemy state without being mistaken for a remote delta.");
Check(
    !MultiplayerSafeExecutePolicy.EnemyStateMatchesExpectedLocalAction(
        oneEnemyBefore,
        [.. oneEnemyAfter, "8:SPAWN:10/10/0:MOVE:powers=-"],
        targetCombatId: null),
    "A targetless local card still fails closed if the enemy identity set changes unexpectedly.");
Check(
    !MultiplayerSafeExecutePolicy.EnemyStateMatchesExpectedLocalAction(
        ["7:A:50/50/0:M:powers=-", "8:B:50/50/0:M:powers=-"],
        ["7:A:40/50/0:M:powers=-", "8:B:40/50/0:M:powers=-"],
        targetCombatId: 7),
    "A targeted action still rejects mutation of a different enemy.");
Check(
    MultiplayerSafeExecutePolicy.RevalidateAction(
        RevalidationFacts() with { WorldVersionStable = false })
        == MultiplayerSafeActionRevalidationDecision.WorldUnstable,
    "An unstable WorldVersion blocks continuation.");
conflictSession.Abort("remote_or_unknown_change");
Check(
    conflictSession.State == MultiplayerSafeExecutionState.Aborted
        && !conflictSession.TryBeginAction(0, "PlayCard:STRIKE:0:target=-", 1, 7, 4, out string abortReason)
        && abortReason == "session_state_Aborted",
    "An aborted session clears authorization and cannot leak into a later action.");

MultiplayerSafeEndTurnFacts EndTurnFacts(bool allowed = true)
    => new(
        CurrentCombatLifecycle: allowed,
        LocalPlayableTurn: allowed,
        RouteGenerationCurrent: allowed,
        RouteEndsWithEndTurn: allowed,
        ActionQueueIdle: allowed,
        NoPendingChoice: allowed,
        LocalTurnIdentityStable: allowed,
        WorldVersionMatchesAccepted: allowed,
        WorldVersionStable: allowed,
        NoPendingWorldObservation: allowed);

Check(
    MultiplayerSafeExecutePolicy.ValidateSafeEndTurn(EndTurnFacts())
        == MultiplayerSafeEndTurnDecision.Safe,
    "A fully revalidated accepted route may consume Safe EndTurn.");
Check(
    MultiplayerSafeExecutePolicy.ValidateSafeEndTurn(
        EndTurnFacts() with { CurrentCombatLifecycle = false })
        == MultiplayerSafeEndTurnDecision.CombatLifecycleChanged,
    "A lifecycle change before EndTurn cancels the boundary.");
Check(
    MultiplayerSafeExecutePolicy.ValidateSafeEndTurn(
        EndTurnFacts() with { RouteGenerationCurrent = false })
        == MultiplayerSafeEndTurnDecision.RouteGenerationChanged,
    "A stale route generation cannot authorize EndTurn.");
Check(
    MultiplayerSafeExecutePolicy.ValidateSafeEndTurn(
        EndTurnFacts() with { RouteEndsWithEndTurn = false })
        == MultiplayerSafeEndTurnDecision.RouteBoundaryMissing,
    "Action exhaustion without an accepted EndTurn boundary cannot end the turn.");
Check(
    MultiplayerSafeExecutePolicy.ValidateSafeEndTurn(
        EndTurnFacts() with { ActionQueueIdle = false })
        == MultiplayerSafeEndTurnDecision.ActionQueuePending,
    "A pending native action blocks Safe EndTurn.");
Check(
    MultiplayerSafeExecutePolicy.ValidateSafeEndTurn(
        EndTurnFacts() with { NoPendingChoice = false })
        == MultiplayerSafeEndTurnDecision.ChoicePending,
    "A pending native choice blocks Safe EndTurn.");
Check(
    MultiplayerSafeExecutePolicy.ValidateSafeEndTurn(
        EndTurnFacts() with { WorldVersionMatchesAccepted = false })
        == MultiplayerSafeEndTurnDecision.WorldVersionNotAccepted,
    "An observed world change not accepted by the session blocks EndTurn.");
Check(
    MultiplayerSafeExecutePolicy.ValidateSafeEndTurn(
        EndTurnFacts() with { NoPendingWorldObservation = false })
        == MultiplayerSafeEndTurnDecision.WorldObservationPending,
    "A pending remote observation blocks Safe EndTurn.");

MultiplayerSafeExecutionSession endTurnSession = new(
    startTurnNumber: 1,
    routeGeneration: 7,
    startWorldVersion: 4,
    maxActions: 0);
Check(
    endTurnSession.TryBeginEndTurn(1, 7, 4, out string endTurnStartReason)
        && endTurnStartReason == "end_turn_executing"
        && endTurnSession.State == MultiplayerSafeExecutionState.EndTurnExecuting
        && endTurnSession.CompleteEndTurn()
        && endTurnSession.EndTurnConsumed
        && !endTurnSession.TryBeginEndTurn(1, 7, 4, out string repeatEndTurnReason)
        && repeatEndTurnReason == "end_turn_already_consumed",
    "Safe EndTurn consumes its authorization exactly once.");

MultiplayerSafeExecutionSession actionThenEndTurnSession = new(
    startTurnNumber: 1,
    routeGeneration: 7,
    startWorldVersion: 4,
    maxActions: 1);
Check(
    actionThenEndTurnSession.TryBeginAction(0, "PlayCard:STRIKE:0:target=-", 1, 7, 4, out _)
        && actionThenEndTurnSession.MarkAwaitingWorldUpdate()
        && actionThenEndTurnSession.BeginRevalidation()
        && actionThenEndTurnSession.AcceptAction(5, hasNextAction: false)
        && actionThenEndTurnSession.TryBeginEndTurn(1, 7, 5, out _)
        && actionThenEndTurnSession.CompleteEndTurn(),
    "Safe EndTurn follows a completed local action sequence without reusing action authorization.");
Check(
    !actionThenEndTurnSession.TryBeginAction(
        0,
        "PlayCard:STALE:0:target=-",
        2,
        8,
        6,
        out string staleAfterEndTurnReason)
        && staleAfterEndTurnReason == "session_state_Completed",
    "The previous turn session cannot deploy after EndTurn.");

MultiplayerSafeExecutionSession nextTurnSession = new(
    startTurnNumber: 2,
    routeGeneration: 8,
    startWorldVersion: 6,
    maxActions: 1);
Check(
    nextTurnSession.TryBeginAction(
        0,
        "PlayCard:FRESH:0:target=-",
        2,
        8,
        6,
        out _),
    "The next local turn requires a new session, turn identity, and route generation.");

MultiplayerSafeExecutionSession repeatedRemoteChangeSession = new(
    startTurnNumber: 1,
    routeGeneration: 7,
    startWorldVersion: 4,
    maxActions: 1);
repeatedRemoteChangeSession.Abort("remote_or_unknown_change_1");
repeatedRemoteChangeSession.Abort("remote_or_unknown_change_2");
Check(
    repeatedRemoteChangeSession.State == MultiplayerSafeExecutionState.Aborted
        && !repeatedRemoteChangeSession.TryBeginAction(
            0,
            "PlayCard:STALE:0:target=-",
            1,
            7,
            5,
            out string repeatedRemoteReason)
        && repeatedRemoteReason == "session_state_Aborted",
    "Repeated remote changes never re-authorize an aborted deployment.");

Check(
    MultiplayerSafeExecutePolicy.ShouldAutoDeploy(
        safeAutoEnabled: true,
        explicitDeploymentRequested: false)
        && MultiplayerSafeExecutePolicy.ShouldAutoDeploy(
            safeAutoEnabled: false,
            explicitDeploymentRequested: true)
        && !MultiplayerSafeExecutePolicy.ShouldAutoDeploy(
            safeAutoEnabled: false,
            explicitDeploymentRequested: false),
    "Safe Auto or one explicit request may arm deployment, but an advisor-only result cannot.");

Check(
    MultiplayerSafeExecutePolicy.ShouldKeepSafeAutoAfterBoundary(SafeLocalActionDecision.Allow)
        && MultiplayerSafeExecutePolicy.ShouldKeepSafeAutoAfterBoundary(
            new(false, MultiplayerSafeExecutePolicy.ManualMultiplayerCardReason))
        && MultiplayerSafeExecutePolicy.ShouldKeepSafeAutoAfterBoundary(
            new(false, MultiplayerSafeExecutePolicy.TeammateForecastBoundaryReason)),
    "A completed safe prefix, manual multiplayer-card boundary, or U5 teammate forecast observation keeps Safe Auto eligible for a fresh search.");

Check(
    !MultiplayerSafeExecutePolicy.ShouldKeepSafeAutoAfterBoundary(new(false, "replay_semantics"))
        && !MultiplayerSafeExecutePolicy.ShouldKeepSafeAutoAfterBoundary(new(false, "potion_target_invalid"))
        && !MultiplayerSafeExecutePolicy.ShouldKeepSafeAutoAfterBoundary(
            new(false, "remote_player_or_unknown_target")),
    "Replay, invalid-potion-target, or teammate/unknown-target boundaries stop Safe Auto instead of looping.");

Console.WriteLine($"PASS: {checks} multiplayer safe-execute policy checks");
