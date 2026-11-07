using System.Diagnostics;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    // Only the recovery caller uses this gate. Diagnostic replay and ordinary
    // search still materialize normally. Approval always uses the same policy as
    // the finished result and does not grant execution authority.
    private ContinuationStamp? QualifyContinuationReplay(SearchNode node)
    {
        SimulationSnapshot snapshot = node.Snapshot;
        IReadOnlyList<PredictedPotionUse> potionUses = policy.PotionStrategy.HasForcedDirectives
            ? PotionStrategySnapshot.CaptureUses(node, ((SimulatedCombatState)snapshot.Simulator.State.CombatState).PotionUses)
            : [];
        var quality = new CombatSearchCoordinator.ContinuationReplayQuality(
            SolverInterimResultOrdering.IsCompleteVictory(node.ActionCount, snapshot.AllEnemiesDead,
                snapshot.PlayerDead, snapshot.ProjectedPlayerHp),
            !snapshot.AllEnemiesDead && !snapshot.PlayerDead && snapshot.ProjectedPlayerHp > 0,
            snapshot.BoundaryReason, battleDamage.HpLostSoFar + snapshot.CumulativePlayerHpLost,
            node.PotionCount, battleDamage.PotionsUsedSoFar, potionUses, snapshot.ProjectedDeathSaveUseCount,
            snapshot.GrowthRewards, snapshot.RelicCounters, snapshot.OutstandingStolenResource);
        ContinuationStamp? stamp = null;
        if (!CombatSearchCoordinator.IsAcceptableContinuationReplay(policy, quality,
            () => stamp = ContinuationStamp.CapturePredicted(_player, snapshot.Simulator,
                snapshot.Turn, _forecast, _startTurnNumber), out _))
            throw new ContinuationSeedRejectedException("quality_rejected");
        return stamp;
    }

    // Replay only the supplied local route. No Beam expansion and no old branch state.
    private SearchNode ReplayContinuationRoute(SearchNode rootNode,
        IReadOnlyList<PlanAction> actions, Stopwatch stopwatch,
        RouteEvaluationWindow? targetWindow = null, bool chargeCompletionWork = false)
    {
        SearchNode node = rootNode;
        try
        {
            bool budgetedLocalReplay = policy.ChargeLocalRouteReplayActions
                && _routePolicy == SearchRoutePolicy.MultiplayerSinglePlayerCore
                && policy.RecoveryBudget != null;
            int actionLimit = budgetedLocalReplay ? MultiplayerPlanRefreshRequest.MaximumRetainedRouteActions
                : MultiplayerPlanRefreshRequest.UnchargedReplayActionLimit;
            if (_includeTurnSetup || actions.Count == 0 || actions.Count > actionLimit)
                throw new ContinuationSeedRejectedException("route_shape");
            foreach (PlanAction action in actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (node.Snapshot.AllEnemiesDead && !node.Snapshot.PlayerDead)
                    break;
                if (targetWindow is { } target
                    && RouteEvaluationWindow.CanCompare(node.Snapshot.EvaluationWindow?.ForTurn(_startTurnNumber), target))
                    break;
                if (node.IsTerminal)
                {
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] MP_LOCAL_XTURN_ROUTE_REPLAY_BOUNDARY " +
                        $"turn={node.Turn} boundary={node.Snapshot.BoundaryReason} " +
                        $"previous_action={node.Action?.CardId ?? node.Action?.Kind.ToString()} " +
                        $"next_action={action.CardId ?? action.Kind.ToString()}");
                    throw new ContinuationSeedRejectedException("route_boundary");
                }
                if (EffectiveSearchElapsedMilliseconds(stopwatch) >= _profile.SoftTimeBudgetMilliseconds)
                    throw new ContinuationSeedRejectedException("route_time_budget");
                if (action.Turn != node.Turn || action.ShadowForecast != null
                    || action.IsForecastOnlyObservation
                    || action.Kind is not (PlanActionKind.PlayCard or PlanActionKind.EndTurn))
                    throw new ContinuationSeedRejectedException("route_action_shape");
                if (action.Kind == PlanActionKind.PlayCard && !CanApplyFixedPrefixAction(node, action))
                    throw new ContinuationSeedRejectedException("route_action_unavailable");

                // Optional evaluation completion spends the existing request's work budget.
                // Long-route recovery charges every step across all competing routes.
                if (chargeCompletionWork || budgetedLocalReplay)
                {
                    if (!HasExpandedNodeBudgetRemaining() || !TryConsumeExpandedNodeBudget())
                        throw new ContinuationSeedRejectedException("route_node_budget");
                    _run.Expanded++;
                }

                SimulationSnapshot snapshot = Replay([action], node.Snapshot, node.Turn,
                    node.ActionCount, allowExecutionCapture: false);
                SearchNode child = new(action, node.ActionCount + 1, snapshot.PotionUseCount,
                    snapshot.PotionStrategicCost, snapshot.Turn, node.Traits, node.FutureSoldHp,
                    ApplySoldHpPenalty(snapshot.Score, node.FutureSoldHp), snapshot.StateKey,
                    snapshot.HasRisk, snapshot.BoundaryReason,
                    snapshot.PlayerDead || snapshot.AllEnemiesDead
                        || snapshot.BoundaryReason != SearchBoundaryReason.None,
                    node, snapshot, CombatProgressState.Capture(snapshot))
                { CumulativeEnemyHpLost = AccumulateEnemyHpLost(node, snapshot) };
                node.Snapshot.ReleaseSimulator();
                node = child;
                if (action.Kind == PlanActionKind.EndTurn || action.EndsPlayerTurn || node.IsTerminal)
                {
                    List<SearchNode> annotated = AnnotateTurnOutcomes([node]);
                    if (annotated.Count != 1)
                        throw new ContinuationSeedRejectedException("route_no_progress");
                    node = annotated[0];
                    if (budgetedLocalReplay)
                        CaptureContinuation(node);
                }
            }
            return node;
        }
        catch
        {
            node.Snapshot.ReleaseSimulator();
            throw;
        }
    }
}
