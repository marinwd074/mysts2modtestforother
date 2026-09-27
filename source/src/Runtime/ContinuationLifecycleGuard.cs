namespace CombatSolver;

/// <summary>
/// Prevents cached cross-turn continuation from surviving combat lifecycle changes
/// such as boss phase transitions. Integration point: SolverController search lifecycle.
/// </summary>
internal static class ContinuationLifecycleGuard
{
    internal static bool CanReuse(
        string expectedLifecycle,
        string actualLifecycle,
        out string reason)
    {
        if (string.Equals(expectedLifecycle, actualLifecycle, StringComparison.Ordinal))
        {
            reason = "exact_lifecycle_match";
            return true;
        }

        reason = $"lifecycle_changed expected={expectedLifecycle} actual={actualLifecycle}";
        return false;
    }

    internal static bool CanReuse(
        ContinuationStamp expected,
        ContinuationStamp actual,
        out string reason)
    {
        return CanReuse(
            expected.CombatIdentity,
            actual.CombatIdentity,
            out reason)
            && CanReuseStateText(expected.StateText, actual.StateText, out reason);
    }

    private static bool CanReuseStateText(
        string expectedState,
        string actualState,
        out string reason)
    {
        if (string.Equals(expectedState, actualState, StringComparison.Ordinal))
        {
            reason = "exact_state_match";
            return true;
        }

        reason = "state_lifecycle_changed";
        return false;
    }

    internal static bool ShouldRejectContinuation(
        string expectedLifecycle,
        string actualLifecycle)
    {
        return !CanReuse(expectedLifecycle, actualLifecycle, out _);
    }
}
