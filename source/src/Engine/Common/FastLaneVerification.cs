namespace CombatSolver.Engine.Common;

internal static class FastLaneVerification
{
    internal static readonly bool Enabled =
        Environment.GetEnvironmentVariable("COMBATSOLVER_VERIFY_FAST_LANES") == "1"
        || Environment.GetEnvironmentVariable("COMBATSOLVER_VERIFY_HOOK_MASK") == "1";
}
