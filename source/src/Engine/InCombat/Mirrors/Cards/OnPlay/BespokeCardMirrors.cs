using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static class BespokeCardMirrors
{
    public static void AstralPulseOnPlay(AstralPulse _, CardOnPlayMirrorContext context)
        => context.AttackAllOpponents(hitCount: 2);

    public static void DaggerSprayOnPlay(DaggerSpray _, CardOnPlayMirrorContext context)
        => context.AttackAllOpponents(hitCount: 2);

    // Vanilla wraps the whole body in Osty.CheckMissingWithAnim, so the attack and the block are both
    // skipped once the Osty is gone. The sacrifice stays in CardEffectSpecRegistry, which runs after
    // this mirror and is gated on the same condition.
    public static void BoneShardsOnPlay(BoneShards card, CardOnPlayMirrorContext context)
    {
        if (context.State.GetOsty(card.Owner) is not { } osty || context.State.GetCreature(osty).IsDead)
        {
            return;
        }

        DamageCmd.Attack(card.DynamicVars.OstyDamage.BaseValue)
            .FromOsty(osty, card, context.CardPlay)
            .TargetingAllOpponents(context.CombatState)
            .Simulate(context.Simulator);

        ContinueOrQueueTail(context, BespokeTailKind.BoneShardsGainBlock);
    }

    public static void PactsEndOnPlay(PactsEnd card, CardOnPlayMirrorContext context)
    {
        if (context.OwnerState.ExhaustPile.Cards.Count >= card.DynamicVars.Cards.IntValue)
            context.AttackAllOpponents();
    }

    public static void ExpectAFightOnPlay(ExpectAFight card, CardOnPlayMirrorContext context)
        => context.Simulator.GainEnergy(
            card.Owner,
            context.Calculate(card.DynamicVars["CalculatedEnergy"]));

    public static void DemonicShieldOnPlay(DemonicShield card, CardOnPlayMirrorContext context)
    {
        Creature owner = card.Owner.Creature;
        context.Simulator.Damage(
            [owner],
            card.DynamicVars.HpLoss.BaseValue,
            MegaCrit.Sts2.Core.ValueProps.ValueProp.Unblockable
            | MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered
            | MegaCrit.Sts2.Core.ValueProps.ValueProp.Move,
            owner,
            context.Card,
            null);
        ContinueOrQueueTail(context, BespokeTailKind.DemonicShieldGainBlock);
    }

    public static void InterceptOnPlay(Intercept card, CardOnPlayMirrorContext context)
    {
        context.GainBlock(card.Owner.Creature);
        ContinueOrQueueTail(context, BespokeTailKind.InterceptCoverage);
    }

    public static void TwinStrikeOnPlay(TwinStrike _, CardOnPlayMirrorContext context)
        => context.AttackSingle(hitCount: 2);

    public static void HeavenlyDrillOnPlay(HeavenlyDrill card, CardOnPlayMirrorContext context)
    {
        int hits = context.Card.ResolveEnergyXValue(context.State);
        if (hits >= card.DynamicVars.Energy.IntValue)
            hits *= 2;
        context.AttackSingle(hitCount: hits);
    }

    public static void FiendFireOnPlay(FiendFire _, CardOnPlayMirrorContext context)
    {
        PredictedCard[] hand = context.OwnerState.Hand.Cards.ToArray();
        _ = ContinueFiendFire(
            context.Simulator,
            context.Card,
            context.CardPlay,
            hand,
            nextIndex: 0);
    }

    public static void DismantleOnPlay(Dismantle card, CardOnPlayMirrorContext context)
    {
        int hitCount = GetPowerAmount<VulnerablePower>(context, context.Target) > 0 ? 2 : 1;
        DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .WithHitCount(hitCount)
            .FromCard(card, context.CardPlay)
            .Targeting(context.Target)
            .Simulate(context.Simulator);
    }

    public static void EntrenchOnPlay(Entrench card, CardOnPlayMirrorContext context)
    {
        int currentBlock = context.State.GetCreature(card.Owner.Creature).Block;
        context.GainBlock(
            card.Owner.Creature,
            currentBlock,
            MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered
            | MegaCrit.Sts2.Core.ValueProps.ValueProp.Move);
    }

    public static void LeadingStrikeOnPlay(LeadingStrike card, CardOnPlayMirrorContext context)
    {
        DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .FromCard(card, context.CardPlay)
            .Targeting(context.Target)
            .Simulate(context.Simulator);
        if (context.Simulator.HasPendingChoice)
        {
            context.Simulator.AppendExecutionContinuation(
                new LeadingStrikeExecutionFrame(context.Card, context.CardPlay, NextShiv: 0));
            return;
        }

        _ = ContinueLeadingStrikeShivs(
            context.Simulator,
            context.Card,
            context.CardPlay,
            nextShiv: 0);
    }

    public static void MimicOnPlay(Mimic card, CardOnPlayMirrorContext context)
    {
        context.GainBlock(
            card.Owner.Creature,
            context.Calculate(card.DynamicVars.CalculatedBlock),
            card.DynamicVars.CalculatedBlock.Props);
    }

    public static void MiseryOnPlay(Misery card, CardOnPlayMirrorContext context)
    {
        if (context.CombatState is not SimulatedCombatState combat)
            throw new InvalidOperationException("Misery requires writable branch combat state.");

        // 0.107.1 snapshots the target's Debuffs before the attack, then spreads those
        // snapshots after the attack. Preserve listener/power order: temporary Power
        // markers (for example EnfeeblingTouchPower) must remain separate from the
        // negative StrengthPower they originally created.
        MiseryDebuffSnapshot[] debuffs = combat.EffectivePowers()
            .Where(power => ReferenceEquals(power.Owner, context.Target)
                && power.TypeForCurrentAmount == MegaCrit.Sts2.Core.Entities.Powers.PowerType.Debuff)
            .Select(power => new MiseryDebuffSnapshot(power.GetType(), power.Amount, power.Applier))
            .ToArray();

        DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .FromCard(card, context.CardPlay)
            .Targeting(context.Target)
            .Simulate(context.Simulator);
        if (context.Simulator.HasPendingChoice)
        {
            context.Simulator.AppendExecutionContinuation(
                new MiserySpreadExecutionFrame(context.Target, debuffs));
            return;
        }

        ApplyMiseryDebuffs(combat, context.Target, debuffs);
    }

    private static void ApplyMiseryDebuffs(
        SimulatedCombatState combat,
        Creature source,
        IReadOnlyList<MiseryDebuffSnapshot> debuffs)
    {
        foreach (Creature enemy in combat.HittableEnemies.Where(enemy => !ReferenceEquals(enemy, source)).ToArray())
        {
            foreach (MiseryDebuffSnapshot debuff in debuffs)
                combat.ApplyPower(debuff.PowerType, enemy, debuff.Amount, debuff.Applier);
        }
    }

    private readonly record struct MiseryDebuffSnapshot(Type PowerType, int Amount, Creature? Applier);

    private sealed record MiserySpreadExecutionFrame(
        Creature Source,
        IReadOnlyList<MiseryDebuffSnapshot> Debuffs) : ICombatPredictionExecutionFrame
    {
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context) => this;

        public bool Resume(CombatPredictionSimulator simulator)
        {
            if (simulator.State.CombatState is not SimulatedCombatState combat)
                throw new InvalidOperationException("Misery continuation requires writable branch combat state.");
            ApplyMiseryDebuffs(combat, Source, Debuffs);
            return !simulator.HasPendingChoice;
        }
    }

    public static void MaulOnPlay(Maul card, CardOnPlayMirrorContext context)
    {
        DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .WithHitCount(2)
            .FromCard(card, context.CardPlay)
            .Targeting(context.Target)
            .Simulate(context.Simulator);
        ContinueOrQueueTail(context, BespokeTailKind.MaulGrowth);
    }

    public static void SpiteOnPlay(Spite card, CardOnPlayMirrorContext context)
    {
        SimulatedCombatState combat = context.CombatState as SimulatedCombatState
            ?? throw new InvalidOperationException("恶意缺少分支回合受伤状态。");
        int hitCount = 1;
        if (combat.HasLostHpThisTurn(card.Owner.Creature))
        {
            // Spite's RepeatVar is canonical (2, plus one per upgrade). A few live cards can
            // carry a stale DynamicVarSet after an in-run card-state rewrite; recover this
            // card-specific canonical value instead of failing the whole search.
            hitCount = card.DynamicVars.TryGetValue("Repeat", out DynamicVar? repeat)
                ? repeat.IntValue
                : 2 + card.CurrentUpgradeLevel;
        }
        DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .WithHitCount(hitCount)
            .FromCard(card, context.CardPlay)
            .Targeting(context.Target)
            .Simulate(context.Simulator);
    }

    public static void TheScytheOnPlay(TheScythe card, CardOnPlayMirrorContext context)
    {
        DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .FromCard(card, context.CardPlay)
            .Targeting(context.Target)
            .Simulate(context.Simulator);
        ContinueOrQueueTail(context, BespokeTailKind.TheScytheGrowth);
    }

    public static void SacrificeOnPlay(Sacrifice card, CardOnPlayMirrorContext context)
    {
        if (context.State.GetOsty(card.Owner) is not { } osty || !context.State.GetCreature(osty).IsAlive)
            return;
        int block = context.State.GetCreature(osty).MaxHp * 2;
        context.Simulator.Kill(osty, force: true);
        ContinueOrQueueTail(context, BespokeTailKind.SacrificeGainBlock, block);
    }

    public static void SecondWindOnPlay(SecondWind _, CardOnPlayMirrorContext context)
    {
        PredictedCard[] cards = context.OwnerState.Hand.Cards
            .Where(candidate => candidate.Preview.Type != CardType.Attack)
            .ToArray();
        _ = ContinueSecondWind(
            context.Simulator,
            context.Card,
            context.CardPlay,
            cards,
            nextIndex: 0,
            needsBlock: false);
    }

    public static void SovereignBladeOnPlay(SovereignBlade card, CardOnPlayMirrorContext context)
    {
        bool allEnemies = GetPowerAmount<SeekingEdgePower>(context, card.Owner.Creature) > 0;
        var attack = DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .WithHitCount(card.DynamicVars.Repeat.IntValue)
            .FromCard(card, context.CardPlay);
        if (allEnemies)
            attack.TargetingAllOpponents(context.CombatState);
        else
            attack.Targeting(context.Target);
        attack.Simulate(context.Simulator);
        ContinueOrQueueTail(context, BespokeTailKind.SovereignBladeParryBlock);
    }

    private static void ContinueOrQueueTail(
        CardOnPlayMirrorContext context,
        BespokeTailKind tail,
        int value = 0)
    {
        if (context.Simulator.HasPendingChoice)
        {
            context.Simulator.AppendExecutionContinuation(
                new BespokeTailExecutionFrame(context.Card, context.CardPlay, tail, value));
            return;
        }

        _ = ResumeBespokeTail(context.Simulator, context.Card, context.CardPlay, tail, value);
    }

    private static bool ResumeBespokeTail(
        CombatPredictionSimulator simulator,
        PredictedCard playedCard,
        CardPlay play,
        BespokeTailKind tail,
        int value)
    {
        var context = new CardOnPlayMirrorContext
        {
            Simulator = simulator,
            Card = playedCard,
            CardPlay = play
        };

        switch (tail)
        {
            case BespokeTailKind.BoneShardsGainBlock:
            {
                var card = (BoneShards)playedCard.MutablePreview;
                context.GainBlock(card.Owner.Creature);
                break;
            }
            case BespokeTailKind.DemonicShieldGainBlock:
            {
                var card = (DemonicShield)playedCard.MutablePreview;
                context.GainBlock(
                    context.Target,
                    context.Calculate(card.DynamicVars.CalculatedBlock),
                    card.DynamicVars.CalculatedBlock.Props);
                break;
            }
            case BespokeTailKind.InterceptCoverage:
            {
                var card = (Intercept)playedCard.MutablePreview;
                if (context.CombatState is not SimulatedCombatState combat)
                    throw new InvalidOperationException("Intercept continuation requires writable branch combat state.");
                combat.Apply<CoveredPower>(context.Target, 1, card.Owner.Creature);
                PowerPredictionStateSupport.ApplyInterceptCoverage(
                    simulator,
                    combat,
                    card.Owner.Creature,
                    context.Target);
                break;
            }
            case BespokeTailKind.MaulGrowth:
                ApplyMaulGrowth((Maul)playedCard.MutablePreview, context);
                break;
            case BespokeTailKind.TheScytheGrowth:
                ApplyTheScytheGrowth((TheScythe)playedCard.MutablePreview, context);
                break;
            case BespokeTailKind.SacrificeGainBlock:
            {
                var card = (Sacrifice)playedCard.MutablePreview;
                simulator.GainBlock(
                    card.Owner.Creature,
                    value,
                    card.DynamicVars.CalculatedBlock.Props,
                    playedCard,
                    play);
                break;
            }
            case BespokeTailKind.SovereignBladeParryBlock:
            {
                var card = (SovereignBlade)playedCard.MutablePreview;
                int parry = GetPowerAmount<ParryPower>(context, card.Owner.Creature);
                if (parry > 0)
                {
                    simulator.GainBlock(
                        card.Owner.Creature,
                        parry,
                        card.DynamicVars.CalculatedBlock.Props,
                        playedCard,
                        play);
                }
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(tail), tail, null);
        }

        return !simulator.HasPendingChoice;
    }

    private static void ApplyMaulGrowth(Maul card, CardOnPlayMirrorContext context)
    {
        decimal increase = card.DynamicVars["Increase"].BaseValue;
        foreach (PredictedCard candidate in context.OwnerState.AllCards
                     .Where(candidate => candidate.Preview is Maul)
                     .ToArray())
        {
            Maul mutable = (Maul)candidate.MutablePreview;
            mutable.DynamicVars.Damage.BaseValue += increase;
            mutable._extraDamageFromMaulPlays += increase;
        }
    }

    private static void ApplyTheScytheGrowth(TheScythe card, CardOnPlayMirrorContext context)
    {
        TheScythe mutable = (TheScythe)context.Card.MutablePreview;
        int increase = card.DynamicVars["Increase"].IntValue;
        mutable.IncreasedDamage += increase;
        mutable.CurrentDamage = 13 + mutable.IncreasedDamage;
        if (mutable.DeckVersion != null
            && context.CombatState is SimulatedCombatState combat)
        {
            combat.RecordLongTermResource(increase);
            combat.RecordGrowthReward(GrowthSource.TheScythe);
        }
    }

    private static bool ContinueFiendFire(
        CombatPredictionSimulator simulator,
        PredictedCard playedCard,
        CardPlay play,
        IReadOnlyList<PredictedCard> cards,
        int nextIndex)
    {
        for (int index = nextIndex; index < cards.Count; index++)
        {
            simulator.Exhaust(cards[index]);
            if (simulator.HasPendingChoice)
            {
                simulator.AppendExecutionContinuation(
                    new FiendFireExecutionFrame(playedCard, play, cards, index + 1));
                return false;
            }
        }

        var card = (FiendFire)playedCard.MutablePreview;
        DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .WithHitCount(cards.Count)
            .FromCard(card, play)
            .Targeting(play.Target)
            .Simulate(simulator);
        return !simulator.HasPendingChoice;
    }

    private static bool ContinueLeadingStrikeShivs(
        CombatPredictionSimulator simulator,
        PredictedCard playedCard,
        CardPlay play,
        int nextShiv)
    {
        var card = (LeadingStrike)playedCard.MutablePreview;
        int count = card.DynamicVars["Shivs"].IntValue;
        for (int index = nextShiv; index < count; index++)
        {
            simulator.CreateAndAddGeneratedCardsToCombat<Shiv>(
                card.Owner,
                PileType.Hand,
                1,
                card.Owner);
            if (simulator.HasPendingChoice)
            {
                simulator.AppendExecutionContinuation(
                    new LeadingStrikeExecutionFrame(playedCard, play, index + 1));
                return false;
            }
        }

        return true;
    }

    private static bool ContinueSecondWind(
        CombatPredictionSimulator simulator,
        PredictedCard playedCard,
        CardPlay play,
        IReadOnlyList<PredictedCard> cards,
        int nextIndex,
        bool needsBlock)
    {
        var card = (SecondWind)playedCard.MutablePreview;
        int index = nextIndex;
        bool blockCurrent = needsBlock;
        while (index < cards.Count)
        {
            if (!blockCurrent)
            {
                simulator.Exhaust(cards[index]);
                if (simulator.HasPendingChoice)
                {
                    simulator.AppendExecutionContinuation(
                        new SecondWindExecutionFrame(
                            playedCard,
                            play,
                            cards,
                            index,
                            NeedsBlock: true));
                    return false;
                }
            }

            simulator.GainBlock(
                card.Owner.Creature,
                card.DynamicVars.Block,
                playedCard,
                play);
            if (simulator.HasPendingChoice)
            {
                simulator.AppendExecutionContinuation(
                    new SecondWindExecutionFrame(
                        playedCard,
                        play,
                        cards,
                        index + 1,
                        NeedsBlock: false));
                return false;
            }

            blockCurrent = false;
            index++;
        }

        return true;
    }

    private enum BespokeTailKind
    {
        BoneShardsGainBlock,
        DemonicShieldGainBlock,
        InterceptCoverage,
        MaulGrowth,
        TheScytheGrowth,
        SacrificeGainBlock,
        SovereignBladeParryBlock,
    }

    private sealed record BespokeTailExecutionFrame(
        PredictedCard Card,
        CardPlay Play,
        BespokeTailKind Tail,
        int Value) : ICombatPredictionExecutionFrame
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
            => ResumeBespokeTail(simulator, Card, Play, Tail, Value);
    }

    private sealed record FiendFireExecutionFrame(
        PredictedCard Card,
        CardPlay Play,
        IReadOnlyList<PredictedCard> Cards,
        int NextIndex) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
            => CombatPredictionSimulator.PrepareExecutionCardPlay(Card, Play, context);

        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with
            {
                Card = context.RequireRemap(Card),
                Play = context.RequireRemap(Play),
                Cards = Cards.Select(candidate => context.RequireRemap(candidate)).ToArray()
            };

        public bool Resume(CombatPredictionSimulator simulator)
            => ContinueFiendFire(simulator, Card, Play, Cards, NextIndex);
    }

    private sealed record LeadingStrikeExecutionFrame(
        PredictedCard Card,
        CardPlay Play,
        int NextShiv) : ICombatPredictionExecutionFrame
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
            => ContinueLeadingStrikeShivs(simulator, Card, Play, NextShiv);
    }

    private sealed record SecondWindExecutionFrame(
        PredictedCard Card,
        CardPlay Play,
        IReadOnlyList<PredictedCard> Cards,
        int NextIndex,
        bool NeedsBlock) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
            => CombatPredictionSimulator.PrepareExecutionCardPlay(Card, Play, context);

        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with
            {
                Card = context.RequireRemap(Card),
                Play = context.RequireRemap(Play),
                Cards = Cards.Select(candidate => context.RequireRemap(candidate)).ToArray()
            };

        public bool Resume(CombatPredictionSimulator simulator)
            => ContinueSecondWind(simulator, Card, Play, Cards, NextIndex, NeedsBlock);
    }

    private static int GetPowerAmount<TPower>(CardOnPlayMirrorContext context, Creature owner)
        where TPower : PowerModel
    {
        if (context.CombatState is ICombatPredictionHookListenerSource source)
        {
            return source.HookListeners.OfType<TPower>()
                .Where(power => power.Owner == owner)
                .Sum(power => power.Amount);
        }
        return owner.GetPowerAmount<TPower>();
    }
}
