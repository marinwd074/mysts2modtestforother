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
    bool choice = false)
    => MultiplayerSafeExecutePolicy.ClassifyStructural(
        new(kind, isPlayCard, hasCardIdentity, endsTurn, replay, choice));

SafeLocalActionDecision Resolved(
    bool localPlayer = true,
    bool localCard = true,
    bool multiplayerOnly = false,
    bool hasTarget = false,
    bool targetExists = false,
    bool allowedTarget = false,
    bool incompleteTarget = false)
    => MultiplayerSafeExecutePolicy.ClassifyResolved(
        new(localPlayer, localCard, multiplayerOnly, hasTarget, targetExists, allowedTarget, incompleteTarget));

Check(Structural().IsSafe, "A normal local PlayCard shape passes the structural gate.");
Check(Structural("usepotion", isPlayCard: false).Reason == "kind_usepotion", "Potion actions fail closed.");
Check(Structural(hasCardIdentity: false).Reason == "card_identity_missing", "Missing card identity fails closed.");
Check(Structural(endsTurn: true).Reason == "ends_player_turn", "Cards that end the player turn fail closed.");
Check(Structural(replay: true).Reason == "replay_semantics", "Replay semantics fail closed.");
Check(Structural(choice: true).Reason == "choice_required", "Choice-driving cards fail closed.");
Check(Resolved(localPlayer: false).Reason == "local_player_missing", "Missing local player fails closed.");
Check(Resolved(localCard: false).Reason == "local_card_missing", "A card outside the local hand fails closed.");
Check(Resolved(multiplayerOnly: true).Reason == "multiplayer_only_card", "Multiplayer-only cards remain outside MP-2A.");
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
    MultiplayerSafeExecutePolicy.MaxActionsPerDeployment == 6,
    "MP-2C uses one finite six-action Safe Execute ceiling.");
Check(
    MultiplayerSafeExecutePolicy.DeploymentStopAfter(6, 7).Reason
        == MultiplayerSafeExecutePolicy.BoundedActionCeilingReason,
    "A seventh planned action stops at the bounded MP-2C ceiling.");
Check(
    MultiplayerSafeExecutePolicy.DeploymentStopAfter(5, 6).IsSafe,
    "A sixth planned action remains admissible before the MP-2C ceiling is reached.");
IReadOnlyList<SafeLocalActionDecision> allSafe =
    [SafeLocalActionDecision.Allow, SafeLocalActionDecision.Allow,
     SafeLocalActionDecision.Allow, SafeLocalActionDecision.Allow];
IReadOnlyList<SafeLocalActionDecision> fullPrefix =
    MultiplayerSafeExecutePolicy.TakeBoundedSafePrefix(allSafe, decision => decision, out SafeLocalActionDecision fullStop);
Check(fullPrefix.Count == 4 && fullStop.IsSafe, "An all-safe route returns its complete bounded prefix.");
IReadOnlyList<SafeLocalActionDecision> safeThenUnsafe =
    [SafeLocalActionDecision.Allow, SafeLocalActionDecision.Allow,
     new(false, "choice_required"), SafeLocalActionDecision.Allow];
IReadOnlyList<SafeLocalActionDecision> truncatedPrefix =
    MultiplayerSafeExecutePolicy.TakeBoundedSafePrefix(
        safeThenUnsafe,
        decision => decision,
        out SafeLocalActionDecision truncatedStop);
Check(
    truncatedPrefix.Count == 2 && truncatedStop.Reason == "choice_required",
    "The first unsafe route action is a hard boundary; later safe actions are not skipped to.");
IReadOnlyList<SafeLocalActionDecision> unsafeFirst =
    [new(false, "ends_player_turn"), SafeLocalActionDecision.Allow];
IReadOnlyList<SafeLocalActionDecision> emptyPrefix =
    MultiplayerSafeExecutePolicy.TakeBoundedSafePrefix(
        unsafeFirst,
        decision => decision,
        out SafeLocalActionDecision emptyStop);
Check(emptyPrefix.Count == 0 && emptyStop.Reason == "ends_player_turn", "An unsafe first route action returns an empty prefix.");
IReadOnlyList<SafeLocalActionDecision> overCeiling =
    Enumerable.Repeat(SafeLocalActionDecision.Allow, 7).ToArray();
IReadOnlyList<SafeLocalActionDecision> cappedPrefix =
    MultiplayerSafeExecutePolicy.TakeBoundedSafePrefix(
        overCeiling,
        decision => decision,
        out SafeLocalActionDecision ceilingStop);
