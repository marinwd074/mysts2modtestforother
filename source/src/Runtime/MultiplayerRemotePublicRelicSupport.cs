using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

/// <summary>
/// The narrow allow-list for remote public relics in a local-player-only Advisor root.
/// This is intentionally type-exact: an unknown relic, including a derived type, stays
/// fail closed until its native hook semantics receive a separate audit.
/// </summary>
internal static class MultiplayerRemotePublicRelicSupport
{
    /// <summary>
    /// BurningBlood in STS2 0.107.1 overrides only AfterCombatVictory. It has no
    /// current-turn combat hook, so the remote instance cannot affect this root's search.
    /// </summary>
    internal static bool IsKnownCurrentTurnIrrelevant(Type relicType)
        => relicType == typeof(BurningBlood);

    internal static bool IsKnownCurrentTurnIrrelevant(RelicModel relic)
        => IsKnownCurrentTurnIrrelevant(relic.GetType());
}
