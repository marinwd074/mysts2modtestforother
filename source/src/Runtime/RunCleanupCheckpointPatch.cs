using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

internal sealed class RunCleanupCheckpointPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_run_cleanup_checkpoint";
    public static string Description => "原生跑局释放前采集最后一个可恢复战斗检查点";
    public static ModPatchTarget[] GetTargets()
        => [new(typeof(RunManager), nameof(RunManager.CleanUp), [typeof(bool)])];
    public static void Prefix()
    {
        try { SolverController.CaptureBeforeRunCleanup(); }
        catch (Exception error)
        {
            Entry.Logger.Error($"[CombatSolver/Diagnostics] RUN_CLEANUP_CHECKPOINT_FAILURE error={error}");
        }
    }
}
