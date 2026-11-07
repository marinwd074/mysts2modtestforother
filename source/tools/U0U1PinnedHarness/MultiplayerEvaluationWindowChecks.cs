using System.Reflection;
using System.Text.Json;
using CombatSolver;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Potions;

namespace U0U1PinnedHarness;

internal static class MultiplayerEvaluationWindowChecks
{
    internal static string Run(CombatState live, SolverDisplayNames names, BattleDamageSnapshot damage,
        SearchPolicySnapshot captured, string output)
    {
        int checks = 0;
        void Check(bool ok, string reason)
        {
            if (!ok) throw new InvalidOperationException(reason);
            checks++;
        }
        var player = LocalContext.GetMe(live)!;
        foreach (PotionModel potion in player.PotionSlots.OfType<PotionModel>().ToArray())
            player.RemovePotionInternal(potion);
        Check(player.AddPotionInternal(ModelDb.Potion<BlockPotion>().ToMutable(), 0, true).success, "Block potion fixture failed.");
        var policy = captured with
        {
            RoutePolicy = SearchRoutePolicy.MultiplayerSinglePlayerCore, CurrentTurnOnly = false,
            IncludeTurnSetup = false, UseMultiplayerTeamObjective = false, UseMultiplayerTeammateForecast = false,
            UseMultiplayerScenarioReevaluation = false, UseNoveltyPortfolio = false, NoveltySearch = null,
            UseBeamWidthPortfolio = false, IgnoreLongTermRewards = true, RelicTargets = [],
            PotionPolicy = SolverPotionPolicy.Smart, PotionStrategy = new(SolverPotionPolicy.Smart, []),
            MaxDegreeOfParallelism = 1, OptimizationSession = null,
            Diagnostics = new SearchDiagnosticsSink(_ => { }, _ => { }),
        };
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        string before = ContinuationStamp.CaptureLive(live).StateText;
        CombatBeamSolver solver = new(root, names, damage, policy);
        List<SimulationSnapshot> snapshots = [];
        int nativePrefixReplays = 0;
        SearchNode Build(params PlanAction[] actions)
        {
            SearchNode? parent = null;
            for (int count = 0; count <= actions.Length; count++)
            {
                var snapshot = solver.ReplayDiagnosticPrefix(actions.Take(count).ToArray());
                nativePrefixReplays++;
                snapshots.Add(snapshot);
                parent = new(count == 0 ? null : actions[count - 1], count,
                    snapshot.PotionUseCount, snapshot.PotionStrategicCost, snapshot.Turn,
                    SearchRouteTraits.None, 0, snapshot.Score, snapshot.StateKey, snapshot.HasRisk,
                    snapshot.BoundaryReason, snapshot.AllEnemiesDead || snapshot.PlayerDead,
                    parent, snapshot, CombatProgressState.Capture(snapshot));
            }
            return parent!;
        }
        try
        {
            int turn = root.StartTurnNumber;
            PlanAction end = new(PlanActionKind.EndTurn, turn);
            SearchNode shortFree = Build(end);
            SearchNode longFree = Build(end, end with { Turn = turn + 1 }, end with { Turn = turn + 2 });
            SearchNode shortPotion = Build(new(PlanActionKind.UsePotion, turn, PotionSlot: 0, PotionId: "BLOCK_POTION"), end);
            Check(shortFree.Snapshot.EvaluationWindow is { Resolved: true }
                && longFree.Snapshot.EvaluationWindow is { Resolved: true }, "Native replay did not capture resolved coverage.");
            Check(RouteEvaluationWindow.CanCompare(shortFree.Snapshot.EvaluationWindow, shortPotion.Snapshot.EvaluationWindow)
                && !RouteEvaluationWindow.CanCompare(longFree.Snapshot.EvaluationWindow, shortPotion.Snapshot.EvaluationWindow),
                "Short and long native routes were treated as the same cost window.");
            int apparentSaving = longFree.Snapshot.CumulativePlayerHpLost - shortPotion.Snapshot.CumulativePlayerHpLost;
            Check(PotionUsePolicy.IsEligible(SolverPotionPolicy.Smart, 1, shortPotion.PotionStrategicCost,
                false, longFree.Snapshot.CumulativePlayerHpLost, false, false, shortPotion.Snapshot.CumulativePlayerHpLost),
                "Fixture does not reproduce the old cross-window potion admission.");
            var mismatch = solver.SelectDiagnosticCandidates([longFree, shortPotion]);
            var reversed = solver.SelectDiagnosticCandidates([shortPotion, longFree]);
            Check(ReferenceEquals(mismatch.Selected, longFree) && ReferenceEquals(reversed.Selected, longFree)
                && mismatch.Rejected == 1 && mismatch.HpSaved == 0, "Final ordering still accepts a short potion route on false HP savings.");
            var sameWindow = solver.SelectDiagnosticCandidates([shortFree, shortPotion]);
            var withLongWindow = solver.SelectDiagnosticCandidates([longFree, shortFree, shortPotion]);
            Check(ReferenceEquals(sameWindow.Selected, shortPotion)
                && ReferenceEquals(withLongWindow.Selected, longFree)
                && sameWindow.Rejected == withLongWindow.Rejected,
                "Completed coverage failed to replace the shallow suggestion or changed its potion admission.");

            RouteEvaluationWindow full = longFree.Snapshot.EvaluationWindow!.Value;
            var completed = solver.CompleteDiagnosticEvaluationWindows([longFree, shortPotion]);
            SearchNode extendedPotion = completed.Candidates.Single(node => !ReferenceEquals(node, longFree));
            if (!ReferenceEquals(extendedPotion, shortPotion)) snapshots.Add(extendedPotion.Snapshot);
            Check(!ReferenceEquals(extendedPotion, shortPotion) && completed.Expanded == 2 && completed.Transitions == 2
                && RouteEvaluationWindow.CanCompare(extendedPotion.Snapshot.EvaluationWindow, full)
                && shortPotion.Snapshot.HasSimulator && longFree.Snapshot.HasSimulator
                && extendedPotion.Actions.Take(shortPotion.ActionCount).SequenceEqual(shortPotion.Actions),
                "Tail completion failed to preserve the candidate prefix/ownership or charged the wrong work.");
            SimulationSnapshot independent = solver.ReplayDiagnosticPrefix(extendedPotion.Actions);
            nativePrefixReplays++;
            snapshots.Add(independent);
            Check(independent.StateKey == extendedPotion.StateKey
                && independent.CumulativePlayerHpLost == extendedPotion.Snapshot.CumulativePlayerHpLost
                && ReferenceEquals(solver.SelectDiagnosticCandidates(completed.Candidates).Selected, extendedPotion),
                "Completed candidate disagrees with independent replay or loses its valid same-window improvement.");
            SolverSearchProfile oneStep = SolverSearchProfile.Default with { MaxExpandedNodes = 1, SoftTimeBudgetMilliseconds = 20_000 };
            var incomplete = new CombatBeamSolver(root, names, damage, policy, searchProfile: oneStep)
                .CompleteDiagnosticEvaluationWindows([longFree, shortPotion]);
            Check(incomplete.Expanded == 1 && incomplete.Transitions == 1
                && ReferenceEquals(incomplete.Candidates[1], shortPotion)
                && shortPotion.Snapshot.HasSimulator && longFree.Snapshot.HasSimulator
                && ReferenceEquals(solver.SelectDiagnosticCandidates(incomplete.Candidates).Selected, longFree),
                "Exhausted completion budget lost the original candidate or treated its missing tail as free.");
            SearchNode mediumFree = longFree.Parent!;
            RouteEvaluationWindow mediumWindow = mediumFree.Snapshot.EvaluationWindow!.Value;
            var partialScope = solver.SelectDiagnosticCandidates([mediumFree, shortFree, shortPotion], mediumWindow);
            var reversedPartialScope = solver.SelectDiagnosticCandidates([shortPotion, shortFree, mediumFree], mediumWindow);
            Check(!mediumWindow.CoversTarget && ReferenceEquals(partialScope.Selected, mediumFree)
                && ReferenceEquals(reversedPartialScope.Selected, mediumFree),
                "A shorter fallback changed the committed partial comparison scope or depended on input ordering.");
            var alignedPartial = solver.CompleteDiagnosticEvaluationWindows([mediumFree, shortPotion], mediumWindow);
            SearchNode mediumPotion = alignedPartial.Candidates[1];
            if (!ReferenceEquals(mediumPotion, shortPotion)) snapshots.Add(mediumPotion.Snapshot);
            SimulationSnapshot partialReplay = solver.ReplayDiagnosticPrefix(mediumPotion.Actions);
            nativePrefixReplays++;
            snapshots.Add(partialReplay);
            Check(alignedPartial.Expanded == 1 && alignedPartial.Transitions == 1
                && !mediumPotion.Snapshot.EvaluationWindow!.Value.CoversTarget
                && RouteEvaluationWindow.CanCompare(mediumPotion.Snapshot.EvaluationWindow, mediumWindow)
                && partialReplay.StateKey == mediumPotion.StateKey
                && ReferenceEquals(solver.SelectDiagnosticCandidates(alignedPartial.Candidates, mediumWindow).Selected, mediumPotion),
                "Completion to a partial frontier failed, fabricated full coverage or lost a same-scope improvement.");
            RouteEvaluationWindow victory = shortFree.Snapshot.EvaluationWindow!.Value with { CompleteVictory = true };
            Check(RouteEvaluationWindow.CanCompare(victory, full)
                && !RouteEvaluationWindow.CanCompare(victory, full with { AtPlayerDecisionPoint = false })
                && !RouteEvaluationWindow.CanCompare(victory, shortFree.Snapshot.EvaluationWindow)
                && !RouteEvaluationWindow.CanCompare(full with { Resolved = false }, full)
                && !RouteEvaluationWindow.CanCompare(full with { EvaluatedTurn = turn, PlayerPhase = 1 },
                    full with { EvaluatedTurn = turn, PlayerPhase = 2 }),
                "Terminal, unresolved or within-turn phase coverage was conflated.");
            Check(!RouteEvaluationWindow.CanCompare(full.ForTurn(turn + 1), victory)
                && full.ForTurn(turn + 1)?.TargetTurn == full.TargetTurn + 1,
                "Continuation retained the old root or pretended to cover a new third turn.");
            SolverResult materialized = new CombatBeamSolver(root, names, damage, policy,
                continuationRouteReplayActions: longFree.Actions).Solve();
            Check(materialized.ExpandedNodes == 0 && materialized.EvaluationWindow == full
                && MultiplayerPlanRefreshRequest.Capture(materialized).EvaluationWindow == full
                && MultiplayerPlanRefreshRequest.CaptureContinuation(materialized, turn + 1).EvaluationWindow == full.ForTurn(turn + 1),
                "Materialization or refresh capture lost/reused an obsolete evaluation window.");
            var recovery = typeof(CombatSearchCoordinator).GetMethod("CanAcceptRecoveredAlternative",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            object?[] unchanged = [MultiplayerPlanRefreshRequest.Capture(materialized), materialized, materialized, null];
            object?[] wrongPhase = [MultiplayerPlanRefreshRequest.Capture(materialized) with
            {
                EvaluationWindow = full with { PlayerPhase = (int)PlayerTurnPhase.Start, AtPlayerDecisionPoint = false },
            }, materialized, materialized, null];
            Check((bool)recovery.Invoke(null, unchanged)! && !(bool)recovery.Invoke(null, wrongPhase)!
                && (string?)wrongPhase[3] == "evaluation_window_mismatch",
                "Recovery confused equal turn numbers with equal settled phases.");
            Check(PotionUsePolicy.CanAcceptSmartAuditVictory(false, true, 0, 8, false, false)
                && !PotionUsePolicy.CanAcceptSmartAuditVictory(true, true, 20, 8, false, false),
                "Window gating removed a proven win rescue or admitted unproven cost savings.");
            Check(PotionUsePolicy.IsEligible(SolverPotionPolicy.Smart, 1, 0, false, 15, false, false, 0, false)
                && !PotionUsePolicy.IsEligible(SolverPotionPolicy.Smart, 1, 8, false, 15, false, false, 0, false),
                "A missing comparison charged a free generated potion or excused a paid potion.");
            Check(ContinuationStamp.CaptureLive(live).StateText == before, "Window evaluation mutated the native live fixture.");
            SimulationSnapshot single = new CombatBeamSolver(root, names, damage,
                policy with { RoutePolicy = SearchRoutePolicy.SinglePlayerFullRoute }).ReplayDiagnosticPrefix([end]);
            nativePrefixReplays++;
            snapshots.Add(single);
            Check(single.EvaluationWindow is { Resolved: true, TargetTurn: null }
                && single.EvaluationWindow.Value.EvaluatedTurn == shortFree.Snapshot.Turn
                && !RouteEvaluationWindow.CanCompare(single.EvaluationWindow, shortFree.Snapshot.EvaluationWindow)
                && single.EvaluationWindow.Value.ForTurn(turn + 1) is { TargetTurn: null } rebased
                && rebased.StartTurn == turn + 1
                && !SearchEvaluationPolicy.HasReachedTurnLayerLimit(SearchRoutePolicy.SinglePlayerFullRoute, int.MaxValue),
                "Shared window capture changed the singleplayer horizon or conflated it with a finite projection.");
            RouteEvaluationWindow singleWindow = single.EvaluationWindow!.Value;
            Check(RouteEvaluationWindow.CanCompare(singleWindow with { CompleteVictory = true },
                    singleWindow with { CompleteVictory = true, EvaluatedTurn = turn + 8 })
                && !RouteEvaluationWindow.CanCompare(singleWindow with { CompleteVictory = true }, singleWindow)
                && (singleWindow with { EvaluatedTurn = turn + 8 }).ComparisonKey != null,
                "Singleplayer coverage was capped at three turns or a partial route became a complete battle.");
            SolverInterimResult partial = new(false, 0, 0, 0, 0, 0, EnemyHp: 10, Score: 0)
                { Survives = true, EvaluationWindow = full };
            SolverInterimResult win = partial with { Won = true, ProjectedBattleHpLost = 2,
                StrategicHpDeficit = 2, CombatEndedTurn = turn, EnemyHp = 0,
                EvaluationWindow = full with { CompleteVictory = true } };
            Check(SolverInterimResultOrdering.IsBetter(win, partial)
                && CombatSearchCoordinator.IsBetterPotionPolicyResult(null, win, partial)
                && !SolverInterimResultOrdering.IsBetter(win with { RollingHorizonLossFirst = true },
                    partial with { RollingHorizonLossFirst = true })
                && !CombatSearchCoordinator.IsBetterPotionPolicyResult(null,
                    win with { RollingHorizonLossFirst = true }, partial with { RollingHorizonLossFirst = true }),
                "Shared outcome ordering does not preserve singleplayer victory-first and rolling raw-loss-first priorities.");
            Check(TheftEncounterStrategy.CompareRecovery(null, 0, 10) == 0
                && TheftEncounterStrategy.CompareRecovery(SolverTheftPolicy.LetEscape, 0, 10) == 0
                && TheftEncounterStrategy.CompareRecovery(SolverTheftPolicy.PreserveResources, 0, 10) < 0,
                "Recovery ordering no longer respects the requested resource policy.");
            SolverInterimResult shallow = partial with
            { RollingHorizonLossFirst = true, EvaluationWindow = shortFree.Snapshot.EvaluationWindow };
            SolverInterimResult settled = shallow with
            { EvaluationWindow = full, ProjectedBattleHpLost = 15, StrategicHpDeficit = 15 };
            Check(SolverInterimResultOrdering.IsBetter(settled, shallow)
                && SolverInterimResultOrdering.CanPromoteDisplayedResult(settled, shallow)
                && CombatSearchCoordinator.IsBetterPotionPolicyResult(null, settled, shallow)
                && !SolverInterimResultOrdering.IsBetter(shallow, settled)
                && !CombatSearchCoordinator.IsBetterPotionPolicyResult(null, shallow, settled),
                "A cheap incomplete tail replaced a surviving route with completed requested coverage.");
            SolverInterimResult exposed = settled with { Survives = false, SurvivesEvaluationWindow = true };
            Check(SolverInterimResultOrdering.IsBetter(exposed, shallow)
                && CombatSearchCoordinator.IsBetterPotionPolicyResult(null, exposed, shallow)
                && SolverInterimResultOrdering.CompareCompletedCoverage(
                    exposed with { SurvivesEvaluationWindow = false }, shallow) == 0,
                "Unplayed threat was treated as actual death at the evaluated window, or actual death certified coverage.");
            SolverInterimResult medium = settled with { EvaluationWindow = mediumWindow };
            SolverInterimResult cheapShallow = shallow with { Score = 1_000_000 };
            Check(!SolverInterimResultOrdering.IsBetter(cheapShallow, medium)
                && !SolverInterimResultOrdering.IsBetter(medium, cheapShallow)
                && !CombatSearchCoordinator.IsBetterPotionPolicyResult(null, cheapShallow, medium)
                && !CombatSearchCoordinator.IsBetterPotionPolicyResult(null, medium, cheapShallow)
                && !CombatSearchCoordinator.IsBetterPotionPolicyResult(null,
                    cheapShallow with { EvaluationWindow = null }, medium),
                "Incomparable partial/missing windows were treated as better, equal-cost or a score tie-break.");
            SolverSearchProfile singleProfile = new(4, 2_000, 8, 4, 4, 20_000);
            SearchPolicySnapshot singlePolicy = policy with
            {
                RoutePolicy = SearchRoutePolicy.SinglePlayerFullRoute,
                PotionPolicy = SolverPotionPolicy.Disabled, PotionStrategy = new(SolverPotionPolicy.Disabled, []),
                FixedBudget = true, VerifyIncrementalSearch = true,
            };
            SolverResult singleRoute = new CombatBeamSolver(root, names, damage, singlePolicy,
                searchProfile: singleProfile).Solve();
            Check(singleRoute.EvaluationWindow is { TargetTurn: null, CompleteVictory: true }
                && singleRoute.CombatEndedTurn > turn + 2 && singleRoute.ExpandedNodes <= singleProfile.MaxExpandedNodes,
                "The full singleplayer search did not complete beyond the multiplayer three-turn horizon.");
            CombatBeamSolver singleVerifier = new(root, names, damage, singlePolicy);
            SimulationSnapshot singleReplay = singleVerifier.ReplayDiagnosticPrefix(singleRoute.BestNode.Actions);
            nativePrefixReplays++;
            snapshots.Add(singleReplay);
            Check(singleReplay.AllEnemiesDead && !singleReplay.PlayerDead
                && singleRoute.PredictedFinalState != null
                && singleVerifier.CaptureDiagnosticContinuation(singleReplay) == singleRoute.PredictedFinalState
                && singleReplay.CumulativePlayerHpLost == singleRoute.Snapshot.CumulativePlayerHpLost
                && ContinuationStamp.CaptureLive(live).StateText == before,
                "The full singleplayer result disagrees with independent native replay or changed the live fixture.");

            // Compare two real terminal replays against a controlled audited deficit.
            var enemy = live.Enemies.Single();
            enemy.SetCurrentHpInternal(6);
            foreach (var relic in player.Relics.ToArray()) player.RemoveRelicInternal(relic, silent: true);
            player.PlayerCombatState!.Hand.AddInternal(live.CreateCard(ModelDb.Card<Hemokinesis>(), player), -1);
            Check(player.AddPotionInternal(ModelDb.Potion<SwiftPotion>().ToMutable(), 1, true).success,
                "Swift Potion fixture failed.");
            var auditRoot = CombatRootSnapshot.Capture(live);
            string auditBefore = ContinuationStamp.CaptureLive(live).StateText;
            solver = new(auditRoot, names, damage, policy);
            PlanAction strike = new(PlanActionKind.PlayCard, turn, "STRIKE_IRONCLAD", TargetIndex: 0,
                TargetCombatId: enemy.CombatId);
            SearchNode expensive = Build(new(PlanActionKind.UsePotion, turn, PotionSlot: 1, PotionId: "SWIFT_POTION"), strike);
            SearchNode affordable = Build(new(PlanActionKind.UsePotion, turn, PotionSlot: 0, PotionId: "BLOCK_POTION"),
                strike with { CardId = "HEMOKINESIS" });
            int auditDeficit = affordable.Snapshot.CumulativePlayerHpLost + affordable.PotionStrategicCost;
            Check(expensive.Snapshot.CombatEndedTurn == turn && affordable.Snapshot.CombatEndedTurn == turn
                && expensive.Snapshot.CumulativePlayerHpLost == 0 && affordable.Snapshot.CumulativePlayerHpLost == 2
                && expensive.PotionStrategicCost > auditDeficit,
                "Expected a cheaper legal win and a lower-damage but unaffordable win.");
            PotionFreePolicyBaseline audited = new(true, auditDeficit, auditRoot.InitialPlayerHp - auditDeficit, turn)
            {
                BattleHpLost = damage.HpLostSoFar + auditDeficit,
                EvaluationWindow = affordable.Snapshot.EvaluationWindow,
            };
            CombatBeamSolver exactSmart = new(auditRoot, names, damage, policy,
                potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne, potionFreePolicyBaseline: audited,
                minimumPotionUses: 1, maximumPotionUses: 1);
            var exact = exactSmart.SelectDiagnosticCandidates([expensive, affordable]);
            Console.WriteLine($"SMART_LAYER_SELECTION potion={exact.Selected.Actions.First().PotionId} "
                + $"loss={exact.Selected.Snapshot.CumulativePlayerHpLost} baseline={auditDeficit}");
            Check(ReferenceEquals(exact.Selected, affordable)
                && ReferenceEquals(exactSmart.SelectDiagnosticCandidates([affordable, expensive]).Selected, affordable),
                "An unaffordable win hid the eligible Smart win.");
            CombatBeamSolver required = new(auditRoot, names, damage,
                policy with { PotionPolicy = SolverPotionPolicy.RequireAtLeastOne },
                potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne, potionFreePolicyBaseline: audited,
                minimumPotionUses: 1, maximumPotionUses: 1);
            Check(ReferenceEquals(required.SelectDiagnosticCandidates([expensive, affordable]).Selected, expensive),
                "A required-potion request was ranked as an optional Smart audit.");
            CombatBeamSolver mismatchedAudit = new(auditRoot, names, damage, policy,
                potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne,
                potionFreePolicyBaseline: audited with { EvaluationWindow = null },
                minimumPotionUses: 1, maximumPotionUses: 1);
            Check(ReferenceEquals(mismatchedAudit.SelectDiagnosticCandidates([expensive, affordable]).Selected, expensive),
                "A missing audit window changed the rank based on unproven savings.");
            Check(ContinuationStamp.CaptureLive(live).StateText == auditBefore, "Layer selection mutated the live fixture.");

            File.WriteAllText(Path.Combine(output, "multiplayer-window.json"), JsonSerializer.Serialize(new
            {
                checks, NativePrefixReplays = nativePrefixReplays, MaterializedRouteReplays = 1, apparentSaving,
                CompletionWork = new { completed.Expanded, completed.Transitions },
                ExhaustedCompletionWork = new { incomplete.Expanded, incomplete.Transitions },
                completedLoss = extendedPotion.Snapshot.CumulativePlayerHpLost,
                PartialCompletion = new { alignedPartial.Expanded, alignedPartial.Transitions,
                    Loss = mediumPotion.Snapshot.CumulativePlayerHpLost, Window = mediumPotion.Snapshot.EvaluationWindow },
                SingleFullSearch = new { singleRoute.CombatEndedTurn, singleRoute.ExpandedNodes,
                    singleRoute.Snapshot.CumulativePlayerHpLost },
                ShortWindow = shortFree.Snapshot.EvaluationWindow, LongWindow = full,
                shortLoss = shortFree.Snapshot.CumulativePlayerHpLost,
                longLoss = longFree.Snapshot.CumulativePlayerHpLost,
                potionLoss = shortPotion.Snapshot.CumulativePlayerHpLost,
                matchingWindowPotionSelected = ReferenceEquals(sameWindow.Selected, shortPotion),
                SmartLayerSelection = new { auditDeficit, affordable.PotionStrategicCost,
                    SelectedPotion = exact.Selected.Actions.First().PotionId,
                    exact.Selected.Snapshot.CumulativePlayerHpLost },
                NoGameOrNetwork = true,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return $"MULTIPLAYER_WINDOW_PASS checks={checks} native_prefix_replays={nativePrefixReplays}";
        }
        finally { foreach (var snapshot in snapshots) snapshot.ReleaseSimulator(); }
    }
}
