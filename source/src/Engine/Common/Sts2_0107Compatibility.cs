global using CombatSolver.Compatibility;

using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Compatibility;

// Card-play context parameters were added to the attack builder after 0.107.1.
// Keep newer call sites source-compatible while delegating to the old builder.
internal static class AttackCommandCompatibilityExtensions
{
    public static AttackCommand FromCard(
        this AttackCommand command,
        CardModel card,
        CardPlay ignoredCardPlay)
        => command.FromCard(card);

    public static AttackCommand FromOsty(
        this AttackCommand command,
        Creature osty,
        CardModel card,
        CardPlay ignoredCardPlay)
        => command.FromOsty(osty, card);
}
