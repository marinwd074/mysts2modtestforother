using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static partial class CardSelectionCardMirrors
{
    public static void AnointedOnPlay(Anointed card, CardOnPlayMirrorContext context)
    {
        int count = context.Simulator.GetMaxHandSize(card.Owner) - context.OwnerState.Hand.Cards.Count;
        if (count <= 0)
        {
            return;
        }

        var cardsToAdd = context.OwnerState.DrawPile.Cards
            .Where(predictedCard => predictedCard.Preview.Rarity is CardRarity.Rare)
            .TakeRandom(count, context.Rng.CombatCardSelection)
            .ToList();
        if (cardsToAdd.Count == 0)
        {
            return;
        }

        context.Simulator.History.CardsSelected(cardsToAdd);
        context.Simulator.AddToPile(cardsToAdd, PileType.Hand);
    }

    public static void BeatDownOnPlay(BeatDown card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        var selectedCards = context.OwnerState.DiscardPile.Cards
            .Where(predictedCard =>
                predictedCard.Preview.Type == CardType.Attack &&
                !predictedCard.HasKeyword(context.State, CardKeyword.Unplayable))
            .ToList()
            .StableShuffle(context.Rng.Shuffle)
            .Take(card.DynamicVars.Cards.IntValue)
            .ToList();
        if (selectedCards.Count == 0)
            return;

        context.Simulator.History.CardsSelected(selectedCards);
        ContinueBeatDown(context, selectedCards, nextIndex: 0);
    }

    public static void CatastropheOnPlay(Catastrophe _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        ContinueCatastrophe(context, nextIteration: 0);
    }

    public static void CinderOnPlay(Cinder _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        ContinueCardSelectionSequence(context, CardSelectionSequence.Cinder);
    }

    public static void DrainPowerOnPlay(DrainPower _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        ContinueCardSelectionSequence(context, CardSelectionSequence.DrainPower);
    }

    public static void HiddenGemOnPlay(HiddenGem card, CardOnPlayMirrorContext context)
    {
        var drawPile = context.OwnerState.DrawPile;
        if (drawPile.IsEmpty)
        {
            return;
        }

        var eligibleCards = drawPile.Cards
            .Where(predictedCard =>
                !predictedCard.HasKeyword(context.State, CardKeyword.Unplayable) &&
                predictedCard.Preview.Type is not CardType.Status and not CardType.Curse &&
                predictedCard.Preview.GetEnchantedReplayCount() < 1)
            .ToList();
        var preferredCards = eligibleCards
            .Where(predictedCard =>
                predictedCard.Preview.Type is CardType.Attack or CardType.Skill or CardType.Power)
            .ToList();

        var selectedCard = context.Rng.CombatCardSelection.NextItem(
            preferredCards.Count == 0 ? eligibleCards : preferredCards);
        if (selectedCard is null)
        {
            return;
        }

        selectedCard.MutablePreview.BaseReplayCount += card.DynamicVars["Replay"].IntValue;
        context.Simulator.History.CardsSelected([selectedCard]);
    }

    public static void SeekerStrikeOnPlay(SeekerStrike card, CardOnPlayMirrorContext context)
    {
        context.AttackSingle();
    }

    public static void ThrashOnPlay(Thrash _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        ContinueCardSelectionSequence(context, CardSelectionSequence.Thrash);
    }

    public static void TrueGritOnPlay(TrueGrit _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        ContinueCardSelectionSequence(context, CardSelectionSequence.TrueGrit);
    }

    public static void UproarOnPlay(Uproar _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        ContinueCardSelectionSequence(context, CardSelectionSequence.Uproar);
    }


    private static PredictedCard? SelectRandomHandCard(
        CardOnPlayMirrorContext context,
        Func<CardModel, bool> filter)
    {
        var candidates = context.OwnerState.Hand.Cards.Where(card => filter(card.Preview));
        return context.Rng.CombatCardSelection.NextItem(candidates);
    }
}
