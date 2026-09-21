using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static partial class CardSelectionCardMirrors
{
    private static bool ContinueBeatDown(
        CardOnPlayMirrorContext context,
        List<PredictedCard> selectedCards,
        int nextIndex)
    {
        var card = (BeatDown)context.Card.MutablePreview;
        for (int index = nextIndex; index < selectedCards.Count; index++)
        {
            if (context.Simulator.IsOverOrEnding)
                return true;

            PredictedCard selectedCard = selectedCards[index];
            Creature? target = null;
            if (selectedCard.Preview.TargetType == TargetType.AnyEnemy)
            {
                // Native resolves the random target before AutoPlay checks playability.
                target = context.Rng.CombatTargets.NextItem(context.State.HittableEnemies);
            }

            context.Simulator.AutoPlay(
                selectedCard,
                target,
                nestedChoiceSourceId: card.Id.Entry);
            if (QueueSelectionContinuation(
                    context,
                    CardSelectionSequence.BeatDown,
                    index + 1,
                    selectedCards))
                return false;
        }

        return true;
    }

    private static bool ContinueCatastrophe(
        CardOnPlayMirrorContext context,
        int nextIteration)
    {
        var card = (Catastrophe)context.Card.MutablePreview;
        for (int iteration = nextIteration; iteration < card.DynamicVars.Cards.IntValue; iteration++)
        {
            IReadOnlyList<PredictedCard> drawPileCards = context.OwnerState.DrawPile.Cards;
            List<PredictedCard> eligibleCards = drawPileCards
                .Where(predictedCard =>
                    !predictedCard.HasKeyword(context.State, CardKeyword.Unplayable))
                .ToList();
            CombatBeamSolver.StableShuffleProjection(eligibleCards, context.Rng.Shuffle);
            PredictedCard? selectedCard = eligibleCards.FirstOrDefault();

            if (selectedCard is null)
            {
                List<PredictedCard> fallbackCards = drawPileCards.ToList();
                CombatBeamSolver.StableShuffleProjection(fallbackCards, context.Rng.Shuffle);
                selectedCard = fallbackCards.FirstOrDefault();
            }

            if (selectedCard is null)
                continue;

            context.Simulator.History.CardsSelected([selectedCard]);
            context.Simulator.AutoPlay(
                selectedCard,
                nestedChoiceSourceId: card.Id.Entry);
            if (QueueSelectionContinuation(
                    context,
                    CardSelectionSequence.Catastrophe,
                    iteration + 1))
                return false;
        }

        return true;
    }

    private static bool ContinueCardSelectionSequence(
        CardOnPlayMirrorContext context,
        CardSelectionSequence sequence,
        int stage = 0)
    {
        switch (sequence)
        {
            case CardSelectionSequence.Cinder:
            {
                if (stage == 0)
                {
                    context.AttackSingle();
                    if (QueueSelectionContinuation(context, sequence, 1))
                        return false;
                }

                if (SelectRandomHandCard(context, static _ => true) is { } selectedCard)
                {
                    context.Simulator.History.CardsSelected([selectedCard]);
                    context.Simulator.Exhaust(selectedCard);
                }
                return !context.Simulator.HasPendingChoice;
            }
            case CardSelectionSequence.DrainPower:
            {
                var card = (DrainPower)context.Card.MutablePreview;
                if (stage == 0)
                {
                    context.AttackSingle();
                    if (QueueSelectionContinuation(context, sequence, 1))
                        return false;
                }

                var cardsToUpgrade = context.OwnerState.DiscardPile.Cards
                    .Where(predictedCard => predictedCard.Preview.IsUpgradable)
                    .TakeRandom(card.DynamicVars.Cards.IntValue, context.Rng.CombatCardSelection)
                    .ToList();
                if (cardsToUpgrade.Count == 0)
                    return true;

                foreach (PredictedCard cardToUpgrade in cardsToUpgrade)
                    context.Simulator.Upgrade(cardToUpgrade);
                context.Simulator.History.CardsSelected(cardsToUpgrade);
                return true;
            }
            case CardSelectionSequence.Thrash:
            {
                var card = (Thrash)context.Card.MutablePreview;
                if (stage == 0)
                {
                    context.AttackSingle(hitCount: 2);
                    if (QueueSelectionContinuation(context, sequence, 1))
                        return false;
                }

                PredictedCard? cardToExhaust =
                    SelectRandomHandCard(context, model => model.Type == CardType.Attack);
                if (cardToExhaust is null)
                    return true;

                context.Simulator.History.CardsSelected([cardToExhaust]);

                decimal damage = 0m;
                var dynamicVars = cardToExhaust.Preview.DynamicVars;
                if (dynamicVars.ContainsKey("CalculatedDamage"))
                    damage = dynamicVars.CalculatedDamage.InvokeCalculate(
                        context.Simulator,
                        cardToExhaust,
                        null);
                else if (dynamicVars.ContainsKey("Damage"))
                    damage = dynamicVars.Damage.BaseValue;
                else if (dynamicVars.ContainsKey("OstyDamage"))
                    damage = dynamicVars.OstyDamage.BaseValue;
                else
                    EngineDiagnostics.Warn(
                        $"Exhausted attack card {cardToExhaust.Preview.Id.Entry} did not have an appropriate DamageVar");

                damage = HookMirrors.ModifyDamage(
                    context.Simulator,
                    target: null,
                    dealer: cardToExhaust.Preview.Owner.Creature,
                    damage,
                    ValueProp.Move,
                    cardSource: cardToExhaust,
                    cardPlay: null);

                card.DynamicVars.Damage.BaseValue += damage;
                card.ExtraDamage += damage;
                context.Simulator.Exhaust(cardToExhaust);
                return !context.Simulator.HasPendingChoice;
            }
            case CardSelectionSequence.TrueGrit:
            {
                var card = (TrueGrit)context.Card.MutablePreview;
                if (stage == 0)
                {
                    context.GainBlock(card.Owner.Creature);
                    if (QueueSelectionContinuation(context, sequence, 1))
                        return false;
                }

                if (card.IsUpgraded)
                {
                    if (!context.OwnerState.Hand.IsEmpty)
                        context.History.RecordRisk(PredictionRiskReason.UnresolvedPlayerChoice);
                    return true;
                }

                if (SelectRandomHandCard(context, static _ => true) is { } selectedCard)
                {
                    context.Simulator.History.CardsSelected([selectedCard]);
                    context.Simulator.Exhaust(selectedCard);
                }
                return !context.Simulator.HasPendingChoice;
            }
            case CardSelectionSequence.Uproar:
            {
                var card = (Uproar)context.Card.MutablePreview;
                if (stage == 0)
                {
                    context.AttackSingle(hitCount: 2);
                    if (QueueSelectionContinuation(context, sequence, 1))
                        return false;
                }

                var attackCards = context.OwnerState.DrawPile.Cards
                    .Where(predictedCard => predictedCard.Preview.Type == CardType.Attack)
                    .ToList();

                PredictedCard? selectedCard = attackCards
                    .Where(predictedCard =>
                        !predictedCard.HasKeyword(context.State, CardKeyword.Unplayable))
                    .ToList()
                    .StableShuffle(context.Rng.Shuffle)
                    .FirstOrDefault();

                selectedCard ??= attackCards
                    .StableShuffle(context.Rng.Shuffle)
                    .FirstOrDefault();

                if (selectedCard is null)
                    return true;

                context.Simulator.History.CardsSelected([selectedCard]);
                context.Simulator.AutoPlay(
                    selectedCard,
                    nestedChoiceSourceId: card.Id.Entry);
                return !context.Simulator.HasPendingChoice;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(sequence), sequence, null);
        }
    }

    private static bool QueueSelectionContinuation(
        CardOnPlayMirrorContext context,
        CardSelectionSequence sequence,
        int nextPosition,
        List<PredictedCard>? cards = null)
    {
        if (!context.Simulator.HasPendingChoice)
            return false;

        context.Simulator.AppendExecutionContinuation(
            new CardSelectionExecutionFrame(
                context.Card,
                context.CardPlay,
                sequence,
                nextPosition,
                cards));
        return true;
    }

    private enum CardSelectionSequence
    {
        BeatDown,
        Catastrophe,
        Cinder,
        DrainPower,
        Thrash,
        TrueGrit,
        Uproar,
    }

    private sealed record CardSelectionExecutionFrame(
        PredictedCard Card,
        CardPlay Play,
        CardSelectionSequence Sequence,
        int Position,
        List<PredictedCard>? Cards) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
        {
            CombatPredictionSimulator.PrepareExecutionCardPlay(Card, Play, context);
            if (Cards is not null)
                _ = CombatPredictionSimulator.ForkExecutionCardList(Cards, context);
        }

        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with
            {
                Card = context.RequireRemap(Card),
                Play = context.RequireRemap(Play),
                Cards = Cards is null ? null : context.RequireRemap(Cards),
            };

        public bool Resume(CombatPredictionSimulator simulator)
        {
            var context = new CardOnPlayMirrorContext
            {
                Simulator = simulator,
                Card = Card,
                CardPlay = Play
            };

            return Sequence switch
            {
                CardSelectionSequence.BeatDown => ContinueBeatDown(
                    context,
                    Cards ?? throw new InvalidOperationException("Beat Down continuation lost its selected-card sequence."),
                    Position),
                CardSelectionSequence.Catastrophe => ContinueCatastrophe(context, Position),
                _ => ContinueCardSelectionSequence(context, Sequence, Position),
            };
        }
    }
}
