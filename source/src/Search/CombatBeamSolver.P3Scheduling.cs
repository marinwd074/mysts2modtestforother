using System.Diagnostics;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private long EffectiveSearchElapsedMilliseconds(Stopwatch stopwatch)
        => policy.P3SharedWallClockBudget?.ElapsedMilliseconds
            ?? stopwatch.ElapsedMilliseconds;

    private bool TryConsumeExpandedNodeBudget()
        => policy.P3SharedWallClockBudget?.TryConsumeExpandedNode() ?? true;

    private bool HasExpandedNodeBudgetRemaining()
        => policy.P3SharedWallClockBudget?.HasExpandedNodeBudgetRemaining
            ?? _run.Expanded < _profile.MaxExpandedNodes;
}
