using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal static class CardDynamicVarWarmup
{
    public static void EnsureMaterialized(
        CombatState state,
        IReadOnlyList<Player>? capturedPlayers = null)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("Card dynamic variables must be materialized on the main thread.");

        IEnumerable<Player> players = capturedPlayers ?? state.Players;
        foreach (CardModel card in players
                     .Where(player => player.PlayerCombatState != null)
                     .SelectMany(player => player.PlayerCombatState!.AllCards))
        {
            _ = card.DynamicVars;
        }
    }
}
