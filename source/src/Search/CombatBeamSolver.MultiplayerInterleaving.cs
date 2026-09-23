using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private bool CanUseInlineTeammateForecast(SearchNode node)
    {
        if (policy.RoutePolicy != SearchRoutePolicy.MultiplayerLocalCrossTurn)
            return false;
        if (node.Action is not
            {
                Kind: PlanActionKind.PlayCard,
                EndsPlayerTurn: false,
            }
            || node.Action.Turn != node.Turn)
        {
            return false;
        }

        CombatPredictionSimulator simulator =
            (CombatPredictionSimulator)node.Snapshot.Simulator;
        if (simulator.State.RootCapturedPlayers.Count <= 1)
            return false;

        SimulatedCombatState combat =
            (SimulatedCombatState)simulator.State.CombatState;
        if (combat.HasPotentialExtraPlayerTurn(simulator.State.RootCapturedPlayers))
            return false;

        int forecastCount = 0;
        for (SearchNode? current = node;
             current?.Action is { } action && action.Turn == node.Turn;
             current = current.Parent)
        {
            if (action.Kind == PlanActionKind.TeammateForecast)
                forecastCount++;
        }
        return forecastCount < MultiplayerInterleaveOrderPolicy.MaximumForecastObservationsPerTurn;
    }

    /// <summary>
    /// Adds one bounded teammate observation after a completed local card. The resulting child is
    /// a real shared-simulator state transition, so vulnerable/attack ordering, kill triggers,
    /// shared RNG, draw/resource mutations and death processing all flow through the same F used
    /// by the rest of the combat simulator. The observation action is never deployable.
    /// </summary>
    private IEnumerable<SearchNode> BuildAcceptedInlineTeammateForecastNodes(SearchNode node)
    {
        if (!CanUseInlineTeammateForecast(node))
            yield break;

        CombatPredictionSimulator source =
            (CombatPredictionSimulator)node.Snapshot.Simulator;
        int sourceShuffleEvents = source.ShuffleEventCount;
        ShadowTeammatePlanResult forecast =
            ShadowTeammatePlanner.BuildTeamSingleActionRoutes(
                source,
                _player,
                node.Snapshot.ProcessedEnemyDeaths,
                MultiplayerInterleaveOrderPolicy.MaximumSingleObservationRoutes);

        foreach (ShadowTeammateRoute route in forecast.Routes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (route.Actions.Count != 1)
                throw new InvalidOperationException(
                    "U5 inline teammate forecast must contain exactly one remote action.");

            ForkableSet<uint> processedEnemyDeaths =
                new(route.ProcessedEnemyDeaths);
            int shufflesCrossed = checked(
                node.Snapshot.ShufflesCrossed
                + route.Simulator.ShuffleEventCount
                - sourceShuffleEvents);

            SimulationSnapshot snapshot = Snapshot(
                route.Simulator,
                node.Turn,
                node.ActionCount + 1,
                shufflesCrossed,
                SearchBoundaryReason.None,
                processedEnemyDeaths);
            ShadowTeammateActionCandidate remoteAction = route.Actions[0];
            PlanAction forecastAction = new(
                PlanActionKind.TeammateForecast,
                node.Turn,
                CardId: remoteAction.CardId,
                TargetCombatId: remoteAction.TargetCombatId,
                CardTitle: remoteAction.CardId,
                ShadowForecast: new ShadowForecastPlan(
                    route.Actions.ToArray(),
                    route.BehaviorLogProbability,
                    route.BehaviorDecisionCount,
                    route.ScenarioProbabilityMass,
                    route.ScenarioConditionalProbability,
                    route.RetainedScenarioProbabilityMass,
                    route.ScenarioProbabilityTrusted,
                    route.ScenarioFingerprint,
                    ShadowTeammateScenarioKind.Unspecified,
                    ScenarioSetComplete: false,
                    TurnEndedPlayerNetIds:
                        route.TurnEndedPlayerNetIds.OrderBy(id => id, StringComparer.Ordinal).ToArray()));

            bool terminal = snapshot.PlayerDead
                || snapshot.AllEnemiesDead
                || snapshot.BoundaryReason != SearchBoundaryReason.None;
            SearchNode child = new(
                forecastAction,
                node.ActionCount + 1,
                snapshot.PotionUseCount,
                snapshot.PotionStrategicCost,
                node.Turn,
                node.Traits,
                node.FutureSoldHp,
                ApplySoldHpPenalty(snapshot.Score, node.FutureSoldHp),
                snapshot.StateKey,
                snapshot.HasRisk,
                snapshot.BoundaryReason,
                terminal,
                node,
                snapshot,
                node.CombatProgress)
            {
                CumulativeEnemyHpLost = AccumulateEnemyHpLost(node, snapshot),
            };

            if (_detailedDiagnostics)
            {
                policy.Diagnostics.Info(
                    $"[CombatSolver/Multiplayer] MP_U5_INTERLEAVE " +
                    $"turn={node.Turn} order=local_then_teammate " +
                    $"local={node.Action.CardId} remote_player={remoteAction.PlayerNetId} " +
                    $"remote={remoteAction.CardId} target={remoteAction.TargetCombatId?.ToString() ?? "-"} " +
                    $"expanded={forecast.ExpandedBranches} " +
                    $"approximate_pruning={(forecast.ExpandedBranches > forecast.Routes.Count).ToString().ToLowerInvariant()} " +
                    "deployable=false proactive_wait=false");
            }

            if (TryAcceptTransposition(child))
                yield return child;
            else
                snapshot.ReleaseSimulator();
        }
    }

    /// <summary>
    /// U5 routes use the conservative Shadow future-state fingerprint for transposition identity.
    /// This prevents a generic beam state key from claiming that two local/teammate orderings are
    /// commutative merely because coarse combat summaries happen to match.
    /// </summary>
    private StateFingerprint ExactTranspositionKey(SearchNode node)
    {
        List<ShadowTeammateActionCandidate>? forecastActions = null;
        HashSet<string>? turnEndedPlayers = null;
        foreach (PlanAction action in node.Actions)
        {
            if (action.Kind != PlanActionKind.TeammateForecast
                || action.ShadowForecast == null)
            {
                continue;
            }

            forecastActions ??= [];
            forecastActions.AddRange(action.ShadowForecast.Actions);
            if (action.ShadowForecast.TurnEndedPlayerNetIds is { Count: > 0 } ended)
            {
                turnEndedPlayers ??= new HashSet<string>(StringComparer.Ordinal);
                turnEndedPlayers.UnionWith(ended);
            }
        }

        if (forecastActions == null)
            return node.StateKey;

        return ShadowFutureStateFingerprint.Capture(
            (CombatPredictionSimulator)node.Snapshot.Simulator,
            node.Snapshot.ProcessedEnemyDeaths,
            turnEndedPlayers ?? new HashSet<string>(StringComparer.Ordinal),
            forecastActions);
    }
}
