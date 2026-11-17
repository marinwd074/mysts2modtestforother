using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal static class Sts2CombatStateCompatibility
{
    internal static MultiplayerScalingModel? GetMultiplayerScalingModel(this CombatState state)
        => state.RunModels.OfType<MultiplayerScalingModel>().FirstOrDefault();
}
