namespace CombatSolver;

internal static partial class CompatibilitySmoke
{
    private static bool HasChoiceTrace(string owner, int turn, string stage)
        => NativeChoiceRuntime.TraceSnapshotForTesting.Any(trace =>
            trace.Owner == owner && trace.Turn == turn && trace.Stage == stage);
}
