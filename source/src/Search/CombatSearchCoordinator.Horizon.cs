using CombatSolver.Engine.Common;

namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    private static bool CanRefineLocalHorizon(CombatRootSnapshot root, SearchPolicySnapshot policy)
        => root.PlayerCount > 1 && root.AllowsLocalPlayerOnlySearch
            && policy.LocalCoreTurnLayers is > 0
            && policy.LocalCoreAdaptiveTurnLimit > policy.LocalCoreTurnLayers
            && policy.PotionPolicy != SolverPotionPolicy.RequireAtLeastOne
            && (policy.PotionPolicy == SolverPotionPolicy.Disabled || root.SearchablePotionCount == 0)
            && policy.PotionStrategy.Directives.All(directive => directive.Directive != SolverPotionDirective.Force)
            && !policy.UseBoundedWinTempo
            && policy.MultiplayerPlanRefresh == null
            && policy.ContinuationSeedActions.Count == 0 && policy.ContinuationRouteReplayActions.Count == 0;

    private static SolverResult RefineLocalHorizon(CombatRootSnapshot root, SolverDisplayNames names,
        BattleDamageSnapshot damage, SearchPolicySnapshot policy, SolverResult selected, CancellationToken token)
    {
        if (policy.DeferLocalMultiplayerSpeculation)
        {
            policy.Diagnostics.Info("[CombatSolver/Test] SEARCH_HORIZON_REFINEMENT stopped=observed_churn");
            return selected;
        }
        SearchRequestWallClockBudget budget = policy.RecoveryBudget!;
        int layers = policy.LocalCoreTurnLayers!.Value;
        int maximumLayers = policy.LocalCoreAdaptiveTurnLimit!.Value;
        while (layers < maximumLayers && !IsCompleteVictory(selected)
            && QualifiesForHorizon(selected, root.StartTurnNumber, layers))
        {
            token.ThrowIfCancellationRequested();
            policy.Diagnostics.Info($"[CombatSolver/Test] SEARCH_HORIZON_REFINEMENT checkpoint_layers={layers}");
            if (!CanStartHorizonMember(policy, budget))
            {
                policy.Diagnostics.Info("[CombatSolver/Test] SEARCH_HORIZON_REFINEMENT stopped=pressure_or_budget");
                break;
            }
            if (!CanReplayHorizonSeed(selected.BestNode.Actions))
                break;

            List<IReadOnlyList<PlanAction>> routes = [selected.BestNode.Actions];
            foreach (MultiplayerReplayCandidate alternative in selected.MultiplayerReplayCandidates)
            {
                if (alternative.OriginalRank > 0 && CanReplayHorizonSeed(alternative.Route))
                    routes.Add(alternative.Route);
                if (routes.Count == 3)
                    break;
            }
            if (routes.Count == 1 && !HasNearTermHorizonSignal(selected))
                break;

            // Keep alternative suffixes inside Beam for several turns before
            // committing. A single-turn seal favors delaying any attack that
            // needs to trade block now for a later lethal.
            int nextLayers = layers + Math.Min(3, maximumLayers - layers);
            List<SolverResult> evaluated = [];
            for (int index = 0; index < routes.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                if (!CanStartHorizonMember(policy, budget) || budget.RemainingProfile(policy.Profile) is not { } remaining)
                    break;

                int membersLeft = routes.Count - index;
                SolverSearchProfile profile = remaining with
                {
                    MaxExpandedNodes = remaining.MaxExpandedNodes / membersLeft,
                    SoftTimeBudgetMilliseconds = remaining.SoftTimeBudgetMilliseconds / membersLeft,
                };
                if (profile.MaxExpandedNodes <= routes[index].Count || profile.SoftTimeBudgetMilliseconds <= 0)
                    break;

                SearchPolicySnapshot member = policy with
                {
                    Profile = profile, LocalCoreTurnLayers = nextLayers,
                    LocalCoreAdaptiveTurnLimit = null, ChargeLocalRouteReplayActions = true,
                    Interaction = null, MaxDegreeOfParallelism = 1, NoveltySearch = null,
                    R0TransitionMemo = null, OptimizationSession = null,
                    RequestMemberKind = SearchRequestMemberKind.HorizonRefinement,
                };
                SolverResult? result = TryRunHorizonMember(root, names, damage, member, routes[index], layers, token);
                if (result == null || !QualifiesForHorizon(result, root.StartTurnNumber, nextLayers)
                    || (index > 0 && !RouteEvaluationWindow.CanCompare(result.EvaluationWindow, evaluated[0].EvaluationWindow)))
                    break;
                evaluated.Add(result);
            }
            if (evaluated.Count != routes.Count)
            {
                policy.Diagnostics.Info($"[CombatSolver/Test] SEARCH_HORIZON_REFINEMENT stopped=unproven " +
                    $"target_layers={nextLayers} covered={evaluated.Count}/{routes.Count}");
                break;
            }

            // Every route, including the previous winner, was independently advanced
            // to the same comparison scope before quality can replace the published result.
            SolverResult best = evaluated[0];
            foreach (SolverResult challenger in evaluated.Skip(1))
                if (IsBetterCompletedResult(root, policy, challenger, best))
                    best = challenger;

            best.MultiplayerReplayCandidates = Array.AsReadOnly(evaluated
                .OrderBy(result => !ReferenceEquals(result, best))
                .Select((result, rank) => MultiplayerPlanRefreshRequest.CaptureCandidate(rank, result)).ToArray());
            best.PortfolioTelemetry = selected.PortfolioTelemetry;
            selected = best;
            layers = nextLayers;
            policy.Diagnostics.Info($"[CombatSolver/Test] SEARCH_HORIZON_REFINEMENT committed_layers={layers} " +
                $"candidates={evaluated.Count} remaining_nodes={budget.RemainingExpandedNodes}");
        }
        token.ThrowIfCancellationRequested();
        return selected;
    }

    private static bool CanStartHorizonMember(SearchPolicySnapshot policy, SearchRequestWallClockBudget budget)
        => !budget.IsExpired && budget.HasExpandedNodeBudgetRemaining
            && policy.Interaction?.CurrentTakeoverRequest == null
            && !policy.MemoryPressureSignal.ConservativeParallelismRequired
            && !policy.MemoryPressureSignal.IsLimitReached()
            && !policy.MemoryPressureSignal.HasUnexpectedNoGcLoss()
            && policy.MemoryPressureSignal.CanReachCommit(SearchWaveMemoryPolicy.SingleParentReserve(0));

    private static bool CanReplayHorizonSeed(IReadOnlyList<PlanAction> route)
        => MultiplayerPlanRefreshRequest.CanRetainRoute(route)
            && route.All(action => action.Kind is PlanActionKind.PlayCard or PlanActionKind.EndTurn);

    private static bool QualifiesForHorizon(SolverResult result, int startTurn, int layers)
        => result.ResultScope == SolverResultScope.SearchCompletion && result.PotionCount == 0
            && !result.Snapshot.HasRisk && !result.Snapshot.PlayerDead && result.Snapshot.PlayerHp > 0
            && result.EvaluationWindow is { CoversTarget: true } window
            && window.StartTurn == startTurn && window.TargetTurn == checked(startTurn + layers);

    private static bool HasNearTermHorizonSignal(SolverResult result)
        => result.Snapshot.ProjectedPlayerHp <= 0
            || result.EnemyHpLostByTurn.OrderByDescending(pair => pair.Key).Take(2)
                .Sum(pair => (long)pair.Value) >= result.Snapshot.EnemyHp;

    private static SolverResult? TryRunHorizonMember(CombatRootSnapshot root, SolverDisplayNames names,
        BattleDamageSnapshot damage, SearchPolicySnapshot policy, IReadOnlyList<PlanAction> route,
        int seedLayers, CancellationToken token)
    {
        try
        {
            return new CombatBeamSolver(root, names, damage, policy, token,
                searchProfile: policy.Profile, continuationRouteReplayActions: route,
                localCoreRefinementSeedLayers: seedLayers).Solve();
        }
        catch (Exception ex) when (ex is ContinuationSeedRejectedException or PredictionUnsupportedException
            or NativeChoicePlanMismatchException or InvalidPlannedChoiceBranchException)
        {
            policy.Diagnostics.Info($"[CombatSolver/Test] SEARCH_HORIZON_REFINEMENT rejected={ex.Message}");
            return null;
        }
    }
}
