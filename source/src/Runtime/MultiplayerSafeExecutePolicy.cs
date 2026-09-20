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

internal readonly record struct MultiplayerSafeExecuteLabFacts(
    string? ModeToken,
    bool ProbeEvidenceEnabled,
    bool OwnedClientInstance);

internal enum MultiplayerSafeExecutionState
{
    Authorized,
    Executing,
    AwaitingWorldUpdate,
    Revalidating,
    Completed,
    Aborted,
}

internal enum MultiplayerSafeActionRevalidationDecision
{
    ExpectedLocalChange,
    RemoteOrUnknownChange,
    ActionMismatch,
    WorldUnstable,
    SafeToContinue,
}

internal readonly record struct MultiplayerSafeActionRevalidationFacts(
    bool NativePlayCardCaptured,
    bool ActionQueueIdle,
    bool LocalCardRemovedFromHand,
    bool LocalPlayerIdentityStable,
    bool EnergyStateConsistent,
    bool TargetIdentityStable,
    bool RemotePublicStateUnchanged,
    bool EnemyStateMatchesExpectedTarget,
    bool WorldVersionAdvanced,
    bool WorldVersionStable,
    bool HasNextAction);

/// <summary>
/// Explicit lifecycle for one user-authorized multiplayer Safe Execute request.
/// The session is deliberately independent of live game objects so its transitions
/// can be pinned by contract tests and stale deployment completions cannot re-arm it.
/// </summary>
internal sealed class MultiplayerSafeExecutionSession
{
    private static int _nextRequestId;

    internal MultiplayerSafeExecutionSession(
        int startTurnNumber,
        int routeGeneration,
        long startWorldVersion,
        int maxActions)
    {
        if (maxActions <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxActions));

        RequestId = Interlocked.Increment(ref _nextRequestId);
        StartTurnNumber = startTurnNumber;
        RouteGeneration = routeGeneration;
        StartWorldVersion = startWorldVersion;
        LastAcceptedWorldVersion = startWorldVersion;
        MaxActions = maxActions;
        State = MultiplayerSafeExecutionState.Authorized;
    }

    internal int RequestId { get; }
    internal int StartTurnNumber { get; }
    internal int RouteGeneration { get; }
    internal long StartWorldVersion { get; }
    internal long LastAcceptedWorldVersion { get; private set; }
    internal int MaxActions { get; }
    internal int CompletedActions { get; private set; }
    internal int CurrentActionIndex { get; private set; } = -1;
    internal string? ExpectedActionToken { get; private set; }
    internal string? AbortReason { get; private set; }
    internal MultiplayerSafeExecutionState State { get; private set; }

    internal bool TryBeginAction(
        int actionIndex,
        string actionToken,
        long worldVersion,
        out string reason)
    {
        if (State != MultiplayerSafeExecutionState.Authorized)
        {
            reason = $"session_state_{State}";
            return false;
        }
        if (CompletedActions >= MaxActions)
        {
            reason = "action_cap_reached";
            return false;
        }
        if (actionIndex != CompletedActions)
        {
            reason = "action_index_mismatch";
            return false;
        }
        if (worldVersion != LastAcceptedWorldVersion)
        {
            reason = "world_version_not_accepted";
            return false;
        }
        if (string.IsNullOrWhiteSpace(actionToken))
        {
            reason = "action_identity_missing";
            return false;
        }

        CurrentActionIndex = actionIndex;
        ExpectedActionToken = actionToken;
        State = MultiplayerSafeExecutionState.Executing;
        reason = "executing";
        return true;
    }

    internal bool MarkAwaitingWorldUpdate()
    {
        if (State != MultiplayerSafeExecutionState.Executing)
            return false;

        State = MultiplayerSafeExecutionState.AwaitingWorldUpdate;
        return true;
    }

    internal bool BeginRevalidation()
    {
        if (State != MultiplayerSafeExecutionState.AwaitingWorldUpdate)
            return false;

        State = MultiplayerSafeExecutionState.Revalidating;
        return true;
    }

    internal bool AcceptAction(long worldVersion, bool hasNextAction)
    {
        if (State != MultiplayerSafeExecutionState.Revalidating
            || worldVersion <= LastAcceptedWorldVersion)
        {
            return false;
        }

        LastAcceptedWorldVersion = worldVersion;
        CompletedActions++;
        ExpectedActionToken = null;
        State = hasNextAction && CompletedActions < MaxActions
            ? MultiplayerSafeExecutionState.Authorized
            : MultiplayerSafeExecutionState.Completed;
        return true;
    }

    internal void Complete(string reason)
    {
        if (State is MultiplayerSafeExecutionState.Completed
            or MultiplayerSafeExecutionState.Aborted)
        {
            return;
        }

        ExpectedActionToken = null;
        State = MultiplayerSafeExecutionState.Completed;
        AbortReason = reason;
    }

    internal void Abort(string reason)
    {
        if (State == MultiplayerSafeExecutionState.Aborted)
            return;

        ExpectedActionToken = null;
        AbortReason = reason;
        State = MultiplayerSafeExecutionState.Aborted;
    }
}

