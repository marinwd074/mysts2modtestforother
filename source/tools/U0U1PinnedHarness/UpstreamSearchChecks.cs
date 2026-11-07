using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Badges;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Platform.Null;

namespace U0U1PinnedHarness;

internal static class UpstreamSearchChecks
{
    private static void ForeignBadgePrefix() { }
    private static bool SkipHeadlessPlatformCtor() => false;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Upstream search: " + message);
    }

    internal static string Run(CombatState live, SolverDisplayNames names, BattleDamageSnapshot damage,
        SearchPolicySnapshot captured, SolverSearchProfile original, string outputDirectory,
        bool conditionalRelicOnly = false)
    {
        var player = LocalContext.GetMe(live)!;
        _ = CombatRootSnapshot.Capture(live); // Finish canonical DynamicVar warm-up before removing localization bypasses.
        // Null platform display names need no native command line or filesystem.
        // Initialize its native strategy, then remove even this temporary bypass.
        OfflineSearchHarness.GameBootstrap.Harmony.Patch(AccessTools.Constructor(typeof(NullPlatformUtilStrategy)),
            prefix: new HarmonyMethod(typeof(UpstreamSearchChecks), nameof(SkipHeadlessPlatformCtor)));
        RuntimeHelpers.RunClassConstructor(typeof(PlatformUtil).TypeHandle);
        // Bootstrap's Godot/UI bypasses are foreign native patches. Initialization
        // is complete: remove them before capturing the proof and running search.
        foreach (var method in Harmony.GetAllPatchedMethods().ToArray())
            OfflineSearchHarness.GameBootstrap.Harmony.Unpatch(method, HarmonyPatchType.All,
                OfflineSearchHarness.GameBootstrap.HarmonyId);
        SolverSearchProfile profile = original with { BeamWidth = 8, MaxExpandedNodes = 1_200,
            SoftTimeBudgetMilliseconds = 20_000 };
        SearchPolicySnapshot plain = captured with
        {
            Profile = profile, RoutePolicy = SearchRoutePolicy.SinglePlayerFullRoute,
            CurrentTurnOnly = false, IncludeTurnSetup = false, UseMultiplayerTeamObjective = false,
            UseMultiplayerTeammateForecast = false, UseMultiplayerScenarioReevaluation = false,
            UseNoveltyPortfolio = false, NoveltySearch = null, UseBeamWidthPortfolio = true,
            BeamWidthPortfolioWidths = [8, 8, 12], FixedBudget = true,
            MaxDegreeOfParallelism = 1, StopAtAcceptableBattleHpLoss = false, Interaction = null,
            VerifyIncrementalSearch = true,
            IgnoreLongTermRewards = true, RelicTargets = [],
            PotionPolicy = SolverPotionPolicy.Disabled, PotionStrategy = new(SolverPotionPolicy.Disabled, []),
            RequestWorkTotals = null, OptimizationSession = null, RequestTransitionHydrationCache = null,
        };
        CombatRootSnapshot firstRoot = CombatRootSnapshot.Capture(live);
        Check(firstRoot.HasPinnedHealingCertificate,
            $"The pinned starter fixture was not certified (native MVID {typeof(CardModel).Module.ModuleVersionId}).");
        Harmony foreign = new("CombatSolver.Harness.ForeignHealingCertificate");
        MethodInfo badgeHook = AccessTools.Method(typeof(CccComboModel), nameof(CccComboModel.AfterCardPlayed));
        if (conditionalRelicOnly)
        {
            foreach (RelicModel relic in player.Relics.ToArray())
                player.RemoveRelicInternal(relic, silent: true);
            live.Enemies.Single().SetMaxHpInternal(100);
            live.Enemies.Single().SetCurrentHpInternal(100);
            object comparison = CheckConditionalRelicHealing(live, names, damage, plain, profile, foreign, badgeHook);
            object oracle = CheckSmallHealingOracle(live, names, damage, plain, profile);
            File.WriteAllText(Path.Combine(outputDirectory, "healing-bound.json"),
                JsonSerializer.Serialize(new { comparison, oracle }, new JsonSerializerOptions { WriteIndented = true }));
            return "HealingBound PASS (same-root certificate A/B, complete route replays and exhaustive small combat)";
        }
        try
        {
            foreign.Patch(badgeHook, prefix: new HarmonyMethod(typeof(UpstreamSearchChecks), nameof(ForeignBadgePrefix)));
            Check(!CombatRootSnapshot.Capture(live).HasPinnedHealingCertificate,
                "A foreign gameplay patch bypassed the native healing certificate.");
        }
        finally { foreign.Unpatch(badgeHook, HarmonyPatchType.All, foreign.Id); }
        Check(CombatRootSnapshot.Capture(live).HasPinnedHealingCertificate,
            "Removing the foreign patch did not restore native certificate eligibility.");
        CombatBeamSolver diagnostic = new(firstRoot, names, damage,
            plain with { VerifyIncrementalSearch = false }, searchProfile: profile);
        PlanAction defend = new(PlanActionKind.PlayCard, firstRoot.StartTurnNumber, "DEFEND_IRONCLAD");
        SimulationSnapshot ordinary = diagnostic.ReplayDiagnosticActionWithR0Memo(defend);
        SimulationSnapshot unknown = diagnostic.ReplayDiagnosticActionWithR0Memo(defend, prepareParent: sim =>
            sim.State.GetPlayerCombatState(player).Hand.Add(PredictedCard.Create(CanonicalModels.Card<Feed>(), player)));
        try
        {
            Check(PinnedHealingBound.RemainingRankedHealing(firstRoot, ordinary, out var ordinaryStatus)
                    == firstRoot.PostCombatRelicHeal.UnconditionalHeal && ordinaryStatus == HealingBoundStatus.Certified,
                "Closed native loadout did not use the finite post-combat healing allowance.");
            Check(PinnedHealingBound.RemainingRankedHealing(firstRoot, unknown, out var unknownStatus) == null
                    && unknownStatus == HealingBoundStatus.UnclassifiedBranch,
                "A newly introduced unclassified healing/growth source was treated as certified.");
        }
        finally { ordinary.ReleaseSimulator(); unknown.ReleaseSimulator(); }

        PrimarySearchIncumbent? incumbent = null;
        PotionFreePolicyBaseline baseline = new(true, 10, 70, 4);
        Check(!CombatBeamSolver.TryTightenPrimarySearchIncumbent(baseline, 1, 1,
                true, true, 1, 9, 2, ref incumbent, candidatePotionStrategicCost: 9),
            "A one-HP improvement bypassed a nine-HP optional-potion opportunity cost.");
        Check(CombatBeamSolver.TryTightenPrimarySearchIncumbent(baseline, 1, 1,
                true, true, 1, 0, 2, ref incumbent, candidatePotionStrategicCost: 9),
            "A genuinely eligible potion victory did not establish an incumbent.");
        Check(CombatBeamSolver.SmartEligibilityCeiling(baseline, 1, 9, BossHpRelief.None) == 1
            && CombatBeamSolver.SmartEligibilityCeiling(null, 1, 9, BossHpRelief.None) == null,
            "Smart eligibility did not require the audited complete no-potion baseline.");
        Check(BeamWidthPortfolioGate.EstimateMemberCost(long.MaxValue, 1, int.MaxValue) == long.MaxValue,
            "Portfolio admission overflowed instead of saturating.");
        List<object> rows = [];
        int requests = 0, replays = 0;
        CheckPotionCostSnapshots(firstRoot, names, damage, plain, profile, defend);
        MethodInfo summary = typeof(CombatSearchCoordinator).GetMethod("BuildInterimResult",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        SolverInterimResult Quality(CombatRootSnapshot root, SearchPolicySnapshot policy, SolverResult result)
            => (SolverInterimResult)summary.Invoke(null, [root, policy, result])!;
        foreach (string fixture in new[] { "native-closed", "native-closed-hard", "smart-potions", "smart-wounded", "unknown-source" })
        {
            var enemy = live.Enemies.Single();
            int priorEnemyHp = enemy.CurrentHp;
            int priorPlayerHp = player.Creature.CurrentHp;
            if (fixture == "native-closed-hard")
            {
                foreach (var relic in player.Relics.ToArray()) player.RemoveRelicInternal(relic, silent: true);
                live.Enemies.Single().SetMaxHpInternal(100);
                live.Enemies.Single().SetCurrentHpInternal(100);
            }
            if (fixture == "smart-potions")
                for (int slot = 0; slot < 2; slot++)
                    Check(player.AddPotionInternal(ModelDb.Potion<FirePotion>().ToMutable(), slot, silent: true).success,
                        "Could not prepare pinned Smart fixture.");
            if (fixture == "smart-wounded")
            {
                player.Creature.SetCurrentHpInternal(player.Creature.MaxHp - 30);
                enemy.SetCurrentHpInternal(6);
            }
            if (fixture == "unknown-source")
                player.PlayerCombatState!.DrawPile.AddInternal(live.CreateCard(CanonicalModels.Card<Feed>(), player), -1);
            CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
            if (fixture == "unknown-source") Check(!root.HasPinnedHealingCertificate,
                "An unknown root source bypassed the certificate rejection.");
            SearchPolicySnapshot policy = fixture.StartsWith("native-closed", StringComparison.Ordinal) ? plain : plain with
            { PotionPolicy = SolverPotionPolicy.Smart, PotionStrategy = new(SolverPotionPolicy.Smart, []) };
            if (fixture == "smart-wounded")
            {
                int price = root.SearchablePotions[0].StrategicHpCost;
                Check(CombatSearchCoordinator.MaximumSmartPotionUses(root, policy, true, price - 1) == 0,
                    "Missing HP without a healing source was counted as potion savings.");
                Check(CombatSearchCoordinator.MaximumSmartPotionUses(root, policy, true, price) == 1,
                    "A potion that can exactly earn back its cost must stay eligible.");
                Check(CombatSearchCoordinator.MaximumSmartPotionUses(root, policy, false, 0) == 2,
                    "A missing potion-free victory must keep rescue potions eligible.");
                Check(CombatSearchCoordinator.MaximumSmartPotionUses(root,
                        policy with { RelicTargets = [new(RelicCounterId.HappyFlower, 1, 1, 0, 3)] }, true, 0) == 2,
                    "A resource objective must keep the optimistic recovery allowance.");
            }
            string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
            SolverResult? reference = null;
            // Each request owns a fresh session and every selected route is replayed independently.
            bool[] modes = fixture == "smart-wounded" ? [false, true] : [false, true, true, false];
            foreach (bool enabled in modes)
            {
                SearchPolicySnapshot runPolicy = policy with { UseUpstreamSearchOptimizations = enabled };
                Stopwatch clock = Stopwatch.StartNew();
                SolverResult result = CombatSearchCoordinator.Solve(root, names, damage, runPolicy, default, null);
                long elapsed = clock.ElapsedMilliseconds;
                Check(result.BoundaryReason != SearchBoundaryReason.TimeLimit,
                    "An A/B request hit its wall-clock limit; it is not fixed-input quality evidence.");
                if (!enabled)
                {
                    if (reference != null) Check(Quality(root, policy, result) == Quality(root, policy, reference),
                        "Reference requests did not reproduce their complete result quality.");
                    reference = result;
                }
                else Check(!CombatSearchCoordinator.IsBetterPotionPolicyResult(policy.TheftPolicy,
                    Quality(root, policy, reference!), Quality(root, policy, result)),
                    "The optimization reduced selected-route quality.");
                if (fixture == "smart-wounded")
                    Check(root.HasPinnedHealingCertificate && result.Snapshot.AllEnemiesDead
                        && result.ExplicitPotionCount == 0 && result.ProjectedBattleHpLost == 0,
                        "The wounded fixture must produce a certified zero-loss win without potions.");

                CombatBeamSolver replaySolver = new(root, names, damage, runPolicy, searchProfile: profile);
                SimulationSnapshot replay = replaySolver.ReplayDiagnosticPrefix(result.BestNode.Actions);
                try
                {
                    Check(replaySolver.CaptureDiagnosticContinuation(replay) == result.PredictedFinalState
                        && replay.CumulativePlayerHpLost == result.Snapshot.CumulativePlayerHpLost
                        && replay.AllEnemiesDead == result.Snapshot.AllEnemiesDead,
                        "The selected route's independent replay disagreed with its final state.");
                    var uses = ((SimulatedCombatState)replay.Simulator.State.CombatState).PotionUses;
                    Check(replay.ExplicitPotionStrategicCost == uses.Where(use => !use.Automatic).Sum(use => use.StrategicHpCost),
                        "Replayed explicit potion cost disagreed with the branch use ledger.");
                    replays++;
                }
                finally { replay.ReleaseSimulator(); }
                Check(liveBefore == ContinuationStamp.CaptureLive(live).StateText, "Search/replay changed live combat.");
                Check(!enabled || result.OptimizationMetrics != null, "Enabled request lost optimization accounting.");
                rows.Add(new { fixture, enabled, elapsed, root.HasPinnedHealingCertificate,
                    quality = Quality(root, policy, result), result.ExpandedNodes, result.TotalTransitionCount,
                    result.OptimizationMetrics, route = result.BestNode.Actions });
                requests++;
                Console.WriteLine($"UPSTREAM_AB fixture={fixture} enabled={enabled} won={result.Snapshot.AllEnemiesDead} "
                    + $"hp_loss={result.ProjectedBattleHpLost} expanded={result.ExpandedNodes} "
                    + $"transitions={result.TotalTransitionCount} elapsed_ms={elapsed} "
                    + JsonSerializer.Serialize(result.OptimizationMetrics));
            }
            if (fixture == "smart-wounded")
            {
                player.Creature.SetCurrentHpInternal(priorPlayerHp);
                enemy.SetCurrentHpInternal(priorEnemyHp);
            }
            if (fixture == "native-closed" && reference!.Snapshot.AllEnemiesDead)
            {
                SearchOptimizationSession session = new(root, policy);
                session.Observe(reference, policy, Quality(root, policy, reference).StrategicHpDeficit);
                Check(session.Witnesses == 1 && session.Matches(root, policy)
                    && !session.Matches(CombatRootSnapshot.Capture(live), policy)
                    && !session.Matches(root, policy with { CurrentTurnOnly = true }),
                    "A finalized witness was lost or shared with a fresh root.");
                session.Observe(reference, policy with { RelicTargets = [new(RelicCounterId.HappyFlower, 1, 1, 0, 3)] }, 0);
                Check(session.Witnesses == 1, "Resource-goal policy leaked into the HP-only shared bucket.");
            }
            if (fixture == "native-closed-hard")
            {
                SearchOptimizationSession reuse = new(root, policy);
                reuse.Observe(reference!, policy, Quality(root, policy, reference!).StrategicHpDeficit);
                Check(reuse.PotionFreeBound != null, "Complete same-root witness was not retained.");
                SolverResult result = new CombatBeamSolver(root, names, damage,
                    policy with { OptimizationSession = reuse, UseUpstreamSearchOptimizations = true },
                    searchProfile: profile).Solve();
                Check(reuse.SharedBranchesPruned > 0, "The shared victory bound did not run in a later member.");
                Check(!CombatSearchCoordinator.IsBetterPotionPolicyResult(policy.TheftPolicy,
                    Quality(root, policy, reference!), Quality(root, policy, result)), "Witness reuse reduced route quality.");
                CombatBeamSolver verifier = new(root, names, damage, policy, searchProfile: profile);
                SimulationSnapshot replay = verifier.ReplayDiagnosticPrefix(result.BestNode.Actions);
                try { Check(verifier.CaptureDiagnosticContinuation(replay) == result.PredictedFinalState,
                    "Witness-reuse result did not replay to its complete final state."); }
                finally { replay.ReleaseSimulator(); }
                requests++; replays++;
                rows.Add(new { fixture = "shared-witness-reuse", result.ExpandedNodes,
                    result.TotalTransitionCount, metrics = reuse.Capture() });
                Console.WriteLine($"SHARED_WITNESS_PASS pruned={reuse.SharedBranchesPruned} "
                    + $"expanded={result.ExpandedNodes} transitions={result.TotalTransitionCount}");
                rows.Add(CheckConditionalRelicHealing(live, names, damage, policy, profile, foreign, badgeHook));
                requests += 2;
                replays += 2;
            }
        }
        File.WriteAllText(Path.Combine(outputDirectory, "upstream-search.json"), JsonSerializer.Serialize(
            new { requests, replays, evidence = "pinned-offline", nativeMvid = PinnedHealingBound.AuditedNativeMvid,
                rows }, new JsonSerializerOptions { WriteIndented = true }));
        return $"UpstreamSearch PASS requests={requests} independent_replays={replays}; native/UI/Host-Client unverified";
    }

    private static object CheckConditionalRelicHealing(CombatState live, SolverDisplayNames names,
        BattleDamageSnapshot damage, SearchPolicySnapshot policy, SolverSearchProfile profile,
        Harmony foreign, MethodInfo badgeHook)
    {
        var player = LocalContext.GetMe(live)!;
        RelicModel meat = ModelDb.Relic<MeatOnTheBone>().ToMutable();
        player.AddRelicInternal(meat, silent: true);
        try
        {
            CombatRootSnapshot certified = CombatRootSnapshot.Capture(live);
            Check(certified.HasPinnedHealingCertificate
                && certified.PostCombatRelicHeal.HealFor(1, certified.InitialPlayerMaxHp) > 0
                && PinnedHealingBound.RankedPostCombatHealingUpperBound(certified) == 0,
                "Threshold relic healing was either excluded from the closed domain or credited to ranked HP.");
            CombatRootSnapshot unproven;
            foreign.Patch(badgeHook, prefix: new HarmonyMethod(typeof(UpstreamSearchChecks), nameof(ForeignBadgePrefix)));
            try { unproven = CombatRootSnapshot.Capture(live); }
            finally { foreign.Unpatch(badgeHook, HarmonyPatchType.All, foreign.Id); }
            Check(!unproven.HasPinnedHealingCertificate && certified.ContinuationStamp == unproven.ContinuationStamp,
                "No-op patch did not isolate certificate availability on an identical fixture.");

            var measured = policy with { VerifyIncrementalSearch = false, CaptureFinalStateForDiagnostics = true,
                MeasurePhasePerformance = true };
            SolverResult SearchAndReplay(CombatRootSnapshot root)
            {
                SolverResult result = CombatSearchCoordinator.Solve(root, names, damage, measured, default, null);
                CombatBeamSolver replay = new(root, names, damage, measured, searchProfile: profile);
                SimulationSnapshot snapshot = replay.ReplayDiagnosticPrefix(result.BestNode.Actions);
                try
                {
                    Check(snapshot.TerminalStamp is { Outcome: CombatTerminalOutcome.Victory }
                        && replay.CaptureDiagnosticContinuation(snapshot) == result.PredictedFinalState
                        && snapshot.CumulativePlayerHpLost == result.ProjectedBattleHpLost,
                        "Conditional relic fixture did not replay to the selected complete victory.");
                }
                finally { snapshot.ReleaseSimulator(); }
                return result;
            }
            SolverResult baseline = SearchAndReplay(unproven);
            SolverResult optimized = SearchAndReplay(certified);
            Check(optimized.TotalExpandedNodes < baseline.TotalExpandedNodes
                && optimized.ProjectedBattleHpLost == baseline.ProjectedBattleHpLost
                && optimized.CombatEndedTurn == baseline.CombatEndedTurn
                && optimized.OptimizationMetrics?.HealingBounds?.Certified > 0
                && baseline.OptimizationMetrics?.HealingBounds?.UncertifiedRoot > 0
                && ContinuationStamp.CaptureLive(live) == certified.ContinuationStamp,
                "The new certificate failed to reduce work while preserving the matched victory quality.");
            return new { fixture = "conditional-post-combat-relic", certified.PostCombatRelicHeal,
                baseline.TotalExpandedNodes, baseline.TotalTransitionCount,
                certifiedExpanded = optimized.TotalExpandedNodes, certifiedTransitions = optimized.TotalTransitionCount,
                optimized.ProjectedBattleHpLost, optimized.CombatEndedTurn,
                sameRoute = JsonSerializer.Serialize(baseline.BestNode.Actions)
                    == JsonSerializer.Serialize(optimized.BestNode.Actions),
                sameFinalState = baseline.PredictedFinalState == optimized.PredictedFinalState,
                optimized.OptimizationMetrics };
        }
        finally { player.RemoveRelicInternal(meat, silent: true); }
    }

    private static object CheckSmallHealingOracle(CombatState live, SolverDisplayNames names,
        BattleDamageSnapshot damage, SearchPolicySnapshot policy, SolverSearchProfile profile)
    {
        var player = LocalContext.GetMe(live)!;
        var enemy = live.Enemies.Single();
        var piles = player.PlayerCombatState!;
        foreach (var pile in new[] { piles.Hand, piles.DrawPile, piles.DiscardPile, piles.ExhaustPile })
            pile.Clear(silent: true);
        piles.Hand.AddInternal(live.CreateCard(CanonicalModels.Card<StrikeIronclad>(), player), -1);
        piles.Hand.AddInternal(live.CreateCard(CanonicalModels.Card<Bloodletting>(), player), -1);
        player.Creature.SetCurrentHpInternal(4);
        enemy.SetCurrentHpInternal(6);
        player.AddRelicInternal(ModelDb.Relic<MeatOnTheBone>().ToMutable(), silent: true);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        Check(root.HasPinnedHealingCertificate && root.SearchablePotions.Count == 0,
            "Oracle requires a closed two-card combat with no potions.");
        CombatBeamSolver replay = new(root, names, damage, policy, searchProfile: profile);
        int turn = root.StartTurnNumber;
        PlanAction strike = new(PlanActionKind.PlayCard, turn, "STRIKE_IRONCLAD", TargetCombatId: enemy.CombatId);
        PlanAction bloodletting = new(PlanActionKind.PlayCard, turn, "BLOODLETTING");
        PlanAction end = new(PlanActionKind.EndTurn, turn);
        int victories = 0, defeats = 0, certifiedCuts = 0, equalBranches = 0;
        int? Visit(PlanAction[] actions)
        {
            SimulationSnapshot snapshot = replay.ReplayDiagnosticPrefix(actions);
            try
            {
                Check(!snapshot.HasRisk, "Oracle encountered an unmodeled effect.");
                if (snapshot.TerminalStamp is { Outcome: CombatTerminalOutcome.Victory })
                {
                    victories++;
                    return snapshot.CumulativePlayerHpLost;
                }
                if (snapshot.TerminalStamp is { Outcome: CombatTerminalOutcome.Defeat })
                {
                    defeats++;
                    return null;
                }
                Check(snapshot.Turn == turn && snapshot.Energy >= 1 && snapshot.BoundaryReason == SearchBoundaryReason.None,
                    "Oracle escaped the fully enumerated play phase.");
                int? allowance = PinnedHealingBound.RemainingRankedHealing(root, snapshot, out var status);
                Check(allowance == 0 && status == HealingBoundStatus.Certified, "Oracle lost its closed recovery proof.");
                int lower = CombatBeamSolver.StrategicHpLowerBound(snapshot, BossHpRelief.None, allowance);
                int? best = Visit([.. actions, end]);
                foreach (PredictedCard card in snapshot.Simulator.State.GetPlayerCombatState(player).Hand.Cards)
                {
                    PlanAction action = card.Preview switch
                    {
                        StrikeIronclad => strike,
                        Bloodletting => bloodletting,
                        _ => throw new InvalidOperationException("Unexpected legal action in closed healing oracle."),
                    };
                    if (Visit([.. actions, action]) is { } childBest && (best == null || childBest < best))
                        best = childBest;
                }
                Check(best == null || lower <= best, "Certified lower bound excluded a better terminal descendant.");
                // The one-Strike route witnesses zero strategic HP deficit. Both outcomes
                // are enumerated even when the proposed cut could already exclude a branch.
                if (lower > 0)
                {
                    certifiedCuts++;
                    Check(best == null || best > 0, "Strict bound cut a route that could match the witness.");
                }
                if (lower == 0)
                    equalBranches++;
                return best;
            }
            finally { snapshot.ReleaseSimulator(); }
        }
        Check(Visit([]) == 0 && victories == 2 && defeats == 2 && certifiedCuts == 1 && equalBranches == 1
            && ContinuationStamp.CaptureLive(live) == root.ContinuationStamp,
            "Oracle must exhaust both action orders and verify one strict cut plus the equality case.");
        return new { victories, defeats, certifiedCuts, equalBranches };
    }

    private static void CheckPotionCostSnapshots(CombatRootSnapshot root, SolverDisplayNames names,
        BattleDamageSnapshot damage, SearchPolicySnapshot policy, SolverSearchProfile profile, PlanAction defend)
    {
        var player = root.PlayerIdentity;
        // Exercise the actual branch ledger and Snapshot/Fork path without UI or
        // triggering native death animations. Automatic consumption itself is a
        // ledger input here, not a claim about native death-preventer timing.
        foreach (string scenario in new[] { "empty", "manual", "automatic", "mixed", "reused-slot" })
        {
            CombatBeamSolver solver = new(root, names, damage, policy, searchProfile: profile);
            SimulationSnapshot snapshot = solver.ReplayDiagnosticActionWithR0Memo(defend, prepareParent: sim =>
            {
                var combat = (SimulatedCombatState)sim.State.CombatState;
                void Consume(PotionModel potion, bool automatic)
                {
                    Check(combat.TryProcurePotion(player, potion), "Could not prepare branch potion.");
                    int slot = Enumerable.Range(0, root.PotionSlotCount)
                        .Single(index => combat.GetPotionAtSlot(player, index)?.Id == potion.Id);
                    if (automatic) combat.ConsumePotion(combat.GetPotionAtSlot(player, slot)!);
                    else combat.ConsumePotion(player, slot);
                }
                if (scenario is "manual" or "reused-slot") Consume(ModelDb.Potion<FirePotion>(), false);
                if (scenario == "reused-slot") Consume(ModelDb.Potion<FirePotion>(), false);
                if (scenario == "mixed") Consume(ModelDb.Potion<SwiftPotion>(), false);
                if (scenario is "automatic" or "mixed") Consume(ModelDb.Potion<FairyInABottle>(), true);
            });
            try
            {
                int expected = scenario switch {
                    "manual" => SolverWeights.PotionMinimumHpSaved,
                    "mixed" => SolverWeights.PotionHighValueHpSaved,
                    "reused-slot" => 2 * SolverWeights.PotionMinimumHpSaved,
                    _ => 0
                };
                int automaticCost = scenario is "automatic" or "mixed" ? SolverWeights.PotionMinimumHpSaved : 0;
                Check(snapshot.ExplicitPotionStrategicCost == expected
                    && snapshot.PotionStrategicCost == expected + automaticCost,
                    $"{scenario}: automatic consumption leaked into explicit cost or manual costs were lost.");
                var parent = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
                int originalUses = parent.PotionUses.Count;
                var child = (SimulatedCombatState)snapshot.Simulator.Fork().State.CombatState;
                Check(child.TryProcurePotion(player, ModelDb.Potion<FirePotion>()), "Could not prepare fork potion.");
                child.ConsumePotion(player, 0);
                Check(parent.PotionUses.Count == originalUses && child.PotionUses.Count == originalUses + 1
                    && snapshot.ExplicitPotionStrategicCost == expected,
                    "A child potion use changed the parent ledger or frozen cost.");
            }
            finally { snapshot.ReleaseSimulator(); }
        }
        Console.WriteLine("POTION_COST_SNAPSHOT_PASS empty/manual/automatic/mixed/reused-slot/fork-isolation");
    }
}
