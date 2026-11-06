using System.Diagnostics;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    // These are optional proposals within the original budget, not a second search.
    private const int MaximumEvaluationCompletionCandidates = 3;
    private const int MaximumEvaluationCompletionTemplates = 2;

    private List<SearchNode> CompleteEvaluationWindows(IReadOnlyList<SearchNode> candidates, Stopwatch stopwatch)
    {
        List<SearchNode> result = [.. candidates];
        if (!SearchEvaluationPolicy.UsesLocalEvaluationWindows(_routePolicy)
            || policy.CurrentTurnOnly || _includeTurnSetup || candidates.Count < 2)
            return result;

        RouteEvaluationWindow? Window(SearchNode node)
            => node.Snapshot.EvaluationWindow?.ForTurn(_startTurnNumber);
        SearchNode[] templates = candidates
            .Where(node => Window(node) is { CoversTarget: true }
                && !node.Snapshot.PlayerDead && node.Snapshot.ProjectedPlayerHp > 0
                && node.ActionCount is > 0 and <= 128)
            .OrderByDescending(node => node.Score)
            .Take(MaximumEvaluationCompletionTemplates).ToArray();
        if (templates.Length == 0)
            return result;
        RouteEvaluationWindow target = Window(templates[0])!.Value;

        // Scores schedule a bounded amount of work; they do not certify cross-window
        // quality. Only a settled turn-start snapshot can borrow another route's tail.
        SearchNode[] pending = candidates.Where(node => !node.IsTerminal
                && node.Snapshot.HasSimulator && Window(node) is { Resolved: true, AtPlayerDecisionPoint: true } window
                && window.StartTurn == target.StartTurn && window.TargetTurn == target.TargetTurn
                && !window.CoversTarget
                && node.Action is { } action && (action.Kind == PlanActionKind.EndTurn || action.EndsPlayerTurn))
            .OrderByDescending(node => node.Score)
            .Take(MaximumEvaluationCompletionCandidates).ToArray();
        int completed = 0;
        int attempts = 0;
        int expandedBefore = _run.Expanded;
        long transitionsBefore = _run.TransitionCount;
        List<SimulationSnapshot> owned = [];
        try
        {
            foreach (SearchNode candidate in pending)
            {
                foreach (SearchNode template in templates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!HasExpandedNodeBudgetRemaining()
                        || EffectiveSearchElapsedMilliseconds(stopwatch) >= _profile.SoftTimeBudgetMilliseconds)
                        break;
                    if (!RouteEvaluationWindow.CanCompare(Window(template), target))
                        continue;
                    PlanAction[] tail = template.Actions.Where(action => action.Turn >= candidate.Turn).ToArray();
                    if (tail.Length == 0 || tail.Any(action => action.ShadowForecast != null
                        || action.Kind is not (PlanActionKind.PlayCard or PlanActionKind.EndTurn)))
                        continue;

                    attempts++;
                    // ReplayContinuationRoute consumes its seed snapshot. Fork before handing
                    // over ownership: failure must leave the retained candidate and donor intact.
                    SimulationSnapshot fork = Replay([], candidate.Snapshot, candidate.Turn,
                        candidate.ActionCount, allowExecutionCapture: false);
                    if (fork.StateKey != candidate.StateKey)
                    {
                        fork.ReleaseSimulator();
                        throw new InvalidOperationException("Evaluation completion changed the candidate while forking.");
                    }
                    SearchNode seed = candidate with { Snapshot = fork };
                    SearchNode extended;
                    try
                    {
                        extended = ReplayContinuationRoute(seed, tail, stopwatch, target,
                            chargeCompletionWork: true);
                    }
                    catch (ContinuationSeedRejectedException rejected)
                    {
                        policy.Diagnostics.Info($"[CombatSolver/Test] SEARCH_WINDOW_COMPLETION_REJECT " +
                            $"turn={candidate.Turn} reason={rejected.Reason}");
                        continue;
                    }
                    if (!RouteEvaluationWindow.CanCompare(Window(extended), target))
                    {
                        extended.Snapshot.ReleaseSimulator();
                        continue;
                    }
                    int index = result.FindIndex(node => ReferenceEquals(node, candidate));
                    result[index] = extended;
                    owned.Add(extended.Snapshot);
                    completed++;
                    break;
                }
            }
            if (attempts > 0)
                policy.Diagnostics.Info($"[CombatSolver/Test] SEARCH_WINDOW_COMPLETION " +
                    $"attempts={attempts} completed={completed} expanded={_run.Expanded - expandedBefore} " +
                    $"transitions={_run.TransitionCount - transitionsBefore}");
            return result;
        }
        catch
        {
            foreach (SimulationSnapshot snapshot in owned)
                snapshot.ReleaseSimulator();
            throw;
        }
    }
}
