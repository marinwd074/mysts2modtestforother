using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    private bool ContinueGeneratedCardBatch(
        List<PredictedCard> cards,
        PileType newPileType,
        Player? creator,
        CardPilePosition position,
        CardGenerationResultKind resultKind,
        int nextIndex,
        CombatPredictionCardGeneratedEntry? pendingEntry,
        PredictedCard? pendingCard,
        List<SimCardPileAddResult>? results = null)
    {
        if (pendingEntry is not null)
            History.CardGenerationResolved(pendingEntry, pendingCard!);

        for (int index = nextIndex; index < cards.Count; index++)
        {
            PredictedCard card = cards[index];
            CombatPredictionCardGeneratedEntry entry = History.CardGenerated(card, creator, resultKind);
            results?.Add(AddToPile(card, newPileType, position));

            HookMirrors.AfterCardGeneratedForCombat(this, card, creator);
            if (HasPendingChoice)
            {
                AppendExecutionContinuation(
                    new GeneratedCardBatchExecutionFrame(
                        cards,
                        newPileType,
                        creator,
                        position,
                        resultKind,
                        index + 1,
                        entry,
                        card));
                return false;
            }

            History.CardGenerationResolved(entry, card);
        }

        return true;
    }

    private sealed record GeneratedCardBatchExecutionFrame(
        List<PredictedCard> Cards,
        PileType NewPileType,
        Player? Creator,
        CardPilePosition Position,
        CardGenerationResultKind ResultKind,
        int NextIndex,
        CombatPredictionCardGeneratedEntry PendingEntry,
        PredictedCard PendingCard) : ICombatPredictionExecutionFrame
    {
        public IEnumerable<CombatPredictionHistoryEntry> DeferredEntries => [PendingEntry];

        public void PrepareFork(PredictionForkContext context)
            => ForkExecutionCardList(Cards, context);

        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with
            {
                Cards = context.RequireRemap(Cards),
                PendingEntry = context.RequireRemap(PendingEntry),
                PendingCard = context.RequireRemap(PendingCard)
            };

        public bool Resume(CombatPredictionSimulator simulator)
            => simulator.ContinueGeneratedCardBatch(
                Cards,
                NewPileType,
                Creator,
                Position,
                ResultKind,
                NextIndex,
                PendingEntry,
                PendingCard);
    }
}
