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
        SearchPolicySnapshot captured, SolverSearchProfile original, string outputDirectory)
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
            Check(PinnedHealingBound.RemainingHealing(firstRoot, ordinary) == firstRoot.PostCombatRelicHeal.UnconditionalHeal,
                "Closed native loadout did not use the finite post-combat healing allowance.");
            Check(PinnedHealingBound.RemainingHealing(firstRoot, unknown) == int.MaxValue,
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
        MethodInfo summary = typeof(CombatSearchCoordinator).GetMethod("BuildInterimResult",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        SolverInterimResult Quality(CombatRootSnapshot root, SearchPolicySnapshot policy, SolverResult result)
            => (SolverInterimResult)summary.Invoke(null, [root, policy, result])!;
        foreach (string fixture in new[] { "native-closed", "native-closed-hard", "smart-potions", "unknown-source" })
        {
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
            if (fixture == "unknown-source")
                player.PlayerCombatState!.DrawPile.AddInternal(live.CreateCard(CanonicalModels.Card<Feed>(), player), -1);
            CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
            if (fixture == "unknown-source") Check(!root.HasPinnedHealingCertificate,
                "An unknown root source bypassed the certificate rejection.");
            SearchPolicySnapshot policy = fixture.StartsWith("native-closed", StringComparison.Ordinal) ? plain : plain with
            { PotionPolicy = SolverPotionPolicy.Smart, PotionStrategy = new(SolverPotionPolicy.Smart, []) };
            string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
            SolverResult? reference = null;
            // ABBA, each request owns a fresh session and every selected route is replayed independently.
            foreach (bool enabled in new[] { false, true, true, false })
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

                CombatBeamSolver replaySolver = new(root, names, damage, runPolicy, searchProfile: profile);
                SimulationSnapshot replay = replaySolver.ReplayDiagnosticPrefix(result.BestNode.Actions);
                try
                {
                    Check(replaySolver.CaptureDiagnosticContinuation(replay) == result.PredictedFinalState
                        && replay.CumulativePlayerHpLost == result.Snapshot.CumulativePlayerHpLost
                        && replay.AllEnemiesDead == result.Snapshot.AllEnemiesDead,
                        "The selected route's independent replay disagreed with its final state.");
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
            if (fixture == "native-closed" && reference!.Snapshot.AllEnemiesDead)
            {
                SearchOptimizationSession session = new(root);
                session.Observe(reference, policy, Quality(root, policy, reference).StrategicHpDeficit);
                Check(session.Witnesses == 1 && session.Matches(root)
                    && !session.Matches(CombatRootSnapshot.Capture(live)),
                    "A finalized witness was lost or shared with a fresh root.");
                session.Observe(reference, policy with { RelicTargets = [new(RelicCounterId.HappyFlower, 1, 1, 0, 3)] }, 0);
                Check(session.Witnesses == 1, "Resource-goal policy leaked into the HP-only shared bucket.");
            }
            if (fixture == "native-closed-hard")
            {
                SearchOptimizationSession reuse = new(root);
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
            }
        }
        File.WriteAllText(Path.Combine(outputDirectory, "upstream-search.json"), JsonSerializer.Serialize(
            new { requests, replays, evidence = "pinned-offline", nativeMvid = PinnedHealingBound.AuditedNativeMvid,
                rows }, new JsonSerializerOptions { WriteIndented = true }));
        return $"UpstreamSearch PASS requests={requests} independent_replays={replays}; native/UI/Host-Client unverified";
    }
}
