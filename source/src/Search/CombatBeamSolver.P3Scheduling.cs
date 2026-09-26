using System.Diagnostics;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private long EffectiveSearchElapsedMilliseconds(Stopwatch stopwatch)
        => policy.P3SharedWallClockBudget?.ElapsedMilliseconds
            ?? stopwatch.ElapsedMilliseconds;

    private bool TryConsumeExpandedNodeBudget()
        => policy.P3SharedWallClockBudget?.TryConsumeExpandedNode() ?? true;

    private int EffectiveRemainingExpandedNodes()
    {
        int localRemaining = Math.Max(0, _profile.MaxExpandedNodes - _run.Expanded);
        return policy.P3SharedWallClockBudget is { } shared
            ? Math.Min(localRemaining, shared.RemainingExpandedNodes)
            : localRemaining;
    }

    private bool HasExpandedNodeBudgetRemaining()
        => EffectiveRemainingExpandedNodes() > 0;
}
