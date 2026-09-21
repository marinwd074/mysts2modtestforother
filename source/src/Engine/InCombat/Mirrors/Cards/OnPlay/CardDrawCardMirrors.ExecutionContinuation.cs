using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static partial class CardDrawCardMirrors
{
    private static bool ContinueCardDrawSequence(
        CardOnPlayMirrorContext context,
        CardDrawSequence sequence,
        int stage = 0,
        IReadOnlyList<PredictedCard>? drawnCards = null,
        IReadOnlyList<Player>? players = null,
        int nextPlayer = 0)
    {
        switch (sequence)
        {
            case CardDrawSequence.Adrenaline:
            {
                var card = (Adrenaline)context.Card.MutablePreview;
                if (stage == 0)
                {
                    context.Simulator.GainEnergy(card.Owner, card.DynamicVars.Energy.IntValue);
                    if (QueueCardDrawContinuation(context, sequence, 1))
                        return false;
                }
                context.Simulator.Draw(card.Owner, card.DynamicVars.Cards.BaseValue);
                return !context.Simulator.HasPendingChoice;
            }
            case CardDrawSequence.Offering:
            {
                var card = (Offering)context.Card.MutablePreview;
                if (stage == 0)
                {
                    context.Simulator.Damage(
                        [card.Owner.Creature],
                        card.DynamicVars.HpLoss.BaseValue,
                        ValueProp.Unblockable | ValueProp.Unpowered | ValueProp.Move,
                        card.Owner.Creature,
                        context.Card,
                        context.CardPlay);
                    if (QueueCardDrawContinuation(context, sequence, 1))
                        return false;
                    stage = 1;
                }
                if (stage == 1)
                {
                    context.Simulator.GainEnergy(card.Owner, card.DynamicVars.Energy.IntValue);
                    if (QueueCardDrawContinuation(context, sequence, 2))
                        return false;
                }
                context.Simulator.Draw(card.Owner, card.DynamicVars.Cards.BaseValue);
                return !context.Simulator.HasPendingChoice;
            }
            case CardDrawSequence.Neurosurge:
            {
                var card = (Neurosurge)context.Card.MutablePreview;
                if (stage == 0)
                {
                    context.Simulator.GainEnergy(card.Owner, card.DynamicVars.Energy.IntValue);
                    if (QueueCardDrawContinuation(context, sequence, 1))
                        return false;
                    stage = 1;
                }
                if (stage == 1)
                {
                    context.Simulator.Draw(card.Owner, card.DynamicVars.Cards.BaseValue);
                    if (QueueCardDrawContinuation(context, sequence, 2))
                        return false;
                }
                SimulatedCombatState combat = context.State.CombatState as SimulatedCombatState
                    ?? throw new InvalidOperationException("Neurosurge continuation requires simulated combat state.");
                combat.Apply<NeurosurgePower>(
                    card.Owner.Creature,
                    card.DynamicVars["NeurosurgePower"].IntValue,
                    card.Owner.Creature);
                return !context.Simulator.HasPendingChoice;
            }
            case CardDrawSequence.SpoilsOfBattle:
            {
                var card = (SpoilsOfBattle)context.Card.MutablePreview;
                if (stage == 0)
                {
                    PersistentPowerSupport.Forge(context.Simulator, card.Owner, card.DynamicVars.Forge.IntValue);
                    if (QueueCardDrawContinuation(context, sequence, 1))
                        return false;
                }
                context.Simulator.Draw(card.Owner, card.DynamicVars.Cards.BaseValue);
                return !context.Simulator.HasPendingChoice;
            }
            case CardDrawSequence.CompileDriver:
            {
                var card = (CompileDriver)context.Card.MutablePreview;
                if (stage == 0)
                {
                    context.AttackSingle();
                    if (QueueCardDrawContinuation(context, sequence, 1))
                        return false;
                }
                int drawCount = context.OwnerState.OrbQueue.Orbs.Select(orb => orb.Id).Distinct().Count();
                context.Simulator.Draw(card.Owner, drawCount);
                return !context.Simulator.HasPendingChoice;
            }
            case CardDrawSequence.EscapePlan:
            {
                var card = (EscapePlan)context.Card.MutablePreview;
                if (stage == 0)
                {
                    drawnCards = context.Simulator.Draw(card.Owner, 1);
                    if (QueueCardDrawContinuation(context, sequence, 1, drawnCards))
                        return false;
                }
                if (drawnCards is [{ Preview.Type: CardType.Skill }])
                    context.GainBlock(card.Owner.Creature);
                return !context.Simulator.HasPendingChoice;
            }
            case CardDrawSequence.Fetch:
            {
                var card = (Fetch)context.Card.MutablePreview;
                if (stage == 0)
                {
                    if (context.State.GetOsty(card.Owner) is not { } osty
                        || context.State.GetCreature(osty).IsDead)
                        return true;
                    DamageCmd.Attack(card.DynamicVars.OstyDamage.BaseValue)
                        .FromOsty(osty, card, context.CardPlay)
                        .Targeting(context.Target)
                        .Simulate(context.Simulator);
                    if (QueueCardDrawContinuation(context, sequence, 1))
                        return false;
                }
                SimulatedCombatState combat = context.State.CombatState as SimulatedCombatState
                    ?? throw new InvalidOperationException("Fetch continuation requires simulated combat state.");
                if (!combat.WasFetchPlayedThisTurn(context.Card))
                    context.Simulator.Draw(card.Owner, card.DynamicVars.Cards.BaseValue);
                return !context.Simulator.HasPendingChoice;
            }
            case CardDrawSequence.Ftl:
            {
                var card = (Ftl)context.Card.MutablePreview;
                if (stage == 0)
                {
                    context.AttackSingle();
                    if (QueueCardDrawContinuation(context, sequence, 1))
                        return false;
                }
                SimulatedCombatState combat = context.State.CombatState as SimulatedCombatState
                    ?? throw new InvalidOperationException("FTL continuation requires simulated combat state.");
                if (combat.GetCardsPlayedThisTurn(card.Owner.Creature) < card.DynamicVars[Ftl._playMaxKey].IntValue)
                    context.Simulator.Draw(card.Owner, card.DynamicVars.Cards.BaseValue);
                return !context.Simulator.HasPendingChoice;
            }
            case CardDrawSequence.HuddleUp:
            {
                var card = (HuddleUp)context.Card.MutablePreview;
                players ??= [];
                for (int index = nextPlayer; index < players.Count; index++)
                {
                    context.Simulator.Draw(players[index], card.DynamicVars.Cards.BaseValue);
                    if (QueueCardDrawContinuation(
                            context,
                            sequence,
                            stage,
                            players: players,
                            nextPlayer: index + 1))
                        return false;
                }
                return true;
            }
            case CardDrawSequence.Pillage:
            {
                var card = (Pillage)context.Card.MutablePreview;
                if (stage == 0)
                {
                    context.AttackSingle();
                    if (QueueCardDrawContinuation(context, sequence, 1))
                        return false;
                    stage = 1;
                }

                while (true)
                {
                    if (stage == 1)
                    {
                        drawnCards = context.Simulator.Draw(card.Owner, 1);
                        if (QueueCardDrawContinuation(context, sequence, 2, drawnCards))
                            return false;
                        stage = 2;
                    }

                    if (drawnCards is not [{ Preview.Type: CardType.Attack }]
                        || context.OwnerState.Hand.Cards.Count >= context.Simulator.GetMaxHandSize(card.Owner))
                        return true;

                    drawnCards = null;
                    stage = 1;
                }
            }
            case CardDrawSequence.Reboot:
            {
                var card = (Reboot)context.Card.MutablePreview;
                if (stage == 0)
                {
                    context.Simulator.MoveHandToDrawPile(card.Owner);
                    if (QueueCardDrawContinuation(context, sequence, 1))
                        return false;
                    stage = 1;
                }
                if (stage == 1)
                {
                    context.Simulator.Shuffle(card.Owner);
                    if (QueueCardDrawContinuation(context, sequence, 2))
                        return false;
                }
                context.Simulator.Draw(card.Owner, card.DynamicVars.Cards.BaseValue);
                return !context.Simulator.HasPendingChoice;
            }
            case CardDrawSequence.Restlessness:
            {
                var card = (Restlessness)context.Card.MutablePreview;
                if (stage == 0)
                {
                    if (!context.OwnerState.Hand.IsEmpty)
                        return true;
                    context.Simulator.Draw(card.Owner, card.DynamicVars.Cards.IntValue);
                    if (QueueCardDrawContinuation(context, sequence, 1))
                        return false;
                }
                context.Simulator.GainEnergy(card.Owner, card.DynamicVars.Energy.IntValue);
                return !context.Simulator.HasPendingChoice;
            }
            case CardDrawSequence.Scrape:
            {
                var card = (Scrape)context.Card.MutablePreview;
                if (stage == 0)
                {
                    context.AttackSingle();
                    if (QueueCardDrawContinuation(context, sequence, 1))
                        return false;
                    stage = 1;
                }
                if (stage == 1)
                {
                    drawnCards = context.Simulator.Draw(card.Owner, card.DynamicVars.Cards.IntValue);
                    if (QueueCardDrawContinuation(context, sequence, 2, drawnCards))
                        return false;
                }

                var cardsToDiscard = (drawnCards ?? [])
                    .Where(drawnCard =>
                        drawnCard.Preview.EnergyCost.GetWithModifiers(CostModifiers.Local) != 0
                        || drawnCard.Preview.EnergyCost.CostsX)
                    .ToList();
                context.Simulator.Discard(cardsToDiscard);
                return !context.Simulator.HasPendingChoice;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(sequence), sequence, null);
        }
    }

    private static bool QueueCardDrawContinuation(
        CardOnPlayMirrorContext context,
        CardDrawSequence sequence,
        int nextStage,
        IReadOnlyList<PredictedCard>? drawnCards = null,
        IReadOnlyList<Player>? players = null,
        int nextPlayer = 0)
    {
        if (!context.Simulator.HasPendingChoice)
            return false;

        context.Simulator.AppendExecutionContinuation(
            new CardDrawExecutionFrame(
                context.Card,
                context.CardPlay,
                sequence,
                nextStage,
                drawnCards,
                players,
                nextPlayer));
        return true;
    }

    private enum CardDrawSequence
    {
        Adrenaline,
        Offering,
        Neurosurge,
        SpoilsOfBattle,
        CompileDriver,
        EscapePlan,
        Fetch,
        Ftl,
        HuddleUp,
        Pillage,
        Reboot,
        Restlessness,
        Scrape,
    }

    private sealed record CardDrawExecutionFrame(
        PredictedCard Card,
        CardPlay Play,
        CardDrawSequence Sequence,
        int Stage,
        IReadOnlyList<PredictedCard>? DrawnCards,
        IReadOnlyList<Player>? Players,
        int NextPlayer) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
        {
            CombatPredictionSimulator.PrepareExecutionCardPlay(Card, Play, context);
            if (DrawnCards is List<PredictedCard> list)
                _ = CombatPredictionSimulator.ForkExecutionCardList(list, context);
        }

        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with
            {
                Card = context.RequireRemap(Card),
                Play = context.RequireRemap(Play),
                DrawnCards = DrawnCards is null ? null : context.RequireRemap(DrawnCards),
            };

        public bool Resume(CombatPredictionSimulator simulator)
            => ContinueCardDrawSequence(
                new CardOnPlayMirrorContext
                {
                    Simulator = simulator,
                    Card = Card,
                    CardPlay = Play
                },
                Sequence,
                Stage,
                DrawnCards,
                Players,
                NextPlayer);
    }
}
