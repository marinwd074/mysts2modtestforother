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
            || !IsBoundedMultiplayerRefreshCompatible(
                previousStamp,
                currentStamp,
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

    private static bool IsBoundedMultiplayerRefreshCompatible(
        LiveCombatStamp previous,
        LiveCombatStamp current,
        out string reason)
    {
        if (previous == current)
        {
            reason = "exact_local_root";
            return true;
        }

        string[] previousFields = previous.StateText.Split(';');
        string[] currentFields = current.StateText.Split(';');
        if (previousFields.Length != currentFields.Length)
        {
            reason = "field_count_changed";
            return false;
        }

        bool sawSoftEnemyDelta = false;
        for (int index = 0; index < previousFields.Length; index++)
        {
            string expectedField = previousFields[index];
            string actualField = currentFields[index];
            if (string.Equals(expectedField, actualField, StringComparison.Ordinal))
                continue;

            int expectedSeparator = expectedField.IndexOf('=');
            int actualSeparator = actualField.IndexOf('=');
            if (expectedSeparator <= 0
                || actualSeparator <= 0
                || !string.Equals(
                    expectedField[..expectedSeparator],
                    actualField[..actualSeparator],
                    StringComparison.Ordinal))
            {
                reason = "field_identity_changed";
                return false;
            }

            string fieldName = expectedField[..expectedSeparator];
            if (!fieldName.StartsWith('E')
                || !IsSoftLivingEnemyDurabilityDelta(
                    expectedField[(expectedSeparator + 1)..],
                    actualField[(actualSeparator + 1)..]))
            {
                reason = $"strong_field_change:{fieldName}";
                return false;
            }
            sawSoftEnemyDelta = true;
        }

        reason = sawSoftEnemyDelta
            ? "living_enemy_hp_or_block_only"
            : "exact_local_root";
        return true;
    }

    private static bool IsSoftLivingEnemyDurabilityDelta(
        string previousValue,
        string currentValue)
    {
        string[] previousParts = previousValue.Split('/');
        string[] currentParts = currentValue.Split('/');
        if (previousParts.Length != 7 || currentParts.Length != 7)
            return false;

        // combat-id, monster id, slot, max HP and move must stay identical.
        foreach (int fixedIndex in new[] { 0, 1, 2, 4, 6 })
        {
            if (!string.Equals(
                    previousParts[fixedIndex],
                    currentParts[fixedIndex],
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (!int.TryParse(previousParts[3], out int previousHp)
            || !int.TryParse(currentParts[3], out int currentHp)
            || !int.TryParse(previousParts[5], out _)
            || !int.TryParse(currentParts[5], out _))
        {
            return false;
        }

        // Crossing the alive/dead boundary can invalidate targets and lethal ordering.
        return previousHp > 0 && currentHp > 0;
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
