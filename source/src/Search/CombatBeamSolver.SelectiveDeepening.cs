namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private List<SearchNode> SelectDeepeningFrontier(List<SearchNode> frontier, int completedLayers)
    {
        if (!policy.UseSelectiveDeepening || policy.CurrentTurnOnly || _includeTurnSetup
            || policy.NoveltySearch != null || _continuationSeedProbe
            || root.PlayerCount < 2 || !root.AllowsLocalPlayerOnlySearch
            || policy.UseMultiplayerTeamObjective || policy.UseMultiplayerTeammateForecast
            || _minimumPotionUses > 0 || _potionPolicy == SolverPotionPolicy.RequireAtLeastOne
            || _potionStrategy.Directives.Any(item => item.Directive == SolverPotionDirective.Force)
            || _routePolicy != SearchRoutePolicy.MultiplayerSinglePlayerCore
            || completedLayers is < 1 or > 2)
            return frontier;

        int width = _profile.BeamWidth;
        int limit = Math.Max(4, width - width / (completedLayers == 1 ? 4 : 2));
        List<SearchNode> selected = Retention.SelectForDeepening(frontier, limit);
        policy.Diagnostics.Info($"[CombatSolver/Test] SEARCH_DEEPENING " +
            $"next_layer={completedLayers + 1} candidates={frontier.Count} selected={selected.Count} " +
            $"expanded={_run.Expanded} transitions={_run.TransitionCount} remaining_nodes={EffectiveRemainingExpandedNodes()}");
        if (_detailedDiagnostics && selected.Count < frontier.Count)
        {
            var retained = selected.ToHashSet(ReferenceEqualityComparer.Instance);
            foreach (SearchNode candidate in frontier)
                policy.Diagnostics.Info($"[CombatSolver/Test] SEARCH_DEEPENING_CANDIDATE " +
                    $"next_layer={completedLayers + 1} kept={retained.Contains(candidate)} " +
                    $"loss={candidate.Snapshot.CumulativePlayerHpLost} hp={candidate.Snapshot.ProjectedPlayerHp} " +
                    $"setup={candidate.Snapshot.PersistentBuffValue}/{candidate.Snapshot.LatentSetupValue} " +
                    $"actions={string.Join(',', candidate.Actions.Select(PolicyActionToken))}");
        }
        return selected;
    }

    private sealed partial class BeamRetentionPolicy
    {
        // A scheduling heuristic, never state equivalence or a dominance proof.
        // Preserve the ordinary quality leader plus setup, target-bound delay and
        // defense representatives before spending other slots on distinct openings.
        internal List<SearchNode> SelectForDeepening(IReadOnlyList<SearchNode> candidates, int limit)
        {
            if (candidates.Count <= limit) return [.. candidates];
            List<SearchNode> ranked = candidates.OrderBy(node => node,
                Comparer<SearchNode>.Create(CompareFinalCandidates)).ToList();
            List<SearchNode> selected = [];
            HashSet<SearchNode> seen = new(ReferenceEqualityComparer.Instance);
            void Add(SearchNode? node)
            {
                if (node != null && selected.Count < limit && seen.Add(node))
                    selected.Add(node);
            }
            Add(ranked[0]);
            // Compare setup already in place at the same realized cost. Otherwise
            // one extra trigger can displace the same engine established safely a
            // turn later, before either can show its benefit in the last layer.
            List<SearchNode> establishedSetup = ranked.Where(node => node.Snapshot.StrategicEffects.RetentionValue > 0
                || node.Snapshot.FutureResourceValue > 0).ToList();
            SearchNode? lowestCostSetup = establishedSetup.MinBy(node =>
                (node.Snapshot.DeathSaveUseCount, node.Snapshot.CumulativePlayerHpLost));
            Add(lowestCostSetup == null
                ? FindBestSetup(ranked.Where(node => SetupLaneValue(node.Snapshot) > 0))
                : FindBestSetup(establishedSetup.Where(node =>
                    node.Snapshot.DeathSaveUseCount == lowestCostSetup.Snapshot.DeathSaveUseCount
                    && node.Snapshot.CumulativePlayerHpLost == lowestCostSetup.Snapshot.CumulativePlayerHpLost)));
            Add(ranked.Where(node => node.Snapshot.DelayedDamageValue > 0)
                .OrderByDescending(node => node.Snapshot.DelayedDamageValue).FirstOrDefault());
            Add(ranked.OrderByDescending(node => node.Snapshot.ProjectedPlayerHp)
                .ThenByDescending(node => node.Snapshot.PlayerHp).FirstOrDefault());

            string Opening(SearchNode node)
            {
                PlanAction? first = null;
                for (SearchNode? cursor = node; cursor != null; cursor = cursor.Parent)
                    if (cursor.Action is { } action && action.Turn == _startTurnNumber)
                        first = action;
                return first == null ? "" : MultiplayerChanceDecisionIdentity.CurrentTurnDecisionKey([first], _startTurnNumber);
            }
            HashSet<string> openings = selected.Select(Opening).ToHashSet(StringComparer.Ordinal);
            foreach (SearchNode node in ranked)
            {
                if (selected.Count == limit) break;
                if (openings.Add(Opening(node))) Add(node);
            }
            // Same opening does not mean same state; spare slots retain its distinct
            // continuations in ordinary quality order rather than collapsing them.
            foreach (SearchNode node in ranked) Add(node);
            return selected;
        }
    }
}
