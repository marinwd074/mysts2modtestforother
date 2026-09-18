using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace CombatSolver;

// Native non-card hook parameter shapes stay at the compatibility boundary.
// The mirror registry consumes one static array and does not branch per node.
internal static class Sts2HookCompatibility
{
#if STS2_01071
    internal static readonly Type[] AfterBlockBrokenParameterTypes =
    [typeof(Creature)];
#else
    internal static readonly Type[] AfterBlockBrokenParameterTypes =
    [typeof(PlayerChoiceContext), typeof(Creature), typeof(Creature)];
#endif
}
