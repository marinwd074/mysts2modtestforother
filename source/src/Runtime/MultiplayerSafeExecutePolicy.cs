namespace CombatSolver;

internal readonly record struct SafeLocalActionDecision(bool IsSafe, string Reason)
{
    public static SafeLocalActionDecision Allow { get; } = new(true, "safe_local_play_card");
}

internal readonly record struct MultiplayerSafeActionStructuralFacts(
    string KindToken,
    bool IsPlayCard,
    bool HasCardIdentity,
    bool EndsPlayerTurn,
    bool HasReplaySemantics,
    bool RequiresChoice);

internal readonly record struct MultiplayerSafeActionResolvedFacts(
    bool HasLocalPlayer,
    bool HasLocalCard,
    bool IsMultiplayerOnlyCard,
    bool HasTarget,
    bool TargetExists,
    bool IsAllowedTarget,
    bool HasIncompleteTargetIdentity);

/// <summary>
/// Pure admission policy for the dormant MP-2A tier. It contains no game objects so CI
/// can pin fail-closed reasons independently from native/runtime integration.
/// </summary>
internal static class MultiplayerSafeExecutePolicy
{
    internal const int MaxActionsPerDeployment = 1;
    internal const string SingleActionLimitReason = "mp2a_single_action_limit";

    internal static SafeLocalActionDecision ClassifyStructural(
        MultiplayerSafeActionStructuralFacts facts)
    {
        if (!facts.IsPlayCard)
            return new(false, $"kind_{facts.KindToken}");
        if (!facts.HasCardIdentity)
            return new(false, "card_identity_missing");
        if (facts.EndsPlayerTurn)
            return new(false, "ends_player_turn");
        if (facts.HasReplaySemantics)
            return new(false, "replay_semantics");
        if (facts.RequiresChoice)
            return new(false, "choice_required");
        return SafeLocalActionDecision.Allow;
    }

    internal static SafeLocalActionDecision ClassifyResolved(
        MultiplayerSafeActionResolvedFacts facts)
    {
        if (!facts.HasLocalPlayer)
            return new(false, "local_player_missing");
        if (!facts.HasLocalCard)
            return new(false, "local_card_missing");
        if (facts.IsMultiplayerOnlyCard)
            return new(false, "multiplayer_only_card");
        if (facts.HasTarget)
        {
            if (!facts.TargetExists)
                return new(false, "target_missing");
            if (!facts.IsAllowedTarget)
                return new(false, "remote_player_or_unknown_target");
        }
        else if (facts.HasIncompleteTargetIdentity)
        {
            return new(false, "target_identity_incomplete");
        }
        return SafeLocalActionDecision.Allow;
    }

    internal static SafeLocalActionDecision DeploymentStopAfter(
        int safeActionCount,
        int plannedActionCount)
        => safeActionCount >= MaxActionsPerDeployment && plannedActionCount > safeActionCount
            ? new(false, SingleActionLimitReason)
            : SafeLocalActionDecision.Allow;
}
