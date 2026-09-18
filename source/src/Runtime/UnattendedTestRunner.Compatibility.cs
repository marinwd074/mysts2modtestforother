using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

// The unattended harness targets newer game builds and is intentionally inert in
// the 0.107.1 production build. Runtime hooks use this surface so the battle
// solver remains independent from test-only implementation files.
internal static class UnattendedTestRunner
{
    public static bool IsActive =>
#if COMPATIBILITY_SMOKE
        System.Environment.GetEnvironmentVariable("COMBATSOLVER_COMPAT_SMOKE") is not null;
#else
        false;
#endif
    internal static bool IsReplayingRecordedInputs => false;
    public static bool AutomaticTurnSearchEnabled => !IsActive;
    public static bool VerifyIncrementalSearch => false;
    public static bool ForceShortSearchOnly => false;
    public static bool MeasureSearchPhases => false;
    public static int? ShortSearchBudgetOverrideMilliseconds => null;
    public static int? DeepSearchBudgetOverrideMilliseconds => null;
    public static bool UseNoveltyPortfolioOverride => false;
    public static bool UseBeamWidthPortfolioOverride => false;
    public static IReadOnlyList<int>? BeamWidthPortfolioWidthsOverride => null;
    public static bool? Act3BossStrategyOverride => null;
    public static int? SearchBudgetOverrideMilliseconds => null;
    public static int? SearchMaxDegreeOfParallelismOverride => null;
    public static bool FixedSearchBudget => false;

    public static void TryStart(NGame? host)
    {
#if COMPATIBILITY_SMOKE
        if (IsActive && host is not null)
            MegaCrit.Sts2.Core.Helpers.TaskHelper.RunSafely(CompatibilitySmoke.RunAsync(host));
#endif
    }

    public static Task ApplyScheduledStateDriftAsync(CombatState state, int turn)
        => Task.CompletedTask;

    public static Task ApplyScheduledPreEndTurnDriftAsync(CombatState state, int turn)
        => Task.CompletedTask;

    public static Task ApplyReplayStateAsync(
        CombatState combatState,
        MegaCrit.Sts2.Core.Entities.Players.Player player,
        string replayStatePath,
        string? runSnapshotPath,
        string? nativeStatePath = null)
        => Task.FromException(new NotSupportedException(
            "0.107.1 兼容构建不支持 0.111.0 的无人值守 replay-state 导入。"));
}
