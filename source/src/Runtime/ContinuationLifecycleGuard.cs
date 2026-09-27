namespace CombatSolver;

/// <summary>
/// Prevents cached cross-turn continuation from surviving combat lifecycle changes
/// such as boss phase transitions. Integration point: SolverController search lifecycle.
/// </summary>
internal static class ContinuationLifecycleGuard
{
    internal static bool ShouldRejectContinuation(
        string expectedLifecycle,
        string actualLifecycle)
    {
        return !string.Equals(
            expectedLifecycle,
            actualLifecycle,
            StringComparison.Ordinal);
    }
}
