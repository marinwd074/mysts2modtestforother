using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.InCombat.Mirrors;

namespace CombatSolver;

// Native card-result hook names and signatures changed across the supported
// target boundary. Keep that shape knowledge at the compatibility edge; the
// mirror registry and prediction code use the stable local CardLocation form.
internal static class Sts2CardHookCompatibility
{
#if STS2_01071
    internal const string ModifyCardPlayResultLocationMethodName =
        nameof(AbstractModel.ModifyCardPlayResultPileTypeAndPosition);

    internal static readonly Type[] ModifyCardPlayResultLocationParameterTypes =
    [
        typeof(CardModel),
        typeof(bool),
        typeof(ResourceInfo),
        typeof(PileType),
        typeof(CardPilePosition)
    ];

    internal const string AfterModifyingCardPlayResultLocationMethodName =
        nameof(AbstractModel.AfterModifyingCardPlayResultPileOrPosition);

    internal static readonly Type[] AfterModifyingCardPlayResultLocationParameterTypes =
    [typeof(CardModel), typeof(PileType), typeof(CardPilePosition)];
#else
    internal const string ModifyCardPlayResultLocationMethodName =
        nameof(AbstractModel.ModifyCardPlayResultLocation);

    internal static readonly Type[] ModifyCardPlayResultLocationParameterTypes =
    [typeof(CardModel), typeof(bool), typeof(ResourceInfo), typeof(CardLocation)];

    internal const string AfterModifyingCardPlayResultLocationMethodName =
        nameof(AbstractModel.AfterModifyingCardPlayResultLocation);

    internal static readonly Type[] AfterModifyingCardPlayResultLocationParameterTypes =
    [typeof(CardModel), typeof(CardLocation)];
#endif

    internal static CardLocation InvokeModifyCardPlayResultLocation(
        AbstractModel listener,
        CardModel card,
        bool isAutoPlay,
        ResourceInfo resources,
        CardLocation originalLocation)
    {
#if STS2_01071
        var (pileType, position) = listener.ModifyCardPlayResultPileTypeAndPosition(
            card,
            isAutoPlay,
            resources,
            originalLocation.pileType,
            originalLocation.position);
        originalLocation.pileType = pileType;
        originalLocation.position = position;
        return originalLocation;
#else
        return listener.ModifyCardPlayResultLocation(
            card,
            isAutoPlay,
            resources,
            originalLocation);
#endif
    }
}