/// <summary>
/// Pure admission and revalidation policy for multiplayer Safe Execute. It contains
/// no game objects so CI can pin action fail-closed rules, session boundaries and the
/// Lab-only capability boundary.
/// </summary>
internal static class MultiplayerSafeExecutePolicy
{
    internal const int MaxActionsPerDeployment = 2;
    internal const string SingleActionLimitReason = "mp2a_single_action_limit";
    internal const string TwoActionLimitReason = "mp2b_two_action_limit";
    internal const string FormalModeToken = "safe-execute";
    internal const string LabModeToken = "safe-execute-lab";

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
            ? new(false, TwoActionLimitReason)
            : SafeLocalActionDecision.Allow;

    internal static MultiplayerSafeActionRevalidationDecision RevalidateAction(
        MultiplayerSafeActionRevalidationFacts facts)
    {
        if (!facts.NativePlayCardCaptured || !facts.ActionQueueIdle)
            return MultiplayerSafeActionRevalidationDecision.ActionMismatch;
        if (!facts.WorldVersionAdvanced || !facts.WorldVersionStable)
            return MultiplayerSafeActionRevalidationDecision.WorldUnstable;
        if (!facts.LocalCardRemovedFromHand
            || !facts.LocalPlayerIdentityStable
            || !facts.EnergyStateConsistent
            || !facts.TargetIdentityStable)
        {
            return MultiplayerSafeActionRevalidationDecision.ActionMismatch;
        }
        if (!facts.RemotePublicStateUnchanged || !facts.EnemyStateMatchesExpectedTarget)
            return MultiplayerSafeActionRevalidationDecision.RemoteOrUnknownChange;
        return facts.HasNextAction
            ? MultiplayerSafeActionRevalidationDecision.SafeToContinue
            : MultiplayerSafeActionRevalidationDecision.ExpectedLocalChange;
    }

    internal static string RevalidationReason(
        MultiplayerSafeActionRevalidationDecision decision)
        => decision switch
        {
            MultiplayerSafeActionRevalidationDecision.ExpectedLocalChange
                => "expected_local_change",
            MultiplayerSafeActionRevalidationDecision.RemoteOrUnknownChange
                => "remote_or_unknown_change",
            MultiplayerSafeActionRevalidationDecision.ActionMismatch
                => "action_mismatch",
            MultiplayerSafeActionRevalidationDecision.WorldUnstable
                => "world_unstable",
            MultiplayerSafeActionRevalidationDecision.SafeToContinue
                => "safe_to_continue",
            _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, null),
        };

    internal static bool CanGrantLabCapability(MultiplayerSafeExecuteLabFacts facts)
        => string.Equals(facts.ModeToken, LabModeToken, StringComparison.OrdinalIgnoreCase)
           && facts.ProbeEvidenceEnabled
           && facts.OwnedClientInstance;

    internal static bool CanGrantFormalCapability(string? modeToken)
        => string.Equals(modeToken, FormalModeToken, StringComparison.OrdinalIgnoreCase);
}
