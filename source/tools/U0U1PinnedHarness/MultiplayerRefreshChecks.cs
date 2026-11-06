using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace U0U1PinnedHarness;

/// <summary>
/// Exercises the production refresh calculation on native two-player models.
/// Durability changes are explicit fixture edits, not network or native action evidence.
/// </summary>
internal static class MultiplayerRefreshChecks
{
    internal static string Run(CombatState live, SolverDisplayNames names,
        BattleDamageSnapshot damage, SearchPolicySnapshot captured,
        SolverSearchProfile original, string outputDirectory)
    {
        int checks = 0, replays = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        Check(live.Players.Count == 2, "Refresh fixture must contain two native player models.");
        var enemy = live.Enemies.Single();
        int originalHp = enemy.CurrentHp;
        SolverSearchProfile profile = original with
        {
            BeamWidth = 8, MaxExpandedNodes = 800, SoftTimeBudgetMilliseconds = 20_000,
        };
        SearchPolicySnapshot policy = captured with
        {
            Profile = profile, RoutePolicy = SearchRoutePolicy.MultiplayerSinglePlayerCore,
            CurrentTurnOnly = false, IncludeTurnSetup = false,
            UseMultiplayerTeamObjective = false, UseMultiplayerTeammateForecast = false,
            UseMultiplayerScenarioReevaluation = false, UseNoveltyPortfolio = false,
            NoveltySearch = null, UseBeamWidthPortfolio = false,
            MaxDegreeOfParallelism = 1, FixedBudget = true, Interaction = null,
            IgnoreLongTermRewards = true, RelicTargets = [],
            StopAtAcceptableBattleHpLoss = false, VerifyIncrementalSearch = true,
            PotionPolicy = SolverPotionPolicy.Disabled,
            PotionStrategy = new(SolverPotionPolicy.Disabled, []),
            RequestWorkTotals = null, OptimizationSession = null,
            RequestTransitionHydrationCache = null, R0TransitionMemo = null,
            Diagnostics = new SearchDiagnosticsSink(_ => { }, _ => { }),
        };
        List<object> rows = [];
        SolverResult? previous = null;
        string? previousStamp = null;
        try
        {
            foreach (int hp in new[] { originalHp, Math.Max(1, originalHp - 1), 21, 6 })
            {
                enemy.SetCurrentHpInternal(hp);
                string before = ContinuationStamp.CaptureLive(live).StateText;
                Stopwatch clock = Stopwatch.StartNew();
                CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
                double captureMs = clock.Elapsed.TotalMilliseconds;
                Check(root.PlayerCount == 2, "Root capture lost the second player.");
                double? firstPreviewMs = null, firstApprovedMs = null;
                int completedLayers = 0;
                SolverResult fresh = CombatSearchCoordinator.Solve(root, names, damage, policy,
                    CancellationToken.None, progress =>
                    {
                        completedLayers = Math.Max(completedLayers, progress.CompletedTurnLayers);
                        if (progress.CurrentTurnPreview?.Actions.Count > 0)
                            firstPreviewMs ??= clock.Elapsed.TotalMilliseconds;
                        if (progress.OfficialPublishedOrigin != null)
                            firstApprovedMs ??= clock.Elapsed.TotalMilliseconds;
                    });
                double freshMs = clock.Elapsed.TotalMilliseconds;
                Verify(root, fresh);
                Check(fresh.MultiplayerReplayCandidates.Count is > 0 and <= 3,
                    "Search did not retain its bounded candidate set.");
                Check(fresh.MultiplayerReplayCandidates[0].OriginalRank == 0,
                    "The current selected route was not the first retained candidate.");

                SolverResult source = previous ?? fresh;
                Check(MultiplayerPlanRefreshContracts.IsReplayCompatible(previousStamp ?? before, before, out _),
                    "Living-enemy durability fixture was not admitted by the production guard.");
                clock.Restart();
                MultiplayerPlanRefreshResult refreshed = MultiplayerPlanRefresher.Refresh(
                    root, names, damage, policy, source.MultiplayerReplayCandidates);
                double refreshMs = clock.Elapsed.TotalMilliseconds;
                Check(refreshed.ReplayedCandidates is > 0 and <= 3, "Refresh lost its candidate work bound.");
                Check(refreshed.Result != null,
                    $"Known playable prefixes failed to refresh: {refreshed.Reason}");
                Verify(root, refreshed.Result!);
                Check(refreshed.Metrics is { AttemptedCandidates: > 0, PrefixTransitions: > 0 }
                    && refreshed.Metrics.PrefixMilliseconds + refreshed.Metrics.MaterializationMilliseconds
                        <= refreshed.Metrics.TotalMilliseconds,
                    "Refresh phase accounting is incomplete or overlaps.");
                if (hp == 6)
                    Check(refreshed.Metrics!.TruncatedCandidates > 0 && refreshed.Result!.Snapshot.AllEnemiesDead
                        && refreshed.Result.BestNode.Actions.Count == 1,
                        "New lethal must truncate the stale tail and return a one-card victory.");
                Check(before == ContinuationStamp.CaptureLive(live).StateText,
                    "Search or refresh mutated live state, including teammate/shared state.");

                MultiplayerPlanRefreshRequest request = MultiplayerPlanRefreshRequest.Capture(source);
                SearchPolicySnapshot workerPolicy = policy with
                {
                    VerifyIncrementalSearch = false, StopAtAcceptableBattleHpLoss = true,
                    MeasurePhasePerformance = true, MultiplayerPlanRefresh = request,
                };
                int captureThread = Environment.CurrentManagedThreadId, workerThread = captureThread;
                SolverResult recovered = Task.Run(() =>
                {
                    workerThread = Environment.CurrentManagedThreadId;
                    return CombatSearchCoordinator.Solve(root, names, damage, workerPolicy,
                        CancellationToken.None, null);
                }).GetAwaiter().GetResult();
                Check(workerThread != captureThread, "Recovery fixture did not use a background worker.");
                Check(recovered.PlanRefresh != null && recovered.ResultScope == SolverResultScope.SearchCompletion,
                    "Automatic recovery lost its report or manufactured route-adoption intent.");
                Check(recovered.TotalExpandedNodes <= profile.MaxExpandedNodes,
                    "Recovery and fallback exceeded the request node budget.");
                Verify(root, recovered);
                VerifyLedger(recovered.RequestPhaseMetrics!, recovered.TotalExpandedNodes, recovered.TotalTransitionCount);
                if (hp == originalHp)
                {
                    Check(recovered.PlanRefresh!.WholeRouteReplayed && recovered.TotalExpandedNodes == 0,
                        "An unchanged valid whole route was not restored without expansion.");
                    Check(!ReferenceEquals(request.Route, source.BestNode.Actions)
                        && !ReferenceEquals(request.Candidates[0].Prefix, source.MultiplayerReplayCandidates[0].Prefix),
                        "Worker request retained mutable source action collections.");
                    VerifyRecoveryFailurePaths(root, workerPolicy, request, fresh);
                    VerifyRequestIsolation(source);
                    var recoveryChecks = MultiplayerRecoveryBudgetChecks.Run(live, names, damage, policy, fresh, outputDirectory);
                    checks += recoveryChecks.Checks;
                    replays += recoveryChecks.Replays;
                    var fullRoutes = MultiplayerFullRouteChecks.Run(live, names, damage, policy, fresh, outputDirectory);
                    checks += fullRoutes.Checks;
                    replays += fullRoutes.Replays;
                    var crossTurnRoutes = MultiplayerCrossTurnRouteChecks.Run(live, names, damage, policy, fresh, outputDirectory);
                    checks += crossTurnRoutes.Checks;
                    replays += crossTurnRoutes.Replays;
                    var resources = MultiplayerResourceAccountingChecks.Run(live, names, damage, policy, outputDirectory);
                    checks += resources.Checks;
                    replays += resources.Replays;
                }
                Check(before == ContinuationStamp.CaptureLive(live).StateText,
                    "Worker recovery or failure paths mutated live/shared state.");

                rows.Add(new
                {
                    enemy_hp = hp, players = root.PlayerCount, capture_ms = captureMs,
                    fresh_completion_ms = freshMs, first_preview_ms = firstPreviewMs,
                    first_approved_publication_ms = firstApprovedMs, completed_layers = completedLayers,
                    refresh_ms = refreshMs, refresh_decision = refreshed.Decision.ToString(),
                    refreshed.Reason, refreshed.ReplayedCandidates,
                    refreshed.Metrics,
                    fresh = Describe(fresh), refresh = refreshed.Result == null ? null : Describe(refreshed.Result),
                    worker = Describe(recovered), recovered.PlanRefresh,
                    request_work = recovered.RequestPhaseMetrics,
                });

                Check(MultiplayerPlanRefresher.Refresh(root, names, damage, policy, []).Decision
                    == MultiplayerPlanRefreshDecision.FullRestart, "Empty candidates must request full search.");
                var current = fresh.MultiplayerReplayCandidates[0];
                var unavailable = current with { OriginalRank = 99,
                    Prefix = [current.Prefix[0] with { CardId = "NONEXISTENT_FIXTURE_CARD", CardStateKey = "nonexistent" }] };
                MultiplayerPlanRefreshResult isolated = MultiplayerPlanRefresher.Refresh(root, names, damage,
                    policy, [current, unavailable]);
                Check(isolated.Decision == MultiplayerPlanRefreshDecision.Continue
                    && isolated.Metrics?.RejectedCandidates == 1,
                    "An unavailable alternative invalidated the playable current route.");
                Verify(root, isolated.Result!);
                Check(MultiplayerPlanRefresher.Refresh(root, names, damage, policy,
                    [unavailable with { OriginalRank = 0 }, current with { OriginalRank = 1 }]).Decision
                    == MultiplayerPlanRefreshDecision.FullRestart,
                    "An unavailable current route was replaced without a quality admission.");
                Check(MultiplayerPlanRefresher.Refresh(root, names, damage, policy,
                    [current with { OriginalRank = 1 }]).Decision == MultiplayerPlanRefreshDecision.FullRestart,
                    "A candidate set without the current route must fail closed.");
                Check(MultiplayerPlanRefresher.Refresh(root, names, damage, policy,
                    [current with { Prefix = current.Prefix.Select(action => action with { Turn = action.Turn + 1 }).ToArray() }])
                    .Decision == MultiplayerPlanRefreshDecision.FullRestart,
                    "Future-turn prefixes must not enter current-turn refresh.");
                using CancellationTokenSource cancelled = new();
                cancelled.Cancel();
                bool cancelledRejected = false;
                try { MultiplayerPlanRefresher.Refresh(root, names, damage, policy, [current], cancelled.Token); }
                catch (OperationCanceledException) { cancelledRejected = true; }
                Check(cancelledRejected, "Cancelled refresh returned a deployable result.");
                previous = fresh;
                previousStamp = before;
            }
        }
        finally { enemy.SetCurrentHpInternal(originalHp); }

        string dll = typeof(CombatBeamSolver).Assembly.Location;
        File.WriteAllText(Path.Combine(outputDirectory, "multiplayer-refresh.json"),
            JsonSerializer.Serialize(new
            {
                status = "PASS", checks, independent_replays = replays,
                evidence = "native fake-multiplayer models; controlled HP edits; no transport, game process, or UI",
                baseline = "661fccf0: three-turn local core; refresh now validates each action and truncates victory",
                refresh_budget = new { candidates = 3, prefix_actions = 2, nodes = 192, materialize_ms = 60 },
                profile, dll_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))), rows,
            }, new JsonSerializerOptions { WriteIndented = true }));
        return $"MultiplayerRefresh PASS ({checks} checks, {replays} independent native-model replays)";

        void VerifyLedger(SearchRequestPhaseSnapshot phases, long expanded, long transitions)
        {
            Check(phases != null && phases.RecordedSolverCount == phases.Members.Count,
                "Recovery member ledger is incomplete.");
            Check(phases!.Members.Sum(member => (long)member.Work.ExpandedNodes) == expanded
                && phases.Members.Sum(member => (long)member.Work.TransitionCount) == transitions
                && phases.ExpandedNodes == expanded && phases.LogicalTransitions == transitions,
                "Recovery work was omitted or counted twice.");
        }

        void VerifyRecoveryFailurePaths(CombatRootSnapshot root, SearchPolicySnapshot workerPolicy,
            MultiplayerPlanRefreshRequest request, SolverResult expected)
        {
            PlanAction invalid = request.Route[0] with { CardId = "NONEXISTENT_FIXTURE_CARD", CardStateKey = "nonexistent" };
            MultiplayerPlanRefreshRequest broken = request with { Route = [invalid], Candidates = [] };
            SolverResult fallback = CombatSearchCoordinator.Solve(root, names, damage,
                workerPolicy with { MultiplayerPlanRefresh = broken }, CancellationToken.None, null);
            Check(fallback.PlanRefresh?.Decision == MultiplayerPlanRefreshDecision.FullRestart,
                "An invalid retained route did not fall back within the request.");
            Verify(root, fallback);
            VerifyLedger(fallback.RequestPhaseMetrics!, fallback.TotalExpandedNodes, fallback.TotalTransitionCount);

            SolverResult currentOnly = CombatSearchCoordinator.Solve(root, names, damage, workerPolicy with
            {
                CurrentTurnOnly = true, MultiplayerPlanRefresh = broken,
            }, CancellationToken.None, null);
            Check(currentOnly.PlanRefresh == null,
                "An incompatible policy entered automatic cross-turn plan recovery.");
            Verify(root, currentOnly);

            SolverResult afterRepair = CombatSearchCoordinator.Solve(root, names, damage, workerPolicy with
            {
                StopAtAcceptableBattleHpLoss = false,
                MultiplayerPlanRefresh = request with { Route = [], Baseline = null },
            }, CancellationToken.None, null);
            Check(afterRepair.PlanRefresh is { Decision: MultiplayerPlanRefreshDecision.FullRestart,
                    RecoveryExpandedNodes: > 0 }
                && afterRepair.PlanRefresh.RemainingExpandedNodes
                    == profile.MaxExpandedNodes - afterRepair.PlanRefresh.RecoveryExpandedNodes
                && afterRepair.RequestPhaseMetrics!.Members.Any(member => member.Kind == nameof(SearchRequestMemberKind.Baseline)
                    && member.Work.ExpandedNodes > 0)
                && afterRepair.TotalExpandedNodes <= profile.MaxExpandedNodes,
                "Repair work and subsequent baseline did not consume one node allowance.");
            Check(afterRepair.PredictedFinalState == expected.PredictedFinalState
                && afterRepair.ProjectedBattleHpLost == expected.ProjectedBattleHpLost,
                "The fixed-input fallback lost the baseline route quality.");
            Verify(root, afterRepair);
            VerifyLedger(afterRepair.RequestPhaseMetrics!, afterRepair.TotalExpandedNodes, afterRepair.TotalTransitionCount);

            SearchRequestPhaseSnapshot? observed = null;
            void Observe(string message)
            {
                const string marker = "SEARCH_REQUEST_PHASE ";
                int index = message.IndexOf(marker, StringComparison.Ordinal);
                if (index >= 0) observed = JsonSerializer.Deserialize<SearchRequestPhaseSnapshot>(message[(index + marker.Length)..]);
            }
            foreach (int dop in new[] { 1, 4 })
            {
                observed = null;
                bool exhausted = false;
                try
                {
                    CombatSearchCoordinator.Solve(root, names, damage, workerPolicy with
                    {
                        Profile = profile with { MaxExpandedNodes = 5 }, MaxDegreeOfParallelism = dop,
                        StopAtAcceptableBattleHpLoss = false,
                        MultiplayerPlanRefresh = request with { Route = [], Baseline = null },
                        Diagnostics = new SearchDiagnosticsSink(Observe, Observe),
                    }, CancellationToken.None, null);
                }
                catch (SearchRecoveryBudgetExhaustedException) { exhausted = true; }
                Check(exhausted, "An unproven recovery obtained a second node budget or escaped as a result.");
                Check(observed is { ExpandedNodes: 5 }
                    && observed.Members.Any(member => member.Kind == nameof(SearchRequestMemberKind.PlanRefreshPrefix))
                    && observed.Members.Any(member => member.Kind == nameof(SearchRequestMemberKind.PlanRefreshMaterialization))
                    && !observed.Members.Any(member => member.Kind == nameof(SearchRequestMemberKind.Baseline)),
                    "Failed recovery work was lost, exceeded the cap, or started a new full baseline.");
                VerifyLedger(observed!, observed!.ExpandedNodes, observed.LogicalTransitions);
            }

            observed = null;
            bool timedOut = false;
            void DelayFailedReplay(string message)
            {
                // A deterministic pause in a failed member's cleanup exceeds the 1ms
                // request budget; no game thread or live action is involved.
                if (message.Contains("ROUTING_CHOICE_SUMMARIES scope=solver", StringComparison.Ordinal))
                    Thread.Sleep(5);
                Observe(message);
            }
            try
            {
                CombatSearchCoordinator.Solve(root, names, damage, workerPolicy with
                {
                    BudgetOverrideMilliseconds = 1, MultiplayerPlanRefresh = broken,
                    Diagnostics = new SearchDiagnosticsSink(DelayFailedReplay, Observe),
                }, CancellationToken.None, null);
            }
            catch (SearchRecoveryBudgetExhaustedException) { timedOut = true; }
            Check(timedOut && observed is { ExpandedNodes: 0 },
                "Expired recovery ignored the configured override and started a fresh time budget.");

            observed = null;
            using CancellationTokenSource cancellation = new();
            bool rejected = false;
            void CancelAfterReplay(string message)
            {
                if (message.Contains("ROUTING_CHOICE_SUMMARIES scope=solver", StringComparison.Ordinal))
                    cancellation.Cancel();
                Observe(message);
            }
            try
            {
                CombatSearchCoordinator.Solve(root, names, damage, workerPolicy with
                {
                    Diagnostics = new SearchDiagnosticsSink(CancelAfterReplay, Observe),
                }, cancellation.Token, null);
            }
            catch (OperationCanceledException) { rejected = true; }
            Check(rejected && observed is { RecordedSolverCount: > 0 },
                "Cancellation during recovery returned a result or lost the finished member's work.");
            VerifyLedger(observed!, observed!.ExpandedNodes, observed.LogicalTransitions);
        }

        void VerifyRequestIsolation(SolverResult source)
        {
            var originalCandidates = source.MultiplayerReplayCandidates;
            List<PlanCardToken> cards = [new("STRIKE_IRONCLAD", 0, "fixture", 0, 0, "fixture")];
            PlanCardChoice choice = new(PlanChoiceEffect.Discard, PileType.Hand, cards);
            List<PlanCardChoice> nested = [choice], turnStart = [choice];
            List<PlanAction> actions = [originalCandidates[0].Prefix[0] with
                { Choice = choice, NestedChoices = nested, TurnStartChoices = turnStart }];
            try
            {
                source.MultiplayerReplayCandidates = [new(0, actions)
                {
                    Route = actions,
                    Baseline = MultiplayerPlanRefreshRequest.CaptureBaseline(source)! with { ExplicitRouteAdoption = true },
                }];
                MultiplayerPlanRefreshRequest copy = MultiplayerPlanRefreshRequest.Capture(source);
                cards.Clear(); nested.Clear(); turnStart.Clear(); actions.Clear();
                PlanAction retained = copy.Candidates[0].Prefix[0];
                Check(retained.Choice?.Cards.Count == 1 && retained.NestedChoices?.Count == 1
                    && retained.NestedChoices[0].Cards.Count == 1 && retained.TurnStartChoices?.Count == 1
                    && retained.TurnStartChoices[0].Cards.Count == 1,
                    "Retained request borrowed caller-owned nested choice collections.");
                Check(copy.Candidates[0].Route.Count == 1 && copy.Candidates[0].Route[0].Choice!.Cards.Count == 1
                    && copy.Candidates[0].Route[0].NestedChoices![0].Cards.Count == 1
                    && copy.Candidates[0].Baseline?.ExplicitRouteAdoption == false,
                    "Full retained route borrowed mutable choices or explicit adoption authority.");
            }
            finally { source.MultiplayerReplayCandidates = originalCandidates; }

            var current = originalCandidates[0];
            var invalidChoice = current with { OriginalRank = 1,
                Prefix = [current.Prefix[0] with { Choice = new(PlanChoiceEffect.Discard,
                    PileType.Hand, [new("NONEXISTENT_FIXTURE_CARD", 0, "missing", 0, 0, "missing")]) }] };
            MultiplayerPlanRefreshResult isolated = MultiplayerPlanRefresher.Refresh(
                CombatRootSnapshot.Capture(live), names, damage, policy, [current, invalidChoice]);
            Check(isolated.Decision == MultiplayerPlanRefreshDecision.Continue
                && isolated.Metrics?.RejectedCandidates == 1,
                "A stale native choice in an alternative prevented recovery of the current candidate.");
            Verify(CombatRootSnapshot.Capture(live), isolated.Result!);
            Check(MultiplayerPlanRefresher.Refresh(CombatRootSnapshot.Capture(live), names, damage, policy,
                [invalidChoice with { OriginalRank = 0 }, current with { OriginalRank = 1 }]).Decision
                    == MultiplayerPlanRefreshDecision.FullRestart,
                "A stale choice in the current route authorized an alternative without quality review.");
        }

        void Verify(CombatRootSnapshot root, SolverResult result)
        {
            Check(result.BestNode.Actions.All(action => !action.IsForecastOnlyObservation && action.ShadowForecast == null),
                "Local-core result predicted a teammate action.");
            CombatBeamSolver verifier = new(root, names, damage, policy);
            SimulationSnapshot replay = verifier.ReplayDiagnosticPrefix(result.BestNode.Actions);
            try
            {
                Check(verifier.CaptureDiagnosticContinuation(replay) == result.PredictedFinalState,
                    "Selected route final state disagreed with independent replay.");
                Check(replay.CumulativePlayerHpLost == result.Snapshot.CumulativePlayerHpLost
                    && replay.AllEnemiesDead == result.Snapshot.AllEnemiesDead,
                    "Selected route quality disagreed with independent replay.");
                replays++;
            }
            finally { replay.ReleaseSimulator(); }
        }
    }

    private static object Describe(SolverResult result) => new
    {
        result.ExpandedNodes, result.TransitionCount, result.ProjectedBattleHpLost,
        won = result.Snapshot.AllEnemiesDead && !result.Snapshot.PlayerDead,
        result.CombatEndedTurn, boundary = result.BoundaryReason.ToString(),
        actions = result.BestNode.Actions.Select(action =>
            new { action.Turn, kind = action.Kind.ToString(), action.CardId, action.TargetCombatId }),
    };
}
