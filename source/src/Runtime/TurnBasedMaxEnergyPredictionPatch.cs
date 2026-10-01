using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

internal sealed class TurnBasedMaxEnergyPredictionPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_turn_based_max_energy_prediction";
    public static string Description => "预测最大能量使用分支回合，真实游戏保留原生查询";
    public static bool IsCritical => true;
    public static ModPatchTarget[] GetTargets()
        => [new(typeof(PaelsFlesh), "ModifyMaxEnergy", [typeof(Player), typeof(decimal)]),
            new(typeof(Bread), "ModifyMaxEnergy", [typeof(Player), typeof(decimal)])];

    public static bool Prefix(RelicModel __instance, Player __0, decimal __1, ref decimal __result)
    {
        if (!PersistentPowerSupport.TryModifyTurnBasedMaxEnergy(__instance, __0, __1, out decimal result))
            return true;
        __result = result;
        return false;
    }
}
