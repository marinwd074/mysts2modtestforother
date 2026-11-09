using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Factories;

namespace U0U1PinnedHarness;

internal static class EntropicCostChecks
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Entropic cost: " + message);
        _checks++;
    }

    internal static string Run(CombatState live, SolverDisplayNames names, BattleDamageSnapshot damage,
        SearchPolicySnapshot captured, SolverSearchProfile original, string output)
    {
        var player = LocalContext.GetMe(live)!;
        foreach (PotionModel potion in player.PotionSlots.OfType<PotionModel>().ToArray())
            player.RemovePotionInternal(potion);
        Check(player.AddPotionInternal(ModelDb.Potion<EntropicBrew>().ToMutable(), 0, true).success, "Brew fixture");
        Check(player.AddPotionInternal(ModelDb.Potion<FirePotion>().ToMutable(), 1, true).success, "Paid fixture");
        live.Enemies.Single().SetMaxHpInternal(100);
        live.Enemies.Single().SetCurrentHpInternal(100);
        SolverSearchProfile profile = original with { BeamWidth = 8, MaxExpandedNodes = 1200, SoftTimeBudgetMilliseconds = 20000 };
        SearchPolicySnapshot policy = captured with { Profile = profile, CurrentTurnOnly = false,
            RoutePolicy = SearchRoutePolicy.SinglePlayerFullRoute, VerifyIncrementalSearch = true,
            PotionPolicy = SolverPotionPolicy.Smart, PotionStrategy = new(SolverPotionPolicy.Smart,
                [new(0, "ENTROPIC_BREW", SolverPotionDirective.Force), new(1, "FIRE_POTION", SolverPotionDirective.Disabled)]),
            UseBeamWidthPortfolio = false, UseMultiplayerTeamObjective = false, UseMultiplayerScenarioReevaluation = false };

        // Select a deterministic native RNG state with a searchable generated Fire
        // Potion. The fixture is artificial; production generation is not overridden.
        CombatRootSnapshot root = null!;
        int fireSlot = -1, advances = 0;
        string[] expected = [];
        for (; advances < 256; advances++)
        {
            root = CombatRootSnapshot.Capture(live);
            var oracle = root.ForkSimulator();
            expected = new string[root.PotionSlotCount];
            for (int slot = 0; slot < expected.Length; slot++)
            {
                expected[slot] = slot == 1 ? "FIRE_POTION" : PotionFactory.CreateRandomPotionOutOfCombat(
                    player, oracle.Rng.CombatPotionGeneration).Id.Entry;
                if (slot != 1 && expected[slot] == "FIRE_POTION") fireSlot = slot;
            }
            if (fireSlot >= 0) break;
            live.RunState.Rng.CombatPotionGeneration.NextInt(100);
        }
        Check(fireSlot >= 0, "Could not find deterministic generated Fire fixture");
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        PlanAction brew = new(PlanActionKind.UsePotion, 1, PotionSlot: 0, PotionId: "ENTROPIC_BREW");
        PlanAction fire = new(PlanActionKind.UsePotion, 2, TargetIndex: 0,
            TargetCombatId: live.Enemies.Single().CombatId, PotionSlot: fireSlot, PotionId: "FIRE_POTION");
        var solver = new CombatBeamSolver(root, names, damage, policy, searchProfile: profile);
        SimulationSnapshot afterBrew = solver.ReplayDiagnosticPrefix([brew]);
        try
        {
            var combat = (SimulatedCombatState)afterBrew.Simulator.State.CombatState;
            Check(Enumerable.Range(0, root.PotionSlotCount).Select(slot => combat.GetPotionAtSlot(player, slot)!.Id.Entry)
                .SequenceEqual(expected), "Predicted Brew differs from pinned native factory sequence");
            Check(afterBrew.ExplicitPotionStrategicCost == SolverWeights.PotionMinimumHpSaved, "Brew itself was not charged");
            Check(combat.PotionOriginAtSlot(player, 1) == PotionOrigin.RootInventory, "Brew changed occupied paid slot");
            foreach (int slot in Enumerable.Range(0, root.PotionSlotCount).Where(slot => slot != 1))
                Check(combat.PotionOriginAtSlot(player, slot) == PotionOrigin.EntropicBrew, "Missing generated origin");

            var child = (SimulatedCombatState)afterBrew.Simulator.Fork().State.CombatState;
            child.ConsumePotion(player, fireSlot);
            Check(child.PotionUses.Last().StrategicHpCost == 0 && child.PotionUses.Last().Origin == PotionOrigin.EntropicBrew,
                "Generated use was repriced");
            Check(combat.GetPotionAtSlot(player, fireSlot) != null && combat.PotionUses.Count == 1, "Fork mutated parent");
            Check(child.TryProcurePotion(player, ModelDb.Potion<FirePotion>()), "Ordinary refill failed");
            child.ConsumePotion(player, fireSlot);
            Check(child.PotionUses.Last().StrategicHpCost == SolverWeights.PotionMinimumHpSaved
                && child.PotionUses.Last().Origin == PotionOrigin.Procured, "Free flag leaked into ordinary refill");
            child.ConsumePotion(player, 1);
            Check(child.PotionUses.Last().Origin == PotionOrigin.RootInventory
                && child.PotionUses.Last().StrategicHpCost == SolverWeights.PotionMinimumHpSaved, "Original bottle became free");
            Check(child.TryProcurePotion(player, ModelDb.Potion<FairyInABottle>(), true), "Generated automatic fixture");
            int fairySlot = Enumerable.Range(0, root.PotionSlotCount).First(slot => child.GetPotionAtSlot(player, slot) is FairyInABottle);
            child.ConsumePotion(child.GetPotionAtSlot(player, fairySlot)!);
            Check(child.PotionUses.Last() is { Automatic: true, StrategicHpCost: 0, Origin: PotionOrigin.EntropicBrew },
                "Generated automatic bottle was charged");
        }
        finally { afterBrew.ReleaseSimulator(); }

        // Equal gameplay inventories with distinct provenance must not share a key.
        PlanAction defend = new(PlanActionKind.PlayCard, 1, "DEFEND_IRONCLAD");
        // Synthetic pre-parent ledger writes have no PlanAction prefix to replay.
        // Full native Brew requests below keep incremental verification enabled.
        var syntheticPolicy = policy with { VerifyIncrementalSearch = false };
        SimulationSnapshot Refilled(bool free) => new CombatBeamSolver(root, names, damage, syntheticPolicy, searchProfile: profile)
            .ReplayDiagnosticActionWithR0Memo(defend, prepareParent: sim => {
                var combat = (SimulatedCombatState)sim.State.CombatState;
                combat.ConsumePotion(player, 0);
                Check(combat.TryProcurePotion(player, ModelDb.Potion<EntropicBrew>(), free), "Refill fixture");
            });
        SimulationSnapshot paid = Refilled(false), generated = Refilled(true);
        try { Check(paid.StateKey != generated.StateKey
                && solver.CaptureDiagnosticContinuation(paid) == solver.CaptureDiagnosticContinuation(generated),
            "Provenance keys must differ while native gameplay state stays equal"); }
        finally { paid.ReleaseSimulator(); generated.ReleaseSimulator(); }

        // A second Brew is itself generated: charge the original exactly once.
        SimulationSnapshot chained = new CombatBeamSolver(root, names, damage, syntheticPolicy, searchProfile: profile)
            .ReplayDiagnosticActionWithR0Memo(brew, prepareParent: sim => {
                var combat = (SimulatedCombatState)sim.State.CombatState;
                combat.ConsumePotion(player, 0);
                Check(combat.TryProcurePotion(player, ModelDb.Potion<EntropicBrew>(), true), "Chain fixture");
            });
        try
        {
            Check(chained.PotionUseCount == 2 && chained.ExplicitPotionStrategicCost == SolverWeights.PotionMinimumHpSaved
                && chained.LastExplicitPotionOrigin == PotionOrigin.EntropicBrew, "Chained Brew cost was duplicated");
            var uses = ((SimulatedCombatState)chained.Simulator.State.CombatState).PotionUses;
            Check(!policy.PotionStrategy.EvaluateForcedUses(uses.Skip(1)).AllForcedUsesSatisfied,
                "A generated Brew in the same slot impersonated the required root Brew");
        }
        finally { chained.ReleaseSimulator(); }

        // Ambergris is not a native 0.107.1 model. Its string-based policy is
        // covered by PotionStrategyChecks; do not fabricate a native model here.
        Check(!ModelDb.AllPotions.Any(p => p.Id.Entry == "AMBERGRIS"), "Pinned Ambergris availability changed");

        Check(CombatSearchCoordinator.MaximumSmartPotionUses(root, policy, true, 0) == root.PotionSlotCount,
            "Forced Brew's free follow-up actions were excluded from Smart layers");
        var optional = policy with { PotionStrategy = new(SolverPotionPolicy.Smart, [new(1, "FIRE_POTION", SolverPotionDirective.Disabled)]) };
        Check(CombatSearchCoordinator.MaximumSmartPotionUses(root, optional, false, 0) == 1 + root.PotionSlotCount,
            "Optional Brew admission omitted generated capacity");
        Check(CombatSearchCoordinator.MaximumSmartPotionUses(root, optional, true, 0) == 0,
            "Unaffordable optional Brew admitted free follow-ups without paying for Brew");

        // Full search/materialization, followed by an independent complete replay.
        PlanAction[] prefix = [brew, fire with { Turn = 1 }];
        SolverResult result = new CombatBeamSolver(root, names, damage, policy, searchProfile: profile,
            fixedPrefixActions: prefix, maximumPotionUses: 2, minimumPotionUses: 2).Solve();
        Check(result.BoundaryReason != SearchBoundaryReason.TimeLimit, "Fixed fixture timed out");
        Check(result.BestNode.Actions.Take(2).Select(a => (a.Kind, a.Turn, a.PotionSlot))
            .SequenceEqual(prefix.Select(a => (a.Kind, a.Turn, a.PotionSlot))), "Fixed prefix was not retained");
        Check(result.PotionUses.Count == 2 && result.PotionUses[0].StrategicHpCost == SolverWeights.PotionMinimumHpSaved
            && result.PotionUses[1] is { StrategicHpCost: 0, Turn: 1, Origin: PotionOrigin.EntropicBrew }, "Materialized ledger lost source/cost/turn");
        Check(result.PotionUses is ICollection<PredictedPotionUse> { IsReadOnly: true }, "Published ledger is writable");
        Check(result.PotionStrategicCostByTurn.GetValueOrDefault(1) == SolverWeights.PotionMinimumHpSaved
            && result.PotionStrategicCostByTurn.GetValueOrDefault(2) == 0, "Per-turn cost repriced the generated bottle");
        Check(policy.PotionStrategy.EvaluateForcedUses(result.PotionUses) is { AllForcedUsesSatisfied: true, ForcedUseCount: 1 },
            "Materialized forced-use evidence missing");
        var verifier = new CombatBeamSolver(root, names, damage, policy, searchProfile: profile);
        var replay = verifier.ReplayDiagnosticPrefix(result.BestNode.Actions);
        try { Check(verifier.CaptureDiagnosticContinuation(replay) == result.PredictedFinalState
                && replay.ExplicitPotionStrategicCost == SolverWeights.PotionMinimumHpSaved,
            "Complete selected route did not replay with identical state and cost"); }
        finally { replay.ReleaseSimulator(); }
        Check(result.ExpandedNodes > 0, "Full fixture did no search work");
        CheckCrossTurnAccounting(root, names, damage, policy, profile, result, brew, fire);
        var coordinatedPolicy = policy with { FixedBudget = true, MaxDegreeOfParallelism = 1,
            UseNoveltyPortfolio = false, StopAtAcceptableBattleHpLoss = false };
        var coordinated = CombatSearchCoordinator.Solve(root, names, damage, coordinatedPolicy, default, null);
        Check(coordinated.BoundaryReason != SearchBoundaryReason.TimeLimit
            && policy.PotionStrategy.EvaluateForcedUses(coordinated.PotionUses).AllForcedUsesSatisfied,
            "Coordinator/forced-baseline integration did not complete its mandatory use");
        Check(coordinated.PotionUses.Any(use => use is { Origin: PotionOrigin.EntropicBrew, Automatic: false })
            && coordinated.PotionHpRequired == 0
            && coordinated.PotionUses.Where(use => use.Origin == PotionOrigin.EntropicBrew).All(use => use.StrategicHpCost == 0)
            && coordinated.PotionUses.Sum(use => use.StrategicHpCost) == coordinated.PotionStrategicCostByTurn.Values.Sum(),
            "Coordinator disagreed with generated/per-turn costs");
        var coordinatedReplay = verifier.ReplayDiagnosticPrefix(coordinated.BestNode.Actions);
        try { Check(verifier.CaptureDiagnosticContinuation(coordinatedReplay) == coordinated.PredictedFinalState,
            "Coordinator's final route did not independently replay"); }
        finally { coordinatedReplay.ReleaseSimulator(); }
        Check(liveBefore == ContinuationStamp.CaptureLive(live).StateText, "Search changed live fixture");
        CheckForcedBrewSelection(live, names, damage, coordinatedPolicy, profile);
        CheckSmartPotionSelection(live, names, damage, policy);
        SolverResult crossLayer = CheckCompletedSmartLayers(live, names, damage, coordinatedPolicy, profile);
        File.WriteAllText(Path.Combine(output, "entropic-cost.json"), JsonSerializer.Serialize(new {
            status = "PASS", checks = _checks, advances, expected, result.PotionUses, result.PotionStrategicCostByTurn,
            result.ExpandedNodes, result.TotalTransitionCount, result.BoundaryReason,
            coordinator = new { coordinated.ExpandedNodes, coordinated.TotalTransitionCount, coordinated.PotionUses },
            crossLayer = new { crossLayer.CombatEndedTurn, crossLayer.ProjectedBattleHpLost, crossLayer.PotionUses,
                crossLayer.TotalExpandedNodes, crossLayer.TotalTransitionCount },
            route = result.BestNode.Actions, ambergrisEvidence = "policy contract only; absent from pinned native catalog",
            evidence = "pinned-offline; native UI and Host/Client unverified"
        }, new JsonSerializerOptions { WriteIndented = true }));
        return $"ENTROPIC_COST_PASS checks={_checks} native-generation/ledger/fork/keys/chains/Smart/materialization/continuation/replay";
    }

    private static SolverResult CheckCompletedSmartLayers(CombatState live, SolverDisplayNames names,
        BattleDamageSnapshot damage, SearchPolicySnapshot policy, SolverSearchProfile profile)
    {
        var player = LocalContext.GetMe(live)!;
        foreach (PotionModel potion in player.PotionSlots.OfType<PotionModel>().ToArray())
            player.RemovePotionInternal(potion);
        foreach (var relic in player.Relics.ToArray()) player.RemoveRelicInternal(relic, silent: true);
        Check(player.AddPotionInternal(ModelDb.Potion<EntropicBrew>().ToMutable(), 0, true).success
            && player.AddPotionInternal(ModelDb.Potion<FirePotion>().ToMutable(), 1, true).success,
            "Could not install the cross-layer Brew fixture");
        var rng = live.RunState.Rng.CombatPotionGeneration;
        bool foundPair = false;
        for (int attempt = 0; attempt < 16_384; attempt++)
        {
            var oracle = rng.Clone();
            if (PotionFactory.CreateRandomPotionOutOfCombat(player, oracle) is FirePotion
                && PotionFactory.CreateRandomPotionOutOfCombat(player, oracle) is FirePotion)
            {
                foundPair = true;
                break;
            }
            rng.NextInt(100);
        }
        Check(foundPair, "Could not find two Fire Potions in the native generation sequence");
        live.Enemies.Single().SetCurrentHpInternal(42);
        var state = player.PlayerCombatState!;
        foreach (var pile in new[] { state.Hand, state.DrawPile, state.DiscardPile, state.ExhaustPile })
            pile.Clear(silent: true);
        state.Hand.AddInternal(live.CreateCard(ModelDb.Card<StrikeIronclad>(), player), -1);
        state.Hand.AddInternal(live.CreateCard(ModelDb.Card<DefendIronclad>(), player), -1);
        for (int card = 0; card < 5; card++)
            state.DrawPile.AddInternal(live.CreateCard(ModelDb.Card<StrikeIronclad>(), player), -1);
        var root = CombatRootSnapshot.Capture(live);
        string before = ContinuationStamp.CaptureLive(live).StateText;
        List<string> scheduling = [];
        SearchPolicySnapshot scheduled = policy with
        {
            UseE3FixedPortfolioScheduling = true,
            UseE3AdaptivePortfolioScheduling = false,
            Diagnostics = new SearchDiagnosticsSink(message =>
            {
                if (message.Contains("SMART_POTION_GRADIENT layer=")
                    || message.Contains("E3_PORTFOLIO_SLICE kind=smart_potion")) scheduling.Add(message);
            }, _ => { }),
        };
        SolverResult result = CombatSearchCoordinator.Solve(root, names, damage, scheduled, default, null);
        foreach (string message in scheduling) Console.WriteLine(message);
        Console.WriteLine($"SMART_CROSS_LAYER turn={result.CombatEndedTurn} loss={result.ProjectedBattleHpLost} "
            + $"uses={result.ExplicitPotionCount} expanded={result.TotalExpandedNodes} transitions={result.TotalTransitionCount}");
        int higherCompleted = scheduling.FindIndex(message => message.Contains("potion_count=3 status=Completed"));
        int lowerAccepted = scheduling.FindIndex(message => message.Contains("SMART_POTION_GRADIENT layer=2 ")
            && message.Contains("selected=True"));
        Check(higherCompleted >= 0 && lowerAccepted > higherCompleted,
            "The higher layer must be finished before the first qualifying lower layer is accepted");
        Check(result.BoundaryReason != SearchBoundaryReason.TimeLimit
            && result.CombatEndedTurn == root.StartTurnNumber && result.ProjectedBattleHpLost == 0
            && result.PotionHpRequired == 0 && result.PotionUses.Count == 3
            && scheduled.PotionStrategy.EvaluateForcedUses(result.PotionUses).AllForcedUsesSatisfied
            && result.PotionUses.Count(use => use is { Origin: PotionOrigin.EntropicBrew, StrategicHpCost: 0 }) == 2,
            "The already completed victory using two free bottles was dropped");
        var verifier = new CombatBeamSolver(root, names, damage, scheduled, searchProfile: profile);
        SimulationSnapshot replay = verifier.ReplayDiagnosticPrefix(result.BestNode.Actions);
        try
        {
            Check(verifier.CaptureDiagnosticContinuation(replay) == result.PredictedFinalState
                && ContinuationStamp.CaptureLive(live).StateText == before,
                "The cross-layer winner failed replay or changed live combat");
        }
        finally { replay.ReleaseSimulator(); }
        return result;
    }

    private static void CheckForcedBrewSelection(CombatState live, SolverDisplayNames names,
        BattleDamageSnapshot damage, SearchPolicySnapshot policy, SolverSearchProfile profile)
    {
        var player = LocalContext.GetMe(live)!;
        Check(player.AddPotionInternal(ModelDb.Potion<BlockPotion>().ToMutable(), 2, true).success,
            "Could not fill the second paid slot for the forced Brew fixture");
        var enemy = live.Enemies.Single();
        enemy.SetCurrentHpInternal(21);
        var hand = player.PlayerCombatState!.Hand;
        CardModel hemokinesis = live.CreateCard(ModelDb.Card<Hemokinesis>(), player);
        hand.AddInternal(hemokinesis, -1);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        for (int attempt = 0; attempt < 256; attempt++)
        {
            var oracle = root.ForkSimulator();
            if (PotionFactory.CreateRandomPotionOutOfCombat(player, oracle.Rng.CombatPotionGeneration) is FirePotion)
                break;
            live.RunState.Rng.CombatPotionGeneration.NextInt(100);
            root = CombatRootSnapshot.Capture(live);
        }
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        SearchPolicySnapshot smart = policy with
        {
            RoutePolicy = SearchRoutePolicy.MultiplayerSinglePlayerCore,
            PotionStrategy = new(SolverPotionPolicy.Smart, [new(0, "ENTROPIC_BREW", SolverPotionDirective.Force)]),
        };
        var verifier = new CombatBeamSolver(root, names, damage, smart, searchProfile: profile);
        int turn = root.StartTurnNumber;
        PlanAction brew = new(PlanActionKind.UsePotion, turn, PotionSlot: 0, PotionId: "ENTROPIC_BREW");
        PlanAction paidFire = new(PlanActionKind.UsePotion, turn, TargetIndex: 0,
            TargetCombatId: enemy.CombatId, PotionSlot: 1, PotionId: "FIRE_POTION");
        PlanAction block = new(PlanActionKind.UsePotion, turn, PotionSlot: 2, PotionId: "BLOCK_POTION");
        PlanAction strike = new(PlanActionKind.PlayCard, turn, "STRIKE_IRONCLAD", TargetIndex: 0,
            TargetCombatId: enemy.CombatId);
        List<SimulationSnapshot> snapshots = [];
        try
        {
            SearchNode generated = ReplayRoute(verifier, snapshots,
                [brew, paidFire with { PotionSlot = 0 }, strike with { CardId = "HEMOKINESIS" }]);
            SearchNode paid = ReplayRoute(verifier, snapshots, [brew, paidFire, strike]);
            SearchNode missingBrew = ReplayRoute(verifier, snapshots, [block, paidFire, strike]);
            Check(generated.Snapshot.AllEnemiesDead && paid.Snapshot.AllEnemiesDead && missingBrew.Snapshot.AllEnemiesDead
                && generated.Snapshot.CumulativePlayerHpLost == 2 && paid.Snapshot.CumulativePlayerHpLost == 0
                && generated.PotionStrategicCost == SolverWeights.PotionMinimumHpSaved
                && paid.PotionStrategicCost == 2 * SolverWeights.PotionMinimumHpSaved,
                "The replayed wins must trade two raw HP for a free generated bottle");
            // The audited comparison is controlled to isolate the exact-layer ordering.
            PotionFreePolicyBaseline baseline = new(true, 0, root.InitialPlayerHp, turn)
            {
                BattleHpLost = damage.HpLostSoFar + 3,
                EvaluationWindow = generated.Snapshot.EvaluationWindow,
            };
            var layer = new CombatBeamSolver(root, names, damage, smart, searchProfile: profile,
                potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne, potionFreePolicyBaseline: baseline,
                minimumPotionUses: 2, maximumPotionUses: 2);
            var selected = layer.SelectDiagnosticCandidates([paid, generated]);
            Console.WriteLine($"FORCED_BREW_LAYER slot={selected.Selected.Actions[1].PotionSlot} "
                + $"loss={selected.Selected.Snapshot.CumulativePlayerHpLost} total_cost={selected.Selected.PotionStrategicCost}");
            Check(ReferenceEquals(selected.Selected, generated), "The forced Brew charge hid the free generated follow-up");
            Check(ReferenceEquals(layer.SelectDiagnosticCandidates([missingBrew, generated]).Selected, generated),
                "An exact-use count was allowed to replace the forced root Brew");
            try
            {
                _ = layer.SelectDiagnosticCandidates([missingBrew]);
                Check(false, "A layer without the original Brew should report an unsatisfied policy");
            }
            catch (PotionPolicyUnsatisfiedException) { _checks++; }
            Check(liveBefore == ContinuationStamp.CaptureLive(live).StateText, "Forced layer selection changed live combat");
        }
        finally
        {
            foreach (SimulationSnapshot snapshot in snapshots) snapshot.ReleaseSimulator();
            hand.RemoveInternal(hemokinesis, silent: true);
        }
    }

    private static SearchNode ReplayRoute(CombatBeamSolver solver, List<SimulationSnapshot> snapshots,
        IReadOnlyList<PlanAction> actions)
    {
        SearchNode? parent = null;
        for (int length = 0; length <= actions.Count; length++)
        {
            var snapshot = solver.ReplayDiagnosticPrefix(actions.Take(length).ToArray());
            snapshots.Add(snapshot);
            parent = new(length == 0 ? null : actions[length - 1], length, snapshot.PotionUseCount,
                snapshot.PotionStrategicCost, snapshot.Turn, SearchRouteTraits.None, 0,
                snapshot.Score, snapshot.StateKey, snapshot.HasRisk, snapshot.BoundaryReason,
                snapshot.AllEnemiesDead || snapshot.PlayerDead, parent, snapshot, CombatProgressState.Capture(snapshot));
        }
        return parent!;
    }

    private static void CheckSmartPotionSelection(CombatState live, SolverDisplayNames names,
        BattleDamageSnapshot damage, SearchPolicySnapshot policy)
    {
        var player = LocalContext.GetMe(live)!;
        foreach (PotionModel potion in player.PotionSlots.OfType<PotionModel>().ToArray())
            player.RemovePotionInternal(potion);
        player.AddRelicInternal(ModelDb.Relic<PetrifiedToad>().ToMutable(), silent: true);
        player.AddRelicInternal(ModelDb.Relic<HappyFlower>().ToMutable(), silent: true);
        Check(player.AddPotionInternal(ModelDb.Potion<PotionShapedRock>().ToMutable(), 0, true).success,
            "Could not install the renewable potion fixture");
        live.Enemies.Single().SetCurrentHpInternal(6);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        Check(root.SearchablePotions.Single().StrategicHpCost == 0, "The renewable potion must have zero cost");
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        CheckAuditMemberDeadline(root, names, damage, policy);
        foreach (bool scheduled in new[] { false, true })
        {
            List<string> diagnostics = [];
            SearchPolicySnapshot smart = policy with
            {
                PotionStrategy = new(SolverPotionPolicy.Smart, []),
                StopAtAcceptableBattleHpLoss = false,
                UseE3FixedPortfolioScheduling = scheduled,
                UseE3AdaptivePortfolioScheduling = false,
                RelicTargets = [new(RelicCounterId.HappyFlower, 1, 1, 0, 3)],
                Diagnostics = new SearchDiagnosticsSink(message =>
                {
                    if (message.Contains("SMART_POTION_GRADIENT layer=1 ")) diagnostics.Add(message);
                }, _ => { }),
            };
            bool publishedPotionPreview = false;
            SolverResult result = CombatSearchCoordinator.Solve(root, names, damage, smart, default, progress =>
            {
                publishedPotionPreview |= progress.CurrentTurnPreview?.Actions.Any(action => action.Kind == PlanActionKind.UsePotion) == true
                    || progress.SpeculativeRoutePreview?.Turns.Any(turn =>
                        turn.Actions.Any(action => action.Kind == PlanActionKind.UsePotion)) == true;
            });
            Console.WriteLine($"SMART_POTION_QUALITY scheduled={scheduled} won={result.Snapshot.AllEnemiesDead} "
                + $"turn={result.CombatEndedTurn} loss={result.ProjectedBattleHpLost} potions={result.ExplicitPotionCount}");
            string? optionalVictory = diagnostics.FirstOrDefault(message => message.Contains("won=True")
                && message.Contains("required=0") && message.Contains("acceptable=True"));
            Check(optionalVictory != null,
                "The fixture did not evaluate an eligible, zero-cost potion victory");
            Check(result.Snapshot.AllEnemiesDead && result.CombatEndedTurn == root.StartTurnNumber + 1
                && result.ProjectedBattleHpLost == 0 && result.ExplicitPotionCount == 0,
                "An eligible potion victory displaced the better potion-free victory");
            Check(optionalVictory!.Contains("selected=False"), "The audit promoted a weaker optional-potion victory");
            Check(!publishedPotionPreview, "A weaker optional-potion route was published before Smart selection");
            var verifier = new CombatBeamSolver(root, names, damage, smart);
            SimulationSnapshot replay = verifier.ReplayDiagnosticPrefix(result.BestNode.Actions);
            try { Check(verifier.CaptureDiagnosticContinuation(replay) == result.PredictedFinalState,
                "The selected potion-free victory did not replay"); }
            finally { replay.ReleaseSimulator(); }
        }
        Check(liveBefore == ContinuationStamp.CaptureLive(live).StateText, "Smart selection changed live combat");
    }

    private static void CheckAuditMemberDeadline(CombatRootSnapshot root, SolverDisplayNames names,
        BattleDamageSnapshot damage, SearchPolicySnapshot policy)
    {
        SearchPolicySnapshot audit = policy with
        {
            PotionStrategy = new(SolverPotionPolicy.Smart, []),
            StopAtAcceptableBattleHpLoss = false,
            CaptureFinalStateForDiagnostics = true,
        };

        CombatBeamSolver CreateSolver(CancellationToken cancellation) => new(root, names, damage, audit,
            cancellation, searchProfile: audit.Profile, potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
            maximumPotionUses: 1, minimumPotionUses: 1);

        using (var expired = new CancellationTokenSource())
        using (var session = CreateSolver(default).CreateExecutionSession())
        {
            expired.Cancel();
            SearchStepResult step = session.Step(SearchWorkAllowance.SingleParent, default, expired.Token);
            Check(step.Status == SearchStepStatus.BudgetExhausted && session.Result == null
                && session.ExpandedNodesForScheduling == 0 && !session.HasLiveSimulatorsForTesting,
                "An expired audit initialized work without a committed victory");
        }

        foreach (bool cancelRequest in new[] { false, true })
        {
            using var request = new CancellationTokenSource();
            using var deadline = new CancellationTokenSource();
            using var session = CreateSolver(request.Token).CreateExecutionSession();
            SearchStepResult step;
            do
            {
                step = session.Step(SearchWorkAllowance.SingleParent, request.Token, deadline.Token);
            }
            while (step.Status == SearchStepStatus.Yielded && session.CurrentBestResultForScheduling is not { Won: true });
            Check(step.Status == SearchStepStatus.Yielded && session.CurrentBestResultForScheduling is { Won: true },
                "The audit fixture did not yield after committing a victory");

            long expanded = session.ExpandedNodesForScheduling;
            long transitions = session.TransitionCountForScheduling;
            if (cancelRequest) request.Cancel();
            deadline.Cancel();
            step = session.Step(SearchWorkAllowance.SingleParent, request.Token, deadline.Token);
            Check(step.CommittedParents == 0 && session.ExpandedNodesForScheduling == expanded
                && session.TransitionCountForScheduling == transitions && !session.HasLiveSimulatorsForTesting,
                "Audit shutdown expanded more work or retained live simulators");
            if (cancelRequest)
            {
                Check(step.Status == SearchStepStatus.Canceled && session.Result == null,
                    "Request cancellation published a route at the audit deadline");
                continue;
            }

            SolverResult? result = session.Result;
            Check(step.Status == SearchStepStatus.Completed && result is { Snapshot.AllEnemiesDead: true,
                ExplicitPotionCount: 1, BoundaryReason: SearchBoundaryReason.TimeLimit },
                "The deadline discarded a committed potion victory");
            CombatBeamSolver verifier = CreateSolver(default);
            SimulationSnapshot replay = verifier.ReplayDiagnosticPrefix(result!.BestNode.Actions);
            try
            {
                Check(replay.AllEnemiesDead && verifier.CaptureDiagnosticContinuation(replay) == result.PredictedFinalState,
                    "The deadline-selected victory did not replay to the same terminal state");
            }
            finally { replay.ReleaseSimulator(); }
            Console.WriteLine($"AUDIT_DEADLINE_REPLAY expanded={expanded} transitions={transitions} "
                + $"turn={result.CombatEndedTurn} loss={result.ProjectedBattleHpLost} potions={result.ExplicitPotionCount}");
        }
    }

    private static void CheckCrossTurnAccounting(CombatRootSnapshot root, SolverDisplayNames names,
        BattleDamageSnapshot damage, SearchPolicySnapshot policy, SolverSearchProfile profile,
        SolverResult searched, PlanAction brew, PlanAction fire)
    {
        // Route-adoption deliberately rejects potions and fixed prefixes reject
        // EndTurn. Replay legal native transitions, then exercise the production
        // annotation/capture/continuation contracts with this synthetic result.
        PlanAction[] actions = [brew, new(PlanActionKind.EndTurn, 1), fire];
        var solver = new CombatBeamSolver(root, names, damage, policy, searchProfile: profile);
        var snapshots = new List<SimulationSnapshot>();
        try
        {
            SearchNode node = ReplayRoute(solver, snapshots, actions);
            Check(snapshots[^1].Turn == 2 && snapshots[^1].ExplicitPotionStrategicCost == SolverWeights.PotionMinimumHpSaved,
                "Cross-turn native replay lost generated cost");
            var annotations = typeof(CombatBeamSolver).GetMethod("BuildRouteAnnotations",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(solver, [node, null])!;
            var costs = (IReadOnlyDictionary<int, int>)annotations.GetType().GetProperty("PotionStrategicCostByTurn")!.GetValue(annotations)!;
            var counts = (IReadOnlyDictionary<int, int>)annotations.GetType().GetProperty("PotionCountByTurn")!.GetValue(annotations)!;
            var uses = PotionStrategySnapshot.CaptureUses(node, ((SimulatedCombatState)snapshots[^1].Simulator.State.CombatState).PotionUses);
            Check(costs.GetValueOrDefault(1) == SolverWeights.PotionMinimumHpSaved && costs.GetValueOrDefault(2) == 0
                && counts.GetValueOrDefault(2) == 1 && uses[1].Turn == 2, "Cross-turn production annotations repriced a generated use");
            var synthetic = (SolverResult)typeof(object).GetMethod("MemberwiseClone",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(searched, null)!;
            void Set(string name, object value) => typeof(SolverResult).GetProperty(name)!.SetValue(synthetic, value);
            var expected = solver.CaptureDiagnosticContinuation(snapshots[2]);
            Set(nameof(SolverResult.BestNode), new SelectedSearchPlan(actions, actions.Length, snapshots[^1].Score));
            Set(nameof(SolverResult.PotionUses), uses);
            Set(nameof(SolverResult.PotionStrategicCostByTurn), costs);
            Set(nameof(SolverResult.PotionCountByTurn), counts);
            Set(nameof(SolverResult.Continuations), new[] { new CachedContinuation(expected, 2, 1) });
            Check(synthetic.TryCreateContinuation(expected, 2, snapshots[2].PlayerHp, damage, null,
                out var continuation, out string reason), "Continuation contract rejected: " + reason);
            Check(continuation!.PotionHpRequired == 0 && continuation.PotionCount == 1
                && ReferenceEquals(continuation.PotionUses, uses), "Continuation lost zero cost or retained ledger");
            var unforced = policy with { PotionStrategy = new(SolverPotionPolicy.Smart, []) };
            int repriced = (int)typeof(CombatSearchCoordinator).GetMethod("SmartPotionHpRequired",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null,
                [root, unforced, continuation])!;
            Check(repriced == 0, "Coordinator charged a previous turn's Brew again on retained route");
        }
        finally { foreach (var snapshot in snapshots) snapshot.ReleaseSimulator(); }
    }
}
