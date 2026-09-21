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

    // v0.108 fixed Swift + Hellraiser + Shining Strike recursion by preventing
    // Swift from remaining active across the draw it starts. v0.107.1 preserves
    // the old await order: Draw first, then disable Swift.
    internal static bool ShouldDisableSwiftBeforeDraw()
    {
#if STS2_01071
        return false;
#else
        return true;
#endif
    }
}
