using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Events;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static partial class CardGenerationCardMirrors
{
#if !STS2_01071
    public static void AbundanceOnPlay(Abundance card, CardOnPlayMirrorContext context)
    {
        var cards = card.Owner.GetUnlockedCharacterCards(context.CardMultiplayerConstraint)
            .Where(candidate => candidate.Type == CardType.Power)
            .GetDistinctForCombat(
                card.Owner,
                3,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .Select(candidate => candidate.Upgrade())
            .ToList();

        RecordOptions(context, cards);
    }
#endif

    public static void BundleOfJoyOnPlay(BundleOfJoy card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        var cards = context.Simulator
            .GetDistinctUnlockedColorlessForCombat(
                card.Owner,
                card.DynamicVars.Cards.IntValue,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .ToList();

        context.Simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, card.Owner);
    }

    public static void DistractionOnPlay(Distraction card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        var cards = card.Owner.GetUnlockedCharacterCards(context.CardMultiplayerConstraint)
            .Where(candidate => candidate.Type == CardType.Skill)
            .GetDistinctForCombat(
                card.Owner,
                1,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .Select(generatedCard => generatedCard.SetToFreeThisTurn())
            .ToList();

        context.Simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, card.Owner);
    }

    public static void DiscoveryOnPlay(Discovery card, CardOnPlayMirrorContext context)
    {
        var cards = card.Owner.GetUnlockedCharacterCards(context.CardMultiplayerConstraint)
            .GetDistinctForCombat(
                card.Owner,
                3,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .ToList();

        RecordOptions(context, cards);
    }

    public static void InfernalBladeOnPlay(InfernalBlade card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        var cards = context.Simulator
            .GetDistinctUnlockedCharacterAttacksForCombat(
                card.Owner,
                1,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .Select(generatedCard => generatedCard.SetToFreeThisTurn())
            .ToList();

        context.Simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, card.Owner);
    }

    public static void JackOfAllTradesOnPlay(JackOfAllTrades card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        var cards = card.Owner.GetUnlockedColorlessCards(context.CardMultiplayerConstraint)
            .Where(candidate => candidate is not JackOfAllTrades)
            .GetDistinctForCombat(
                card.Owner,
                card.DynamicVars.Cards.IntValue,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .ToList();

        context.Simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, card.Owner);
    }

    public static void JackpotOnPlay(Jackpot _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        ContinueGenerationCardSequence(context, GenerationCardSequence.Jackpot);
    }

    public static void LargesseOnPlay(Largesse card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        var targetPlayer = context.TargetPlayer;
        var cards = context.Simulator
            .GetDistinctUnlockedColorlessForCombat(
                targetPlayer,
                1,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .UpgradeIf(card.IsUpgraded)
            .ToList();

        context.Simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, card.Owner);
    }

    public static void MadScienceOnPlay(MadScience card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        switch (card.TinkerTimeType)
        {
            case CardType.Attack:
            {
                // 0.107.1 executes Violence as separate AttackCommands. This matters for
                // one-attack effects such as Vigor, which are consumed after the first command.
                int hitCount = card.TinkerTimeRider == TinkerTime.RiderEffect.Violence
                    ? card.DynamicVars["ViolenceHits"].IntValue
                    : 1;
                ContinueMadScienceAttacks(card, context, nextHit: 0, hitCount);
                return;
            }
            case CardType.Skill:
            case CardType.Power:
                ContinueMadScienceMain(context, stage: 0);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(card.TinkerTimeType), card.TinkerTimeType, null);
        }
    }

    private static bool ContinueMadScienceAttacks(
        MadScience card,
        CardOnPlayMirrorContext context,
        int nextHit,
        int hitCount)
    {
        for (int hitIndex = nextHit; hitIndex < hitCount; hitIndex++)
        {
            DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
                .FromCard(card, context.CardPlay)
                .Targeting(context.Target)
                .Simulate(context.Simulator);
            if (context.Simulator.HasPendingChoice)
            {
                context.Simulator.AppendExecutionContinuation(
                    new MadScienceAttackExecutionFrame(
                        context.Card,
                        context.CardPlay,
                        hitIndex + 1,
                        hitCount));
                return false;
            }
        }

        return ContinueMadScienceRider(context, stage: 0);
    }

    private sealed record MadScienceAttackExecutionFrame(
        PredictedCard Card,
        CardPlay Play,
        int NextHit,
        int HitCount) : ICombatPredictionExecutionFrame
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
            => ContinueMadScienceAttacks(
                (MadScience)Card.MutablePreview,
                new CardOnPlayMirrorContext
                {
                    Simulator = simulator,
                    Card = Card,
                    CardPlay = Play
                },
                NextHit,
                HitCount);
    }

    public static void ManifestAuthorityOnPlay(ManifestAuthority _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        ContinueGenerationCardSequence(
            context,
            GenerationCardSequence.ManifestAuthority);
    }

    public static void MetamorphosisOnPlay(Metamorphosis card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        var cards = context.Simulator
            .GetUnlockedCharacterAttacksForCombat(
                card.Owner,
                card.DynamicVars.Cards.IntValue,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .Select(generatedCard => generatedCard.SetToFreeThisCombat())
            .ToList();

        context.Simulator.AddGeneratedCardsToCombat(
            cards,
            PileType.Draw,
            card.Owner,
            CardPilePosition.Random);
    }

    public static void QuasarOnPlay(Quasar card, CardOnPlayMirrorContext context)
    {
        var cards = context.Simulator
            .GetDistinctUnlockedColorlessForCombat(
                card.Owner,
                3,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .UpgradeIf(card.IsUpgraded)
            .ToList();

        RecordOptions(context, cards);
    }

    public static void SplashOnPlay(Splash card, CardOnPlayMirrorContext context)
    {
        var pools = card.Owner.UnlockState.CharacterCardPools.ToList();
        if (pools.Count > 1)
        {
            pools.Remove(card.Owner.Character.CardPool);
        }

        var cards = pools
            .SelectMany(pool => card.Owner.GetUnlockedCards(pool, context.CardMultiplayerConstraint))
            .Where(candidate => candidate.Type == CardType.Attack)
            .GetDistinctForCombat(
                card.Owner,
                3,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .UpgradeIf(card.IsUpgraded)
            .ToList();

        RecordOptions(context, cards);
    }

    public static void StokeOnPlay(Stoke _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        List<PredictedCard> cardsToExhaust = context.OwnerState.Hand.Cards.ToList();
        ContinueStoke(
            context.Simulator,
            context.Card,
            context.CardPlay,
            cardsToExhaust,
            nextIndex: 0);
    }

    private static bool ContinueStoke(
        CombatPredictionSimulator simulator,
        PredictedCard playedCard,
        CardPlay play,
        List<PredictedCard> cardsToExhaust,
        int nextIndex)
    {
        for (int index = nextIndex; index < cardsToExhaust.Count; index++)
        {
            simulator.Exhaust(cardsToExhaust[index]);
            if (simulator.HasPendingChoice)
            {
                simulator.AppendExecutionContinuation(
                    new StokeExecutionFrame(playedCard, play, cardsToExhaust, index + 1));
                return false;
            }
        }

        var context = new CardOnPlayMirrorContext
        {
            Simulator = simulator,
            Card = playedCard,
            CardPlay = play
        };
        var card = (Stoke)playedCard.MutablePreview;
        var cards = card.Owner.GetUnlockedCharacterCards(context.CardMultiplayerConstraint)
            .GetForCombat(
                card.Owner,
                cardsToExhaust.Count,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .UpgradeIf(card.IsUpgraded)
            .ToList();

        simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, card.Owner);
        return !simulator.HasPendingChoice;
    }

    private sealed record StokeExecutionFrame(
        PredictedCard Card,
        CardPlay Play,
        List<PredictedCard> CardsToExhaust,
        int NextIndex) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
        {
            CombatPredictionSimulator.PrepareExecutionCardPlay(Card, Play, context);
            CombatPredictionSimulator.ForkExecutionCardList(CardsToExhaust, context);
        }

        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with
            {
                Card = context.RequireRemap(Card),
                Play = context.RequireRemap(Play),
                CardsToExhaust = context.RequireRemap(CardsToExhaust)
            };

        public bool Resume(CombatPredictionSimulator simulator)
            => ContinueStoke(simulator, Card, Play, CardsToExhaust, NextIndex);
    }

    public static void WhiteNoiseOnPlay(WhiteNoise card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        var cards = card.Owner.GetUnlockedCharacterCards(context.CardMultiplayerConstraint)
            .Where(candidate => candidate.Type == CardType.Power)
            .GetDistinctForCombat(
                card.Owner,
                1,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .Select(generatedCard => generatedCard.SetToFreeThisTurn())
            .ToList();

        context.Simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, card.Owner);
    }

    private static void RecordOptions(CardOnPlayMirrorContext context, IReadOnlyList<PredictedCard> cards)
    {
        if (cards.Count == 0)
        {
            return;
        }

        context.Simulator.History.CardGenerationOptions(cards);
        // Vanilla next asks the player to choose an option. Record the deterministic options first,
        // then mark the unresolved choice so replayed or nested results inherit the uncertainty.
        context.History.RecordRisk(PredictionRiskReason.UnresolvedPlayerChoice);
    }
}
