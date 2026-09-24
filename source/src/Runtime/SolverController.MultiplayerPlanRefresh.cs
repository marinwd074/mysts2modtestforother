using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal enum MultiplayerPlanRefreshDecision
{
    Continue,
    Reselect,
    FullRestart,
}

internal static partial class SolverController
{
    private readonly record struct BoundedReplayEvaluation(
        MultiplayerReplayCandidate Candidate,
        MultiplayerCombatObjectiveRank Objective,
        double Score);

    private static MultiplayerPlanRefreshDecision TryBoundedMultiplayerPlanRefresh(
        CombatState state,
        long worldVersion)
    {
        AssertMainThread();
        Stopwatch stopwatch = Stopwatch.StartNew();
        _combat.PendingMultiplayerPlanRefresh = false;

        SolverResult? source = _combat.LatestResult;
        LiveCombatStamp currentStamp = LiveCombatStamp.Capture(state);
        if (source == null
            || _combat.LatestStamp is not { } previousStamp
            || !MultiplayerPlanRefreshContracts.IsReplayCompatible(
                previousStamp.StateText,
                currentStamp.StateText,
                out _)
            || source.StartTurnNumber != LocalContext.GetMe(state)?.PlayerCombatState?.TurnNumber
            || source.MultiplayerReplayCandidates.Count == 0)
        {
            DropRetainedPlanForFullRestart();
            LogPlanRefresh(
                MultiplayerPlanRefreshDecision.FullRestart,
                worldVersion,
                source,
                replayedCandidates: 0,
                firstActionChanged: false,
                stopwatch.Elapsed,
                "root_or_candidate_mismatch");
            return MultiplayerPlanRefreshDecision.FullRestart;
        }

        try
        {
            BattleDamageSnapshot battleDamage = BattleDamageTracker.Observe(state);
            SolverDisplayNames displayNames = SolverDisplayNames.Capture(state);
            SolverSettingsSnapshot settings = SolverSettings.Capture();
            SolverTheftPolicy? theftPolicy = ResolveTheftPolicy(state);
            SearchPolicySnapshot searchPolicy = CaptureSearchPolicy(
                settings,
                state,
                includeTurnSetup: false,
                theftPolicy: theftPolicy);
            CombatRootSnapshot root = CombatRootSnapshot.Capture(state);
            int initialEnemyMaximumHp = Math.Max(
                1,
                root.Enemies.Sum(enemy => Math.Max(0, enemy.MaxHp)));
            CombatBeamSolver solver = new(
                root,
                displayNames,
                battleDamage,
                searchPolicy);

            List<BoundedReplayEvaluation> evaluations = [];
            foreach (MultiplayerReplayCandidate candidate in source.MultiplayerReplayCandidates.Take(3))
            {
                PlanAction? firstAction = candidate.Prefix.FirstOrDefault();
                if (firstAction == null
                    || !firstAction.IsExecutable
                    || firstAction.Turn != root.StartTurnNumber)
                {
                    continue;
                }

                SimulationSnapshot snapshot = solver.ReplayDiagnosticPrefix([firstAction]);
                try
                {
                    bool completeVictory = SolverInterimResultOrdering.IsCompleteVictory(
                        1,
                        snapshot.AllEnemiesDead,
                        snapshot.PlayerDead,
                        snapshot.ProjectedPlayerHp);
                    double enemyDurabilityRatio =
                        MultiplayerCombatObjectivePolicy.ComputeEnemyDurabilityRatio(
                            snapshot.EnemyDurabilityByCombatId,
                            initialEnemyMaximumHp);
                    MultiplayerCombatObjectiveRank objective =
                        MultiplayerCombatObjectiveMath.BuildRank(
                            searchPolicy.MultiplayerCombatObjectiveStrategy,
                            completeVictory,
                            snapshot.AllPlayersAlive,
                            snapshot.TeamLossRatio,
                            snapshot.WorstPlayerLossRatio,
                            enemyDurabilityRatio,
                            searchPolicy.MultiplayerEnemyDurabilityRatio,
                            completeVictory ? snapshot.CombatEndedTurn : null,
                            root.StartTurnNumber);
                    evaluations.Add(new BoundedReplayEvaluation(
                        candidate,
                        objective,
                        snapshot.Score));
                }
                finally
                {
                    snapshot.ReleaseSimulator();
                }
            }

            int currentIndex = evaluations.FindIndex(
                evaluation => evaluation.Candidate.OriginalRank == 0);
            if (currentIndex < 0)
            {
                DropRetainedPlanForFullRestart();
                LogPlanRefresh(
                    MultiplayerPlanRefreshDecision.FullRestart,
                    worldVersion,
                    source,
                    evaluations.Count,
                    firstActionChanged: false,
                    stopwatch.Elapsed,
                    "current_prefix_unreplayable");
                return MultiplayerPlanRefreshDecision.FullRestart;
            }

            BoundedReplayEvaluation selected = evaluations[currentIndex];
            foreach (BoundedReplayEvaluation candidate in evaluations)
            {
                int comparison = searchPolicy.UseMultiplayerTeamObjective
                    ? MultiplayerCombatObjectiveMath.Compare(candidate.Objective, selected.Objective)
                    : selected.Score.CompareTo(candidate.Score);
                if (comparison < 0
                    || comparison == 0
                    && candidate.Candidate.OriginalRank < selected.Candidate.OriginalRank)
                {
                    selected = candidate;
                }
            }

            bool firstActionChanged = selected.Candidate.OriginalRank != 0;
            if (firstActionChanged)
            {
                DropRetainedPlanForFullRestart();
                LogPlanRefresh(
                    MultiplayerPlanRefreshDecision.Reselect,
                    worldVersion,
                    source,
                    evaluations.Count,
                    firstActionChanged: true,
                    stopwatch.Elapsed,
                    "retained_alternative_preferred");
                return MultiplayerPlanRefreshDecision.Reselect;
            }

            _combat.LatestStamp = currentStamp;
            _combat.LastPlanRefreshDecisionTimestampMilliseconds =
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            LogPlanRefresh(
                MultiplayerPlanRefreshDecision.Continue,
                worldVersion,
                source,
                evaluations.Count,
                firstActionChanged: false,
                stopwatch.Elapsed,
                "current_prefix_still_preferred");
            SolverOverlay.RefreshControls();
            return MultiplayerPlanRefreshDecision.Continue;
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn(
                $"[CombatSolver/MultiplayerPlanRefresh] MP_PLAN_REFRESH_REPLAY_FAILED " +
                $"world_version={worldVersion} exception={ex.GetType().Name} message={ex.Message}");
            DropRetainedPlanForFullRestart();
            LogPlanRefresh(
                MultiplayerPlanRefreshDecision.FullRestart,
                worldVersion,
                source,
                replayedCandidates: 0,
                firstActionChanged: false,
                stopwatch.Elapsed,
                "replay_exception");
            return MultiplayerPlanRefreshDecision.FullRestart;
        }
    }

