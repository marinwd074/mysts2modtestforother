using System.Diagnostics;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private bool CanUseInlineTeammateForecast(SearchNode node)
    {
        if (!policy.UseMultiplayerTeammateForecast)
            return false;
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
        long e0ShadowStarted = Stopwatch.GetTimestamp();
        ShadowTeammatePlanResult forecast;
        try
        {
            forecast = ShadowTeammatePlanner.BuildTeamSingleActionRoutes(
                source,
                _player,
                node.Snapshot.ProcessedEnemyDeaths,
                MultiplayerInterleaveOrderPolicy.MaximumSingleObservationRoutes);
        }
        finally
        {
            RecordSearchEfficiencyPhase("shadow", e0ShadowStarted);
        }

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
                // Reverse-order replay is diagnostic-only. Running it for every retained
                // teammate route multiplied production search work without changing any
                // candidate, score, transposition key or deployment decision.
                MultiplayerInterleaveOrderRelation orderRelation =
                    ProbeReverseInterleaveOrder(node, route);
                policy.Diagnostics.Info(
                    $"[CombatSolver/Multiplayer] MP_U5_INTERLEAVE " +
                    $"turn={node.Turn} order=local_then_teammate " +
                    $"local={node.Action?.CardId ?? "-"} remote_player={remoteAction.PlayerNetId} " +
                    $"remote={remoteAction.CardId} target={remoteAction.TargetCombatId?.ToString() ?? "-"} " +
                    $"reverse_order={orderRelation} " +
                    $"order_collapsible={MultiplayerInterleaveOrderPolicy.CanCollapseOrder(orderRelation).ToString().ToLowerInvariant()} " +
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
    /// Replays the same local/teammate pair in reverse order on a detached fork. This is a
    /// diagnostic/equivalence probe only: a teammate-first result never becomes a deployable
    /// "wait for teammate" action. If the local action becomes illegal after the teammate action,
    /// the reverse ordering is explicitly unavailable.
    /// </summary>
    private MultiplayerInterleaveOrderRelation ProbeReverseInterleaveOrder(
        SearchNode afterLocal,
        ShadowTeammateRoute localThenRemote)
    {
        if (afterLocal.Parent is not { } beforeLocal
            || afterLocal.Action is not
            {
                Kind: PlanActionKind.PlayCard,
                EndsPlayerTurn: false,
            } localAction
            || localThenRemote.Actions.Count != 1)
        {
            return MultiplayerInterleaveOrderRelation.ReverseUnavailable;
        }

        CombatPredictionSimulator forwardSimulator = localThenRemote.Simulator;
        if (!MultiplayerInterleaveOrderPolicy.CanProbeReverseOrder(
                beforeLocal.Snapshot.HasSimulator))
        {
            return MultiplayerInterleaveOrderRelation.ReverseUnavailable;
        }

        CombatPredictionSimulator reverseSimulator =
            ((CombatPredictionSimulator)beforeLocal.Snapshot.Simulator).Fork();
        ForkableSet<uint> reverseDeaths =
            ((ForkableSet<uint>)beforeLocal.Snapshot.ProcessedEnemyDeaths).Fork();
        int reverseShuffleEventsBefore = reverseSimulator.ShuffleEventCount;
        if (!ShadowTeammatePlanner.TryReplayForecastActionsForOrderProbe(
                reverseSimulator,
                localThenRemote.Actions,
                reverseDeaths))
        {
            return MultiplayerInterleaveOrderRelation.ReverseUnavailable;
        }

        SimulatedCombatState reverseCombat =
            (SimulatedCombatState)reverseSimulator.State.CombatState;
        var reversePlayerState =
            reverseSimulator.State.GetPlayerCombatState(_player);
        var localCard = FindCardForReplay(reversePlayerState.Hand.Cards, localAction);
        if (localCard == null
            || !CanConsiderCardAction(localCard)
            || !reverseCombat.CanPlayCard(reverseSimulator, localCard))
        {
            return MultiplayerInterleaveOrderRelation.ReverseUnavailable;
        }
        if (localAction.TargetCombatId is { } targetId
            && reverseCombat.GetCreature(targetId) == null)
        {
            return MultiplayerInterleaveOrderRelation.ReverseUnavailable;
        }

        int reverseShufflesCrossed = checked(
            beforeLocal.Snapshot.ShufflesCrossed
            + reverseSimulator.ShuffleEventCount
            - reverseShuffleEventsBefore);
        SimulationSnapshot reverseParent = Snapshot(
            reverseSimulator,
            beforeLocal.Turn,
            beforeLocal.ActionCount + 1,
            reverseShufflesCrossed,
            SearchBoundaryReason.None,
            reverseDeaths);
        SimulationSnapshot reverseOutcome;
        try
        {
            reverseOutcome = Replay(
                [localAction],
                reverseParent,
                startingTurn: beforeLocal.Turn,
                priorActionCount: beforeLocal.ActionCount + 1,
                countTransition: false,
                allowExecutionCapture: false);
        }
        finally
        {
            reverseParent.ReleaseSimulator();
        }

        try
        {
            if (reverseOutcome.BoundaryReason != SearchBoundaryReason.None)
                return MultiplayerInterleaveOrderRelation.ReverseUnavailable;

            IReadOnlySet<string> ended =
                localThenRemote.TurnEndedPlayerNetIds;
            StateFingerprint forwardKey = ShadowFutureStateFingerprint.Capture(
                forwardSimulator,
                localThenRemote.ProcessedEnemyDeaths,
                ended,
                localThenRemote.Actions);
            StateFingerprint reverseKey = ShadowFutureStateFingerprint.Capture(
                (CombatPredictionSimulator)reverseOutcome.Simulator,
                reverseOutcome.ProcessedEnemyDeaths,
                ended,
                localThenRemote.Actions);
            return forwardKey == reverseKey
                ? MultiplayerInterleaveOrderRelation.ExactEquivalent
                : MultiplayerInterleaveOrderRelation.OrderSensitive;
        }
        finally
        {
            reverseOutcome.ReleaseSimulator();
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
