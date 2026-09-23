namespace CombatSolver;

internal readonly record struct SafeLocalActionDecision(
    bool IsSafe,
    string Reason)
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
    EndTurnExecuting,
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

internal readonly record struct MultiplayerSafeEndTurnFacts(
    bool CurrentCombatLifecycle,
    bool LocalPlayableTurn,
    bool RouteGenerationCurrent,
    bool RouteEndsWithEndTurn,
    bool ActionQueueIdle,
    bool NoPendingChoice,
    bool LocalTurnIdentityStable,
    bool WorldVersionMatchesAccepted,
    bool WorldVersionStable,
    bool NoPendingWorldObservation);

internal enum MultiplayerSafeEndTurnDecision
{
    Safe,
    CombatLifecycleChanged,
    NotLocalPlayableTurn,
    RouteGenerationChanged,
    RouteBoundaryMissing,
    ActionQueuePending,
    ChoicePending,
    LocalTurnIdentityChanged,
    WorldVersionNotAccepted,
    WorldUnstable,
    WorldObservationPending,
}

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
    internal bool EndTurnConsumed { get; private set; }

    internal bool TryBeginAction(
        int actionIndex,
        string actionToken,
        int currentTurnNumber,
        int currentRouteGeneration,
        long worldVersion,
        out string reason)
    {
        if (State != MultiplayerSafeExecutionState.Authorized)
        {
            reason = $"session_state_{State}";
            return false;
        }
        if (currentTurnNumber != StartTurnNumber)
        {
            reason = "turn_identity_mismatch";
            return false;
        }
        if (currentRouteGeneration != RouteGeneration)
        {
            reason = "route_generation_changed";
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

    internal bool TryBeginEndTurn(
        int currentTurnNumber,
        int currentRouteGeneration,
        long worldVersion,
        out string reason)
    {
        if (EndTurnConsumed)
        {
            reason = "end_turn_already_consumed";
            return false;
        }
        if ((State == MultiplayerSafeExecutionState.Authorized && CompletedActions != 0)
            || (State != MultiplayerSafeExecutionState.Authorized
                && State != MultiplayerSafeExecutionState.Completed))
        {
            reason = $"session_state_{State}";
            return false;
        }
        if (currentTurnNumber != StartTurnNumber)
        {
            reason = "turn_identity_mismatch";
            return false;
        }
        if (currentRouteGeneration != RouteGeneration)
        {
            reason = "route_generation_changed";
            return false;
        }
        if (worldVersion != LastAcceptedWorldVersion)
        {
            reason = "world_version_not_accepted";
            return false;
        }

        EndTurnConsumed = true;
        State = MultiplayerSafeExecutionState.EndTurnExecuting;
        reason = "end_turn_executing";
        return true;
    }

    internal bool CompleteEndTurn()
    {
        if (State != MultiplayerSafeExecutionState.EndTurnExecuting)
            return false;

        ExpectedActionToken = null;
        State = MultiplayerSafeExecutionState.Completed;
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
    // Keep one finite ceiling for every Safe Execute deployment, but make it large enough
    // to cover a normal complete current-turn route. Per-action live revalidation remains the
    // actual safety boundary; this cap only prevents unbounded automation.
    internal const int MaxActionsPerDeployment = 32;
    internal const string SingleActionLimitReason = "mp2a_single_action_limit";
    internal const string BoundedActionCeilingReason = "mp2c_action_ceiling";
    internal const string TwoActionLimitReason = BoundedActionCeilingReason;
    internal const string ManualMultiplayerCardReason = "multiplayer_only_manual_play";
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
            return new(false, ManualMultiplayerCardReason);
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
            ? new(false, BoundedActionCeilingReason)
            : SafeLocalActionDecision.Allow;

    internal static bool ShouldAutoDeploy(
        bool safeAutoEnabled,
        bool explicitDeploymentRequested)
        => safeAutoEnabled || explicitDeploymentRequested;

    internal static bool ShouldKeepSafeAutoAfterBoundary(SafeLocalActionDecision stop)
        => stop.IsSafe
           || string.Equals(stop.Reason, BoundedActionCeilingReason, StringComparison.Ordinal)
           || string.Equals(stop.Reason, ManualMultiplayerCardReason, StringComparison.Ordinal);

    internal static IReadOnlyList<T> TakeBoundedSafePrefix<T>(
        IReadOnlyList<T> actions,
        Func<T, SafeLocalActionDecision> classify,
        out SafeLocalActionDecision stop)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(classify);
        if (actions.Count == 0)
        {
            stop = SafeLocalActionDecision.Allow;
            return [];
        }

        List<T> safe = [];
        foreach (T action in actions)
        {
            if (safe.Count >= MaxActionsPerDeployment)
            {
                stop = DeploymentStopAfter(safe.Count, actions.Count);
                return safe;
            }

            SafeLocalActionDecision decision = classify(action);
            if (!decision.IsSafe)
            {
                stop = decision;
                return safe;
            }
            safe.Add(action);
        }

        stop = DeploymentStopAfter(safe.Count, actions.Count);
        return safe;
    }

    internal static bool EnemyStateMatchesExpectedLocalAction(
        IReadOnlyList<string> before,
        IReadOnlyList<string> after,
        uint? targetCombatId)
    {
        if (!TryBuildEnemyTokensById(before, out Dictionary<string, string> beforeById)
            || !TryBuildEnemyTokensById(after, out Dictionary<string, string> afterById))
        {
            return false;
        }

        string[] beforeIds = [.. beforeById.Keys.OrderBy(key => key, StringComparer.Ordinal)];
        string[] afterIds = [.. afterById.Keys.OrderBy(key => key, StringComparer.Ordinal)];
        if (!beforeIds.SequenceEqual(afterIds, StringComparer.Ordinal))
            return false;

        // A targetless local card may legitimately damage/apply powers to one or many enemies.
        // Remote teammate mutations are guarded independently by RemotePublicStateUnchanged.
        if (targetCombatId is null)
            return true;

        string targetId = targetCombatId.Value.ToString();
        foreach ((string id, string token) in beforeById)
        {
            if (string.Equals(id, targetId, StringComparison.Ordinal))
                continue;
            if (!string.Equals(afterById[id], token, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static bool TryBuildEnemyTokensById(
        IEnumerable<string> tokens,
        out Dictionary<string, string> byId)
    {
        byId = new(StringComparer.Ordinal);
        foreach (string token in tokens)
        {
            int separator = token.IndexOf(':');
            if (separator <= 0 || !byId.TryAdd(token[..separator], token))
            {
                byId.Clear();
                return false;
            }
        }
        return true;
    }

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

    internal static MultiplayerSafeEndTurnDecision ValidateSafeEndTurn(
        MultiplayerSafeEndTurnFacts facts)
    {
        if (!facts.CurrentCombatLifecycle)
            return MultiplayerSafeEndTurnDecision.CombatLifecycleChanged;
        if (!facts.LocalPlayableTurn)
            return MultiplayerSafeEndTurnDecision.NotLocalPlayableTurn;
        if (!facts.RouteGenerationCurrent)
            return MultiplayerSafeEndTurnDecision.RouteGenerationChanged;
        if (!facts.RouteEndsWithEndTurn)
            return MultiplayerSafeEndTurnDecision.RouteBoundaryMissing;
        if (!facts.ActionQueueIdle)
            return MultiplayerSafeEndTurnDecision.ActionQueuePending;
        if (!facts.NoPendingChoice)
            return MultiplayerSafeEndTurnDecision.ChoicePending;
        if (!facts.LocalTurnIdentityStable)
            return MultiplayerSafeEndTurnDecision.LocalTurnIdentityChanged;
        if (!facts.WorldVersionMatchesAccepted)
            return MultiplayerSafeEndTurnDecision.WorldVersionNotAccepted;
        if (!facts.WorldVersionStable)
            return MultiplayerSafeEndTurnDecision.WorldUnstable;
        if (!facts.NoPendingWorldObservation)
            return MultiplayerSafeEndTurnDecision.WorldObservationPending;
        return MultiplayerSafeEndTurnDecision.Safe;
    }

    internal static string SafeEndTurnReason(MultiplayerSafeEndTurnDecision decision)
        => decision switch
        {
            MultiplayerSafeEndTurnDecision.Safe => "safe_end_turn",
            MultiplayerSafeEndTurnDecision.CombatLifecycleChanged => "combat_lifecycle_changed",
            MultiplayerSafeEndTurnDecision.NotLocalPlayableTurn => "not_local_playable_turn",
            MultiplayerSafeEndTurnDecision.RouteGenerationChanged => "route_generation_changed",
            MultiplayerSafeEndTurnDecision.RouteBoundaryMissing => "route_boundary_missing",
            MultiplayerSafeEndTurnDecision.ActionQueuePending => "action_queue_pending",
            MultiplayerSafeEndTurnDecision.ChoicePending => "choice_pending",
            MultiplayerSafeEndTurnDecision.LocalTurnIdentityChanged => "local_turn_identity_changed",
            MultiplayerSafeEndTurnDecision.WorldVersionNotAccepted => "world_version_not_accepted",
            MultiplayerSafeEndTurnDecision.WorldUnstable => "world_unstable",
            MultiplayerSafeEndTurnDecision.WorldObservationPending => "world_observation_pending",
            _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, null),
        };

    internal static bool CanGrantLabCapability(MultiplayerSafeExecuteLabFacts facts)
        => string.Equals(facts.ModeToken, LabModeToken, StringComparison.OrdinalIgnoreCase)
           && facts.ProbeEvidenceEnabled
           && facts.OwnedClientInstance;

    internal static bool CanGrantFormalCapability(string? modeToken)
        => string.Equals(modeToken, FormalModeToken, StringComparison.OrdinalIgnoreCase);
}
