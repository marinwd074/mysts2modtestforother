using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static partial class CardOnPlaySupport
{
    private static void ApplyBatch043(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PredictedCard playedCard,
        Creature? target,
        ISet<uint> processedEnemyDeaths)
    {
        CardModel card = playedCard.Preview;
        switch (card)
        {
            case Afterlife or Bodyguard or Reanimate:
                combat.SummonOsty(simulator, card.Owner, card.DynamicVars.Summon.IntValue);
                break;
            case Cleanse:
                combat.SummonOsty(simulator, card.Owner, card.DynamicVars.Summon.IntValue);
                break;
            case Dirge:
                ApplyDirge(simulator, combat, card);
                break;
            case Eidolon:
                ApplyEidolon(simulator, combat, playedCard);
                break;
            case KnifeTrap when target != null:
                ApplyKnifeTrap(simulator, combat, card, target, processedEnemyDeaths);
                break;
            case Monologue:
            {
                MonologuePower power = combat.AddPowerInstance<MonologuePower>(
                    card.Owner.Creature,
                    1,
                    card.Owner.Creature);
                power.DynamicVars.Strength.BaseValue = card.DynamicVars["Power"].BaseValue;
                break;
            }
            case NecroMastery:
                combat.SummonOsty(simulator, card.Owner, card.DynamicVars.Summon.IntValue);
                combat.Apply<NecroMasteryPower>(card.Owner.Creature, 1, card.Owner.Creature);
                break;
            case Spur:
                combat.SummonOsty(simulator, card.Owner, card.DynamicVars.Summon.IntValue);
                combat.HealOsty(simulator, card.Owner, card.DynamicVars.Heal.IntValue);
                break;
            case TheBomb:
            {
                TheBombPower power = combat.AddPowerInstance<TheBombPower>(
                    card.Owner.Creature,
                    card.DynamicVars["Turns"].IntValue,
                    card.Owner.Creature);
                power.SetDamage(card.DynamicVars["BombDamage"].BaseValue);
                break;
            }
            case VoidForm:
                TurnStartPowerSupport.PrepareVoidFormApplication(
                    simulator,
                    combat,
                    card.Owner.Creature);
                combat.Apply<VoidFormPower>(
                    card.Owner.Creature,
                    card.DynamicVars["VoidFormPower"].IntValue,
                    card.Owner.Creature);
                combat.RequestPlayerTurnEnd();
                break;
        }
    }

    private static void ApplyDirge(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        CardModel card)
    {
        int xValue = Hook.ModifyXValue(
            combat,
            card,
            card.EnergyCost.CapturedXValue);
        for (int index = 0; index < xValue; index++)
            combat.SummonOsty(simulator, card.Owner, card.DynamicVars.Summon.IntValue);

        List<PredictedCard> souls = new(xValue);
        for (int index = 0; index < xValue; index++)
        {
            PredictedCard soul = PredictedCard.Create(CanonicalModels.Card<Soul>(), card.Owner);
            if (card.IsUpgraded)
                soul.Upgrade();
            souls.Add(soul);
        }
        simulator.AddGeneratedCardsToCombat(
            souls,
            PileType.Draw,
            card.Owner,
            CardPilePosition.Random,
            CardGenerationResultKind.Fixed);
    }

    private static void ApplyEidolon(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PredictedCard playedCard)
    {
        // This compensation runs inside the card-effect dispatch. Exhaust hooks can open
        // a choice, so keep the adapted suffix eligible for execution continuation.
        simulator.AcknowledgeExecutionDispatch();

        PredictedCard[] cards = simulator.State.GetPlayerCombatState(playedCard.Preview.Owner)
            .Hand.Cards
            .ToArray();
        _ = ContinueEidolon(simulator, combat, playedCard, cards, 0, 0);
    }

    private static bool ContinueEidolon(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PredictedCard playedCard,
        IReadOnlyList<PredictedCard> cards,
        int nextIndex,
        int exhaustedCount)
    {
        for (int index = nextIndex; index < cards.Count; index++)
        {
            simulator.Exhaust(cards[index]);
            exhaustedCount++;
            if (simulator.HasPendingChoice)
            {
                simulator.AppendExecutionContinuation(
                    new EidolonExecutionFrame(playedCard, cards, index + 1, exhaustedCount));
                return false;
            }
        }

        if (exhaustedCount >= 9)
        {
            Creature owner = playedCard.Preview.Owner.Creature;
            combat.Apply<IntangiblePower>(owner, 1, owner);
        }
        return !simulator.HasPendingChoice;
    }

    private sealed record EidolonExecutionFrame(
        PredictedCard PlayedCard,
        IReadOnlyList<PredictedCard> Cards,
        int NextIndex,
        int ExhaustedCount) : ICombatPredictionExecutionFrame
    {
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with
            {
                PlayedCard = context.RequireRemap(PlayedCard),
                Cards = Cards.Select(card => context.RequireRemap(card)).ToArray(),
            };

        public bool Resume(CombatPredictionSimulator simulator)
            => ContinueEidolon(
                simulator,
                (SimulatedCombatState)simulator.State.CombatState,
                PlayedCard,
                Cards,
                NextIndex,
                ExhaustedCount);
    }

    private static void ApplyKnifeTrap(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        CardModel card,
        Creature target,
        ISet<uint> processedEnemyDeaths)
    {
        PredictedCard[] shivs = simulator.State.GetPlayerCombatState(card.Owner)
            .ExhaustPile.Cards
            .Where(candidate => candidate.Preview.Tags.Contains(CardTag.Shiv))
            .ToArray();
        foreach (PredictedCard shiv in shivs)
        {
            if (card.IsUpgraded && shiv.Preview.IsUpgradable)
                shiv.Upgrade();
            if (!CardExecutionSupport.AutoPlay(
                    simulator,
                    combat,
                    shiv,
                    target,
                    processedEnemyDeaths,
                    nestedChoiceSourceId: card.Id.Entry))
            {
                break;
            }
        }
    }
}
