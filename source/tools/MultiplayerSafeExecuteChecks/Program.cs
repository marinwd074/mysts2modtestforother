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
Check(MultiplayerSafeExecutePolicy.MaxActionsPerDeployment == 2, "MP-2B is capped at two actions per deployment.");
Check(
    MultiplayerSafeExecutePolicy.DeploymentStopAfter(2, 3).Reason
        == MultiplayerSafeExecutePolicy.TwoActionLimitReason,
    "A third planned action stops at the MP-2B two-action boundary.");
Check(
    MultiplayerSafeExecutePolicy.DeploymentStopAfter(1, 2).IsSafe,
    "A second planned action remains admissible before the MP-2B cap is reached.");
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
    maxActions: 2);
Check(session.State == MultiplayerSafeExecutionState.Authorized, "A Safe Execute request starts authorized.");
Check(
    session.TryBeginAction(0, "PlayCard:STRIKE:0:target=-", 4, out _)
        && session.State == MultiplayerSafeExecutionState.Executing,
    "The first action consumes the authorization and enters Executing.");
Check(session.MarkAwaitingWorldUpdate(), "A completed native action enters AwaitingWorldUpdate.");
Check(session.BeginRevalidation(), "A stable observation enters Revalidating.");
Check(
    session.AcceptAction(5, hasNextAction: true)
        && session.State == MultiplayerSafeExecutionState.Authorized
        && session.CompletedActions == 1,
    "A matched first action re-authorizes only the next bounded action.");
Check(
    !session.TryBeginAction(2, "PlayCard:DEFEND:0:target=-", 5, out string indexReason)
        && indexReason == "action_index_mismatch",
    "A stale or skipped action index is rejected.");
MultiplayerSafeExecutionSession conflictSession = new(
    startTurnNumber: 1,
    routeGeneration: 7,
    startWorldVersion: 4,
    maxActions: 2);
Check(
    !conflictSession.TryBeginAction(0, "PlayCard:STRIKE:0:target=-", 5, out string worldReason)
        && worldReason == "world_version_not_accepted",
    "A WorldVersion conflict cannot consume a new action authorization.");
Check(
    session.TryBeginAction(1, "PlayCard:DEFEND:0:target=-", 5, out _)
        && session.MarkAwaitingWorldUpdate()
        && session.BeginRevalidation()
        && session.AcceptAction(6, hasNextAction: false)
        && session.State == MultiplayerSafeExecutionState.Completed,
    "The final matched action closes the execution session.");

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
