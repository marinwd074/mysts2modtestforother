using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static partial class RandomTargetAttackCardMirrors
{
    private static bool ContinueFlakCannon(
        CardOnPlayMirrorContext context,
        List<PredictedCard> statuses,
        int hitCount,
        int nextIndex)
    {
        for (int index = nextIndex; index < statuses.Count; index++)
        {
            context.Simulator.Exhaust(statuses[index]);
            if (!context.Simulator.HasPendingChoice)
                continue;

            context.Simulator.AppendExecutionContinuation(
                new FlakCannonExecutionFrame(
                    context.Card,
                    context.CardPlay,
                    statuses,
                    hitCount,
                    index + 1));
            return false;
        }

        context.AttackRandomOpponents(hitCount);
        return !context.Simulator.HasPendingChoice;
    }

    private sealed record FlakCannonExecutionFrame(
        PredictedCard Card,
        CardPlay Play,
        List<PredictedCard> Statuses,
        int HitCount,
        int NextIndex) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
        {
            CombatPredictionSimulator.PrepareExecutionCardPlay(Card, Play, context);
            _ = CombatPredictionSimulator.ForkExecutionCardList(Statuses, context);
        }

        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with
            {
                Card = context.RequireRemap(Card),
                Play = context.RequireRemap(Play),
                Statuses = context.RequireRemap(Statuses),
            };

        public bool Resume(CombatPredictionSimulator simulator)
            => ContinueFlakCannon(
                new CardOnPlayMirrorContext
                {
                    Simulator = simulator,
                    Card = Card,
                    CardPlay = Play
                },
                Statuses,
                HitCount,
                NextIndex);
    }
}