    private static void DropRetainedPlanForFullRestart()
    {
        _combat.PendingMultiplayerPlanRefresh = false;
        _combat.LastPlanRefreshDecisionTimestampMilliseconds = null;
        _combat.LatestResult = null;
        _combat.LatestStamp = null;
        _combat.ContinuationSource = null;
        InvalidateRenderedRouteAdoptionSeed();
        SolverOverlay.RefreshControls();
    }

    private static void LogPlanRefresh(
        MultiplayerPlanRefreshDecision decision,
        long worldVersion,
        SolverResult? source,
        int replayedCandidates,
        bool firstActionChanged,
        TimeSpan elapsed,
        string reason)
    {
        Entry.Logger.Info(
            $"[CombatSolver/MultiplayerPlanRefresh] MP_PLAN_REFRESH " +
            $"decision={decision.ToString().ToLowerInvariant()} " +
            $"full_restart={(decision == MultiplayerPlanRefreshDecision.FullRestart).ToString().ToLowerInvariant()} " +
            $"prefix_replay={(replayedCandidates > 0).ToString().ToLowerInvariant()} " +
            $"first_action_changed={firstActionChanged.ToString().ToLowerInvariant()} " +
            $"candidate_count={source?.MultiplayerReplayCandidates.Count ?? 0} " +
            $"replayed_candidates={replayedCandidates} world_version={worldVersion} " +
            $"route_identity={source?.RouteIdentity ?? "-"} replay_latency_ms={elapsed.TotalMilliseconds:F2} " +
            $"reason={reason}");
    }
}
