using System.Diagnostics;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private long EffectiveSearchElapsedMilliseconds(Stopwatch stopwatch)
        => policy.P3SharedWallClockBudget?.ElapsedMilliseconds
            ?? stopwatch.ElapsedMilliseconds;
}
