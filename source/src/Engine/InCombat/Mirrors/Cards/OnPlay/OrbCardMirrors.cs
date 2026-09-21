using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Orbs;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static partial class OrbCardMirrors
{
    public static void BallLightningOnPlay(BallLightning _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        context.AttackSingle();
        ContinueOrQueueTail(context, OrbCardTailKind.BallLightningChannel);
    }

    public static void ChaosOnPlay(Chaos _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        ContinueChaos(context, nextIndex: 0);
    }

    public static void ChillOnPlay(Chill card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        context.Simulator.OrbChannel<FrostOrb>(card.Owner, context.State.HittableEnemies.Count);
    }

    public static void ColdSnapOnPlay(ColdSnap _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        context.AttackSingle();
        ContinueOrQueueTail(context, OrbCardTailKind.ColdSnapChannel);
    }

    public static void ConsumingShadowOnPlay(ConsumingShadow card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        context.Simulator.OrbChannel<DarkOrb>(card.Owner, card.DynamicVars.Repeat.IntValue);
        ContinueOrQueueTail(context, OrbCardTailKind.ConsumingShadowPower);
    }

    public static void CoolheadedOnPlay(Coolheaded card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        context.Simulator.OrbChannel<FrostOrb>(card.Owner);
        ContinueOrQueueTail(context, OrbCardTailKind.CoolheadedDraw);
    }

    public static void DarknessOnPlay(Darkness card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        context.Simulator.OrbChannel<DarkOrb>(card.Owner);
        ContinueOrQueueTail(context, OrbCardTailKind.DarknessPassives);
    }

    public static void DualcastOnPlay(Dualcast card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        context.Simulator.OrbEvokeNext(card.Owner, repeat: 2);
    }

    public static void FusionOnPlay(Fusion card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        context.Simulator.OrbChannel<PlasmaOrb>(card.Owner);
    }

    public static void GlacierOnPlay(Glacier card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        context.GainBlock(card.Owner.Creature);
        ContinueOrQueueTail(context, OrbCardTailKind.GlacierChannels);
    }

    public static void GlassworkOnPlay(Glasswork card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        context.GainBlock(card.Owner.Creature);
        ContinueOrQueueTail(context, OrbCardTailKind.GlassworkChannel);
    }

    public static void IceLanceOnPlay(IceLance _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        context.AttackSingle();
        ContinueOrQueueTail(context, OrbCardTailKind.IceLanceChannels);
    }

    public static void IgnitionOnPlay(Ignition _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        context.Simulator.OrbChannel<PlasmaOrb>(context.TargetPlayer);
    }

    public static void MeteorStrikeOnPlay(MeteorStrike _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        context.AttackSingle();
        ContinueOrQueueTail(context, OrbCardTailKind.MeteorStrikeChannels);
    }

    public static void MultiCastOnPlay(MultiCast card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        var repeat = context.Card.ResolveEnergyXValue(context.State) + (card.IsUpgraded ? 1 : 0);
        context.Simulator.OrbEvokeNext(card.Owner, repeat);
    }

    public static void NullOnPlay(Null _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        context.AttackSingle();
        ContinueOrQueueTail(context, OrbCardTailKind.NullWeakThenDark);
    }

    public static void QuadcastOnPlay(Quadcast card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        context.Simulator.OrbEvokeNext(card.Owner, repeat: card.DynamicVars.Repeat.IntValue);
    }

    public static void RainbowOnPlay(Rainbow card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        context.Simulator.OrbChannel<LightningOrb>(card.Owner);
        ContinueOrQueueTail(context, OrbCardTailKind.RainbowFrostThenDark);
    }

    public static void RefractOnPlay(Refract _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        context.AttackSingle(hitCount: 2);
        ContinueOrQueueTail(context, OrbCardTailKind.RefractChannels);
    }

    public static void ShadowShieldOnPlay(ShadowShield card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        context.GainBlock(card.Owner.Creature);
        ContinueOrQueueTail(context, OrbCardTailKind.ShadowShieldChannel);
    }

    public static void ShatterOnPlay(Shatter _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        context.AttackAllOpponents();
        ContinueOrQueueTail(context, OrbCardTailKind.ShatterEvokes);
    }

    public static void SpinnerOnPlay(Spinner card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        if (card.IsUpgraded)
            context.Simulator.OrbChannel<GlassOrb>(card.Owner);
    }

    public static void TempestOnPlay(Tempest card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        var count = context.Card.ResolveEnergyXValue(context.State) + (card.IsUpgraded ? 1 : 0);
        context.Simulator.OrbChannel<LightningOrb>(card.Owner, count);
    }

    public static void TeslaCoilOnPlay(TeslaCoil _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        context.AttackSingle();
        ContinueOrQueueTail(context, OrbCardTailKind.TeslaPassives);
    }

    public static void VoltaicOnPlay(Voltaic card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        SimulatedCombatState combat = context.CombatState as SimulatedCombatState
            ?? throw new InvalidOperationException("Voltaic requires frozen branch combat history.");
        int count = combat.GetLightningChannelsForCalculatedVar(context.Simulator, card.Owner);
        context.Simulator.OrbChannel<LightningOrb>(card.Owner, count);
    }

    public static void ZapOnPlay(Zap card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        context.Simulator.OrbChannel<LightningOrb>(card.Owner);
    }
}
