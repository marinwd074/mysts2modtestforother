namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private IReadOnlyList<MultiplayerScenarioDecisionEvaluation>
        ReevaluateCurrentDecisionsAcrossScenarios(
            IReadOnlyList<SearchNode> decisionRepresentatives)
    {
        if (decisionRepresentatives.Count == 0)
            return [];

        int decisionCount = Math.Min(
            decisionRepresentatives.Count,
            MultiplayerScenarioReevaluationPolicy.MaximumCurrentDecisions);
        int cellBudget =
            MultiplayerScenarioReevaluationPolicy.ExpandedBranchBudgetPerScenario(
                _scenarioReevaluationReservedBranches,
                decisionCount);

        List<MultiplayerScenarioDecisionEvaluation> decisions =
            new(decisionCount);
        for (int decisionIndex = 0; decisionIndex < decisionCount; decisionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SearchNode representative = decisionRepresentatives[decisionIndex];
            string decisionKey =
                MultiplayerChanceDecisionIdentity.CurrentTurnDecisionKey(
                    representative,
                    _startTurnNumber);
            List<MultiplayerScenarioEvaluation> cells =
                new(MultiplayerScenarioReevaluationPolicy.MaximumScenariosPerDecision);

            foreach (MultiplayerScenarioSpec spec in
                     MultiplayerScenarioReevaluationPolicy.ScenarioSpecs)
            {
                cells.Add(EvaluateScenarioCell(
                    representative,
                    spec,
                    cellBudget));
            }

            decisions.Add(new MultiplayerScenarioDecisionEvaluation(
                decisionKey,
                cells));
        }

        policy.Diagnostics.Info(
            $"[CombatSolver/Multiplayer] MP_SCENARIO_BUDGET " +
            $"total_node_budget={_totalExpandedNodeBudget} " +
            $"main_node_budget={_profile.MaxExpandedNodes} " +
            $"reserved={_scenarioReevaluationReservedBranches} " +
            $"decisions={decisionCount} " +
            $"scenarios_per_decision={MultiplayerScenarioReevaluationPolicy.MaximumScenariosPerDecision} " +
            $"cell_budget={cellBudget} " +
            $"replay_expanded={decisions.Sum(decision => decision.ExpandedBranches)}");
        return decisions;
    }

    private MultiplayerScenarioEvaluation EvaluateScenarioCell(
        SearchNode representative,
        MultiplayerScenarioSpec spec,
        int maxExpandedBranches)
    {
        if (_includeTurnSetup || maxExpandedBranches <= 0)
        {
            return new MultiplayerScenarioEvaluation(
                spec,
                MultiplayerScenarioEvaluationStatus.Unknown,
                Outcome: null,
                ExpandedBranches: 0);
        }

        List<PlanAction> prefix = [];
        PlanAction? endTurn = null;
        bool unsupportedImplicitTurnEnd = false;
        foreach (PlanAction action in representative.Actions)
        {
            if (action.Turn != _startTurnNumber)
                continue;
            if (action.Kind == PlanActionKind.EndTurn)
            {
                endTurn = action;
                break;
            }

            prefix.Add(action);
            if (action.EndsPlayerTurn)
            {
                unsupportedImplicitTurnEnd = true;
                break;
            }
        }

        SimulationSnapshot preEndSnapshot = Replay(
            prefix,
            parentSnapshot: null,
            startingTurn: _startTurnNumber,
            priorActionCount: 0,
            countTransition: false,
            allowExecutionCapture: false);
        _run.TransitionCount += prefix.Count;
        try
        {
            if (preEndSnapshot.AllEnemiesDead || preEndSnapshot.PlayerDead)
            {
                return new MultiplayerScenarioEvaluation(
                    spec,
                    MultiplayerScenarioEvaluationStatus.Terminal,
                    BuildScenarioOutcome(
                        preEndSnapshot,
                        prefix.Count,
                        spec.Kind),
                    ExpandedBranches: 0);
            }

            if (endTurn == null
                || unsupportedImplicitTurnEnd
                || preEndSnapshot.BoundaryReason != SearchBoundaryReason.None)
            {
                return new MultiplayerScenarioEvaluation(
                    spec,
                    MultiplayerScenarioEvaluationStatus.Unknown,
                    Outcome: null,
                    ExpandedBranches: 0);
            }

            ShadowTeammatePlanResult forecast =
                ShadowTeammatePlanner.BuildTeamTopKRoutes(
                    (Engine.InCombat.Simulation.CombatPredictionSimulator)
                        preEndSnapshot.Simulator,
                    _player,
                    preEndSnapshot.ProcessedEnemyDeaths,
                    beamWidth: ShadowTeammateScenarioPolicy.DefaultScenarioCount,
                    maxActionsPerPlayer: ShadowTeammatePlanner.DefaultMaxActions,
                    maxExpandedBranches: maxExpandedBranches);
            _run.Expanded += forecast.ExpandedBranches;
            _run.TransitionCount += forecast.ExpandedBranches;

            if (forecast.HitExpansionBudgetLimit
                || forecast.PendingChoiceBranches > 0
                || forecast.HitActionDepthLimit)
            {
                return new MultiplayerScenarioEvaluation(
                    spec,
                    MultiplayerScenarioEvaluationStatus.Unknown,
                    Outcome: null,
                    forecast.ExpandedBranches);
            }

            ShadowTeammateRoute? route = forecast.Routes.FirstOrDefault(candidate =>
                candidate.ScenarioKind == spec.Kind
                && candidate.ScenarioSetComplete);
            if (route == null)
            {
                return new MultiplayerScenarioEvaluation(
                    spec,
                    MultiplayerScenarioEvaluationStatus.Unknown,
                    Outcome: null,
                    forecast.ExpandedBranches);
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
                route.ScenarioSetComplete);
            PlanAction replayEndTurn = endTurn with
            {
                // U3 fixes only the current decision. Future turn-start choices are observations
                // after the scenario divergence boundary and must not leak into reevaluation.
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
            _run.TransitionCount++;
            try
            {
                bool terminal =
                    outcomeSnapshot.AllEnemiesDead || outcomeSnapshot.PlayerDead;
                if (!terminal
                    && outcomeSnapshot.BoundaryReason != SearchBoundaryReason.None)
                {
                    return new MultiplayerScenarioEvaluation(
                        spec,
                        MultiplayerScenarioEvaluationStatus.Unknown,
                        Outcome: null,
                        forecast.ExpandedBranches);
                }

                return new MultiplayerScenarioEvaluation(
                    spec,
                    terminal
                        ? MultiplayerScenarioEvaluationStatus.Terminal
                        : MultiplayerScenarioEvaluationStatus.Completed,
                    BuildScenarioOutcome(
                        outcomeSnapshot,
                        prefix.Count + 1,
                        spec.Kind),
                    forecast.ExpandedBranches);
            }
            finally
            {
                outcomeSnapshot.ReleaseSimulator();
            }
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
        return new MultiplayerScenarioOutcome(
            kind,
            completeVictory,
            snapshot.AllPlayersAlive,
            objective.LossEquivalent,
            objective.WorstPlayerLossRatio,
            objective.TeamLossRatio,
            objective.EnemyDurabilityRatio);
    }
}
