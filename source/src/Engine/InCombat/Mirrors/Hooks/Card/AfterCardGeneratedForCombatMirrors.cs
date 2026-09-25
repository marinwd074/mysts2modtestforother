using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;

using Registry = MethodMirrorRegistry<AbstractModel, AfterCardGeneratedForCombatMirrorContext>;

// Mirrors the prediction-relevant parts of Hook.AfterCardGeneratedForCombat.
internal static class AfterCardGeneratedForCombatMirrors
{
    private static readonly MirrorMethodSpec AfterCardGeneratedForCombat = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterCardGeneratedForCombat),
        [typeof(CardModel), typeof(Player)]);

    private static readonly Registry Registry = CreateRegistry();

    public static void Invoke(AbstractModel listener, AfterCardGeneratedForCombatMirrorContext context)
    {
        using var dispatch = context.Simulator.BeginExecutionDispatch();
        Registry.Invoke(listener, context);
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(AfterCardGeneratedForCombat);

        registry.Register<Aeonglass>(HandleAeonglass);
        registry.Register<ArsenalPower>(HandleArsenalPower);
        registry.Register<Regalite>(HandleRegalite);
#if !STS2_01071
        registry.Register<SoulboundPower>(HandleSoulboundPower);
#endif
        registry.Register<PillarOfCreationPower>(HandlePillarOfCreationPower);
        registry.Register<SmokestackPower>(HandleSmokestackPower);
        registry.Register<TrashToTreasurePower>(HandleTrashToTreasurePower);
        registry.Register<RocketPunch>(HandleRocketPunch);

        return registry;
    }

    private static void HandleAeonglass(Aeonglass monster, AfterCardGeneratedForCombatMirrorContext context)
    {
        if (context.PreviewCard is not Wither)
            return;
        var wither = (Wither)context.MutablePreviewCard;
        if (context.CombatState is not ICombatPredictionMonsterStateSink monsterState)
            throw new InvalidOperationException("永世沙漏生成凋零缺少预测怪物状态。");
        int upgradeCount = monsterState.GetAeonglassWitherUpgradeCount(monster.Creature);
        for (int index = 0; index < upgradeCount; index++)
            wither.FakeUpgrade();
    }

    private static void HandleArsenalPower(ArsenalPower power, AfterCardGeneratedForCombatMirrorContext context)
    {
        if (context.Creator?.Creature == power.Owner)
        {
            if (context.CombatState is not ICombatPredictionEffectSink effects)
                throw new InvalidOperationException("军械库效果缺少可写的预测状态。");
            effects.ApplyPowerFromSource(typeof(StrengthPower), power.Owner, power.Amount, power.Owner, cardSource: null);
        }
    }

    private static void HandleRegalite(Regalite relic, AfterCardGeneratedForCombatMirrorContext context)
    {
        if (context.Creator == relic.Owner)
            context.Simulator.GainBlock(relic.Owner.Creature, relic.DynamicVars.Block);
    }

#if !STS2_01071
    private static void HandleSoulboundPower(SoulboundPower power, AfterCardGeneratedForCombatMirrorContext context)
    {
        if (context.Creator?.Creature != power.Applier ||
            context.PreviewCard is not Soul ||
            power.Owner.Player is not { } player)
        {
            return;
        }

        var state = context.StateStore.Get(power, () => new SoulboundPredictionState(power));
        if (state.IsAddingSoul)
        {
            return;
        }

        state.IsAddingSoul = true;
        try
        {
            context.Simulator.CreateAndAddGeneratedCardsToCombat<Soul>(
                player,
                PileType.Draw,
                power.Amount,
                player,
                CardPilePosition.Random);
        }
        finally
        {
            state.IsAddingSoul = false;
        }
    }
#endif

    private static void HandlePillarOfCreationPower(PillarOfCreationPower power, AfterCardGeneratedForCombatMirrorContext context)
    {
        if (context.Creator?.Creature == power.Owner)
        {
            context.Simulator.GainBlock(power.Owner, power.Amount, ValueProp.Unpowered);
        }
    }

    private static void HandleSmokestackPower(SmokestackPower power, AfterCardGeneratedForCombatMirrorContext context)
    {
        if (context.PreviewCard.Type == CardType.Status &&
            context.Creator?.Creature == power.Owner)
        {
            context.Simulator.Damage(context.State.HittableEnemies, power.Amount, ValueProp.Unpowered, power.Owner);
        }
    }

    private static void HandleTrashToTreasurePower(TrashToTreasurePower power, AfterCardGeneratedForCombatMirrorContext context)
    {
        if (context.PreviewCard.Type != CardType.Status ||
            context.Creator?.Creature != power.Owner ||
            power.Owner.Player is not { } player)
        {
            return;
        }

        context.Simulator.AcknowledgeExecutionDispatch();
        _ = ContinueTrashToTreasure(context.Simulator, power, player, nextIndex: 0);
    }

    private static bool ContinueTrashToTreasure(
        CombatPredictionSimulator simulator,
        TrashToTreasurePower power,
        Player player,
        int nextIndex)
    {
        for (int index = nextIndex; index < power.Amount; index++)
        {
            OrbModel orb = OrbModel.GetRandomOrb(simulator.Rng.CombatOrbGeneration).ToMutable();
            simulator.OrbChannel(player, orb);
            if (simulator.HasPendingChoice)
            {
                simulator.AppendExecutionContinuation(
                    new TrashToTreasureExecutionFrame(power, player, index + 1));
                return false;
            }
        }

        return true;
    }

    private sealed record TrashToTreasureExecutionFrame(
        TrashToTreasurePower Power,
        Player Player,
        int NextIndex) : ICombatPredictionExecutionFrame
    {
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Power = (TrashToTreasurePower)context.RemapOrSelf(Power) };

        public bool Resume(CombatPredictionSimulator simulator)
            => ContinueTrashToTreasure(simulator, Power, Player, NextIndex);
    }

    private static void HandleRocketPunch(RocketPunch card, AfterCardGeneratedForCombatMirrorContext context)
    {
        if (context.Creator == card.Owner &&
            context.PreviewCard.Owner == card.Owner &&
            context.PreviewCard.Type == CardType.Status)
        {
            // Rocket Punch's native hook sets the until-played cost to zero.  Using an
            // additive modifier is not equivalent when the card already carries a
            // temporary/local cost modifier, and v0.107.1 exposes the exact setter.
            context.State.FindCard(card)?.MutablePreview.EnergyCost.SetUntilPlayed(0);
        }
    }
}

internal sealed class AfterCardGeneratedForCombatMirrorContext : CombatCardMirrorContext
{
    public required Player? Creator { get; init; }
}

#if !STS2_01071
internal sealed class SoulboundPredictionState(SoulboundPower power) : IPredictionStateForkable
{
    public bool IsAddingSoul { get; set; } = power._isAddingSoul;

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}
#endif

