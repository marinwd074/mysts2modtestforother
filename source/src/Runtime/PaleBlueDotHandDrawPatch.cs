using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Powers;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

internal sealed class PaleBlueDotHandDrawPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_pale_blue_dot_predicted_hand_draw";
    public static string Description => "模拟抽牌时避免苍蓝星球重复按实战历史加牌";

    public static ModPatchTarget[] GetTargets()
        => [new(typeof(PaleBlueDotPower), nameof(PaleBlueDotPower.ModifyHandDraw),
            [typeof(Player), typeof(decimal)])];

    public static bool Prefix(decimal __1, ref decimal __result)
    {
        if (!PaleBlueDotHandDrawScope.Active)
            return true;
        __result = __1;
        return false;
    }
}
