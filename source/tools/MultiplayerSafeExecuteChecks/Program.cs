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
Check(MultiplayerSafeExecutePolicy.MaxActionsPerDeployment == 1, "MP-2A is capped at one action per deployment.");
Check(
    MultiplayerSafeExecutePolicy.DeploymentStopAfter(1, 2).Reason
        == MultiplayerSafeExecutePolicy.SingleActionLimitReason,
    "A second planned action stops at the MP-2A single-action boundary.");
Check(
    MultiplayerSafeExecutePolicy.DeploymentStopAfter(1, 1).IsSafe,
    "A single planned action does not synthesize an extra stop reason.");
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

Console.WriteLine($"PASS: {checks} multiplayer safe-execute policy checks");
