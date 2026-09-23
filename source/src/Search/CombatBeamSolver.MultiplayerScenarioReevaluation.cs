using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private IReadOnlyList<MultiplayerScenarioDecisionEvaluation>
        ReevaluateCurrentDecisionsAcrossScenarios(
            IReadOnlyList<SearchNode> decisionRepresentatives)
    {
        if (decisionRepresentatives.Count == 0)
            return [];

        int expandedBefore = _run.Expanded;
        int transitionsBefore = _run.TransitionCount;
        int decisionCount = Math.Min(
            decisionRepresentatives.Count,
            MultiplayerScenarioReevaluationPolicy.MaximumCurrentDecisions);
        int decisionBudget =
            MultiplayerScenarioReevaluationPolicy.ExpandedBranchBudgetPerDecision(
                _scenarioReevaluationReservedBranches,
                decisionCount);

        List<MultiplayerScenarioDecisionEvaluation> decisions =
            new(decisionCount);
        for (int decisionIndex = 0; decisionIndex < decisionCount; decisionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            decisions.Add(EvaluateScenarioDecision(
                decisionRepresentatives[decisionIndex],
                decisionBudget));
        }

        int replayExpanded = _run.Expanded - expandedBefore;
        int replayTransitions = _run.TransitionCount - transitionsBefore;
        int matrixExpanded = decisions.Sum(decision => decision.ExpandedBranches);
        if (replayExpanded != matrixExpanded)
        {
            throw new InvalidOperationException(
                $"U3 scenario work accounting diverged: run={replayExpanded} matrix={matrixExpanded}.");
        }
        policy.Diagnostics.Info(
            $"[CombatSolver/Multiplayer] MP_SCENARIO_BUDGET " +
            $"total_node_budget={_totalExpandedNodeBudget} " +
            $"main_node_budget={_profile.MaxExpandedNodes} " +
            $"reserved={_scenarioReevaluationReservedBranches} " +
            $"decisions={decisionCount} " +
            $"scenarios_per_decision={MultiplayerScenarioReevaluationPolicy.MaximumScenariosPerDecision} " +
            $"decision_budget={decisionBudget} " +
            $"replay_expanded={replayExpanded} " +
            $"replay_transitions={replayTransitions}");
        return decisions;
    }

    private MultiplayerScenarioDecisionEvaluation EvaluateScenarioDecision(
        SearchNode representative,
        int maxExpandedBranches)
    {
        string decisionKey =
            MultiplayerChanceDecisionIdentity.CurrentTurnDecisionKey(
                representative,
                _startTurnNumber);

        MultiplayerScenarioDecisionEvaluation UnknownDecision(int sharedWork = 0)
            => new(
                decisionKey,
                MultiplayerScenarioReevaluationPolicy.ScenarioSpecs
                    .Select(spec => new MultiplayerScenarioEvaluation(
                        spec,
                        MultiplayerScenarioEvaluationStatus.Unknown,
                        Outcome: null,
                        ExpandedBranches: 0))
                    .ToArray(),
                SharedExpandedBranches: sharedWork);

        if (_includeTurnSetup || maxExpandedBranches <= 0)
            return UnknownDecision();

        List<PlanAction> prefix = [];
        PlanAction? endTurn = null;
        bool unsupportedImplicitTurnEnd = false;
        bool forecastObservationBoundary = false;
        foreach (PlanAction action in representative.Actions)
        {
            if (action.Turn != _startTurnNumber)
                continue;
            if (action.Kind == PlanActionKind.EndTurn)
            {
                endTurn = action;
                break;
            }
            if (action.Kind == PlanActionKind.TeammateForecast)
            {
                // U3 cannot freeze a specific forecast observation into the current local
                // decision. Everything after this boundary is contingent and must be replanned
                // from the observed live state, so fair scenario reranking fails closed.
                forecastObservationBoundary = true;
                break;
            }

            prefix.Add(action);
            if (action.EndsPlayerTurn)
            {
                unsupportedImplicitTurnEnd = true;
                break;
            }
        }

        if (forecastObservationBoundary)
            return UnknownDecision();

        int sharedWork = prefix.Count;
        if (sharedWork > maxExpandedBranches)
            return UnknownDecision();

        SimulationSnapshot preEndSnapshot = Replay(
            prefix,
            parentSnapshot: null,
            startingTurn: _startTurnNumber,
            priorActionCount: 0,
            countTransition: false,
            allowExecutionCapture: false);
        _run.Expanded += sharedWork;
        _run.TransitionCount += sharedWork;
        try
        {
            if (preEndSnapshot.AllEnemiesDead || preEndSnapshot.PlayerDead)
            {
                return new MultiplayerScenarioDecisionEvaluation(
                    decisionKey,
                    MultiplayerScenarioReevaluationPolicy.ScenarioSpecs
                        .Select(spec => new MultiplayerScenarioEvaluation(
                            spec,
                            MultiplayerScenarioEvaluationStatus.Terminal,
                            BuildScenarioOutcome(
                                preEndSnapshot,
                                prefix.Count,
                                spec.Kind),
                            ExpandedBranches: 0))
                        .ToArray(),
                    SharedExpandedBranches: sharedWork);
            }

            if (endTurn == null
                || unsupportedImplicitTurnEnd
                || preEndSnapshot.BoundaryReason != SearchBoundaryReason.None)
            {
                return UnknownDecision(sharedWork);
            }

            int scenarioCount =
                MultiplayerScenarioReevaluationPolicy.MaximumScenariosPerDecision;
            int remainingBudget = maxExpandedBranches - sharedWork;
            if (remainingBudget <= scenarioCount)
                return UnknownDecision(sharedWork);

            // One bounded teammate search produces all four fixed ScenarioSpec representatives.
            // Do not rerun the same Shadow tree once per stress lane.
            int plannerBudget = remainingBudget - scenarioCount;
            ShadowTeammatePlanResult forecast =
                ShadowTeammatePlanner.BuildTeamTopKRoutes(
                    (global::CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator)
                        preEndSnapshot.Simulator,
                    _player,
                    preEndSnapshot.ProcessedEnemyDeaths,
                    beamWidth: ShadowTeammateScenarioPolicy.DefaultScenarioCount,
                    maxActionsPerPlayer: ShadowTeammatePlanner.DefaultMaxActions,
                    maxExpandedBranches: plannerBudget);
            _run.Expanded += forecast.ExpandedBranches;
            _run.TransitionCount += forecast.ExpandedBranches;
            sharedWork += forecast.ExpandedBranches;

            if (forecast.HitExpansionBudgetLimit
                || forecast.PendingChoiceBranches > 0
                || forecast.HitActionDepthLimit)
            {
                return UnknownDecision(sharedWork);
            }

            Dictionary<ShadowTeammateScenarioKind, ShadowTeammateRoute> routes = [];
            foreach (ShadowTeammateRoute route in forecast.Routes)
            {
                if (!route.ScenarioSetComplete
                    || !MultiplayerScenarioReevaluationPolicy.IsRequiredScenario(
                        route.ScenarioKind)
                    || routes.ContainsKey(route.ScenarioKind))
                {
                    continue;
                }
                routes.Add(route.ScenarioKind, route);
            }

            List<MultiplayerScenarioEvaluation> evaluations =
                new(scenarioCount);
            int scenarioReplayWork = 0;
            foreach (MultiplayerScenarioSpec spec in
                     MultiplayerScenarioReevaluationPolicy.ScenarioSpecs)
            {
                if (!routes.TryGetValue(spec.Kind, out ShadowTeammateRoute? route)
                    || sharedWork + scenarioReplayWork >= maxExpandedBranches)
                {
                    evaluations.Add(new MultiplayerScenarioEvaluation(
                        spec,
                        MultiplayerScenarioEvaluationStatus.Unknown,
                        Outcome: null,
                        ExpandedBranches: 0));
                    continue;
                }

                ShadowForecastPlan replayForecast = new(
                    route.Actions.ToArray(),
                    route.BehaviorLogProbability,
                    route.BehaviorDecisionCount,
                    route.ScenarioProbabilityMass,
                    route.ScenarioConditionalProbability,
                    route.RetainedScenarioProbabilityMass,
                    route.ScenarioProbabilityTrusted,
                    route.ScenarioFingerprint,
                    route.ScenarioKind,
                    route.ScenarioSetComplete,
                    route.TurnEndedPlayerNetIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());
                PlanAction replayEndTurn = endTurn with
                {
                    // Only the root-turn local decision is fixed. TurnStartChoices and the
                    // original Shadow metadata are post-decision observations and must not leak.
                    TurnStartChoices = null,
                    ShadowForecast = replayForecast,
                };

                SimulationSnapshot outcomeSnapshot = Replay(
                    [replayEndTurn],
                    preEndSnapshot,
                    startingTurn: _startTurnNumber,
                    priorActionCount: prefix.Count,
                    countTransition: false,
                    allowExecutionCapture: false);
                _run.Expanded++;
                _run.TransitionCount = checked(
                    _run.TransitionCount + route.Actions.Count + 1);
                scenarioReplayWork++;
                try
                {
                    bool terminal =
                        outcomeSnapshot.AllEnemiesDead || outcomeSnapshot.PlayerDead;
                    if (!terminal
                        && outcomeSnapshot.BoundaryReason != SearchBoundaryReason.None)
                    {
                        evaluations.Add(new MultiplayerScenarioEvaluation(
                            spec,
                            MultiplayerScenarioEvaluationStatus.Unknown,
                            Outcome: null,
                            ExpandedBranches: 1));
                        continue;
                    }

                    evaluations.Add(new MultiplayerScenarioEvaluation(
                        spec,
                        terminal
                            ? MultiplayerScenarioEvaluationStatus.Terminal
                            : MultiplayerScenarioEvaluationStatus.Completed,
                        BuildScenarioOutcome(
                            outcomeSnapshot,
                            prefix.Count + 1,
                            spec.Kind),
                        ExpandedBranches: 1));
                }
                finally
                {
                    outcomeSnapshot.ReleaseSimulator();
                }
            }

            return new MultiplayerScenarioDecisionEvaluation(
                decisionKey,
                evaluations,
                SharedExpandedBranches: sharedWork);
        }
        finally
        {
            preEndSnapshot.ReleaseSimulator();
        }
    }

    private MultiplayerScenarioOutcome BuildScenarioOutcome(
        SimulationSnapshot snapshot,
        int actionCount,
        ShadowTeammateScenarioKind kind)
    {
        bool completeVictory = SolverInterimResultOrdering.IsCompleteVictory(
            actionCount,
            snapshot.AllEnemiesDead,
            snapshot.PlayerDead,
            snapshot.ProjectedPlayerHp);
        double enemyDurabilityRatio =
            MultiplayerCombatObjectivePolicy.ComputeEnemyDurabilityRatio(
                snapshot.EnemyDurabilityByCombatId,
                _initialEnemyMaximumHp);
        MultiplayerCombatObjectiveRank objective =
            MultiplayerCombatObjectiveMath.BuildRank(
                policy.MultiplayerCombatObjectiveStrategy,
                completeVictory,
                snapshot.AllPlayersAlive,
                snapshot.TeamLossRatio,
                snapshot.WorstPlayerLossRatio,
                enemyDurabilityRatio,
                policy.MultiplayerEnemyDurabilityRatio,
                completeVictory ? snapshot.CombatEndedTurn : null,
                _startTurnNumber);
        (double teamRemainingHpRatio, double worstPlayerRemainingHpRatio) =
            ComputeScenarioRemainingHpRatios(snapshot);
        return new MultiplayerScenarioOutcome(
            kind,
            completeVictory,
            snapshot.AllPlayersAlive,
            objective.LossEquivalent,
            objective.WorstPlayerLossRatio,
            objective.TeamLossRatio,
            objective.EnemyDurabilityRatio,
            teamRemainingHpRatio,
            worstPlayerRemainingHpRatio);
    }

    private static (double TeamRemainingHpRatio, double WorstPlayerRemainingHpRatio)
        ComputeScenarioRemainingHpRatios(SimulationSnapshot snapshot)
    {
        CombatPredictionSimulator simulator =
            (CombatPredictionSimulator)snapshot.Simulator;
        long teamHp = 0;
        long teamMaximumHp = 0;
        double worstPlayerRemainingHpRatio = double.PositiveInfinity;
        foreach (var player in simulator.State.RootCapturedPlayers)
        {
            var state = simulator.State.GetCreature(player.Creature);
            int maximumHp = Math.Max(1, state.MaxHp);
            int currentHp = Math.Max(0, state.CurrentHp);
            teamHp = checked(teamHp + currentHp);
            teamMaximumHp = checked(teamMaximumHp + maximumHp);
            worstPlayerRemainingHpRatio = Math.Min(
                worstPlayerRemainingHpRatio,
                currentHp / (double)maximumHp);
        }

        if (teamMaximumHp == 0)
            return (double.NaN, double.NaN);
        if (double.IsPositiveInfinity(worstPlayerRemainingHpRatio))
            worstPlayerRemainingHpRatio = double.NaN;
        return (
            teamHp / (double)teamMaximumHp,
            worstPlayerRemainingHpRatio);
    }
}
