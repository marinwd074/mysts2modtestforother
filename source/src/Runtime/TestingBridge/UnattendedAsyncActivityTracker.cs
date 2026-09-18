namespace CombatSolver;

// The production build has no unattended protocol owner. Keep Runtime's
// tracking calls allocation-free and inert; the full tracker stays test-only.
internal static class UnattendedAsyncActivityTracker
{
    public static bool IsRequestActive => false;
    public static bool IsIdle => true;
    public static Task Track(Task task) => task;
    public static IDisposable? BeginActivity() => null;
    public static Task WaitForIdleAsync() => Task.CompletedTask;
}
