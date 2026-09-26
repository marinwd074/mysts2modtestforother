using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes;
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

    private const int BoundedRefreshCandidateLimit = 3;
    private const int BoundedRefreshPrefixActionLimit = 2;
    private const int BoundedRefreshBeamWidth = 24;
    private const int BoundedRefreshExpandedNodeLimit = 192;
    private const int BoundedRefreshTimeLimitMilliseconds = 60;

    private static MultiplayerPlanRefreshDecision TryBoundedMultiplayerPlanRefresh(
        NGame host,
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
                refreshed: null,
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
            CombatBeamSolver replaySolver = new(
                root,
                displayNames,
                battleDamage,
                searchPolicy);

            List<BoundedReplayEvaluation> evaluations = [];
            foreach (MultiplayerReplayCandidate candidate in
                     source.MultiplayerReplayCandidates.Take(BoundedRefreshCandidateLimit))
            {
                if (candidate.Prefix.Count == 0
                    || candidate.Prefix.Count > BoundedRefreshPrefixActionLimit
                    || candidate.Prefix.Any(action =>
                        action.Kind != PlanActionKind.PlayCard
                        || action.EndsPlayerTurn
                        || !action.IsExecutable
                        || action.Turn != root.StartTurnNumber))
                {
                    continue;
                }

                SimulationSnapshot snapshot =
                    replaySolver.ReplayDiagnosticPrefix(candidate.Prefix);
                try
                {
                    bool completeVictory = SolverInterimResultOrdering.IsCompleteVictory(
                        candidate.Prefix.Count,
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
                    refreshed: null,
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

            SolverSearchProfile boundedProfile = searchPolicy.Profile with
            {
                BeamWidth = Math.Min(
                    searchPolicy.Profile.BeamWidth,
                    BoundedRefreshBeamWidth),
                MaxExpandedNodes = Math.Min(
                    searchPolicy.Profile.MaxExpandedNodes,
                    BoundedRefreshExpandedNodeLimit),
                SoftTimeBudgetMilliseconds = Math.Min(
                    searchPolicy.Profile.SoftTimeBudgetMilliseconds,
                    BoundedRefreshTimeLimitMilliseconds),
            };
            CombatBeamSolver materializeSolver = new(
                root,
                displayNames,
                battleDamage,
                searchPolicy,
                searchProfile: boundedProfile,
                fixedPrefixActions: selected.Candidate.Prefix,
                reserveScenarioReevaluationBudget: false);
            SolverResult refreshed = materializeSolver.Solve();

            bool prefixPreserved = refreshed.BestNode.Actions.Count >= selected.Candidate.Prefix.Count;
            for (int index = 0; prefixPreserved && index < selected.Candidate.Prefix.Count; index++)
            {
                prefixPreserved = HasSameExecutionIdentity(
                    refreshed.BestNode.Actions[index],
                    selected.Candidate.Prefix[index]);
            }
            if (!prefixPreserved)
            {
                DropRetainedPlanForFullRestart();
                LogPlanRefresh(
                    MultiplayerPlanRefreshDecision.FullRestart,
                    worldVersion,
                    source,
                    refreshed,
                    evaluations.Count,
                    firstActionChanged: false,
                    stopwatch.Elapsed,
                    "materialized_prefix_mismatch");
                return MultiplayerPlanRefreshDecision.FullRestart;
            }

            MultiplayerPlanRefreshDecision decision =
                selected.Candidate.OriginalRank == 0
                    ? MultiplayerPlanRefreshDecision.Continue
                    : MultiplayerPlanRefreshDecision.Reselect;
            bool firstActionChanged = decision == MultiplayerPlanRefreshDecision.Reselect;

            _combat.State = state;
            _combat.LatestResult = refreshed;
            _combat.LatestStamp = currentStamp;
            _combat.LatestRouteVersion = MultiplayerRouteChangeTracker.Version;
            _combat.ContinuationSource = refreshed;
            _combat.AwaitingMultiplayerContinuation = false;
            _combat.LastPlanRefreshDecisionTimestampMilliseconds =
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            BattleDamageTracker.RegisterPlan(state, refreshed);
            SolverOverlay.ShowResult(
                host,
                SolverOverlaySnapshot.CaptureWithReviewedWorldlines(
                    refreshed,
                    UnexpectedReplanCount > 0,
                    _combat.ReviewedWorldlinesTotal));
            LogPlanRefresh(
                decision,
                worldVersion,
                source,
                refreshed,
                evaluations.Count,
                firstActionChanged,
                stopwatch.Elapsed,
                decision == MultiplayerPlanRefreshDecision.Continue
                    ? "current_prefix_rematerialized"
                    : "retained_alternative_rematerialized");
            return decision;
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
                refreshed: null,
                replayedCandidates: 0,
                firstActionChanged: false,
                stopwatch.Elapsed,
                "replay_or_materialize_exception");
            return MultiplayerPlanRefreshDecision.FullRestart;
        }
    }

    private static bool HasSameExecutionIdentity(
        PlanAction actual,
        PlanAction expected)
        => actual.Kind == expected.Kind
            && actual.Turn == expected.Turn
            && string.Equals(actual.CardId, expected.CardId, StringComparison.Ordinal)
            && actual.CardOccurrence == expected.CardOccurrence
            && actual.TargetIndex == expected.TargetIndex
            && actual.TargetCombatId == expected.TargetCombatId
            && string.Equals(actual.CardStateKey, expected.CardStateKey, StringComparison.Ordinal)
            && actual.CardStateOccurrence == expected.CardStateOccurrence
            && actual.CardUpgradeLevel == expected.CardUpgradeLevel
            && string.Equals(
                actual.CardEnchantmentId,
                expected.CardEnchantmentId,
                StringComparison.Ordinal)
            && Equals(actual.Choice, expected.Choice)
            && Equals(actual.NestedChoices, expected.NestedChoices);

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
        SolverResult? refreshed,
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
            $"source_route_identity={source?.RouteIdentity ?? "-"} " +
            $"refreshed_route_identity={refreshed?.RouteIdentity ?? "-"} " +
            $"bounded_expanded_nodes={refreshed?.ExpandedNodes.ToString() ?? "-"} " +
            $"bounded_boundary={refreshed?.BoundaryReason.ToString() ?? "-"} " +
            $"replay_latency_ms={elapsed.TotalMilliseconds:F2} reason={reason}");
    }
}
