using CombatSolver;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace U0U1PinnedHarness;

internal static class CardChoiceIdentityChecks
{
    private static string CheckEnumeration(Player player, SolverDisplayNames names, string outputDirectory)
    {
        PredictedCard[] cards = Enumerable.Range(0, 20).Select(index =>
            PredictedCard.Create(index % 2 == 0 ? CanonicalModels.Card<StrikeIronclad>()
                : CanonicalModels.Card<DefendIronclad>(), player)).ToArray();
        for (int index = 0; index < cards.Length; index += 3) cards[index].Upgrade();
        for (int index = 1; index < cards.Length; index += 5) cards[index].SetToFreeThisTurn();
        List<CardChoiceSpec> specs = [];
        foreach (PlanChoiceEffect effect in new[] { PlanChoiceEffect.Discard, PlanChoiceEffect.Exhaust,
                     PlanChoiceEffect.Upgrade, PlanChoiceEffect.Transform, PlanChoiceEffect.Duplicate })
        foreach (bool repeated in new[] { false, true })
        foreach ((int minimum, int maximum) in new[] { (1, 1), (2, 2), (0, 5), (17, 17) })
        {
            // Unique state keys exercise the no-representative path; ordinary duplicate
            // physical cards and repeated references exercise both occurrence paths.
            PredictedCard[] options = repeated ? cards.Concat(cards[..3]).ToArray()
                : cards.Select((card, index) => {
                    PredictedCard copy = card.Fork(new PredictionForkContext());
                    copy.MutablePreview.EnergyCost.SetCustomBaseCost(index + 1);
                    return copy;
                }).ToArray();
            specs.Add(new(effect, PileType.Hand, minimum, maximum, options,
                options.Reverse().ToArray(), 7, ContextId: "pinned-choice-enumeration"));
        }
        List<string> serialized = [];
        int choices = 0;
        foreach (CardChoiceSpec spec in specs)
        {
            IReadOnlyList<PlanCardChoice> result = CardChoiceSupport.BuildChoices(spec, names, 42, 54);
            foreach (PlanCardChoice choice in result)
            {
                if (choice.Cards.Count < spec.MinCount || choice.Cards.Count > spec.MaxCount)
                    throw new InvalidOperationException("Choice cardinality escaped its request.");
                foreach (PlanCardToken token in choice.Cards)
                {
                    PredictedCard[] source = spec.SourceCards.Where(card => CardChoiceSupport.MatchesToken(card, token)).ToArray();
                    PredictedCard[] options = spec.Options.Where(card => CardChoiceSupport.MatchesToken(card, token)).ToArray();
                    if (token.SourceOccurrence >= source.Length || token.OptionOccurrence >= options.Length
                        || !ReferenceEquals(source[token.SourceOccurrence], options[token.OptionOccurrence]))
                        throw new InvalidOperationException("Choice token lost its exact physical source/option occurrence.");
                }
            }
            choices += result.Count;
            serialized.Add(JsonSerializer.Serialize(result));
        }
        string snapshot = string.Join('\n', serialized);
        File.WriteAllText(Path.Combine(outputDirectory, "choice-enumeration.jsonl"), snapshot);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
        for (int warm = 0; warm < 3; warm++)
            foreach (CardChoiceSpec spec in specs) CardChoiceSupport.BuildChoices(spec, names, 42, 54);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int repeat = 0; repeat < 20; repeat++)
            foreach (CardChoiceSpec spec in specs) CardChoiceSupport.BuildChoices(spec, names, 42, 54);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        return $"enumeration_cases={specs.Count} choices={choices} order_sha256={hash} allocated_bytes={allocated}";
    }

    internal static string Run(Player player, SolverDisplayNames names, string outputDirectory)
    {
        int comparisons = 0;
        PredictedCard NewCard() => PredictedCard.Create(CanonicalModels.Card<StrikeIronclad>(), player);
        // Exercise the actual continuation card writer, including both live and predicted inputs.
        MethodInfo appendContinuation = typeof(ContinuationStamp).GetMethod("AppendCard",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        string Continuation(PredictedCard card, bool live)
        {
            StringBuilder text = new();
            appendContinuation.Invoke(null, [text, card.Preview, live]);
            return text.ToString();
        }
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Card cost identity: " + message);
        }
        void Compare(PredictedCard left, PredictedCard right, bool equal, string label)
        {
            Check((CombatBeamSolver.CaptureCardStateFingerprintForTesting(left)
                == CombatBeamSolver.CaptureCardStateFingerprintForTesting(right)) == equal, label + " fingerprint");
            Check((CardChoiceSupport.ChoiceCardKey(left) == CardChoiceSupport.ChoiceCardKey(right)) == equal,
                label + " choice key");
            Check((Continuation(left, true) == Continuation(right, false)) == equal, label + " continuation");
            if (!equal)
            {
                string difference = new ContinuationStamp("H=" + Continuation(left, true))
                    .DescribeFirstDifference(new ContinuationStamp("H=" + Continuation(right, false)));
                Check(difference.StartsWith("field=H[0]", StringComparison.Ordinal),
                    label + " continuation field boundaries");
            }
            CardChoiceSpec spec = new(PlanChoiceEffect.Discard, PileType.Hand, 1, 1,
                [left, right], [left, right], 0);
            Check(CardChoiceSupport.BuildChoices(spec, names, 8, 8).Count == (equal ? 1 : 2),
                label + " selection branches");
            comparisons++;
        }

        PredictedCard plain = NewCard(), other = NewCard();
        Compare(plain, other, true, "ordinary duplicates");
        StateFingerprint plainFingerprint = CombatBeamSolver.CaptureCardStateFingerprintForTesting(plain);
        string plainChoice = CardChoiceSupport.ChoiceCardKey(plain);
        string plainContinuation = Continuation(plain, false);

        PredictedCard turn = NewCard().SetToFreeThisTurn();
        PredictedCard combat = NewCard().SetToFreeThisCombat();
        Check(turn.Preview.EnergyCost.GetWithModifiers(CostModifiers.Local)
            == combat.Preview.EnergyCost.GetWithModifiers(CostModifiers.Local), "fixture equal displayed costs");
        Compare(turn, combat, false, "turn versus combat duration");
        Compare(turn, NewCard().SetToFreeThisTurn(), true, "same duration still merges");
        PredictedCard fork = turn.Fork(new PredictionForkContext());
        Compare(turn, fork, true, "fork preserves modifiers");
        fork.MutablePreview.EndOfTurnCleanup();
        Compare(turn, fork, false, "fork cleanup is isolated");
        Check(turn.Preview.EnergyCost.GetWithModifiers(CostModifiers.Local) == 0, "parent cost unchanged");
        turn.MutablePreview.EndOfTurnCleanup();
        combat.MutablePreview.EndOfTurnCleanup();
        Check(turn.Preview.EnergyCost.GetWithModifiers(CostModifiers.Local) == 1
            && combat.Preview.EnergyCost.GetWithModifiers(CostModifiers.Local) == 0, "native turn cleanup");
        Check(CombatBeamSolver.CaptureCardStateFingerprintForTesting(turn) == plainFingerprint
            && CardChoiceSupport.ChoiceCardKey(turn) == plainChoice
            && Continuation(turn, false) == plainContinuation, "cleanup restores unmodified identity");

        PredictedCard untilPlay = NewCard(), untilTurn = NewCard();
        untilPlay.MutablePreview.EnergyCost.SetUntilPlayed(0);
        untilTurn.MutablePreview.EnergyCost.SetThisTurn(0);
        Compare(untilPlay, untilTurn, false, "play versus turn expiry");
        untilPlay.MutablePreview.EnergyCost.AfterCardPlayedCleanup();
        untilTurn.MutablePreview.EnergyCost.AfterCardPlayedCleanup();
        Check(untilPlay.Preview.EnergyCost.GetWithModifiers(CostModifiers.Local) == 1
            && untilTurn.Preview.EnergyCost.GetWithModifiers(CostModifiers.Local) == 0, "native play cleanup");

        PredictedCard hiddenBase = NewCard().SetToFreeThisTurn();
        hiddenBase.MutablePreview.EnergyCost.SetCustomBaseCost(2);
        Compare(NewCard().SetToFreeThisTurn(), hiddenBase, false, "hidden base cost");

        PredictedCard layered = NewCard(), temporary = NewCard();
        layered.MutablePreview.EnergyCost.SetThisCombat(2);
        layered.MutablePreview.EnergyCost.SetThisTurn(0);
        temporary.MutablePreview.EnergyCost.SetThisTurn(0);
        Compare(layered, temporary, false, "hidden permanent layer");
        layered.MutablePreview.EndOfTurnCleanup();
        temporary.MutablePreview.EndOfTurnCleanup();
        Check(layered.Preview.EnergyCost.GetWithModifiers(CostModifiers.Local) == 2
            && temporary.Preview.EnergyCost.GetWithModifiers(CostModifiers.Local) == 1, "uncovered permanent layer");

        PredictedCard starTurn = NewCard(), starCombat = NewCard();
        starTurn.MutablePreview.BaseStarCost = 2;
        starCombat.MutablePreview.BaseStarCost = 2;
        starTurn.MutablePreview.SetStarCostThisTurn(0);
        starCombat.MutablePreview.SetStarCostThisCombat(0);
        Compare(starTurn, starCombat, false, "star duration");
        starTurn.MutablePreview.EndOfTurnCleanup();
        starCombat.MutablePreview.EndOfTurnCleanup();
        Check(starTurn.Preview.CurrentStarCost == 2 && starCombat.Preview.CurrentStarCost == 0,
            "native star cleanup");

        PredictedCard first = NewCard(), second = NewCard();
        first.MutablePreview.EnergyCost.AddThisCombat(1);
        first.MutablePreview.EnergyCost.SetThisTurn(0);
        second.MutablePreview.EnergyCost.SetThisTurn(0);
        second.MutablePreview.EnergyCost.AddThisCombat(1);
        Compare(first, second, false, "modifier order");
        PredictedCard reduceOnly = NewCard(), absolute = NewCard();
        reduceOnly.MutablePreview.EnergyCost.SetThisCombat(0, reduceOnly: true);
        absolute.MutablePreview.EnergyCost.SetThisCombat(0, reduceOnly: false);
        Compare(reduceOnly, absolute, false, "reduce-only semantics");
        string enumeration = CheckEnumeration(player, names, outputDirectory);
        return $"CardChoiceIdentity PASS comparisons={comparisons} native_cleanup=turn+play+stars fork_isolation=true {enumeration}";
    }
}
