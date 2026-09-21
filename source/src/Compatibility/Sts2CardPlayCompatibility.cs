namespace CombatSolver;

internal static class Sts2CardPlayCompatibility
{
    // v0.108 changed Replay so later plays are skipped when the first attack ends combat.
    // v0.107.1 intentionally keeps executing the already-generated repeated plays.
    internal static bool ShouldStopRepeatedPlayWhenCombatEnding(bool isOverOrEnding)
    {
#if STS2_01071
        return false;
#else
        return isOverOrEnding;
#endif
    }
}
