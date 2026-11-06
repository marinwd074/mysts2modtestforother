using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;

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
        var local = LocalContext.GetMe(live)!;
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
                MultiplayerPlanRefreshResult refreshed;
                try
                {
                    refreshed = MultiplayerPlanRefresher.Refresh(
                        root, names, damage, policy, source.MultiplayerReplayCandidates);
                }
                catch (InvalidOperationException ex) when (hp == 6
                    && ex.Message == "回放包含已锁定战斗终局之后的动作。")
                {
                    // Preserve the current Runtime fallback as baseline evidence. A teammate
                    // damage edit can make the first card lethal and invalidate the second.
                    refreshed = new(MultiplayerPlanRefreshDecision.FullRestart, null, 0,
                        false, "legacy_prefix_continues_after_victory");
                }
                double refreshMs = clock.Elapsed.TotalMilliseconds;
                Check(refreshed.ReplayedCandidates is >= 0 and <= 3, "Refresh lost its candidate work bound.");
                Check(refreshed.Result != null || hp == 6
                    && refreshed.Reason == "legacy_prefix_continues_after_victory",
                    $"Known playable prefixes failed to refresh: {refreshed.Reason}");
                if (refreshed.Result != null) Verify(root, refreshed.Result);
                Check(before == ContinuationStamp.CaptureLive(live).StateText,
                    "Search or refresh mutated live state, including teammate/shared state.");

                rows.Add(new
                {
                    enemy_hp = hp, players = root.PlayerCount, capture_ms = captureMs,
                    fresh_completion_ms = freshMs, first_preview_ms = firstPreviewMs,
                    first_approved_publication_ms = firstApprovedMs, completed_layers = completedLayers,
                    refresh_ms = refreshMs, refresh_decision = refreshed.Decision.ToString(),
                    refreshed.Reason, refreshed.ReplayedCandidates,
                    fresh = Describe(fresh), refresh = refreshed.Result == null ? null : Describe(refreshed.Result),
                });

                Check(MultiplayerPlanRefresher.Refresh(root, names, damage, policy, []).Decision
                    == MultiplayerPlanRefreshDecision.FullRestart, "Empty candidates must request full search.");
                var current = fresh.MultiplayerReplayCandidates[0];
                Check(MultiplayerPlanRefresher.Refresh(root, names, damage, policy,
                    [current with { OriginalRank = 1 }]).Decision == MultiplayerPlanRefreshDecision.FullRestart,
                    "A candidate set without the current route must fail closed.");
                Check(MultiplayerPlanRefresher.Refresh(root, names, damage, policy,
                    [current with { Prefix = current.Prefix.Select(action => action with { Turn = action.Turn + 1 }).ToArray() }])
                    .Decision == MultiplayerPlanRefreshDecision.FullRestart,
                    "Future-turn prefixes must not enter current-turn refresh.");
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
                baseline = "existing three-turn local core and legacy three-candidate/two-card refresh",
                refresh_budget = new { candidates = 3, prefix_actions = 2, nodes = 192, materialize_ms = 60 },
                profile, dll_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))), rows,
            }, new JsonSerializerOptions { WriteIndented = true }));
        return $"MultiplayerRefresh PASS ({checks} checks, {replays} independent native-model replays)";

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
