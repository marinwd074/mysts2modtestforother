using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static partial class CardGenerationCardMirrors
{
    private static bool ContinueGenerationCardSequence(
        CardOnPlayMirrorContext context,
        GenerationCardSequence sequence,
        int stage = 0)
    {
        switch (sequence)
        {
            case GenerationCardSequence.Jackpot:
            {
                var card = (Jackpot)context.Card.MutablePreview;
                if (stage == 0)
                {
                    context.AttackSingle();
                    if (QueueGenerationCardContinuation(context, sequence, 1))
                        return false;
                }

                var cards = card.Owner.GetUnlockedCharacterCards(context.CardMultiplayerConstraint)
                    .Where(candidate => candidate.EnergyCost is { Canonical: 0, CostsX: false })
                    .GetForCombat(
                        card.Owner,
                        card.DynamicVars.Cards.IntValue,
                        context.Rng.CombatCardGeneration,
                        context.CardMultiplayerConstraint)
                    .UpgradeIf(card.IsUpgraded)
                    .ToList();

                context.Simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, card.Owner);
                return !context.Simulator.HasPendingChoice;
            }
            case GenerationCardSequence.ManifestAuthority:
            {
                var card = (ManifestAuthority)context.Card.MutablePreview;
                if (stage == 0)
                {
                    context.GainBlock(card.Owner.Creature);
                    if (QueueGenerationCardContinuation(context, sequence, 1))
                        return false;
                }

                var cards = context.Simulator
                    .GetDistinctUnlockedColorlessForCombat(
                        card.Owner,
                        1,
                        context.Rng.CombatCardGeneration,
                        context.CardMultiplayerConstraint)
                    .UpgradeIf(card.IsUpgraded)
                    .ToList();

                context.Simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, card.Owner);
                return !context.Simulator.HasPendingChoice;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(sequence), sequence, null);
        }
    }

    private static bool ContinueMadScienceMain(
        CardOnPlayMirrorContext context,
        int stage)
    {
        var card = (MadScience)context.Card.MutablePreview;
        if (context.CombatState is not ICombatPredictionEffectSink effects)
            throw new InvalidOperationException("疯狂科学效果缺少可写的预测状态。");

        if (stage == 0)
        {
            switch (card.TinkerTimeType)
            {
                case CardType.Skill:
                    context.GainBlock(card.Owner.Creature);
                    if (QueueMadScienceMainContinuation(context, nextStage: 1))
                        return false;
                    break;

                case CardType.Power:
                    switch (card.TinkerTimeRider)
                    {
                        case TinkerTime.RiderEffect.Expertise:
                            effects.ApplyPower(
                                typeof(StrengthPower),
                                card.Owner.Creature,
                                card.DynamicVars["ExpertiseStrength"].IntValue,
                                card.Owner.Creature);
                            if (QueueMadScienceMainContinuation(context, nextStage: 2))
                                return false;
                            stage = 2;
                            break;

                        case TinkerTime.RiderEffect.Curious:
                            effects.ApplyPower(
                                typeof(CuriousPower),
                                card.Owner.Creature,
                                card.DynamicVars["CuriousReduction"].IntValue,
                                card.Owner.Creature);
                            if (QueueMadScienceMainContinuation(context, nextStage: 1))
                                return false;
                            break;

                        case TinkerTime.RiderEffect.Improvement:
                            effects.ApplyPower(
                                typeof(ImprovementPower),
                                card.Owner.Creature,
                                1,
                                card.Owner.Creature);
                            if (QueueMadScienceMainContinuation(context, nextStage: 1))
                                return false;
                            break;
                    }
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(card.TinkerTimeType),
                        card.TinkerTimeType,
                        null);
            }
        }

        if (stage == 2)
        {
            effects.ApplyPower(
                typeof(DexterityPower),
                card.Owner.Creature,
                card.DynamicVars["ExpertiseDexterity"].IntValue,
                card.Owner.Creature);
            if (QueueMadScienceMainContinuation(context, nextStage: 1))
                return false;
        }

        return ContinueMadScienceRider(context, stage: 0);
    }

    private static bool ContinueMadScienceRider(
        CardOnPlayMirrorContext context,
        int stage)
    {
        var card = (MadScience)context.Card.MutablePreview;
        if (context.CombatState is not ICombatPredictionEffectSink effects)
            throw new InvalidOperationException("疯狂科学效果缺少可写的预测状态。");

        switch (card.TinkerTimeRider)
        {
            case TinkerTime.RiderEffect.Sapping:
                if (stage == 0)
                {
                    effects.ApplyPower(
                        typeof(WeakPower),
                        context.Target,
                        card.DynamicVars["SappingWeak"].IntValue,
                        card.Owner.Creature);
                    if (QueueMadScienceRiderContinuation(context, nextStage: 1))
                        return false;
                }
                effects.ApplyPower(
                    typeof(VulnerablePower),
                    context.Target,
                    card.DynamicVars["SappingVulnerable"].IntValue,
                    card.Owner.Creature);
                return !context.Simulator.HasPendingChoice;

            case TinkerTime.RiderEffect.Choking:
                effects.ApplyPower(
                    typeof(StranglePower),
                    context.Target,
                    card.DynamicVars["ChokingDamage"].IntValue,
                    card.Owner.Creature);
                return !context.Simulator.HasPendingChoice;

            case TinkerTime.RiderEffect.Energized:
                context.Simulator.GainEnergy(
                    card.Owner,
                    card.DynamicVars["EnergizedEnergy"].IntValue);
                return !context.Simulator.HasPendingChoice;

            case TinkerTime.RiderEffect.Wisdom:
                context.Simulator.Draw(
                    card.Owner,
                    card.DynamicVars["WisdomCards"].IntValue);
                return !context.Simulator.HasPendingChoice;

            case TinkerTime.RiderEffect.Chaos:
            {
                var cards = card.Owner.GetUnlockedCharacterCards(context.CardMultiplayerConstraint)
                    .GetDistinctForCombat(
                        card.Owner,
                        1,
                        context.Rng.CombatCardGeneration,
                        context.CardMultiplayerConstraint)
                    .Select(generatedCard => generatedCard.SetToFreeThisTurn())
                    .ToList();
                // 0.107.1 uses ordinary CardPileCmd.Add here, not generated-card hooks.
                context.Simulator.AddToPile(cards, PileType.Hand);
                return !context.Simulator.HasPendingChoice;
            }

            default:
                return true;
        }
    }

    private static bool QueueGenerationCardContinuation(
        CardOnPlayMirrorContext context,
        GenerationCardSequence sequence,
        int nextStage)
    {
        if (!context.Simulator.HasPendingChoice)
            return false;

        context.Simulator.AppendExecutionContinuation(
            new GenerationCardExecutionFrame(
                context.Card,
                context.CardPlay,
                sequence,
                nextStage));
        return true;
    }

    private static bool QueueMadScienceMainContinuation(
        CardOnPlayMirrorContext context,
        int nextStage)
    {
        if (!context.Simulator.HasPendingChoice)
            return false;

        context.Simulator.AppendExecutionContinuation(
            new MadScienceMainExecutionFrame(
                context.Card,
                context.CardPlay,
                nextStage));
        return true;
    }

    private static bool QueueMadScienceRiderContinuation(
        CardOnPlayMirrorContext context,
        int nextStage)
    {
        if (!context.Simulator.HasPendingChoice)
            return false;

        context.Simulator.AppendExecutionContinuation(
            new MadScienceRiderExecutionFrame(
                context.Card,
                context.CardPlay,
                nextStage));
        return true;
    }

    private enum GenerationCardSequence
    {
        Jackpot,
        ManifestAuthority,
    }

    private sealed record GenerationCardExecutionFrame(
        PredictedCard Card,
        CardPlay Play,
        GenerationCardSequence Sequence,
        int Stage) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
            => CombatPredictionSimulator.PrepareExecutionCardPlay(Card, Play, context);

        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with
            {
                Card = context.RequireRemap(Card),
                Play = context.RequireRemap(Play)
            };

        public bool Resume(CombatPredictionSimulator simulator)
            => ContinueGenerationCardSequence(
                new CardOnPlayMirrorContext
                {
                    Simulator = simulator,
                    Card = Card,
                    CardPlay = Play
                },
                Sequence,
                Stage);
    }

    private sealed record MadScienceMainExecutionFrame(
        PredictedCard Card,
        CardPlay Play,
        int Stage) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
            => CombatPredictionSimulator.PrepareExecutionCardPlay(Card, Play, context);

        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with
            {
                Card = context.RequireRemap(Card),
                Play = context.RequireRemap(Play)
            };

        public bool Resume(CombatPredictionSimulator simulator)
            => ContinueMadScienceMain(
                new CardOnPlayMirrorContext
                {
                    Simulator = simulator,
                    Card = Card,
                    CardPlay = Play
                },
                Stage);
    }

    private sealed record MadScienceRiderExecutionFrame(
        PredictedCard Card,
        CardPlay Play,
        int Stage) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
            => CombatPredictionSimulator.PrepareExecutionCardPlay(Card, Play, context);

        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with
            {
                Card = context.RequireRemap(Card),
                Play = context.RequireRemap(Play)
            };

        public bool Resume(CombatPredictionSimulator simulator)
            => ContinueMadScienceRider(
                new CardOnPlayMirrorContext
                {
                    Simulator = simulator,
                    Card = Card,
                    CardPlay = Play
                },
                Stage);
    }
}
