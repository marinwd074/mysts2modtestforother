using System.Reflection;
using System.Text.Json;
using CombatSolver;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace U0U1PinnedHarness;

internal static class MultiplayerResourceAccountingChecks
{
    internal static (int Checks, int Replays) Run(CombatState live, SolverDisplayNames names,
        BattleDamageSnapshot damage, SearchPolicySnapshot captured, string output)
    {
        int checks = 0, replays = 0;
        void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException(message);
            checks++;
        }
        var player = LocalContext.GetMe(live)!;
        var state = player.PlayerCombatState!;
        var enemy = live.Enemies.Single();
        int turn = state.TurnNumber, enemyHp = enemy.CurrentHp;
        var turnProperty = state.GetType().GetProperty("TurnNumber")!;
        var royalty = live.CreateCard(ModelDb.Card<Royalties>(), player);
        state.Hand.AddInternal(royalty, state.Hand.Cards.Count);
        SearchPolicySnapshot policy = captured with
        {
            VerifyIncrementalSearch = false, MeasurePhasePerformance = true,
            MultiplayerPlanRefresh = null, RecoveryBudget = null, Interaction = null,
            ContinuationSeedActions = [], ContinuationRouteReplayActions = [], ContinuationRouteReplayBaseline = null,
            IgnoreLongTermRewards = false, GrowthBudgets = new(Royalties: 3),
            StopAtAcceptableBattleHpLoss = true,
        };
        List<object> rows = [];
        try
        {
            CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
            PlanAction[] route =
            [
                new(PlanActionKind.PlayCard, turn, CardId: "ROYALTIES"),
                new(PlanActionKind.EndTurn, turn),
                new(PlanActionKind.PlayCard, turn + 1, CardId: "STRIKE_IRONCLAD", TargetIndex: 0, TargetCombatId: enemy.CombatId),
                new(PlanActionKind.EndTurn, turn + 1),
                new(PlanActionKind.EndTurn, turn + 2),
            ];
            SolverResult source = new CombatBeamSolver(root, names, damage, policy,
                continuationRouteReplayActions: route).Solve();
            Check(source.Snapshot.GrowthRewards.Royalties == 1 && source.Snapshot.GrowthHpCredit == 3
                && source.Snapshot.RealizedLongTermResourceValue > 0, "Native Royalties route did not realize its growth reward.");
            Check(source.ResourceLedger != null && source.MultiplayerReplayCandidates.All(candidate =>
                candidate.Baseline?.ResourceLedger != null), "Materialization lost resource accounting on the current/alternative route.");
            CombatBeamSolver verifier = new(root, names, damage, policy);
            foreach (CachedContinuation cached in source.Continuations)
            {
                var prefix = verifier.ReplayDiagnosticPrefix(route.Where(action => action.Turn < cached.StartTurnNumber).ToArray());
                try
                {
                    Check(verifier.CaptureDiagnosticContinuation(prefix) == cached.ExpectedState,
                        "Resource checkpoint disagrees with independent native-model replay.");
                    replays++;
                    var expected = cached.MultiplayerExpectation!;
                    MultiplayerContinuationValidation actual = new(expected.CombatIdentity, expected.LocalNetId,
                        expected.MultiplayerScalingHooks, expected.CardMultiplayerConstraint,
                        expected.SourceWorldVersion + 2, expected.SourceWorldVersion + 1);
                    var battle = damage with { HpLostSoFar = damage.HpLostSoFar + prefix.CumulativePlayerHpLost };
                    Check(source.TryCreateContinuation(cached.ExpectedState, cached.StartTurnNumber, prefix.PlayerHp,
                        battle, actual, out var continued, out _), "Resource-normalized native continuation was rejected.");
                    Check(continued!.Snapshot.GrowthRewards == default && continued.Snapshot.GrowthHpCredit == 0
                        && continued.Snapshot.RealizedLongTermResourceValue == 0
                        && continued.Snapshot.CumulativePlayerHpLost == source.Snapshot.CumulativePlayerHpLost - prefix.CumulativePlayerHpLost
                        && continued.ProjectedBattleHpLost == source.ProjectedBattleHpLost,
                        "Already-realized rewards or damage survived as future values after continuation.");
                    var fromOld = MultiplayerPlanRefreshRequest.CaptureContinuation(source, cached.StartTurnNumber);
                    var fromContinued = MultiplayerPlanRefreshRequest.Capture(continued);
                    Check(fromOld.Baseline!.GrowthRewards == fromContinued.Baseline!.GrowthRewards
                        && fromOld.Baseline.DeathSaveUses == fromContinued.Baseline.DeathSaveUses
                        && fromOld.Baseline.ProjectedBattleHpLost == fromContinued.Baseline.ProjectedBattleHpLost,
                        "Direct re-evaluation and exact continuation use different resource windows.");
                }
                finally { prefix.ReleaseSimulator(); }
            }
            Check(source.Continuations.Count == 2, "Native resource fixture did not cover two future checkpoints.");

            // Rebase the same baseline twice: consumed rewards cannot be subtracted again.
            var baseline = MultiplayerPlanRefreshRequest.CaptureBaseline(source)!;
            var twice = baseline.ForTurn(turn + 1)!.ForTurn(turn + 2)!;
            var direct = baseline.ForTurn(turn + 2)!;
            Check(twice.GrowthRewards == direct.GrowthRewards && twice.DeathSaveUses == direct.DeathSaveUses
                && twice.ProjectedBattleHpLost == direct.ProjectedBattleHpLost, "Repeated rebasing charged realized resources twice.");
            Check(baseline.ForTurn(turn + 10) == null, "Missing resource checkpoint was treated as zero history.");

            // Exercise real Coordinator acceptance from a controlled new-turn native
            // root. Removing the already-played power card is an explicit fixture edit.
            state.Hand.RemoveInternal(royalty, silent: true);
            turnProperty.SetValue(state, turn + 1);
            enemy.SetCurrentHpInternal(6);
            CombatRootSnapshot futureRoot = CombatRootSnapshot.Capture(live);
            MultiplayerPlanRefreshRequest request = MultiplayerPlanRefreshRequest.CaptureContinuation(source, turn + 1);
            var alternative = request.Candidates.Single() with { OriginalRank = 1 };
            PlanAction invalid = request.Route[0] with { CardId = "NONEXISTENT_FIXTURE_CARD", CardStateKey = "missing" };
            request = request with { Route = [invalid], Candidates = [alternative] };
            SolverResult Run(string name, MultiplayerPlanRefreshRequest suggestion)
            {
                SolverResult result = CombatSearchCoordinator.Solve(futureRoot, names, damage,
                    policy with { MultiplayerPlanRefresh = suggestion }, CancellationToken.None, null);
                CombatBeamSolver replaySolver = new(futureRoot, names, damage, policy);
                var replay = replaySolver.ReplayDiagnosticPrefix(result.BestNode.Actions);
                try
                {
                    Check(replaySolver.CaptureDiagnosticContinuation(replay) == result.PredictedFinalState
                        && replay.GrowthRewards == result.Snapshot.GrowthRewards,
                        "Resource-window acceptance differs from independent replay.");
                    replays++;
                }
                finally { replay.ReleaseSimulator(); }
                rows.Add(new { name, result.PlanRefresh, result.TotalExpandedNodes, result.ProjectedBattleHpLost,
                    result.Snapshot.GrowthRewards, result.Snapshot.GrowthHpCredit });
                return result;
            }
            SolverResult oldWindow = Run("legacy-root-growth-window", request with
            {
                Baseline = request.Baseline! with { GrowthRewards = source.Snapshot.GrowthRewards },
            });
            Check(oldWindow.PlanRefresh!.FullRoutes.Any(item => item.Outcome == "alternative_growth_changed")
                && !oldWindow.PlanRefresh.WholeRouteReplayed, "The old-window regression fixture did not reject the already-earned reward.");
            SolverResult normalized = Run("remaining-growth-window", request);
            Check(normalized.PlanRefresh is { WholeRouteReplayed: true, SelectedOriginalRank: 1 }
                && normalized.TotalExpandedNodes == 0 && normalized.Snapshot.AllEnemiesDead
                && normalized.Snapshot.GrowthRewards == default, "Normalized recovery demanded earning the previous turn's reward again.");

            CheckResourceArithmetic(source, Check);
        }
        finally
        {
            if (state.Hand.Cards.Contains(royalty)) state.Hand.RemoveInternal(royalty, silent: true);
            turnProperty.SetValue(state, turn);
            enemy.SetCurrentHpInternal(enemyHp);
        }
        File.WriteAllText(Path.Combine(output, "multiplayer-resource-accounting.json"), JsonSerializer.Serialize(new
        {
            status = "PASS", checks, independent_replays = replays,
            evidence = "native-model Royalties route/checkpoints; controlled new-turn root; arithmetic death-save/healing contracts; no Controller/network/UI",
            rows,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return (checks, replays);
    }

    private static void CheckResourceArithmetic(SolverResult native, Action<bool, string> check)
    {
        GrowthValues pastGrowth = new(Royalties: 1) { ThirdParty = new() { ["fixture.growth"] = 2 } };
        GrowthValues finalGrowth = new(Royalties: 2) { ThirdParty = new() { ["fixture.growth"] = 3 } };
        RouteResourceTotals past = new(9, 4, 3, 1, 1, 10, 2, 3, pastGrowth);
        RouteResourceLedger ledger = new(1, [new(2, past)]);
        SolverSnapshot total = native.Snapshot with
        {
            CumulativePlayerHpLost = 19, RecoveredPlayerHp = 14, PlayerHp = 80,
            DeathSaveRelicHpRestored = 3, DeathSavePotionHpRestored = 6,
            DeathSaveUseCount = 2, ProjectedDeathSaveUseCount = 3,
            RealizedLongTermResourceValue = 30, LongTermResourceValue = 27,
            AngerCopiesGenerated = 3, GrowthHpCredit = 6, GrowthRewards = finalGrowth,
        };
        check(ledger.TryRebase(total, 2, out var remaining, out _), "Consistent resource ledger rejected.");
        check(remaining.CumulativePlayerHpLost == 10 && remaining.RecoveredPlayerHp == 10,
            "Healing erased gross future damage.");
        check(remaining.DeathSaveUseCount == 1 && remaining.ProjectedDeathSaveUseCount == 2
            && remaining.DeathSaveRelicHpRestored == 0 && remaining.DeathSavePotionHpRestored == 5,
            "Realized death saves were mixed with projected future saves.");
        check(remaining.GrowthRewards.Royalties == 1 && remaining.GrowthRewards.ThirdParty!["fixture.growth"] == 1
            && remaining.GrowthHpCredit == 3 && remaining.RealizedLongTermResourceValue == 20
            && remaining.LongTermResourceValue == 17 && remaining.RelicCounters == total.RelicCounters,
            "Resource rebasing lost third-party growth, terminal penalties, or final relic state.");
        check(!ledger.TryRebase(total with { DeathSaveUseCount = 0 }, 2, out _, out _)
            && !ledger.TryRebase(total with { GrowthRewards = default }, 2, out _, out _),
            "Inconsistent resource history was silently clamped into a valid continuation.");
        var clone = (SolverResult)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(native, null)!;
        typeof(SolverResult).GetProperty(nameof(SolverResult.Snapshot))!.SetValue(clone, total);
        clone.ResourceLedger = ledger;
        var expected = native.Continuations.First(item => item.StartTurnNumber == 2);
        var identity = expected.MultiplayerExpectation!;
        MultiplayerContinuationValidation validation = new(identity.CombatIdentity, identity.LocalNetId,
            identity.MultiplayerScalingHooks, identity.CardMultiplayerConstraint, identity.SourceWorldVersion + 2, identity.SourceWorldVersion + 1);
        check(clone.TryCreateContinuation(expected.ExpectedState, 2, 80, new(9, 0, 0), validation,
            out var continued, out _) && continued!.ProjectedBattleHpLost == 19,
            "Continuation used net zero HP change instead of the remaining ten damage.");
    }
}
