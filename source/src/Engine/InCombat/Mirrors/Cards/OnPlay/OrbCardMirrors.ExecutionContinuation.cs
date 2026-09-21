using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static partial class OrbCardMirrors
{
    private static void ContinueOrQueueTail(
        CardOnPlayMirrorContext context,
        OrbCardTailKind tail)
    {
        if (context.Simulator.HasPendingChoice)
        {
            context.Simulator.AppendExecutionContinuation(
                new OrbCardTailExecutionFrame(context.Card, context.CardPlay, tail));
            return;
        }

        _ = ResumeOrbCardTail(context.Simulator, context.Card, context.CardPlay, tail);
    }

    private static bool ResumeOrbCardTail(
        CombatPredictionSimulator simulator,
        PredictedCard playedCard,
        CardPlay play,
        OrbCardTailKind tail)
    {
        var context = new CardOnPlayMirrorContext
        {
            Simulator = simulator,
            Card = playedCard,
            CardPlay = play
        };

        switch (tail)
        {
            case OrbCardTailKind.BallLightningChannel:
                simulator.OrbChannel<LightningOrb>(playedCard.Preview.Owner);
                break;
            case OrbCardTailKind.ColdSnapChannel:
                simulator.OrbChannel<FrostOrb>(playedCard.Preview.Owner);
                break;
            case OrbCardTailKind.ConsumingShadowPower:
            {
                var card = (ConsumingShadow)playedCard.MutablePreview;
                if (context.CombatState is not ICombatPredictionEffectSink effects)
                    throw new InvalidOperationException("吞噬暗影结算缺少可写的预测状态。");
                effects.ApplyPower(
                    typeof(ConsumingShadowPower),
                    card.Owner.Creature,
                    card.DynamicVars["ConsumingShadowPower"].IntValue,
                    card.Owner.Creature);
                break;
            }
            case OrbCardTailKind.CoolheadedDraw:
            {
                var card = (Coolheaded)playedCard.MutablePreview;
                simulator.Draw(card.Owner, card.DynamicVars.Cards.BaseValue);
                break;
            }
            case OrbCardTailKind.DarknessPassives:
                return StartDarknessPassives(context);
            case OrbCardTailKind.GlacierChannels:
                simulator.OrbChannel<FrostOrb>(playedCard.Preview.Owner, 2);
                break;
            case OrbCardTailKind.GlassworkChannel:
                simulator.OrbChannel<GlassOrb>(playedCard.Preview.Owner);
                break;
            case OrbCardTailKind.IceLanceChannels:
            {
                var card = (IceLance)playedCard.MutablePreview;
                simulator.OrbChannel<FrostOrb>(card.Owner, card.DynamicVars.Repeat.IntValue);
                break;
            }
            case OrbCardTailKind.MeteorStrikeChannels:
                simulator.OrbChannel<PlasmaOrb>(playedCard.Preview.Owner, 3);
                break;
            case OrbCardTailKind.NullWeakThenDark:
            {
                var card = (Null)playedCard.MutablePreview;
                SimulatedCombatState combat = context.CombatState as SimulatedCombatState
                    ?? throw new InvalidOperationException("Null requires simulated combat state.");
                combat.Apply<WeakPower>(
                    context.Target,
                    card.DynamicVars.Weak.IntValue,
                    card.Owner.Creature);
                ContinueOrQueueTail(context, OrbCardTailKind.NullDark);
                break;
            }
            case OrbCardTailKind.NullDark:
                simulator.OrbChannel<DarkOrb>(playedCard.Preview.Owner);
                break;
            case OrbCardTailKind.RainbowFrostThenDark:
                simulator.OrbChannel<FrostOrb>(playedCard.Preview.Owner);
                ContinueOrQueueTail(context, OrbCardTailKind.RainbowDark);
                break;
            case OrbCardTailKind.RainbowDark:
                simulator.OrbChannel<DarkOrb>(playedCard.Preview.Owner);
                break;
            case OrbCardTailKind.RefractChannels:
            {
                var card = (Refract)playedCard.MutablePreview;
                simulator.OrbChannel<GlassOrb>(card.Owner, card.DynamicVars.Repeat.IntValue);
                break;
            }
            case OrbCardTailKind.ShadowShieldChannel:
                simulator.OrbChannel<DarkOrb>(playedCard.Preview.Owner);
                break;
            case OrbCardTailKind.ShatterEvokes:
                return StartShatterEvokes(context);
            case OrbCardTailKind.TeslaPassives:
                return StartTeslaPassives(context);
            default:
                throw new ArgumentOutOfRangeException(nameof(tail), tail, null);
        }

        return !simulator.HasPendingChoice;
    }

    private static bool ContinueChaos(
        CardOnPlayMirrorContext context,
        int nextIndex)
    {
        var card = (Chaos)context.Card.MutablePreview;
        for (int index = nextIndex; index < card.DynamicVars.Repeat.IntValue; index++)
        {
            OrbModel orb = OrbModel.GetRandomOrb(context.Rng.CombatOrbGeneration).ToMutable();
            context.Simulator.OrbChannel(card.Owner, orb);
            if (!context.Simulator.HasPendingChoice)
                continue;

            context.Simulator.AppendExecutionContinuation(
                new ChaosExecutionFrame(context.Card, context.CardPlay, index + 1));
            return false;
        }

        return true;
    }

    private static bool StartDarknessPassives(CardOnPlayMirrorContext context)
    {
        var card = (Darkness)context.Card.MutablePreview;
        DarkOrb[] orbs = context.OwnerState.OrbQueue.Orbs.OfType<DarkOrb>().ToArray();
        int triggerCount = card.IsUpgraded ? 2 : 1;
        return ContinueDarknessPassives(
            context.Simulator,
            context.Card,
            context.CardPlay,
            orbs,
            orbIndex: 0,
            triggerIndex: 0,
            triggerCount);
    }

    private static bool ContinueDarknessPassives(
        CombatPredictionSimulator simulator,
        PredictedCard playedCard,
        CardPlay play,
        IReadOnlyList<DarkOrb> orbs,
        int orbIndex,
        int triggerIndex,
        int triggerCount)
    {
        for (int currentOrb = orbIndex; currentOrb < orbs.Count; currentOrb++)
        {
            int startTrigger = currentOrb == orbIndex ? triggerIndex : 0;
            for (int currentTrigger = startTrigger; currentTrigger < triggerCount; currentTrigger++)
            {
                simulator.OrbPassive(orbs[currentOrb]);
                if (!simulator.HasPendingChoice)
                    continue;

                simulator.AppendExecutionContinuation(
                    new DarknessPassiveExecutionFrame(
                        playedCard,
                        play,
                        orbs,
                        currentOrb,
                        currentTrigger + 1,
                        triggerCount));
                return false;
            }
        }

        return true;
    }

    private static bool StartShatterEvokes(CardOnPlayMirrorContext context)
    {
        int orbCount = context.OwnerState.OrbQueue.Orbs.Count;
        return ContinueShatterEvokes(
            context.Simulator,
            context.Card,
            context.CardPlay,
            orbCount,
            nextIndex: 0);
    }

    private static bool ContinueShatterEvokes(
        CombatPredictionSimulator simulator,
        PredictedCard playedCard,
        CardPlay play,
        int orbCount,
        int nextIndex)
    {
        var card = (Shatter)playedCard.MutablePreview;
        for (int index = nextIndex; index < orbCount; index++)
        {
            simulator.OrbEvokeNext(card.Owner, repeat: 2);
            if (!simulator.HasPendingChoice)
                continue;

            simulator.AppendExecutionContinuation(
                new ShatterExecutionFrame(
                    playedCard,
                    play,
                    orbCount,
                    index + 1));
            return false;
        }

        return true;
    }

    private static bool StartTeslaPassives(CardOnPlayMirrorContext context)
    {
        var card = (TeslaCoil)context.Card.MutablePreview;
        LightningOrb[] orbs = context.OwnerState.OrbQueue.Orbs.OfType<LightningOrb>().ToArray();
        int triggerCount = card.IsUpgraded ? 2 : 1;
        return ContinueTeslaPassives(
            context.Simulator,
            context.Card,
            context.CardPlay,
            orbs,
            orbIndex: 0,
            triggerIndex: 0,
            triggerCount);
    }

    private static bool ContinueTeslaPassives(
        CombatPredictionSimulator simulator,
        PredictedCard playedCard,
        CardPlay play,
        IReadOnlyList<LightningOrb> orbs,
        int orbIndex,
        int triggerIndex,
        int triggerCount)
    {
        Creature target = play.Target
            ?? throw new InvalidOperationException("Tesla Coil requires a target.");
        for (int currentOrb = orbIndex; currentOrb < orbs.Count; currentOrb++)
        {
            int startTrigger = currentOrb == orbIndex ? triggerIndex : 0;
            for (int currentTrigger = startTrigger; currentTrigger < triggerCount; currentTrigger++)
            {
                simulator.OrbPassive(orbs[currentOrb], target);
                if (!simulator.HasPendingChoice)
                    continue;

                simulator.AppendExecutionContinuation(
                    new TeslaPassiveExecutionFrame(
                        playedCard,
                        play,
                        orbs,
                        currentOrb,
                        currentTrigger + 1,
                        triggerCount));
                return false;
            }
        }

        return true;
    }

    private enum OrbCardTailKind
    {
        BallLightningChannel,
        ColdSnapChannel,
        ConsumingShadowPower,
        CoolheadedDraw,
        DarknessPassives,
        GlacierChannels,
        GlassworkChannel,
        IceLanceChannels,
        MeteorStrikeChannels,
        NullWeakThenDark,
        NullDark,
        RainbowFrostThenDark,
        RainbowDark,
        RefractChannels,
        ShadowShieldChannel,
        ShatterEvokes,
        TeslaPassives,
    }

    private sealed record OrbCardTailExecutionFrame(
        PredictedCard Card,
        CardPlay Play,
        OrbCardTailKind Tail) : ICombatPredictionExecutionFrame
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
            => ResumeOrbCardTail(simulator, Card, Play, Tail);
    }

    private sealed record ChaosExecutionFrame(
        PredictedCard Card,
        CardPlay Play,
        int NextIndex) : ICombatPredictionExecutionFrame
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
            => ContinueChaos(
                new CardOnPlayMirrorContext
                {
                    Simulator = simulator,
                    Card = Card,
                    CardPlay = Play
                },
                NextIndex);
    }

    private sealed record DarknessPassiveExecutionFrame(
        PredictedCard Card,
        CardPlay Play,
        IReadOnlyList<DarkOrb> Orbs,
        int OrbIndex,
        int TriggerIndex,
        int TriggerCount) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
            => CombatPredictionSimulator.PrepareExecutionCardPlay(Card, Play, context);

        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with
            {
                Card = context.RequireRemap(Card),
                Play = context.RequireRemap(Play),
                Orbs = Orbs.Select(orb => context.RequireRemap(orb)).ToArray()
            };

        public bool Resume(CombatPredictionSimulator simulator)
            => ContinueDarknessPassives(
                simulator,
                Card,
                Play,
                Orbs,
                OrbIndex,
                TriggerIndex,
                TriggerCount);
    }

    private sealed record ShatterExecutionFrame(
        PredictedCard Card,
        CardPlay Play,
        int OrbCount,
        int NextIndex) : ICombatPredictionExecutionFrame
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
            => ContinueShatterEvokes(
                simulator,
                Card,
                Play,
                OrbCount,
                NextIndex);
    }

    private sealed record TeslaPassiveExecutionFrame(
        PredictedCard Card,
        CardPlay Play,
        IReadOnlyList<LightningOrb> Orbs,
        int OrbIndex,
        int TriggerIndex,
        int TriggerCount) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
            => CombatPredictionSimulator.PrepareExecutionCardPlay(Card, Play, context);

        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with
            {
                Card = context.RequireRemap(Card),
                Play = context.RequireRemap(Play),
                Orbs = Orbs.Select(orb => context.RequireRemap(orb)).ToArray()
            };

        public bool Resume(CombatPredictionSimulator simulator)
            => ContinueTeslaPassives(
                simulator,
                Card,
                Play,
                Orbs,
                OrbIndex,
                TriggerIndex,
                TriggerCount);
    }
}
