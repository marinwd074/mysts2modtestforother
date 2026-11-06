using System.Text.Json;
using CombatSolver;
using MegaCrit.Sts2.Core.Combat;

namespace U0U1PinnedHarness;

internal static class MultiplayerRecoveryBudgetChecks
{
    internal static (int Checks, int Replays) Run(CombatState live, SolverDisplayNames names,
        BattleDamageSnapshot damage, SearchPolicySnapshot captured, SolverResult source, string output)
    {
        int checks = 0, replays = 0;
        List<object> rows = [];
        SearchPolicySnapshot policy = captured with
        {
            VerifyIncrementalSearch = false, MeasurePhasePerformance = true,
            MultiplayerPlanRefresh = null, RecoveryBudget = null, BudgetOverrideMilliseconds = null,
            ContinuationSeedActions = [], ContinuationRouteReplayActions = [],
            ContinuationRouteReplayBaseline = null,
        };
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        IReadOnlyList<PlanAction> seed = source.MultiplayerReplayCandidates[0].Prefix;
        ContinuationRouteReplayBaseline baseline = new(source.PredictedFinalState!, source.ProjectedBattleHpLost,
            source.Snapshot.ProjectedDeathSaveUseCount, source.Snapshot.GrowthRewards, source.Snapshot.RelicCounters);
        PlanAction invalid = seed[0] with { CardId = "NONEXISTENT_FIXTURE_CARD", CardStateKey = "missing" };

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        List<string> messages = [];
        SearchPolicySnapshot Observe(SearchPolicySnapshot value, Action<string>? extra = null)
            => value with { R0TransitionPolicyIdentity = CombatTransitionMemo.CapturePolicyIdentity(value),
                Diagnostics = new SearchDiagnosticsSink(message =>
            {
                messages.Add(message); extra?.Invoke(message);
            }, messages.Add) };

        SearchRecoveryBudgetSnapshot LastBudget()
        {
            const string marker = "SEARCH_RECOVERY_BUDGET ";
            string line = messages.Last(message => message.Contains(marker, StringComparison.Ordinal));
            return JsonSerializer.Deserialize<SearchRecoveryBudgetSnapshot>(line[(line.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..])!;
        }

        SearchRequestPhaseSnapshot LastPhases()
        {
            const string marker = "SEARCH_REQUEST_PHASE ";
            string line = messages.Last(message => message.Contains(marker, StringComparison.Ordinal));
            return JsonSerializer.Deserialize<SearchRequestPhaseSnapshot>(line[(line.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..])!;
        }

        void Verify(CombatRootSnapshot frozen, SolverResult result)
        {
            CombatBeamSolver verifier = new(frozen, names, damage, policy);
            SimulationSnapshot replay = verifier.ReplayDiagnosticPrefix(result.BestNode.Actions);
            try
            {
                Check(verifier.CaptureDiagnosticContinuation(replay) == result.PredictedFinalState,
                    "Cross-turn recovery final state differs from independent replay.");
                Check(replay.CumulativePlayerHpLost == result.Snapshot.CumulativePlayerHpLost
                    && replay.AllEnemiesDead == result.Snapshot.AllEnemiesDead,
                    "Cross-turn recovery quality differs from independent replay.");
                Check(result.BestNode.Actions.All(action => action.ShadowForecast == null && !action.IsForecastOnlyObservation),
                    "Recovery manufactured a teammate action.");
                replays++;
            }
            finally { replay.ReleaseSimulator(); }
        }

        void VerifyWork(SearchRecoveryBudgetSnapshot budget, SearchRequestPhaseSnapshot phases)
        {
            Check(budget.ExpandedNodes <= budget.MaxExpandedNodes
                && phases.ExpandedNodes == budget.ExpandedNodes,
                $"Cross-turn members exceeded or escaped the shared node counter: {JsonSerializer.Serialize(budget)} "
                    + $"ledger={phases.ExpandedNodes} members={JsonSerializer.Serialize(phases.Members.Select(member => new { member.Kind, member.Work.ExpandedNodes }))}");
            Check(phases.RecordedSolverCount == phases.Members.Count
                && phases.Members.Sum(member => (long)member.Work.ExpandedNodes) == phases.ExpandedNodes
                && phases.Members.Sum(member => (long)member.Work.TransitionCount) == phases.LogicalTransitions,
                "Cross-turn work was lost or recorded twice.");
            Check(budget.OverrunMilliseconds == Math.Max(0, budget.ElapsedMilliseconds - budget.BudgetMilliseconds),
                "Request wall-clock overrun is mislabeled or incorrect.");
        }

        void Save(string name, SolverResult? result = null)
        {
            SearchRecoveryBudgetSnapshot budget = LastBudget();
            SearchRequestPhaseSnapshot phases = LastPhases();
            Console.WriteLine($"RecoveryBudget case={name} counter={budget.ExpandedNodes} ledger={phases.ExpandedNodes}");
            VerifyWork(budget, phases);
            rows.Add(new { name, budget, phases,
                quality = result == null ? null : Quality(result),
                cleanup = messages.Where(message => message.Contains("R1_TRANSITION_HYDRATION_RELEASE", StringComparison.Ordinal)).ToArray() });
        }

        messages.Clear();
        SolverResult replayed = CombatSearchCoordinator.Solve(root, names, damage, Observe(policy with
        {
            ContinuationRouteReplayActions = source.BestNode.Actions,
            ContinuationRouteReplayBaseline = baseline,
        }), CancellationToken.None, null);
        Check(replayed.RecoveryBudgetMetrics is { Outcome: "Completed", ExpandedNodes: 0 },
            "Accepted route replay lacks the shared request budget.");
        Verify(root, replayed);
        Save("whole-route", replayed);

        foreach (int dop in new[] { 1, 4 })
        {
            SearchPolicySnapshot current = policy with { MaxDegreeOfParallelism = dop };
            SolverResult control = CombatSearchCoordinator.Solve(root, names, damage, current,
                CancellationToken.None, null);
            Verify(root, control);
            foreach (bool withFailedRoute in new[] { false, true })
            {
                messages.Clear();
                List<SolverProgress> progress = [];
                SolverResult recovered = CombatSearchCoordinator.Solve(root, names, damage, Observe(current with
                {
                    ContinuationSeedActions = seed,
                    ContinuationRouteReplayActions = withFailedRoute ? [invalid] : [],
                }), CancellationToken.None, progress.Add);
                Check(Quality(control) == Quality(recovered),
                    "Shared-budget R1 changed the fixed-input baseline quality.");
                Check(recovered.RequestPhaseMetrics!.Members.Any(member => member.Kind == nameof(SearchRequestMemberKind.ContinuationSeed))
                    && recovered.RequestPhaseMetrics.Members.Any(member => member.Kind == nameof(SearchRequestMemberKind.Baseline)),
                    "R1 and ordinary search did not both contribute to the request.");
                Check(!withFailedRoute || recovered.RequestPhaseMetrics.Members.Any(member => member.Kind == nameof(SearchRequestMemberKind.RouteReplay)),
                    "A rejected full route disappeared from request accounting.");
                Check(progress.Count > 0 && progress.All(item => item.RequestBudgetMilliseconds == policy.Profile.SoftTimeBudgetMilliseconds)
                    && progress.Zip(progress.Skip(1)).All(pair => pair.Second.ElapsedMilliseconds >= pair.First.ElapsedMilliseconds
                        && pair.Second.ReviewedWorldlines >= pair.First.ReviewedWorldlines),
                    "Recovery progress reset its elapsed time, work, or request budget.");
                Verify(root, recovered);
                Save($"r1-dop-{dop}-failed-route-{withFailedRoute}", recovered);
            }
        }

        messages.Clear();
        SolverResult limited = CombatSearchCoordinator.Solve(root, names, damage, Observe(policy with
        {
            Profile = policy.Profile with { MaxExpandedNodes = 40 }, ContinuationSeedActions = seed,
        }), CancellationToken.None, null);
        Check(limited.TotalExpandedNodes <= 40 && limited.RequestPhaseMetrics!.Members
            .Where(member => member.Kind == nameof(SearchRequestMemberKind.ContinuationSeed))
            .Sum(member => member.Work.ExpandedNodes) <= 2,
            "The R1 probe or fallback obtained more than the shared node allowance.");
        Verify(root, limited);
        Save("r1-small-budget", limited);

        foreach (bool route in new[] { true, false })
        {
            messages.Clear();
            bool exhausted = false;
            string marker = route ? "ROUTING_CHOICE_SUMMARIES scope=solver" : "P2_CONTINUATION_SEED_INCUMBENT";
            try
            {
                CombatSearchCoordinator.Solve(root, names, damage, Observe(policy with
                {
                    BudgetOverrideMilliseconds = route ? 1 : 20,
                    ContinuationRouteReplayActions = route ? [invalid] : [],
                    ContinuationSeedActions = route ? [] : seed,
                }, message => { if (message.Contains(marker, StringComparison.Ordinal)) Thread.Sleep(25); }),
                    CancellationToken.None, null);
            }
            catch (SearchRecoveryBudgetExhaustedException) { exhausted = true; }
            Check(exhausted && LastBudget().Outcome == "BudgetExhausted"
                && !LastPhases().Members.Any(member => member.Kind == nameof(SearchRequestMemberKind.Baseline)),
                "Expired cross-turn repair started a fresh baseline or returned an unproved route.");
            Save(route ? "route-timeout" : "r1-timeout");
        }

        foreach (bool fault in new[] { false, true })
        {
            messages.Clear();
            using CancellationTokenSource cancellation = new();
            bool rejected = false;
            try
            {
                CombatSearchCoordinator.Solve(root, names, damage, Observe(policy with { ContinuationSeedActions = seed }, message =>
                {
                    if (!message.Contains("P2_CONTINUATION_SEED_INCUMBENT", StringComparison.Ordinal)) return;
                    if (fault) throw new InvalidOperationException("injected-recovery-fault");
                    cancellation.Cancel();
                }), cancellation.Token, null);
            }
            catch (OperationCanceledException) when (!fault) { rejected = true; }
            catch (InvalidOperationException ex) when (fault && ex.Message == "injected-recovery-fault") { rejected = true; }
            Check(rejected && LastBudget().Outcome == (fault ? "Faulted" : "Canceled"),
                "Interrupted R1 recovery returned a result or mislabeled its outcome.");
            Check(messages.Any(message => message.Contains("R1_TRANSITION_HYDRATION_RELEASE", StringComparison.Ordinal)
                    && message.Contains("entries_after_release=0", StringComparison.Ordinal)
                    && !message.Contains("entries_before_release=0 ", StringComparison.Ordinal)),
                "An interrupted R1 probe failed to release its populated transition cache: "
                    + string.Join(" | ", messages.Where(message => message.Contains("R1_TRANSITION_HYDRATION", StringComparison.Ordinal))));
            Check(fault || LastBudget() is { CancellationObservedMilliseconds: >= 0,
                CoordinatorCancellationDrainMilliseconds: >= 0 }, "Cancellation drain was not measured.");
            Save(fault ? "r1-fault" : "r1-cancel");
        }

        var enemy = live.Enemies.Single();
        int oldHp = enemy.CurrentHp;
        try
        {
            enemy.SetCurrentHpInternal(6);
            CombatRootSnapshot lethalRoot = CombatRootSnapshot.Capture(live);
            messages.Clear();
            SolverResult incumbent = CombatSearchCoordinator.Solve(lethalRoot, names, damage, Observe(policy with
            {
                BudgetOverrideMilliseconds = 20, ContinuationSeedActions = [seed[0]],
            }, message => { if (message.Contains("P2_CONTINUATION_SEED_INCUMBENT", StringComparison.Ordinal)) Thread.Sleep(25); }),
                CancellationToken.None, null);
            Check(incumbent.Snapshot.AllEnemiesDead && !incumbent.Snapshot.PlayerDead
                && incumbent.ResultScope == SolverResultScope.SearchCompletion
                && LastBudget() is { Outcome: "Completed", OverrunMilliseconds: > 0 }
                && !LastPhases().Members.Any(member => member.Kind == nameof(SearchRequestMemberKind.Baseline)),
                "Budget exhaustion discarded the already-proved R1 victory or restarted search.");
            Verify(lethalRoot, incumbent);
            Save("r1-victory-at-deadline", incumbent);
        }
        finally { enemy.SetCurrentHpInternal(oldHp); }

        File.WriteAllText(Path.Combine(output, "multiplayer-recovery-budget.json"), JsonSerializer.Serialize(new
        {
            status = "PASS", checks, independent_replays = replays,
            evidence = "native two-player model; fixed roots and controlled deadline/cancellation hooks; no Controller/network/UI",
            rows,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return (checks, replays);
    }

    private static string Quality(SolverResult result) => JsonSerializer.Serialize(new
    {
        result.Snapshot.AllEnemiesDead, result.Snapshot.PlayerDead, result.ProjectedBattleHpLost,
        result.PotionCount, result.CombatEndedTurn, result.Snapshot.EnemyHp,
        result.Snapshot.PlayerHp, result.Snapshot.GrowthRewards, result.Snapshot.RelicCounters,
        result.BoundaryReason,
    });
}
