using System.Reflection;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.ValueProps;

namespace U0U1PinnedHarness;

internal static class UpstreamPortChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Upstream port: " + message);
    }

    internal static string Run(CombatState live, SolverDisplayNames names, string outputDirectory)
    {
        Player player = LocalContext.GetMe(live)!;
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        CombatPredictionSimulator parent = root.ForkSimulator();
        List<string> passed = [];
        using var isolation = SimulationNotificationIsolation.Enter();
        PredictedCard New<T>() where T : CardModel => PredictedCard.Create(CanonicalModels.Card<T>(), player);

        PredictedCard original = New<StrikeIronclad>();
        CardModel oldPreview = original.MutablePreview;
        PredictedCard[] forks = Enumerable.Range(0, 32)
            .Select(_ => original.Fork(new PredictionForkContext())).ToArray();
        Parallel.For(0, forks.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, index =>
        {
            using var scope = SimulationNotificationIsolation.Enter();
            forks[index].MutablePreview.EnergyCost.SetCustomBaseCost(index + 2);
            Check(forks[index].References(oldPreview) && original.References(forks[index].Preview),
                "COW lost the identity in an earlier hook snapshot.");
            PredictedCard generated = forks[index].CreateClone();
            Check(!original.References(generated.Preview) && !generated.References(oldPreview),
                "A gameplay clone reused its source's combat-card identity.");
        });
        Check(original.Preview.EnergyCost.GetWithModifiers(CostModifiers.Local) == 1,
            "Parallel COW mutated the parent.");
        passed.Add("card_identity:32_parallel_forks:gameplay_clone_isolation");

        var variables = New<StrikeIronclad>().Preview.DynamicVars;
        var expectedVars = variables.ToArray();
        try
        {
            foreach (bool fallback in new[] { false, true })
            {
                DynamicVarSetAccess.ForcePublicFallback = fallback;
                var actual = new List<KeyValuePair<string, MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVar>>();
                foreach (var entry in new DynamicVarSetAccess.EntryEnumerable(variables)) actual.Add(entry);
                Check(actual.SequenceEqual(expectedVars), "DynamicVar field/fallback enumeration differs.");
                Check(DynamicVarSetAccess.Keys(variables).SequenceEqual(expectedVars.Select(entry => entry.Key))
                    && DynamicVarSetAccess.Values(variables).SequenceEqual(expectedVars.Select(entry => entry.Value)),
                    "DynamicVar keys or values differ from the native public view.");
            }
        }
        finally { DynamicVarSetAccess.ForcePublicFallback = false; }
        passed.Add("dynamic_variables:field_and_public_fallback");

        PredictedCard star = New<StrikeIronclad>();
        star.MutablePreview.BaseStarCost = 2;
        star.MutablePreview.SetStarCostThisTurn(0);
        Check(PredictionUtils.NeedsEndOfTurnCleanup(star.Preview), "Temporary star cost skipped native cleanup.");
        star.MutablePreview.EndOfTurnCleanup();
        Check(star.Preview.GetStarCostWithModifiers() == 2
            && !PredictionUtils.NeedsEndOfTurnCleanup(star.Preview), "Native star-cost cleanup was not preserved.");
        passed.Add("temporary_star_cost:native_turn_cleanup");

        PredictedCard[] routing = [New<StrikeIronclad>(), New<StrikeIronclad>(),
            New<DefendIronclad>(), New<StrikeIronclad>()];
        foreach (PileType pile in new[] { PileType.Draw, PileType.Discard, PileType.Exhaust })
        foreach (PlanChoiceEffect effect in new[] { PlanChoiceEffect.MoveToHand, PlanChoiceEffect.MoveToDrawTop,
                     PlanChoiceEffect.MoveToHandFreeThisTurn })
        {
            CardChoiceSpec spec = new(effect, pile, 1, 1, routing, routing, 0,
                PreserveOrderedRoutingResidues: true);
            var choices = CardChoiceSupport.BuildChoices(spec, names, 1, 1);
            Check(choices.Count == 3, "Separated equal-card runs lost a routing branch.");
            Check(choices.SelectMany(choice => choice.Cards).Where(token => token.CardId == "STRIKE_IRONCLAD")
                .Select(token => token.SourceOccurrence).Order().SequenceEqual([0, 2]),
                "Routing branches do not identify the distinct residual piles.");
            Check(CardChoiceSupport.BuildChoices(spec with { PreserveOrderedRoutingResidues = false }, names, 1, 1).Count == 2,
                "Ordinary routing changed its semantic representatives.");
        }
        passed.Add("ordered_routing:9_pile_effect_combinations");

        for (int attempt = 0; attempt < 12; attempt++)
        {
            CombatPredictionSimulator sim = parent.Fork();
            var state = sim.State.GetPlayerCombatState(player);
            state.DrawPile.Clear();
            foreach (var card in routing) state.DrawPile.Add(card.Fork(new PredictionForkContext()));
            for (int advance = 0; advance < attempt; advance++) sim.Rng.CombatCardSelection.NextInt(100);
            CardChoiceSpec spec = CardChoiceSupport.GetSpec(sim, New<SeekerStrike>())!;
            Check(spec.Options.SequenceEqual(state.DrawPile.Cards.Where(spec.Options.Contains)),
                "Seeker displayed random sample order instead of native pile order.");
        }
        passed.Add("seeker:12_rng_states:native_filtered_pile_order");

        CombatPredictionSimulator transformed = parent.Fork();
        var hand = transformed.State.GetPlayerCombatState(player).Hand;
        hand.Clear();
        PredictedCard wound = New<Wound>(), burn = New<Burn>();
        hand.Add(wound); hand.Add(New<StrikeIronclad>()); hand.Add(burn); hand.Add(New<DefendIronclad>());
        Check(CardChoiceSupport.TransformCardBatch(transformed, [wound, burn], CanonicalModels.Card<Fuel>(), false),
            "Batch transform suspended unexpectedly.");
        Check(hand.Cards.Select(card => card.Preview.Id.Entry).SequenceEqual(
            ["FUEL", "FUEL", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD"]),
            "Batch transform did not remove all originals before insertion.");
        Check(parent.State.GetPlayerCombatState(player).Hand.Cards.All(card => card.Preview is not Fuel),
            "Batch transform mutated the parent.");
        {
            var fuelState = transformed.State.GetPlayerCombatState(player);
            fuelState.DrawPile.Clear();
            fuelState.DiscardPile.Clear();
            fuelState.DrawPile.Add(New<MegaCrit.Sts2.Core.Models.Cards.Void>());
            fuelState.DrawPile.Add(New<StrikeIronclad>());
            fuelState.DrawPile.Add(New<DefendIronclad>());
            fuelState.DrawPile.Add(New<Bash>());
            fuelState.LoseEnergy(fuelState.Energy);
            PredictedCard baseFuel = hand.Cards[0];
            PredictedCard upgradedFuel = hand.Cards[1];
            upgradedFuel.Upgrade();
            Check(transformed.ManualPlay(baseFuel, target: null, out _),
                "Base Fuel play suspended unexpectedly.");
            Check(fuelState.Energy == 0,
                $"Base Fuel must gain 1 energy before Void draw drains 1 energy, but energy was {fuelState.Energy}.");
            Check(hand.Cards.Select(card => card.Preview.Id.Entry).SequenceEqual(
                ["FUEL", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "VOID"]),
                $"Base Fuel must draw exactly 1 card, but hand was {string.Join(',', hand.Cards.Select(card => card.Preview.Id.Entry))}.");
            Check(transformed.ManualPlay(upgradedFuel, target: null, out _),
                "Upgraded Fuel play suspended unexpectedly.");
            Check(fuelState.Energy == 1,
                $"Upgraded Fuel must gain 1 energy, but energy was {fuelState.Energy}.");
            Check(hand.Cards.Select(card => card.Preview.Id.Entry).SequenceEqual(
                ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "VOID", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD"]),
                $"Upgraded Fuel must draw exactly 2 cards, but hand was {string.Join(',', hand.Cards.Select(card => card.Preview.Id.Entry))}.");
            Check(fuelState.DrawPile.Cards.Select(card => card.Preview.Id.Entry).SequenceEqual(["BASH"]),
                "Upgraded Fuel consumed the wrong number of draw-pile cards.");
        }
        passed.Add("batch_transform:shrinking_pile_positions:parent_isolation:fuel_single_draw");

        CombatPredictionSimulator scoped = parent.Fork();
        using (scoped.BeginCardOrPotionEffect(player))
        {
            Check(scoped.IsExecutingCardOrPotionEffect(player), "Effect ownership was not recorded.");
            using (scoped.BeginCardOrPotionEffect(player))
                Check(scoped.IsExecutingCardOrPotionEffect(player), "Nested effect ownership was lost.");
            try { scoped.Fork(); throw new Exception("An active effect was forked."); }
            catch (InvalidOperationException error) when (error.Message ==
                "Combat prediction cannot be forked during a card or potion effect.") { }
        }
        Check(!scoped.IsExecutingCardOrPotionEffect(player), "Effect ownership leaked after unwind.");
        _ = scoped.Fork();
        passed.Add("effect_scopes:nesting:fork_boundary:unwind");

        List<CardModel> collection = [oldPreview];
        var guard = LiveCollectionGuard.BeginWindow(("fixture.cards", collection));
        collection[0] = oldPreview; // Same count and identity, but the capture window was written.
        try { guard.Verify(); throw new Exception("A rewritten capture collection was accepted."); }
        catch (LiveCollectionModifiedException error) when (error.CollectionName == "fixture.cards") { }
        Check(LiveCollectionGuard.SnapshotStable(collection, "fixture.cards").Single() == oldPreview,
            "Stable collection snapshot changed an instance.");
        passed.Add("live_collection:same_count_write_rejected:stable_snapshot");

        FieldInfo limits = typeof(SimulatedCombatState).GetField("_rootMaxHandSizes", BindingFlags.Instance | BindingFlags.NonPublic)!;
        CombatPredictionSimulator resized = parent.Fork();
        var resizedCombat = (SimulatedCombatState)resized.State.CombatState;
        var originalLimits = (IReadOnlyDictionary<Player, int>)limits.GetValue(resizedCombat)!;
        var changedLimits = originalLimits.ToDictionary(entry => entry.Key, entry => entry.Value);
        changedLimits[player]++;
        StateFingerprint Fingerprint(CombatPredictionSimulator sim)
        {
            StateFingerprintBuilder key = new();
            ((SimulatedCombatState)sim.State.CombatState).AppendFingerprint(ref key, sim);
            return key.Finish();
        }
        StateFingerprint before = Fingerprint(resized);
        limits.SetValue(resizedCombat, changedLimits);
        Check(Fingerprint(resized) != before && Fingerprint(parent) == before,
            "Hand limits did not distinguish branch identity or leaked to the parent.");
        string stampBefore = ContinuationStamp.CapturePredicted(player, parent, root.StartTurnNumber,
            root.Forecast, root.StartTurnNumber).StateText;
        string stampAfter = ContinuationStamp.CapturePredicted(player, resized, root.StartTurnNumber,
            root.Forecast, root.StartTurnNumber).StateText;
        Check(new ContinuationStamp(stampBefore).DescribeFirstDifference(new(stampAfter)).StartsWith("field=max_hand_size"),
            "Continuation accepted different hand limits.");
        passed.Add("captured_hand_limits:fingerprint:continuation:fork_isolation");

        var differences = new ContinuationStamp("H=a,b,c").DescribeDifferences(new("H=x,y,z"), 2);
        Check(differences.Count == 2 && differences[1].StartsWith("field=H[1]"),
            "Detailed pile diagnostics ignored the caller's difference limit.");
        passed.Add("continuation_diagnostics:multiple_items:bounded_output");

        // Resolve the exact pinned native UI fields before relying on the async selection path.
        var nativeScreen = typeof(NChooseACardSelectionScreen);
        FieldInfo NativeField(string name) => HarmonyLib.AccessTools.Field(nativeScreen, name)
            ?? throw new MissingFieldException(nativeScreen.FullName, name);
        Check(typeof(IReadOnlyList<CardModel>).IsAssignableFrom(NativeField("_cards").FieldType)
            && NativeField("_openedTicks").FieldType == typeof(ulong)
            && NativeField("_completionSource").FieldType == typeof(TaskCompletionSource<IEnumerable<CardModel>>),
            "The 0.107.1 ChooseCard surface has a different native contract.");
        passed.Add("choose_card:0.107.1_native_field_shapes_only");

        foreach (int amount in new[] { 1, 5, 30, 95, 96, 100 })
        {
            CombatPredictionSimulator sim = parent.Fork();
            var combat = (SimulatedCombatState)sim.State.CombatState;
            combat.Apply<ShadowmeldPower>(player.Creature, amount, player.Creature);
            decimal result = HookMirrors.ModifyBlock(sim, player.Creature, 4m, ValueProp.Move, null, null, out _);
            // At 95 stacks the multiplication overflows even though the factor is representable.
            decimal expected = amount >= 95 ? SimCreatureState.BlockSettlementCeiling : 4m * (decimal)Math.Pow(2, amount);
            Check(result == expected, $"Shadowmeld block overflow at {amount} stacks.");
            if (amount < 95)
            {
                ShadowmeldPower nativePower = combat.GetPower<ShadowmeldPower>(player.Creature)!;
                decimal nativeFactor = nativePower.ModifyBlockMultiplicative(
                    player.Creature, 4m, ValueProp.Move, null, null);
                Check(result == 4m * nativeFactor, "Shadowmeld differs from the pinned native multiplier.");
            }
            Check(HookMirrors.ModifyBlock(sim, player.Creature, 0m, ValueProp.Move, null, null, out _) == 0m,
                "Overflowing block modifiers created block from zero.");
        }
        passed.Add("block_saturation:6_stack_counts:zero_block");

        Check(liveBefore == ContinuationStamp.CaptureLive(live).StateText, "Pinned checks changed the live combat.");
        File.WriteAllText(Path.Combine(outputDirectory, "upstream-port.json"), JsonSerializer.Serialize(
            new { game = "0.107.1", upstream = "5c773caaec742bc55588bc9ee21c361326a732f0", passed,
                liveUnchanged = true, nativeUiTimingVerified = false }, new JsonSerializerOptions { WriteIndented = true }));
        return $"UpstreamPort PASS ({passed.Count} groups, pinned 0.107.1; native UI timing unverified)";
    }
}
