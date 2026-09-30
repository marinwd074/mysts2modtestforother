using System.Diagnostics;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    // Replay only the supplied local route. No Beam expansion and no old branch state.
    private SearchNode ReplayContinuationRoute(SearchNode rootNode,
        IReadOnlyList<PlanAction> actions, Stopwatch stopwatch)
    {
        SearchNode node = rootNode;
        try
        {
            if (_includeTurnSetup || actions.Count is 0 or > 128)
                throw new ContinuationSeedRejectedException("route_shape");
            foreach (PlanAction action in actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (node.Snapshot.AllEnemiesDead && !node.Snapshot.PlayerDead)
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
                    || action.Kind is not (PlanActionKind.PlayCard or PlanActionKind.EndTurn))
                    throw new ContinuationSeedRejectedException("route_action_shape");
                if (action.Kind == PlanActionKind.PlayCard && !CanApplyFixedPrefixAction(node, action))
                    throw new ContinuationSeedRejectedException("route_action_unavailable");

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