Check(
    cappedPrefix.Count == 6 && ceilingStop.Reason == MultiplayerSafeExecutePolicy.BoundedActionCeilingReason,
    "A route longer than the hard ceiling is truncated without becoming unbounded.");
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
    maxActions: MultiplayerSafeExecutePolicy.MaxActionsPerDeployment);
Check(session.State == MultiplayerSafeExecutionState.Authorized, "A Safe Execute request starts authorized.");
bool fiveActionRouteAccepted = true;
for (int actionIndex = 0; actionIndex < 5; actionIndex++)
{
    fiveActionRouteAccepted = fiveActionRouteAccepted
        && session.TryBeginAction(
            actionIndex,
            $"PlayCard:CARD_{actionIndex}:0:target=-",
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
    maxActions: MultiplayerSafeExecutePolicy.MaxActionsPerDeployment);
Check(
    indexSession.TryBeginAction(0, "PlayCard:STRIKE:0:target=-", 4, out _)
        && indexSession.MarkAwaitingWorldUpdate()
        && indexSession.BeginRevalidation()
        && indexSession.AcceptAction(5, hasNextAction: true)
        && !indexSession.TryBeginAction(2, "PlayCard:DEFEND:0:target=-", 5, out string indexReason)
        && indexReason == "action_index_mismatch",
    "A stale or skipped action index is rejected after a successful earlier action.");
MultiplayerSafeExecutionSession ceilingSession = new(
    startTurnNumber: 1,
    routeGeneration: 7,
    startWorldVersion: 4,
    maxActions: 2);
for (int actionIndex = 0; actionIndex < 2; actionIndex++)
{
    _ = ceilingSession.TryBeginAction(actionIndex, $"PlayCard:CARD_{actionIndex}:0:target=-", 4 + actionIndex, out _);
    _ = ceilingSession.MarkAwaitingWorldUpdate();
    _ = ceilingSession.BeginRevalidation();
    _ = ceilingSession.AcceptAction(5 + actionIndex, hasNextAction: actionIndex == 0);
}
Check(
    !ceilingSession.TryBeginAction(2, "PlayCard:EXTRA:0:target=-", 6, out string capReason)
        && capReason == "session_state_Completed",
    "A completed bounded session rejects an action beyond its configured ceiling.");
MultiplayerSafeExecutionSession conflictSession = new(
    startTurnNumber: 1,
    routeGeneration: 7,
    startWorldVersion: 4,
    maxActions: MultiplayerSafeExecutePolicy.MaxActionsPerDeployment);
Check(
    !conflictSession.TryBeginAction(0, "PlayCard:STRIKE:0:target=-", 5, out string worldReason)
        && worldReason == "world_version_not_accepted",
    "A WorldVersion conflict cannot consume a new action authorization.");

MultiplayerSafeActionRevalidationFacts RevalidationFacts(bool hasNextAction = true)
    => new(
        NativePlayCardCaptured: true,
        ActionQueueIdle: true,
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
        RevalidationFacts() with { RemotePublicStateUnchanged = false })
        == MultiplayerSafeActionRevalidationDecision.RemoteOrUnknownChange,
    "A remote public mutation aborts before the next action.");
Check(
    MultiplayerSafeExecutePolicy.RevalidateAction(
        RevalidationFacts() with { LocalCardRemovedFromHand = false })
        == MultiplayerSafeActionRevalidationDecision.ActionMismatch,
    "A card that did not leave the local hand fails closed.");
Check(
    MultiplayerSafeExecutePolicy.RevalidateAction(
        RevalidationFacts() with { NativePlayCardCaptured = false })
        == MultiplayerSafeActionRevalidationDecision.ActionMismatch,
    "A missing native PlayCardAction attribution fails closed.");
Check(
    MultiplayerSafeExecutePolicy.RevalidateAction(
        RevalidationFacts() with { EnemyStateMatchesExpectedTarget = false })
        == MultiplayerSafeActionRevalidationDecision.RemoteOrUnknownChange,
    "An enemy mutation outside the expected target is treated as remote or unknown.");
Check(
    MultiplayerSafeExecutePolicy.RevalidateAction(
        RevalidationFacts() with { WorldVersionStable = false })
        == MultiplayerSafeActionRevalidationDecision.WorldUnstable,
    "An unstable WorldVersion blocks continuation.");
conflictSession.Abort("remote_or_unknown_change");
Check(
    conflictSession.State == MultiplayerSafeExecutionState.Aborted
        && !conflictSession.TryBeginAction(0, "PlayCard:STRIKE:0:target=-", 4, out string abortReason)
        && abortReason == "session_state_Aborted",
    "An aborted session clears authorization and cannot leak into a later action.");

Console.WriteLine($"PASS: {checks} multiplayer safe-execute policy checks");
