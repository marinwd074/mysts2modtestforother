using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static partial class CardDrawCardMirrors
{
    public static void AdrenalineOnPlay(Adrenaline _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        _ = ContinueCardDrawSequence(context, CardDrawSequence.Adrenaline);
    }

    public static void OfferingOnPlay(Offering _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        _ = ContinueCardDrawSequence(context, CardDrawSequence.Offering);
    }

    public static void NeurosurgeOnPlay(Neurosurge _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        _ = ContinueCardDrawSequence(context, CardDrawSequence.Neurosurge);
    }

    public static void SpoilsOfBattleOnPlay(SpoilsOfBattle _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        _ = ContinueCardDrawSequence(context, CardDrawSequence.SpoilsOfBattle);
    }

    public static void CompileDriverOnPlay(CompileDriver _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        _ = ContinueCardDrawSequence(context, CardDrawSequence.CompileDriver);
    }

    public static void CalculatedGambleOnPlay(CalculatedGamble card, CardOnPlayMirrorContext context)
    {
        var cards = context.OwnerState.Hand.Cards.ToArray();
        context.Simulator.DiscardAndDraw(cards, cards.Length);
    }

#if !STS2_01071
    public static void ConstellationOnPlay(Constellation card, CardOnPlayMirrorContext context)
    {
        var player = context.TargetPlayer;
        context.Simulator.Draw(player, card.DynamicVars.Cards.BaseValue);
        if (context.Simulator.HasPendingChoice)
            return;
        context.Simulator.GainEnergy(player, card.DynamicVars.Energy.IntValue);
        if (context.Simulator.HasPendingChoice)
            return;
        context.GainBlock(player.Creature);
    }
#endif

    public static void EscapePlanOnPlay(EscapePlan _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        _ = ContinueCardDrawSequence(context, CardDrawSequence.EscapePlan);
    }

    public static void ExpertiseOnPlay(Expertise card, CardOnPlayMirrorContext context)
    {
        decimal drawCount = Math.Max(
            0m,
            card.DynamicVars.Cards.BaseValue - context.OwnerState.Hand.Cards.Count);
        context.Simulator.Draw(card.Owner, drawCount);
    }

    public static void FetchOnPlay(Fetch _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        _ = ContinueCardDrawSequence(context, CardDrawSequence.Fetch);
    }

    public static void FtlOnPlay(Ftl _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        _ = ContinueCardDrawSequence(context, CardDrawSequence.Ftl);
    }

    public static void HuddleUpOnPlay(HuddleUp card, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        var allies = context.State.GetTeammatesOf(card.Owner.Creature)
            .Where(creature => creature.IsPlayer && context.State.GetCreature(creature).IsAlive)
            .Select(creature => creature.Player!)
            .ToArray();
        _ = ContinueCardDrawSequence(
            context,
            CardDrawSequence.HuddleUp,
            players: allies);
    }

    public static void ImpatienceOnPlay(Impatience card, CardOnPlayMirrorContext context)
    {
        if (context.OwnerState.Hand.Cards.All(predicted => predicted.Preview.Type != CardType.Attack))
        {
            context.Simulator.Draw(card.Owner, card.DynamicVars.Cards.BaseValue);
        }
    }

    public static void PillageOnPlay(Pillage _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        _ = ContinueCardDrawSequence(context, CardDrawSequence.Pillage);
    }

    public static void RebootOnPlay(Reboot _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        _ = ContinueCardDrawSequence(context, CardDrawSequence.Reboot);
    }

    public static void RestlessnessOnPlay(Restlessness _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        _ = ContinueCardDrawSequence(context, CardDrawSequence.Restlessness);
    }

    public static void ScrapeOnPlay(Scrape _, CardOnPlayMirrorContext context)
    {
        context.Simulator.AcknowledgeExecutionDispatch();
        _ = ContinueCardDrawSequence(context, CardDrawSequence.Scrape);
    }

    public static void ScrawlOnPlay(Scrawl card, CardOnPlayMirrorContext context)
    {
        int count = context.Simulator.GetMaxHandSize(card.Owner) - context.OwnerState.Hand.Cards.Count;
        context.Simulator.Draw(card.Owner, count);
    }

}
