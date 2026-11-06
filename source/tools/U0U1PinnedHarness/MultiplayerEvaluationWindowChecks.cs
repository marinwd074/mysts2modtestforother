using System.Reflection;
using System.Text.Json;
using CombatSolver;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Models;
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
        SearchNode Build(params PlanAction[] actions)
        {
            SearchNode? parent = null;
            for (int count = 0; count <= actions.Length; count++)
            {
                var snapshot = solver.ReplayDiagnosticPrefix(actions.Take(count).ToArray());
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
            Check(ReferenceEquals(sameWindow.Selected, withLongWindow.Selected),
                "An unrelated long baseline changed admission against the available matching baseline.");

            RouteEvaluationWindow full = longFree.Snapshot.EvaluationWindow!.Value;
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
            Check(CombatSearchCoordinator.IsSmartPotionGradientCandidateAcceptable(false, true, 0, 8, false, false)
                && !CombatSearchCoordinator.IsSmartPotionGradientCandidateAcceptable(true, true, 20, 8, false, false),
                "Window gating removed a proven win rescue or admitted unproven cost savings.");
            Check(PotionUsePolicy.IsEligible(SolverPotionPolicy.Smart, 1, 0, false, 15, false, false, 0, false)
                && !PotionUsePolicy.IsEligible(SolverPotionPolicy.Smart, 1, 8, false, 15, false, false, 0, false),
                "A missing comparison charged a free generated potion or excused a paid potion.");
            Check(ContinuationStamp.CaptureLive(live).StateText == before, "Window evaluation mutated the native live fixture.");
            SimulationSnapshot single = new CombatBeamSolver(root, names, damage,
                policy with { RoutePolicy = SearchRoutePolicy.SinglePlayerFullRoute }).ReplayDiagnosticPrefix([end]);
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
            File.WriteAllText(Path.Combine(output, "multiplayer-window.json"), JsonSerializer.Serialize(new
            {
                checks, NativePrefixReplays = snapshots.Count, MaterializedRouteReplays = 1, apparentSaving,
                ShortWindow = shortFree.Snapshot.EvaluationWindow, LongWindow = full,
                shortLoss = shortFree.Snapshot.CumulativePlayerHpLost,
                longLoss = longFree.Snapshot.CumulativePlayerHpLost,
                potionLoss = shortPotion.Snapshot.CumulativePlayerHpLost,
                matchingWindowPotionSelected = ReferenceEquals(sameWindow.Selected, shortPotion),
                NoGameOrNetwork = true,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return $"MULTIPLAYER_WINDOW_PASS checks={checks} native_prefix_replays={snapshots.Count}";
        }
        finally { foreach (var snapshot in snapshots) snapshot.ReleaseSimulator(); }
    }
}
