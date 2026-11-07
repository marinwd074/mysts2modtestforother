using System.Text.Json;
using CombatSolver;
using MegaCrit.Sts2.Core.Combat;

namespace U0U1PinnedHarness;

internal static class MultiplayerFullRouteChecks
{
    internal static (int Checks, int Replays) Run(CombatState live, SolverDisplayNames names,
        BattleDamageSnapshot damage, SearchPolicySnapshot captured, SolverResult source, string output)
    {
        int checks = 0, replays = 0;
        List<object> rows = [];
        List<object> comparisons = [];
        SearchPolicySnapshot policy = captured with
        {
            VerifyIncrementalSearch = false, MeasurePhasePerformance = true,
            MultiplayerPlanRefresh = null, RecoveryBudget = null, Interaction = null,
            ContinuationSeedActions = [], ContinuationRouteReplayActions = [], ContinuationRouteReplayBaseline = null,
            BudgetOverrideMilliseconds = null,
            StopAtAcceptableBattleHpLoss = true,
        };
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        MultiplayerPlanRefreshRequest request = MultiplayerPlanRefreshRequest.Capture(source);
        PlanAction invalid = request.Route[0] with { CardId = "NONEXISTENT_FIXTURE_CARD", CardStateKey = "missing" };
        void Check(bool ok, string reason)
        {
            if (!ok) throw new InvalidOperationException(reason);
            checks++;
        }
        Check(request.Candidates.Count == 3 && request.Candidates.All(candidate => candidate.Route.Count > 2
            && candidate.Baseline != null), "The ranked native fixture did not preserve three full routes and baselines.");

        SolverResult Run(string name, CombatRootSnapshot frozen, MultiplayerPlanRefreshRequest suggestion,
            Action<string>? log = null, bool stopAtTarget = true)
        {
            SolverResult result = CombatSearchCoordinator.Solve(frozen, names, damage, policy with
            {
                MultiplayerPlanRefresh = suggestion,
                StopAtAcceptableBattleHpLoss = stopAtTarget,
                Diagnostics = new SearchDiagnosticsSink(log ?? (_ => { }), _ => { }),
            }, CancellationToken.None, null);
            CombatBeamSolver verifier = new(frozen, names, damage, policy);
            var replay = verifier.ReplayDiagnosticPrefix(result.BestNode.Actions);
            try
            {
                Check(verifier.CaptureDiagnosticContinuation(replay) == result.PredictedFinalState
                    && replay.CumulativePlayerHpLost == result.Snapshot.CumulativePlayerHpLost,
                    $"{name}: selected full route differs from independent native-model replay.");
                replays++;
            }
            finally { replay.ReleaseSimulator(); }
            Check(result.ResultScope == SolverResultScope.SearchCompletion
                && result.BestNode.Actions.All(action => action.ShadowForecast == null && !action.IsForecastOnlyObservation),
                $"{name}: automatic recovery gained execution authority or predicted a teammate action.");
            var work = result.RequestPhaseMetrics!;
            Check(result.RecoveryBudgetMetrics!.ExpandedNodes == work.ExpandedNodes
                && work.Members.Sum(member => (long)member.Work.ExpandedNodes) == work.ExpandedNodes
                && work.Members.Sum(member => (long)member.Work.TransitionCount) == work.LogicalTransitions,
                $"{name}: a failed alternative escaped the request ledger.");
            rows.Add(new { name, report = result.PlanRefresh, result.TotalExpandedNodes, result.TotalTransitionCount,
                result.ProjectedBattleHpLost, won = result.Snapshot.AllEnemiesDead, actions = result.BestNode.ActionCount });
            Console.WriteLine($"FullRoutes case={name} selected={result.PlanRefresh?.SelectedOriginalRank} "
                + $"expanded={result.TotalExpandedNodes} actions={result.BestNode.ActionCount} won={result.Snapshot.AllEnemiesDead}");
            return result;
        }

        SolverResult unchanged = Run("all-full-routes", root, request);
        Check(unchanged.PlanRefresh is { WholeRouteReplayed: true, SelectedOriginalRank: 0 }
            && unchanged.PlanRefresh.FullRoutes.Count == 3 && unchanged.TotalExpandedNodes == 0,
            "An unchanged current route skipped alternative review or lost its quality tie.");
        Check(unchanged.MultiplayerReplayCandidates.Count == 3
            && unchanged.MultiplayerReplayCandidates.Select(candidate => candidate.OriginalRank).SequenceEqual([0, 1, 2]),
            "Newly proved alternatives were lost before the next refresh.");
        SolverResult repeated = Run("second-refresh", root, MultiplayerPlanRefreshRequest.Capture(unchanged));
        Check(repeated.PlanRefresh!.FullRoutes.Count == 3 && repeated.TotalExpandedNodes == 0,
            "A second refresh forgot retained full routes or restarted Beam search.");

        MultiplayerPlanRefreshRequest broken = request with { Route = [invalid] };
        SolverResult recovered = Run("current-invalid-alternative-valid", root, broken);
        Check(recovered.PlanRefresh is { WholeRouteReplayed: true, Decision: MultiplayerPlanRefreshDecision.Reselect,
            SelectedOriginalRank: > 0 } && recovered.TotalExpandedNodes == 0,
            "A valid full alternative could not recover an invalid current route.");

        var alternative = request.Candidates[1];
        PlanAction[] badTail = alternative.Route.ToArray();
        badTail[2] = invalid with { Turn = badTail[2].Turn };
        MultiplayerReplayCandidate rejected = alternative with { Route = badTail };
        SolverResult isolated = Run("invalid-third-action", root, request with
        {
            Candidates = [request.Candidates[0], rejected, request.Candidates[2]],
        });
        Check(isolated.PlanRefresh!.SelectedOriginalRank == 0
            && isolated.PlanRefresh.FullRoutes.Any(item => item.OriginalRank == rejected.OriginalRank
                && item.Outcome == "route_action_unavailable"),
            "A valid two-card prefix concealed an invalid third action or invalidated the current route.");
        SolverResult other = Run("current-and-first-alternative-invalid", root, broken with
        {
            Candidates = [request.Candidates[0], rejected, request.Candidates[2]],
        });
        Check(other.PlanRefresh!.SelectedOriginalRank == request.Candidates[2].OriginalRank
            && other.TotalExpandedNodes == 0, "One rejected alternative prevented the remaining complete route from winning.");

        bool delayed = false;
        SolverResult deadline = Run("recovery-deadline-after-current", root, request, message =>
        {
            if (delayed || !message.Contains("MP_LOCAL_XTURN_ROUTE_REPLAY status=", StringComparison.Ordinal)) return;
            delayed = true; Thread.Sleep(65);
        });
        Check(delayed && deadline.PlanRefresh!.SelectedOriginalRank == 0
            && deadline.PlanRefresh.FullRoutes.Any(item => item.Outcome == "recovery_budget_exhausted")
            && deadline.RequestPhaseMetrics!.Members.Count(member => member.Kind == nameof(SearchRequestMemberKind.RouteReplay)) == 1,
            "Each alternative obtained a new full recovery deadline or discarded the valid incumbent.");

        using CancellationTokenSource cancellation = new();
        bool canceled = false;
        SearchRequestWorkSnapshot? canceledWork = null;
        try
        {
            CombatSearchCoordinator.Solve(root, names, damage, policy with
            {
                MultiplayerPlanRefresh = request, RequestWorkObserver = work => canceledWork = work,
                Diagnostics = new SearchDiagnosticsSink(message =>
                {
                    if (message.Contains("MP_LOCAL_XTURN_ROUTE_REPLAY status=", StringComparison.Ordinal)) cancellation.Cancel();
                }, _ => { }),
            }, cancellation.Token, null);
        }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled && canceledWork is { ExpandedNodes: 0, TransitionCount: > 0 },
            "Cancellation between routes returned an old result or lost completed replay work.");

