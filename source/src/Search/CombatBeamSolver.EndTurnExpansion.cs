using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

// End-turn materialization is isolated from the general action expansion body;
// ownership transfer and transposition admission remain exactly as before.
internal sealed partial class CombatBeamSolver
{
    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> BuildEndTurnBranches(
        SearchNode node,
        IReadOnlyList<PlanCardChoice> choices)
    {
        PlanAction action = new(
            PlanActionKind.EndTurn,
            node.Turn,
            TurnStartChoices: choices.Count == 0 ? null : choices);

        if (choices.Count == 0 && CanUseJointForecastEndTurn(node))
        {
            bool producedJointBranch = false;
            foreach ((PlanAction jointAction, SimulationSnapshot jointSnapshot) in
                     BuildJointForecastEndTurnBranches(node, action))
            {
                producedJointBranch = true;
                yield return (jointAction, jointSnapshot);
            }
            if (producedJointBranch)
                yield break;
        }

        SimulationSnapshot snapshot = ReplayAction(node, action);
        foreach ((PlanAction resolvedAction, SimulationSnapshot resolvedSnapshot) in
                 ResolveRoundChoiceBranches(node, action, snapshot))
        {
            yield return (resolvedAction, resolvedSnapshot);
        }
    }

    private bool CanUseJointForecastEndTurn(SearchNode node)
    {
        if (policy.RoutePolicy != SearchRoutePolicy.MultiplayerLocalCrossTurn)
            return false;
        CombatPredictionSimulator simulator =
            (CombatPredictionSimulator)node.Snapshot.Simulator;
        if (simulator.State.RootCapturedPlayers.Count <= 1)
            return false;
        SimulatedCombatState combat =
            (SimulatedCombatState)simulator.State.CombatState;
        // Extra turns are per-player in native multiplayer. Keep the old path until
        // Joint forecast tracks the exact subset of players taking that extra side turn.
        return !combat.HasPotentialExtraPlayerTurn(simulator.State.RootCapturedPlayers);
    }

    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)>
        BuildJointForecastEndTurnBranches(
            SearchNode node,
            PlanAction action)
    {
        CombatPredictionSimulator source =
            (CombatPredictionSimulator)node.Snapshot.Simulator;
        int sourceShuffleEvents = source.ShuffleEventCount;
        int roundHistoryEntryStart = source.History.Entries.Count;

        ShadowTeammatePlanResult forecast = ShadowTeammatePlanner.BuildTeamTopKRoutes(
            source,
            _player,
            node.Snapshot.ProcessedEnemyDeaths);

        foreach (ShadowTeammateRoute route in forecast.Routes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CombatPredictionSimulator simulator = route.Simulator;
            SimulatedCombatState combat =
                (SimulatedCombatState)simulator.State.CombatState;
            ForkableSet<uint> processedEnemyDeaths =
                new(route.ProcessedEnemyDeaths);
            int shufflesCrossed = checked(
                node.Snapshot.ShufflesCrossed
                + simulator.ShuffleEventCount
                - sourceShuffleEvents);

            TurnStartChoiceCursor roundChoices = new(null);
            combat.BeginActionChoices(roundChoices);
            combat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnEnd);
            bool valid = true;
            SearchBoundaryReason boundary = SearchBoundaryReason.None;
            try
            {
                if (simulator.IsInProgress)
                {
                    int playerSideShuffleEvents = simulator.ShuffleEventCount;
                    bool completed = PlayerTurnEndLifecycle.RunForecastFullPlayerSideEnd(
                        simulator,
                        combat,
                        simulator.State.RootCapturedPlayers,
                        processedEnemyDeaths,
                        out _);
                    shufflesCrossed = checked(
                        shufflesCrossed
                        + simulator.ShuffleEventCount
                        - playerSideShuffleEvents);
                    valid = completed && !combat.HasPendingChoice;
                }

                if (valid)
                {
                    simulator.CheckWinCondition(combat.GetPlayerTurnNumber(_player));
                    if (simulator.IsInProgress)
                    {
                        boundary = AdvanceEnemySideAndPlayerStart(
                            simulator,
                            combat,
                            simulator.State.GetPlayerCombatState(_player),
                            node.Turn - _startTurnNumber,
                            processedEnemyDeaths,
                            ref shufflesCrossed,
                            roundChoices,
                            takingExtraTurn: false,
                            hasActiveEmotionChip: false,
                            roundHistoryEntryStart,
                            turnStartChoices: null,
                            roundCheckpointCapture: null,
                            jointForecast: true);
                    }

                    valid = boundary != SearchBoundaryReason.PendingChoice
                        && !combat.HasPendingChoice;
                }
            }
            finally
            {
                combat.EndActionChoices();
            }

            if (!valid)
                continue;

            _ = combat.ConsumePlayerTurnEndRequest();
            if (boundary == SearchBoundaryReason.None
                && !SettleReplayActionBoundary(simulator, combat))
            {
                continue;
            }

            int turn = combat.GetPlayerTurnNumber(_player);
            SimulationSnapshot snapshot = Snapshot(
                simulator,
                turn,
                node.ActionCount + 1,
                shufflesCrossed,
                boundary,
                processedEnemyDeaths);
            PlanAction jointAction = action with
            {
                ShadowForecast = new ShadowForecastPlan(
                    route.Actions.ToArray(),
                    route.BehaviorLogProbability,
                    route.BehaviorDecisionCount,
                    route.ScenarioProbabilityMass,
                    route.ScenarioConditionalProbability,
                    route.RetainedScenarioProbabilityMass,
                    route.ScenarioProbabilityTrusted,
                    route.ScenarioFingerprint,
                    route.ScenarioKind),
            };
            yield return (jointAction, snapshot);
        }
    }

    private IEnumerable<SearchNode> BuildAcceptedEndTurnNodes(SearchNode node)
    {
        using ExpansionBatch batch = RentExpansionBatch();
        GenerateRawEndTurnCandidates(node, batch);
        PruneCommittedCrossTurnCandidates(batch.EndTurns, batch);
        if (NeedsCycleExitAdmission(node, [], null, batch.EndTurns))
        {
            AnnotateCycleExitProgress(node, batch.EndTurns);
            _ = MaterializeAdmittedCycleExitObservation(
                batch.EndTurns,
                _run.CycleFamilyLedger);
        }
        foreach (SearchNode endNode in batch.EndTurns)
        {
            if (!TryAcceptTransposition(endNode))
            {
                batch.Release(endNode.Snapshot);
                continue;
            }
            batch.Transfer(endNode.Snapshot);
            yield return endNode;
        }
    }
}
