namespace CombatSolver;

// The unattended protocol is not part of the 0.107.1 production build. Keep the
// runtime instrumentation calls inert so they do not alter task scheduling.
internal static class UnattendedAsyncActivityTracker
{
    public static bool IsRequestActive => false;
    public static bool IsIdle => true;
    public static Task Track(Task task) => task;
    public static IDisposable? BeginActivity() => null;
    public static Task WaitForIdleAsync() => Task.CompletedTask;
}