        PlanAction defend = request.Candidates.SelectMany(candidate => candidate.Route)
            .First(action => action.Kind == PlanActionKind.PlayCard && action.CardId == "DEFEND_IRONCLAD");
        SolverResult shortRoute = new CombatBeamSolver(root, names, damage, policy,
            continuationRouteReplayActions: [defend]).Solve();
        MultiplayerReplayCandidate shortAlternative = MultiplayerPlanRefreshRequest.CaptureCandidate(1, shortRoute);
        SolverResult shortRejected = Run("different-evaluation-window", root, broken with { Candidates = [shortAlternative] });
        Check(shortRejected.PlanRefresh!.Decision == MultiplayerPlanRefreshDecision.FullRestart
            && shortRejected.PlanRefresh.FullRoutes.Any(item => item.Outcome == "evaluation_window_mismatch"),
            "A one-card partial route displaced the three-turn evaluation window.");

        var enemy = live.Enemies.Single();
        int originalHp = enemy.CurrentHp;
        try
        {
            enemy.SetCurrentHpInternal(12);
            CombatRootSnapshot changedRoot = CombatRootSnapshot.Capture(live);
            SolverResult better = Run("valid-current-but-alternative-wins-earlier", changedRoot, request);
            Check(better.PlanRefresh is { Decision: MultiplayerPlanRefreshDecision.Reselect, SelectedOriginalRank: > 0 }
                && better.PlanRefresh.FullRoutes[0].AcceptedActions > 0
                && better.Snapshot.AllEnemiesDead && better.CombatEndedTurn == changedRoot.StartTurnNumber
                && better.TotalExpandedNodes == 0,
                "The old route remained valid but prevented a freshly better complete alternative from winning: "
                    + JsonSerializer.Serialize(better.PlanRefresh));
            SolverResult oldRecovery = CombatSearchCoordinator.TryReplayContinuationRoute(changedRoot, names, damage,
                policy with { ContinuationRouteReplayActions = request.Route, ContinuationRouteReplayBaseline = request.Baseline },
                CancellationToken.None)!;
            CombatBeamSolver oldVerifier = new(changedRoot, names, damage, policy);
            var oldReplay = oldVerifier.ReplayDiagnosticPrefix(oldRecovery.BestNode.Actions);
            try
            {
                Check(oldVerifier.CaptureDiagnosticContinuation(oldReplay) == oldRecovery.PredictedFinalState
                    && oldRecovery.CombatEndedTurn > better.CombatEndedTurn
                    && better.ProjectedBattleHpLost <= oldRecovery.ProjectedBattleHpLost,
                    "Full alternative selection did not beat the old current-only recovery on the fixed changed root.");
                replays++;
            }
            finally { oldReplay.ReleaseSimulator(); }
            comparisons.Add(new { enemy_hp = 12,
                current_only = new { oldRecovery.CombatEndedTurn, oldRecovery.ProjectedBattleHpLost, oldRecovery.BestNode.ActionCount },
                with_alternatives = new { better.CombatEndedTurn, better.ProjectedBattleHpLost, better.BestNode.ActionCount } });
            enemy.SetCurrentHpInternal(6);
            CombatRootSnapshot lethalRoot = CombatRootSnapshot.Capture(live);
            SolverResult lethal = Run("alternative-victory-truncates-tail", lethalRoot, broken with
            {
                Candidates = [request.Candidates.First(candidate => candidate.Route[0].CardId is "BASH" or "STRIKE_IRONCLAD")
                    with { OriginalRank = 1 }],
            });
            Check(lethal.Snapshot.AllEnemiesDead && lethal.BestNode.ActionCount == 1
                && lethal.PlanRefresh!.Decision == MultiplayerPlanRefreshDecision.Reselect,
                "An alternative did not truncate its stale tail after native victory: "
                    + JsonSerializer.Serialize(lethal.PlanRefresh));
            var longCandidate = request.Candidates.First(candidate =>
                candidate.Route[0].CardId is "BASH" or "STRIKE_IRONCLAD");
            var longRoute = longCandidate.Route.Concat(Enumerable.Repeat(invalid, 129)).Take(129).ToArray();
            IReadOnlyList<PlanAction> retainedLong = MultiplayerPlanRefreshRequest.CaptureRemainingRoute(
                longRoute, root.StartTurnNumber);
            Check(retainedLong.Count == 129, "The capture dropped a route past the old action limit.");
            var longRequest = broken with
            {
                Candidates = [longCandidate with { OriginalRank = 1, Route = retainedLong }],
            };
            SolverResult longVictory = Run("long-alternative-early-victory", lethalRoot, longRequest);
            Check(longVictory is { BestNode.ActionCount: 1, TotalExpandedNodes: 1 }
                && longVictory.PlanRefresh is { WholeRouteReplayed: true, SelectedOriginalRank: 1 },
                "Long recovery did not charge the one executed step and truncate the rest.");
            SearchRequestWorkSnapshot? longWork = null;
            bool longExhausted = false;
            try
            {
                _ = CombatSearchCoordinator.Solve(root, names, damage, policy with
                {
                    Profile = policy.Profile with { MaxExpandedNodes = 1 },
                    MultiplayerPlanRefresh = longRequest,
                    RequestWorkObserver = work => longWork = work,
                }, CancellationToken.None, null);
            }
            catch (SearchRecoveryBudgetExhaustedException) { longExhausted = true; }
            Check(longExhausted && longWork is { ExpandedNodes: 1, TransitionCount: > 0 },
                "An incomplete long route escaped its request node budget or lost its recorded work.");
            SolverResult stoppingDisabled = Run("disabled-hp-shortcut", lethalRoot, broken with
            {
                Candidates = [request.Candidates[0] with { OriginalRank = 1 }],
            }, stopAtTarget: false);
            Check(stoppingDisabled.PlanRefresh is { WholeRouteReplayed: false, Decision: MultiplayerPlanRefreshDecision.FullRestart }
                && stoppingDisabled.PlanRefresh.FullRoutes.Any(item => item.Outcome == "quality_rejected"),
                "Automatic alternative recovery ignored the disabled HP-target shortcut.");
            SolverResult shortAlive = new CombatBeamSolver(lethalRoot, names, damage, policy,
                continuationRouteReplayActions: [defend]).Solve();
            MultiplayerPlanRefreshRequest winning = MultiplayerPlanRefreshRequest.Capture(lethal) with
            {
                Candidates = [MultiplayerPlanRefreshRequest.CaptureCandidate(1, shortAlive)],
            };
            SolverResult preserved = Run("complete-victory-preserved", lethalRoot, winning);
            Check(preserved.Snapshot.AllEnemiesDead && preserved.PlanRefresh!.SelectedOriginalRank == 0
                && preserved.PlanRefresh.FullRoutes.Any(item => item.Outcome == "complete_victory_must_be_preserved"),
                "An apparently safe partial route replaced an already-proved victory.");
        }
        finally { enemy.SetCurrentHpInternal(originalHp); }

        File.WriteAllText(Path.Combine(output, "multiplayer-full-routes.json"), JsonSerializer.Serialize(new
        {
            status = "PASS", checks, independent_replays = replays,
            evidence = "native two-player model; controlled candidate rejection and HP edits; no Controller/network/UI", rows, comparisons,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return (checks, replays);
    }
}
