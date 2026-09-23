using System.Diagnostics;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using BufferCard = MegaCrit.Sts2.Core.Models.Cards.Buffer;

namespace CombatSolver;


internal sealed partial class CombatBeamSolver
{
    private readonly record struct RoutingChoiceSignature(
        int Turn,
        string SourceId,
        PlanChoiceEffect Effect,
        PileType Pile,
        string CardId,
        int Upgrade,
        string CardStateKey,
        int Occurrence,
        string ContextId,
        int StateContext,
        StateFingerprint EnemyCombatDistributionKey,
        StateFingerprint EnemyControlDistributionKey,
        StateFingerprint UnorderedPileKey);
    private readonly record struct RoutingChoiceFamilySignature(
        int Turn,
        string SourceId,
        PlanChoiceEffect Effect,
        PileType Pile);
    private readonly record struct RoutingChoiceOptionSignature(
        string CardId,
        int Upgrade,
        string CardStateKey);
    private readonly record struct AmbiguousChoiceDecisionSignature(
        int PotionCount,
        StateFingerprint ParentStateKey,
        int ParentActionCount,
        int Turn,
        string SourceId,
        PlanChoiceEffect Effect,
        PileType Pile,
        int ChoiceCount,
        string ContextId);

    private readonly record struct DirectRoutingChoice(
        SearchNode Node,
        SearchNode ChoiceNode,
        SearchNode Parent,
        RoutingChoiceSignature Signature);
    private readonly record struct RootActionLineageSignature(
        PlanActionKind Kind,
        string CardId,
        string PotionId,
        uint? TargetCombatId,
        string FirstCardId,
        uint? FirstCardTargetCombatId);

    private sealed partial class BeamRetentionPolicy(
        SolverSearchProfile _profile,
        SearchRoutePolicy _routePolicy,
        MultiplayerCombatObjectiveStrategy _multiplayerCombatObjectiveStrategy,
        double _multiplayerEnemyDurabilityRatio,
        int _multiplayerEnemyMaximumHp,
        int _startTurnNumber,
        bool _isActEndingBoss,
        BossHpRelief _bossHpRelief,
        PostCombatRelicHealProfile _postCombatRelicHeal,
        int _initialEnemyCount,
        int _initialPlayerHp,
        int _initialPlayerMaxHp,
        bool _preserveReplayAllocatorOpening,
        SolverTheftPolicy? _theftPolicy,
        SolverPotionPolicy _potionPolicy,
        PotionStrategySnapshot _potionStrategy,
        bool _enforcePotionDirectives,
        bool _renewablePotionShapedRock,
        SearchRunContext _run,
        Func<SearchNode, StandPatEvaluation> _evaluateStandPat,
        Action<IEnumerable<SearchNode>>? _prepareStandPat = null)
    {
        private void ForEachRetentionIndex(
            int count,
            ParallelExpansionWorkProfile.Kind kind,
            Action<int> evaluate)
        {
            if (count >= 4 && _run.ActiveParallelExpansion is { } executor)
                executor.EvaluateRetentionIndices(count, kind, evaluate);
            else
                for (int index = 0; index < count; index++)
                    evaluate(index);
        }

        private const int PersistentRoutingContextRounds = 8;
        private const int RoutingChoiceLimit = 96;
        private const int AmbiguousCompressedChoiceLimit = 48;
        private sealed record OrderedPileCohort(IReadOnlyList<SearchNode> PrefixVariants);
        private readonly record struct PocketwatchCadenceSignature(
            int PotionCount,
            uint? FocusTargetCombatId,
            int RetainedAttackGrowth,
            StateFingerprint EnemyControlDistributionKey,
            bool TriggeredLastTurn,
            bool CanTriggerThisTurn);
        private readonly record struct PocketwatchCadenceFamilySignature(
            int PotionCount,
            uint? FocusTargetCombatId,
            int RetainedAttackGrowth,
            bool TriggeredLastTurn,
            bool CanTriggerThisTurn);
        public List<SearchNode> RankFinal(IEnumerable<SearchNode> nodes)
        {
            List<SearchNode> candidates = nodes.Distinct((IEqualityComparer<SearchNode>)ReferenceEqualityComparer.Instance).ToList();
            List<SearchNode> ranked = RankBest(
                candidates,
                _profile.BeamWidth * 4,
                finalQualityFirst: true);

            // FinalPlanOrdering has policy eligibility dimensions that are not monotone in
            // ordinary final quality (forced directives, Ambergris HP and theft recovery).
            // Preserve one representative per compact eligibility cohort, not per ordered
            // potion history: order and exact automatic-use count do not affect the policy.
            FinalPolicyQualificationFacts[] facts = new FinalPolicyQualificationFacts[candidates.Count];
            SearchNode? potionFreeBaseline = null;
            for (int index = 0; index < candidates.Count; index++)
            {
                SearchNode candidate = candidates[index];
                facts[index] = BuildFinalPolicyQualificationFacts(candidate);
                if (facts[index].ExplicitPotionUseCount == 0
                    && (potionFreeBaseline == null
                        || ComparePotionFreePolicyBaselines(
                            candidate,
                            potionFreeBaseline,
                            _initialPlayerHp,
                            _initialPlayerMaxHp,
                            _bossHpRelief,
                            _postCombatRelicHeal,
                            _theftPolicy,
                            _routePolicy) < 0))
                {
                    potionFreeBaseline = candidate;
                }
            }
            int potionFreeOutstandingResource = potionFreeBaseline?.Snapshot.OutstandingStolenResource
                ?? int.MaxValue;

            Dictionary<FinalPolicyQualificationSignature, SearchNode> qualificationLeaders = [];
            Dictionary<SearchNode, FinalPolicyQualificationSignature> signatures =
                new(ReferenceEqualityComparer.Instance);
            for (int index = 0; index < candidates.Count; index++)
            {
                SearchNode candidate = candidates[index];
                FinalPolicyQualificationSignature signature = BuildFinalPolicyQualificationSignature(
                    facts[index],
                    candidate,
                    potionFreeOutstandingResource);
                signatures.Add(candidate, signature);
                if (!qualificationLeaders.TryGetValue(signature, out SearchNode? current)
                    || CompareFinalCandidates(candidate, current) < 0)
                {
                    qualificationLeaders[signature] = candidate;
                }
            }
            foreach (SearchNode leader in qualificationLeaders.Values)
            {
                if (!ContainsReference(ranked, leader))
                    ranked.Add(leader);
            }
            if (potionFreeBaseline != null && !ContainsReference(ranked, potionFreeBaseline))
                ranked.Add(potionFreeBaseline);
            ranked.Sort((left, right) =>
            {
                int comparison = CompareFinalCandidates(left, right);
                return comparison != 0
                    ? comparison
                    : CompareFinalPolicyQualificationSignatures(
                        signatures[left],
                        signatures[right]);
            });
            AssignRetentionRanks(ranked, []);
            return ranked;
        }

        public static (int Value, int Count) GetLongTermResourceMaximum(
            IReadOnlyList<SearchNode> nodes)
        {
            int highestValue = int.MinValue;
            int highestCount = 0;
            for (int index = 0; index < nodes.Count; index++)
            {
                int value = nodes[index].Snapshot.LongTermResourceValue;
                if (value > highestValue)
                {
                    highestValue = value;
                    highestCount = 1;
                }
                else if (value == highestValue)
                {
                    highestCount++;
                }
            }
            return (highestValue, highestCount);
        }

        public List<SearchNode> RankLongTermResource(
            IReadOnlyList<SearchNode> nodes,
            int limit,
            (int Value, int Count) maximum)
        {
            if (maximum.Count == nodes.Count)
                return [];
            List<SearchNode> highest = new(maximum.Count);
            for (int index = 0; index < nodes.Count; index++)
            {
                if (nodes[index].Snapshot.LongTermResourceValue == maximum.Value)
                    highest.Add(nodes[index]);
            }
            return RankBest(
                highest,
                limit,
                preserveDefensiveRoute: true);
        }

        // One signature lookup reaches both the ordered candidates and their five extrema.
        // Keep this as a List so the existing family/option ordering consumes the same sequence.
        private sealed class RoutingChoiceNodes(SearchNode first) : List<SearchNode>
        {
            public SearchNode BestScore = first;
            public SearchNode BestOffense = first;
            public SearchNode BestDefense = first;
            public SearchNode BestSetup = first;
            public SearchNode BestPileOrder = first;
            public RoutingRankSummary? RankSummary;
        }

        private readonly record struct RoutingRankSummary(
            double MaximumBeamScore, double MaximumParentScore, int MinimumParentRank);

        private sealed class RoutingChoiceScratch
        {
            public Dictionary<RoutingChoiceSignature, List<SearchNode>> NodesByChoice { get; } = [];
            public void Clear() => NodesByChoice.Clear();
        }

        private RoutingChoiceScratch? _routingChoiceScratch;

        private RoutingChoiceScratch RentRoutingChoiceScratch()
        {
            RoutingChoiceScratch? scratch = _routingChoiceScratch;
            if (scratch is null)
                return new RoutingChoiceScratch();
            _routingChoiceScratch = null;
            scratch.Clear();
            return scratch;
        }

        private void ReturnRoutingChoiceScratch(RoutingChoiceScratch scratch)
        {
            // 归还时清空，避免把这一轮的 SearchNode 一直钉在缓冲里。
            scratch.Clear();
            _routingChoiceScratch = scratch;
        }

        private Comparison<SearchNode>? _finalCandidateComparison;

        private MultiplayerCombatObjectiveRank BuildMultiplayerObjectiveRank(SearchNode node)
        {
            bool completeVictory = IsCompleteVictory(node);
            double enemyDurabilityRatio =
                MultiplayerCombatObjectivePolicy.ComputeEnemyDurabilityRatio(
                    node.Snapshot.EnemyDurabilityByCombatId,
                    _multiplayerEnemyMaximumHp);
            return MultiplayerCombatObjectiveMath.BuildRank(
                _multiplayerCombatObjectiveStrategy,
                completeVictory,
                node.Snapshot.AllPlayersAlive,
                node.Snapshot.TeamLossRatio,
                node.Snapshot.WorstPlayerLossRatio,
                enemyDurabilityRatio,
                _multiplayerEnemyDurabilityRatio,
                completeVictory ? CompletedCombatTurn(node) : null,
                _startTurnNumber);
        }

        private int CompareMultiplayerObjective(SearchNode left, SearchNode right)
            => _routePolicy == SearchRoutePolicy.MultiplayerLocalCrossTurn
                ? MultiplayerCombatObjectiveMath.Compare(
                    BuildMultiplayerObjectiveRank(left),
                    BuildMultiplayerObjectiveRank(right))
                : 0;

        private void SortByBeamRank(List<SearchNode> ranked)
        {
            if (_routePolicy != SearchRoutePolicy.MultiplayerLocalCrossTurn)
            {
                SortByLegacyBeamRank(ranked);
                return;
            }
            if (ranked.Count < 2)
                return;

            // Multiplayer freezes both objective and score inputs once per node. The shared
            // P1 team objective is primary; historical Beam score remains only a tie-break.
            List<(SearchNode Node, double Score, MultiplayerCombatObjectiveRank TeamObjective)> scored =
                new(ranked.Count);
            foreach (SearchNode node in ranked)
            {
                scored.Add((
                    node,
                    BeamRankScore(node),
                    BuildMultiplayerObjectiveRank(node)));
            }
            scored.Sort((left, right) =>
            {
                int objective = MultiplayerCombatObjectiveMath.Compare(
                    left.TeamObjective,
                    right.TeamObjective);
                if (objective != 0)
                    return objective;
                return CompareBeamRankOrder(
                    left.Score, left.Node.Snapshot.OffensiveProgressValue, left.Node.ActionCount,
                    right.Score, right.Node.Snapshot.OffensiveProgressValue, right.Node.ActionCount);
            });
            for (int index = 0; index < ranked.Count; index++)
                ranked[index] = scored[index].Node;
        }

        private void SortByLegacyBeamRank(List<SearchNode> ranked)
        {
            if (ranked.Count < 2)
                return;
            // This is the pre-P1 single-player ordering. Keep it isolated so the extracted
            // BeamRankSortChecks contract proves that P1 does not change single-player order.
            List<(SearchNode Node, double Score)> scored = new(ranked.Count);
            foreach (SearchNode node in ranked)
                scored.Add((node, BeamRankScore(node)));
            scored.Sort(static (left, right) =>
            {
                return CompareBeamRankOrder(
                    left.Score, left.Node.Snapshot.OffensiveProgressValue, left.Node.ActionCount,
                    right.Score, right.Node.Snapshot.OffensiveProgressValue, right.Node.ActionCount);
            });
            for (int index = 0; index < ranked.Count; index++)
                ranked[index] = scored[index].Node;
        }

        private Comparison<SearchNode> FinalCandidateComparison
            => _finalCandidateComparison ??= CompareFinalCandidates;
        /// <summary>
        /// Preserves only proven non-commutative mutation collisions. This is deliberately a
        /// single coordinator pass: secondary RankBest calls cannot mint or extend leases.
        /// Existing leases are continued by semantic next-action family, not by a fixed number
        /// of actions, and every admission is charged to one small hard portfolio.
        /// </summary>

        public List<SearchNode> RankDeferredCandidates(IEnumerable<SearchNode> nodes, int limit)
        {
            List<SearchNode> ranked = nodes.ToList();
            SortByBeamRank(ranked);
            if (ranked.Count > limit)
                ranked.RemoveRange(limit, ranked.Count - limit);
            return ranked;
        }

        public List<SearchNode> RankBest(
            IEnumerable<SearchNode> nodes,
            int limit,
            bool preserveDefensiveRoute = false,
            bool finalQualityFirst = false,
            bool useSecondRankBand = false,
            Action<GlobalRetentionDecision>? observe = null)
        {
            Dictionary<SearchNode, RoutingChoiceSignature>? observedRoutingSignatures =
                observe != null && preserveDefensiveRoute
                    ? new(ReferenceEqualityComparer.Instance)
                    : null;
            HashSet<SearchNode>? observedOptionLeaders = observe != null && preserveDefensiveRoute
                ? new(ReferenceEqualityComparer.Instance)
                : null;
            List<SearchNode> ranked;
            if (finalQualityFirst)
            {
                // Equal simulator states can still have different cumulative battle loss or
                // policy-relevant action histories. Do not erase those distinctions before the
                // final policy pass has inspected them.
                ranked = nodes.ToList();
            }
            else
            {
                Dictionary<StateFingerprint, SearchNode> bestByState = [];
                foreach (SearchNode node in nodes)
                {
                    if (!bestByState.TryGetValue(node.StateKey, out SearchNode? current)
                        || IsBetterSearchNode(node, current))
                    {
                        bestByState[node.StateKey] = node;
                    }
                }
                ranked = [.. bestByState.Values];
            }

            if (finalQualityFirst)
                ranked.Sort(FinalCandidateComparison);
            else
                SortByBeamRank(ranked);
            List<SearchNode> routingChoices = [];
            if (preserveDefensiveRoute)
            {
                foreach (SearchNode candidate in BuildAmbiguousCompressedChoicePortfolio(ranked, limit))
                    AddRoutingCandidate(routingChoices, candidate, RoutingChoiceLimit);
                RoutingChoiceScratch scratch = RentRoutingChoiceScratch();
                Dictionary<RoutingChoiceSignature, List<SearchNode>> nodesByRoutingChoice = scratch.NodesByChoice;
                // The routing signature is a pure walk of the node's parent chain, so it can be
                // computed off-thread; grouping stays serial to preserve insertion order.
                RoutingChoiceSignature?[] signatureByIndex = new RoutingChoiceSignature?[ranked.Count];
                if (ranked.Count >= 64)
                {
                    ForEachRetentionIndex(ranked.Count,
                        ParallelExpansionWorkProfile.Kind.RoutingSignature, index =>
                        signatureByIndex[index] = RetainedRoutingChoice(ranked[index]));
                }
                else
                {
                    for (int index = 0; index < ranked.Count; index++)
                        signatureByIndex[index] = RetainedRoutingChoice(ranked[index]);
                }
                for (int rankedIndex = 0; rankedIndex < ranked.Count; rankedIndex++)
                {
                    SearchNode node = ranked[rankedIndex];
                    RoutingChoiceSignature? signature = signatureByIndex[rankedIndex];
                    if (signature == null)
                        continue;
                    if (observedRoutingSignatures != null)
                        observedRoutingSignatures[node] = signature.Value;
                    if (!nodesByRoutingChoice.TryGetValue(signature.Value, out List<SearchNode>? routingNodes))
                    {
                        routingNodes = new RoutingChoiceNodes(node);
                        nodesByRoutingChoice.Add(signature.Value, routingNodes);
                    }
                    else
                    {
                        RoutingChoiceNodes group = (RoutingChoiceNodes)routingNodes;
                        if (IsBetterSearchNode(node, group.BestScore))
                            group.BestScore = node;
                        if (IsBetterOffensive(node, group.BestOffense))
                            group.BestOffense = node;
                        if (IsBetterDefensive(node, group.BestDefense))
                            group.BestDefense = node;
                        if (IsBetterSetup(node, group.BestSetup))
                            group.BestSetup = node;
                        if (node.Snapshot.ProjectedShuffleOrderValue > group.BestPileOrder.Snapshot.ProjectedShuffleOrderValue
                            || node.Snapshot.ProjectedShuffleOrderValue == group.BestPileOrder.Snapshot.ProjectedShuffleOrderValue
                                && IsBetterSearchNode(node, group.BestPileOrder))
                            group.BestPileOrder = node;
                    }
                    routingNodes.Add(node);
                }
                // The ordered groups are now complete. Parent ranks and score inputs remain
                // unchanged until AssignRetentionRanks, after this entire routing block.
                // Use the original reductions once, including their NaN behavior.
                RoutingChoiceNodes[] summaryGroups = nodesByRoutingChoice.Values
                    .Cast<RoutingChoiceNodes>().ToArray();
                ForEachRetentionIndex(summaryGroups.Length,
                    ParallelExpansionWorkProfile.Kind.RoutingSummary, index =>
                {
                    RoutingChoiceNodes group = summaryGroups[index];
                    group.RankSummary = new(
                        group.Max(BeamRankScore),
                        ComputeRoutingParentScore(group),
                        ComputeRoutingParentRetentionRank(group));
                });
                _run.RoutingChoiceSummaryBuilds += summaryGroups.Length;
                List<IReadOnlyList<SearchNode>> paretoByRoutingChoice = [];
                List<IReadOnlyList<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>>> routingFamilies =
                    nodesByRoutingChoice
                        .OrderByDescending(pair => MaximumRoutingBeamScore(pair.Value))
                        .GroupBy(pair => BuildRoutingChoiceFamilySignature(pair.Key))
                        .OrderBy(family => family.Min(pair => RoutingParentRetentionRank(pair.Value)))
                        .ThenByDescending(family => family.Max(pair => RoutingParentScore(pair.Value)))
                        .ThenByDescending(family => family.Max(pair => MaximumRoutingBeamScore(pair.Value)))
                        .Select(family => (IReadOnlyList<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>>)
                            OrderRoutingChoiceEventContexts(family))
                        .ToList();
                // Each context comes from a unique dictionary key and belongs to exactly one
                // family. The only repeat is the persistent prefix emitted in the first pass.
                List<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>> orderedRoutingContexts =
                    new(nodesByRoutingChoice.Count);
                for (int round = 0; round < PersistentRoutingContextRounds; round++)
                {
                    foreach (IReadOnlyList<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>> family in
                        routingFamilies.Where(family => IsPersistentRoutingEffect(family[0].Key.Effect)))
                    {
                        if (round < family.Count)
                            orderedRoutingContexts.Add(family[round]);
                    }
                }
                int routingContextRound = 0;
                while (routingFamilies.Any(family => routingContextRound < family.Count))
                {
                    foreach (IReadOnlyList<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>> family in routingFamilies)
                    {
                        if (routingContextRound < family.Count
                            && (routingContextRound >= PersistentRoutingContextRounds
                                || !IsPersistentRoutingEffect(family[0].Key.Effect)))
                        {
                            orderedRoutingContexts.Add(family[routingContextRound]);
                        }
                    }
                    routingContextRound++;
                }
                List<SearchNode>[] paretoByContext = new List<SearchNode>[orderedRoutingContexts.Count];
                void BuildContextPareto(int contextIndex)
                {
                    List<SearchNode> routingNodes = orderedRoutingContexts[contextIndex].Value;
                    RoutingChoiceNodes group = (RoutingChoiceNodes)routingNodes;
                    SearchNode? bestDeckCuration = FindBestDeckCuration(routingNodes);
                    SearchNode? bestTargetPressure = PreferMostVulnerableTargetVariant(
                        routingNodes,
                        FindBestTargetPressure(routingNodes));
                    List<SearchNode> candidates = [];
                    if (routingNodes.Min(ActionsSinceRetainedRoutingChoice) <= 1)
                    {
                        AddRoutingCandidate(candidates, group.BestSetup);
                        AddRoutingCandidate(candidates, bestTargetPressure);
                    }
                    else
                    {
                        AddRoutingCandidate(candidates, bestTargetPressure);
                        AddRoutingCandidate(candidates, bestDeckCuration);
                        AddRoutingCandidate(candidates, group.BestSetup);
                    }
                    foreach (SearchNode node in routingNodes.Take(16))
                        AddRoutingCandidate(candidates, node);
                    AddRoutingCandidate(candidates, group.BestScore);
                    AddRoutingCandidate(candidates, group.BestOffense);
                    AddRoutingCandidate(candidates, group.BestDefense);
                    AddRoutingCandidate(candidates, group.BestPileOrder);
                    paretoByContext[contextIndex] = candidates
                        .Where(candidate => !candidates.Any(other =>
                            !ReferenceEquals(candidate, other)
                            && MultiObjectiveDominates(other, candidate)))
                        .ToList();
                }
                if (orderedRoutingContexts.Count >= 8)
                    ForEachRetentionIndex(orderedRoutingContexts.Count,
                        ParallelExpansionWorkProfile.Kind.RoutingPareto, BuildContextPareto);
                else
                    for (int contextIndex = 0; contextIndex < orderedRoutingContexts.Count; contextIndex++)
                        BuildContextPareto(contextIndex);
                foreach (List<SearchNode> pareto in paretoByContext)
                    paretoByRoutingChoice.Add(pareto);
                foreach (IReadOnlyList<KeyValuePair<RoutingChoiceSignature, List<SearchNode>>> family in routingFamilies)
                {
                    IReadOnlyList<SearchNode> familyNodes = family
                        .SelectMany(pair => pair.Value)
                        .ToList();
                    AddRoutingCandidate(
                        routingChoices,
                        PreferMostVulnerableTargetVariant(
                            familyNodes,
                            FindBestTargetPressure(familyNodes)),
                        RoutingChoiceLimit);
                    AddRoutingCandidate(
                        routingChoices,
                        FindBestDeckCuration(familyNodes),
                        RoutingChoiceLimit);
                    AddRoutingCandidate(
                        routingChoices,
                        FindBestSetup(familyNodes),
                        RoutingChoiceLimit);
                    foreach (IGrouping<RoutingChoiceOptionSignature,
                                 KeyValuePair<RoutingChoiceSignature, List<SearchNode>>> optionGroup in family
                                 .GroupBy(pair => BuildRoutingChoiceOptionSignature(pair.Key)))
                    {
                        IReadOnlyList<SearchNode> optionNodes = optionGroup
                            .SelectMany(pair => pair.Value)
                            .ToList();
                        int actionsSinceChoice = optionNodes.Min(ActionsSinceRetainedRoutingChoice);
                        SearchNode? optionLeader;
                        if (actionsSinceChoice == 0)
                        {
                            optionLeader = optionGroup
                                .OrderBy(pair => RoutingParentRetentionRank(pair.Value))
                                .ThenByDescending(pair => RoutingParentScore(pair.Value))
                                .First()
                                .Value
                                .MaxBy(BeamRankScore);
                        }
                        else if (actionsSinceChoice == 1)
                        {
                            optionLeader = FindBestSetup(optionNodes);
                        }
                        else
                        {
                            optionLeader = PreferMostVulnerableTargetVariant(
                                optionNodes,
                                FindBestTargetPressure(optionNodes));
                        }
                        if (observedOptionLeaders != null && optionLeader != null)
                            observedOptionLeaders.Add(optionLeader);
                        AddRoutingCandidate(routingChoices, optionLeader, RoutingChoiceLimit);
                    }
                }
                foreach (SearchNode candidate in BuildDirectRoutingChoiceExtremes(ranked))
                {
                    if (routingChoices.Count >= RoutingChoiceLimit)
                        break;
                    AddRoutingCandidate(routingChoices, candidate, RoutingChoiceLimit);
                }
                int routingRound = 0;
                while (routingChoices.Count < RoutingChoiceLimit
                    && paretoByRoutingChoice.Any(group => routingRound < group.Count))
                {
                    foreach (IReadOnlyList<SearchNode> group in paretoByRoutingChoice)
                    {
                        if (routingRound < group.Count)
                            AddRoutingCandidate(routingChoices, group[routingRound], RoutingChoiceLimit);
                        if (routingChoices.Count >= RoutingChoiceLimit)
                            break;
                    }
                    routingRound++;
                }
                ReturnRoutingChoiceScratch(scratch);
            }
            if (ranked.Count <= limit)
            {
                observe?.Invoke(new GlobalRetentionDecision(
                    ranked, [], routingChoices, ranked, limit, limit, null, RoutingChoiceLimit,
                    observedRoutingSignatures, observedOptionLeaders, BeamRankScore));
                AssignRetentionRanks(ranked, []);
                return ranked;
            }

            int effectiveLimit = limit;
            bool preserveOrderedPile = preserveDefensiveRoute
                && ranked.Any(node => node.Snapshot.PocketwatchCardThreshold >= 0);
            int routingChoiceQuota = preserveOrderedPile
                ? BoundedRoutingChoiceQuota(routingChoices.Count)
                : _isActEndingBoss
                    ? Math.Max(10, (limit + 3) / 2)
                    : Math.Max(8, limit * 2 / 5);
            List<OrderedPileCohort> orderedPileCohorts = [];
            if (preserveOrderedPile)
            {
                List<IGrouping<StateFingerprint, SearchNode>> tacticalGroups = ranked
                    .Where(node => node.Snapshot.PocketwatchCardThreshold >= 0)
                    .GroupBy(BuildOrderedPileTacticalKey)
                    .OrderByDescending(group => group.Max(BeamRankScore))
                    .ToList();
                List<IReadOnlyList<IGrouping<StateFingerprint, SearchNode>>> cadenceBuckets = tacticalGroups
                    .GroupBy(group => BuildPocketwatchCadenceSignature(group.First()))
                    .OrderByDescending(bucket => bucket.Max(group => group.Max(BeamRankScore)))
                    .Select(bucket => (IReadOnlyList<IGrouping<StateFingerprint, SearchNode>>)bucket
                        .OrderByDescending(group => group.Max(BeamRankScore))
                        .ToList())
                    .ToList();
                List<IReadOnlyList<IReadOnlyList<IGrouping<StateFingerprint, SearchNode>>>> cadenceFamilies =
                    cadenceBuckets
                        .GroupBy(bucket => BuildPocketwatchCadenceFamilySignature(bucket[0].First()))
                        .OrderByDescending(family => family.Max(bucket => bucket.Max(group => group.Max(BeamRankScore))))
                        .Select(family => (IReadOnlyList<IReadOnlyList<IGrouping<StateFingerprint, SearchNode>>>)family
                            .OrderByDescending(bucket => bucket.Max(group => group.Max(BeamRankScore)))
                            .ToList())
                        .ToList();
                cadenceBuckets = [];
                int cadenceRound = 0;
                while (cadenceFamilies.Any(family => cadenceRound < family.Count))
                {
                    foreach (IReadOnlyList<IReadOnlyList<IGrouping<StateFingerprint, SearchNode>>> family in cadenceFamilies)
                    {
                        if (cadenceRound < family.Count)
                            cadenceBuckets.Add(family[cadenceRound]);
                    }
                    cadenceRound++;
                }
                List<IReadOnlyList<IGrouping<StateFingerprint, SearchNode>>> paretoByCadence = [];
                foreach (IReadOnlyList<IGrouping<StateFingerprint, SearchNode>> bucket in cadenceBuckets)
                {
                    List<IGrouping<StateFingerprint, SearchNode>> candidates = [];
                    AddTacticalGroup(candidates, bucket[0]);
                    AddTacticalGroup(candidates, bucket
                        .OrderByDescending(group => group.Max(node => node.Snapshot.ProjectedPlayerHp))
                        .ThenBy(group => group.Min(node => node.Snapshot.EnemyHp))
                        .ThenByDescending(group => group.Max(BeamRankScore))
                        .First());
                    AddTacticalGroup(candidates, bucket
                        .OrderBy(group => group.Min(node => node.Snapshot.AliveEnemyCount))
                        .ThenBy(group => group.Min(node => node.Snapshot.EnemyHp))
                        .ThenByDescending(group => group.Max(BeamRankScore))
                        .First());
                    AddTacticalGroup(candidates, bucket
                        .OrderByDescending(group => group.Max(node =>
                            LaneValue(node.Snapshot, SearchRouteTraits.Control)))
                        .ThenByDescending(group => group.Max(BeamRankScore))
                        .First());
                    AddTacticalGroup(candidates, bucket
                        .OrderByDescending(group => group.Max(node =>
                            LaneValue(node.Snapshot, SearchRouteTraits.Resource)))
                        .ThenByDescending(group => group.Max(BeamRankScore))
                        .First());
                    AddTacticalGroup(candidates, bucket
                        .OrderByDescending(group =>
                            group.Max(node => node.Snapshot.ProjectedShuffleOrderValue))
                        .ThenByDescending(group => group.Max(BeamRankScore))
                        .First());
                    foreach (IGrouping<StateFingerprint, SearchNode> group in bucket)
                    {
                        if (candidates.Count >= SolverWeights.PocketwatchParetoCandidatesPerCadence)
                            break;
                        AddTacticalGroup(candidates, group);
                    }
                    List<IGrouping<StateFingerprint, SearchNode>> pareto = [];
                    foreach (IGrouping<StateFingerprint, SearchNode> candidate in candidates)
                    {
                        bool dominated = false;
                        foreach (IGrouping<StateFingerprint, SearchNode> other in candidates)
                        {
                            if (ReferenceEquals(candidate, other)
                                || !MultiObjectiveDominates(other.First(), candidate.First()))
                                continue;
                            dominated = true;
                            break;
                        }
                        if (!dominated)
                            pareto.Add(candidate);
                    }
                    paretoByCadence.Add(pareto);
                }
                List<IGrouping<StateFingerprint, SearchNode>> selectedTacticalGroups = [];
                int paretoRound = 0;
                while (paretoByCadence.Any(bucket => paretoRound < bucket.Count))
                {
                    foreach (IReadOnlyList<IGrouping<StateFingerprint, SearchNode>> bucket in paretoByCadence)
                    {
                        if (paretoRound < bucket.Count)
                            AddTacticalGroup(selectedTacticalGroups, bucket[paretoRound]);
                    }
                    paretoRound++;
                }
                orderedPileCohorts = selectedTacticalGroups
                    .Select(group => new OrderedPileCohort(group
                        .GroupBy(node => node.Snapshot.ProjectedShuffleOrderKey)
                        .SelectMany(prefixGroup => prefixGroup
                            .OrderByDescending(node => node.Snapshot.ProjectedShuffleOrderValue)
                            .ThenByDescending(BeamRankScore)
                            .Take(SolverWeights.ExactStatesPerProjectedShuffleOrder))
                        .OrderByDescending(node => node.Snapshot.ProjectedShuffleOrderValue)
                        .ThenByDescending(BeamRankScore)
                        .Take(SolverWeights.OrderedPileVariantsPerTacticalState)
                        .ToList()))
                    .ToList();
                int orderedPileRepresentativeCount = orderedPileCohorts.Sum(cohort => cohort.PrefixVariants.Count);
                effectiveLimit = Math.Max(
                    limit,
                    Math.Min(
                        checked(limit + Math.Min(routingChoiceQuota, routingChoices.Count) + 1),
                        limit + orderedPileRepresentativeCount));
            }

            SearchNode? bestPotionFree = null;
            SearchNode? bestPotion = null;
            SearchNode? bestPotionFreeDefensive = null;
            SearchNode? bestPotionDefensive = null;
            SearchNode? bestDefensive = null;
            SearchNode? bestUtilityDefensive = null;
            SearchNode? bestPotionFreeUtilityDefensive = null;
            SearchNode? bestOffensive = null;
            SearchNode? bestPotionFreeOffensive = null;
            SearchNode? bestPotionOffensive = null;
            SearchNode? bestResourcePreserving = null;
            foreach (SearchNode node in ranked)
            {
                bool potion = UsesPotion(node);
                if (potion)
                {
                    bestPotion ??= node;
                    if (IsBetterDefensive(node, bestPotionDefensive))
                        bestPotionDefensive = node;
                    if (IsBetterOffensive(node, bestPotionOffensive))
                        bestPotionOffensive = node;
                }
                else
                {
                    bestPotionFree ??= node;
                    if (IsBetterDefensive(node, bestPotionFreeDefensive))
                        bestPotionFreeDefensive = node;
                    if (node.Traits != SearchRouteTraits.None
                        && IsBetterUtilityDefensive(node, bestPotionFreeUtilityDefensive))
                    {
                        bestPotionFreeUtilityDefensive = node;
                    }
                    if (IsBetterOffensive(node, bestPotionFreeOffensive))
                        bestPotionFreeOffensive = node;
                }
                if (!preserveDefensiveRoute)
                    continue;
                if (IsBetterDefensive(node, bestDefensive))
                    bestDefensive = node;
                if (node.Traits != SearchRouteTraits.None && IsBetterUtilityDefensive(node, bestUtilityDefensive))
                    bestUtilityDefensive = node;
                if (IsBetterOffensive(node, bestOffensive))
                    bestOffensive = node;
                if (_theftPolicy == SolverTheftPolicy.PreserveResources
                    && IsBetterResourcePreserving(node, bestResourcePreserving))
                {
                    bestResourcePreserving = node;
                }
            }

            List<SearchNode> required = [];
            if (_routePolicy == SearchRoutePolicy.MultiplayerLocalCrossTurn)
            {
                foreach (SearchNode teamCandidate in BuildTeamSafetyPortfolio(ranked))
                    AddRequired(required, teamCandidate, limit);
            }
            foreach (IGrouping<int, SearchNode> victoryGroup in ranked
                         .Where(IsCompleteVictory)
                         .GroupBy(node => node.PotionCount)
                         .OrderBy(group => group.Key))
            {
                AddRequired(required, victoryGroup.Aggregate(
                    (SearchNode?)null,
                    (best, node) => IsBetterCompletedVictory(node, best) ? node : best), limit);
            }
            if (preserveDefensiveRoute)
            {
                foreach (IGrouping<int, SearchNode> potionGroup in ranked
                             .GroupBy(node => node.PotionCount)
                             .OrderBy(group => group.Key))
                {
                    IReadOnlyList<SearchNode> group = potionGroup.ToList();
                    AddRequired(required, FindBestFreshResourceStandPat(group), limit);
                    AddRequired(required, FindBestStandPat(group, SearchRouteTraits.Scaling), limit);
                    AddRequired(required, FindBestStandPat(group, SearchRouteTraits.Resource), limit);
                    AddRequired(required, FindBestStandPat(group, SearchRouteTraits.Control), limit);
                }

                int rootLineageLimit = Math.Clamp(limit / 8, 4, 16);
                foreach (IGrouping<RootActionLineageSignature, SearchNode> lineage in ranked
                             .Where(node => node.Action != null)
                             .GroupBy(BuildRootActionLineageSignature)
                             .OrderBy(group => RootActionLineageNode(group.First()).RetentionRank)
                             .ThenByDescending(group => group.Max(BeamRankScore))
                             .Take(rootLineageLimit))
                {
                    IReadOnlyList<SearchNode> candidates = lineage.ToList();
                    AddRequired(required, candidates.MaxBy(BeamRankScore), limit);
                    AddRequired(required, candidates.Aggregate(
                        (SearchNode?)null,
                        (best, node) => IsBetterDefensive(node, best) ? node : best), limit);
                    AddRequired(required, candidates.Aggregate(
                        (SearchNode?)null,
                        (best, node) => IsBetterOffensive(node, best) ? node : best), limit);
                    AddRequired(required, FindBestSetup(candidates), limit);
                    if (_preserveReplayAllocatorOpening)
                    {
                        AddRequired(required, FindBestCuratedTurnBoundaryHand(candidates), limit);
                        AddRequired(required, FindBestTacticalEnabler(candidates), limit);
                        AddRequired(required, FindBestTargetPressure(candidates), limit);
                        AddRequired(required, FindBestDeckCuration(candidates), limit);
                        AddRequired(required, candidates
                            .OrderByDescending(node => node.Snapshot.ProjectedShuffleOrderValue)
                            .ThenByDescending(BeamRankScore)
                            .First(), limit);
                    }
                }
            }
            bool endTurnFrontier = ranked.All(node =>
                node.Action is { } action
                && (action.Kind == PlanActionKind.EndTurn || action.EndsPlayerTurn));
            if (endTurnFrontier && preserveDefensiveRoute)
            {
                foreach (IGrouping<int, SearchNode> potionGroup in ranked
                             .GroupBy(node => node.PotionCount)
                             .OrderBy(group => group.Key))
                {
                    AddRequired(required, FindBestTurnBoundaryHand(potionGroup), effectiveLimit);
                }
            }
            int orderedPileQuota = Math.Min(effectiveLimit, orderedPileCohorts.Count == 0
                ? 0
                : endTurnFrontier
                    || ranked.Any(node => node.Traits.HasFlag(SearchRouteTraits.EndTurnDeckCompression))
                    ? Math.Max(8, limit * 2 / 3)
                    : limit + 1);
            int orderedPileRounds = orderedPileCohorts.Count == 0
                ? 0
                : orderedPileCohorts.Max(cohort => cohort.PrefixVariants.Count);
            if (endTurnFrontier && orderedPileQuota > 0)
            {
                int strategicExactQuota = Math.Min(16, orderedPileQuota / 2);
                foreach (var cadence in ranked
                             .Where(node => node.PotionCount > 0
                                 && node.Traits.HasFlag(SearchRouteTraits.EndTurnDeckCompression))
                             .GroupBy(node => (
                                 Cadence: BuildPocketwatchCadenceSignature(node),
                                 node.Snapshot.RetainedAttackValue))
                             .OrderByDescending(group => group.Max(node => node.Snapshot.FocusTargetPressure))
                             .ThenBy(group => group.Min(node => node.Snapshot.FocusTargetRemainingHp))
                             .ThenByDescending(group => group.Max(node => node.Snapshot.ProjectedShuffleOrderValue))
                             .Take(Math.Max(1, strategicExactQuota /
                                 SolverWeights.PotionEndTurnExactStatesPerProjectedShuffleOrder)))
                {
                    SearchNode? representative = FindMostCompressedDeck(cadence.ToList());
                    if (representative == null)
                        continue;
                    StateFingerprint tacticalKey = BuildOrderedPileTacticalKey(representative);
                    foreach (SearchNode exactState in cadence
                                 .Where(node => BuildOrderedPileTacticalKey(node) == tacticalKey
                                     && node.Snapshot.ProjectedShuffleOrderKey ==
                                        representative.Snapshot.ProjectedShuffleOrderKey)
                                 .OrderByDescending(BeamRankScore)
                                 .Take(SolverWeights.PotionEndTurnExactStatesPerProjectedShuffleOrder))
                    {
                        AddRequired(required, exactState, strategicExactQuota);
                    }
                }
            }
            int exactStateRounds = Math.Min(
                SolverWeights.ExactStatesPerProjectedShuffleOrder,
                orderedPileRounds);
            for (int round = 0; round < exactStateRounds && required.Count < orderedPileQuota; round++)
            {
                foreach (OrderedPileCohort cohort in orderedPileCohorts)
                {
                    if (round < cohort.PrefixVariants.Count)
                        AddRequired(required, cohort.PrefixVariants[round], orderedPileQuota);
                }
            }
            for (int round = exactStateRounds;
                 round < orderedPileRounds && required.Count < orderedPileQuota;
                 round++)
            {
                foreach (OrderedPileCohort cohort in orderedPileCohorts)
                {
                    if (round < cohort.PrefixVariants.Count)
                        AddRequired(required, cohort.PrefixVariants[round], orderedPileQuota);
                }
            }
            if (ranked.Any(node => node.Traits.HasFlag(SearchRouteTraits.EndTurnDeckCompression)))
            {
                List<IGrouping<StateFingerprint, SearchNode>> compressionLineages = ranked
                             .Where(node => node.Traits.HasFlag(SearchRouteTraits.EndTurnDeckCompression))
                             .GroupBy(EndTurnDeckCompressionLineageKey)
                             .OrderBy(group => group.Min(node =>
                                 EndTurnDeckCompressionLineageRoot(node).RetentionRank))
                             .ThenByDescending(group => group.Max(BeamRankScore))
                             .ToList();
                foreach (IGrouping<StateFingerprint, SearchNode> compressionLineage in compressionLineages.Take(12))
                {
                    IReadOnlyList<SearchNode> lineageCandidates = compressionLineage.ToList();
                    AddRequired(
                        required,
                        PreferMostVulnerableTargetVariant(
                            lineageCandidates,
                            FindBestLane(
                                lineageCandidates,
                                SearchRouteTraits.EndTurnDeckCompression)),
                        effectiveLimit);
                    AddRequired(
                        required,
                        PreferMostVulnerableTargetVariant(
                            lineageCandidates,
                            FindBestCompressionAttackGrowth(lineageCandidates)),
                        effectiveLimit);
                    AddRequired(
                        required,
                        FindBestLane(lineageCandidates, SearchRouteTraits.Resource),
                        effectiveLimit);
                    AddRequired(required, FindBestDeckCuration(lineageCandidates), effectiveLimit);
                    AddRequired(
                        required,
                        PreferMostVulnerableTargetVariant(
                            lineageCandidates,
                            FindBestTargetPressure(lineageCandidates)),
                        effectiveLimit);
                    AddRequired(
                        required,
                        lineageCandidates.Aggregate(
                            (SearchNode?)null,
                            (best, node) => IsBetterOffensive(node, best) ? node : best),
                        effectiveLimit);
                }
                foreach (IGrouping<int, SearchNode> potionCountGroup in ranked
                             .GroupBy(node => node.PotionCount)
                             .OrderBy(group => group.Key))
                {
                    foreach (var lineage in potionCountGroup
                                 .Where(node => node.Traits.HasFlag(SearchRouteTraits.EndTurnDeckCompression))
                                 .GroupBy(node => (
                                     Lineage: EndTurnDeckCompressionLineageKey(node),
                                     Parent: node.Parent?.StateKey ?? default))
                                 .OrderBy(group => group.Min(node =>
                                     node.Parent?.RetentionRank ?? node.RetentionRank))
                                 .ThenByDescending(group => group.Max(BeamRankScore))
                                 .Take(12))
                    {
                        IReadOnlyList<SearchNode> group = lineage.ToList();
                        SearchNode? compressionLeader = PreferMostVulnerableTargetVariant(
                            group,
                            FindBestLane(group, SearchRouteTraits.EndTurnDeckCompression));
                        AddRequired(required, compressionLeader, effectiveLimit);
                        foreach (IGrouping<(PlanActionKind Kind, string CardId, string PotionId), SearchNode>
                                     actionGroup in group
                                 .Where(node => node.Action != null)
                                 .GroupBy(node => (
                                     node.Action!.Kind,
                                     node.Action.CardId,
                                     node.Action.PotionId))
                                 .OrderByDescending(candidates => candidates.Max(node =>
                                     LaneValue(node.Snapshot, SearchRouteTraits.EndTurnDeckCompression)))
                                 .ThenByDescending(candidates => candidates.Max(BeamRankScore))
                                 .Take(8))
                        {
                            IReadOnlyList<SearchNode> actionCandidates = actionGroup.ToList();
                            AddRequired(
                                required,
                                PreferMostVulnerableTargetVariant(
                                    actionCandidates,
                                    FindBestLane(
                                        actionCandidates,
                                        SearchRouteTraits.EndTurnDeckCompression)),
                                effectiveLimit);
                        }
                    }
                }
            }
            foreach (SearchNode routingChoice in routingChoices.Take(routingChoiceQuota))
            {
                AddRequired(required, routingChoice, effectiveLimit);
            }
            if (preserveDefensiveRoute)
            {
                foreach (IGrouping<int, SearchNode> potionGroup in ranked
                             .GroupBy(node => node.PotionCount)
                             .OrderBy(group => group.Key))
                {
                    IReadOnlyList<SearchNode> artOfWarCandidates = potionGroup
                        .Where(node => node.Snapshot.CanTriggerArtOfWarNextTurn)
                        .ToList();
                    AddRequired(required, artOfWarCandidates.Aggregate(
                        (SearchNode?)null,
                        (best, node) => IsBetterDefensive(node, best) ? node : best), effectiveLimit);
                    AddRequired(required, FindBestSetup(artOfWarCandidates), effectiveLimit);
                }
            }
            if (preserveDefensiveRoute)
            {
                int signatureLimitPerPotionGroup = Math.Max(4, limit / 6);
                foreach (IGrouping<int, SearchNode> potionGroup in ranked
                             .GroupBy(node => node.PotionCount)
                             .OrderBy(group => group.Key))
                {
                    foreach (IGrouping<PersistentSetupTraits, SearchNode> setupGroup in potionGroup
                                 .Where(node => node.Snapshot.StrategicSetupTraits != PersistentSetupTraits.None)
                                 .GroupBy(node => node.Snapshot.StrategicSetupTraits)
                                 .OrderByDescending(group => group.Max(BeamRankScore))
                                 .Take(signatureLimitPerPotionGroup))
                    {
                        IReadOnlyList<SearchNode> candidates = setupGroup.ToList();
                        AddRequired(required, candidates.Aggregate(
                            (SearchNode?)null,
                            (best, node) => IsBetterDefensive(node, best) ? node : best), limit);
                        AddRequired(required, candidates.Aggregate(
                            (SearchNode?)null,
                            (best, node) => IsBetterSetup(node, best) ? node : best), limit);
                    }
                }

                int focusTargetsPerPotionGroup = Math.Clamp(limit / 10, 2, 4);
                foreach (IGrouping<int, SearchNode> potionGroup in ranked
                             .GroupBy(node => node.PotionCount)
                             .OrderBy(group => group.Key))
                {
                    foreach (IGrouping<uint?, SearchNode> targetGroup in potionGroup
                                 .Where(node => node.Snapshot.FocusTargetCombatId != null)
                                 .GroupBy(node => node.Snapshot.FocusTargetCombatId)
                                 .OrderByDescending(group => group.Max(node => node.Snapshot.FocusTargetPressure))
                                 .Take(focusTargetsPerPotionGroup))
                    {
                        IReadOnlyList<SearchNode> candidates = targetGroup.ToList();
                        AddRequired(required, FindBestTargetPressure(candidates), limit);
                        AddRequired(required, FindBestTargetSetup(candidates), limit);
                    }
                }
            }
            IReadOnlyList<SearchNode> declinedExtraTurn = ranked
                .Where(node => node.Traits.HasFlag(SearchRouteTraits.DeclinedExtraTurn))
                .ToList();
            if (declinedExtraTurn.Count > 0)
            {
                AddRequired(required, declinedExtraTurn[0], limit);
                AddRequired(required, declinedExtraTurn.Aggregate(
                    (SearchNode?)null,
                    (best, node) => IsBetterDefensive(node, best) ? node : best), limit);
                AddRequired(required, declinedExtraTurn.Aggregate(
                    (SearchNode?)null,
                    (best, node) => IsBetterOffensive(node, best) ? node : best), limit);
                AddRequired(required, FindBestSetup(declinedExtraTurn), limit);
            }
            if (_potionPolicy != SolverPotionPolicy.Disabled)
            {
                int potionLineageLimit = Math.Clamp(limit / 6, 2, 6);
                foreach (IGrouping<int, SearchNode> potionCountGroup in ranked
                             .Where(UsesPotion)
                             .GroupBy(node => node.PotionCount)
                             .OrderBy(group => group.Key))
                {
                    foreach (IGrouping<string, SearchNode> potionLineage in potionCountGroup
                                 .GroupBy(PotionUseLineageKey, StringComparer.Ordinal)
                                 .OrderByDescending(group => group.Max(BeamRankScore))
                                 .Take(potionLineageLimit))
                    {
                        AddRequired(
                            required,
                            FindBestPotionLineage(potionLineage),
                            limit);
                    }
                }
            }
            foreach (IGrouping<int, SearchNode> potionCountGroup in ranked
                         .GroupBy(node => node.PotionCount)
                         .OrderBy(group => group.Key))
            {
                IReadOnlyList<SearchNode> group = potionCountGroup.ToList();
                AddRequired(required, group[0], limit);
                AddRequired(required, group.Aggregate(
                    (SearchNode?)null,
                    (best, node) => IsBetterDefensive(node, best) ? node : best), limit);
                AddRequired(required, group.Aggregate(
                    (SearchNode?)null,
                    (best, node) => IsBetterOffensive(node, best) ? node : best), limit);
                AddRequired(required, FindBestEnemyStrengthControl(group), limit);
                AddRequired(required, FindBestEnemyWeakControl(group), limit);
                AddRequired(required, FindBestDeckCuration(group), limit);
                AddRequired(required, FindMostCompressedDeck(group), limit);
                AddRequired(required, FindBestTacticalEnabler(group), limit);
                AddRequired(required, FindBestSetup(group), limit);
                if (_theftPolicy == SolverTheftPolicy.PreserveResources)
                {
                    AddRequired(required, group.Aggregate(
                        (SearchNode?)null,
                        (best, node) => IsBetterResourcePreserving(node, best) ? node : best), limit);
                }
            }
            AddRequired(required, bestPotionFree, limit);
            AddRequired(required, bestPotionFreeDefensive, limit);
            AddRequired(required, bestPotionFreeOffensive, limit);
            AddRequired(required, FindBestSetup(ranked.Where(node => !UsesPotion(node))), limit);
            AddRequired(required, bestPotion, limit);
            AddRequired(required, bestPotionDefensive, limit);
            AddRequired(required, bestPotionOffensive, limit);
            AddRequired(required, FindBestSetup(ranked.Where(UsesPotion)), limit);
            AddRequired(required, bestDefensive, limit);
            AddRequired(required, bestUtilityDefensive, limit);
            AddRequired(required, bestPotionFreeUtilityDefensive, limit);
            AddRequired(required, bestOffensive, limit);
            AddRequired(required, bestResourcePreserving, limit);
            AddRequired(required, FindBestLane(ranked, SearchRouteTraits.LongTermResource), limit);
            AddRequired(required, FindBestLane(ranked, SearchRouteTraits.HpInvestment), limit);
            if (preserveDefensiveRoute
                && limit >= 18)
            {
                foreach (SearchRouteTraits trait in new[]
                         {
                             SearchRouteTraits.Scaling,
                             SearchRouteTraits.Resource,
                             SearchRouteTraits.Control,
                             SearchRouteTraits.RevivalWindow,
                             SearchRouteTraits.ReactiveDamage,
                             SearchRouteTraits.EndTurnDeckCompression,
                             SearchRouteTraits.LongTermResource,
                             SearchRouteTraits.HpInvestment,
                         })
                {
                    foreach (IGrouping<int, SearchNode> potionCountGroup in ranked
                                 .GroupBy(node => node.PotionCount)
                                 .OrderBy(group => group.Key))
                    {
                        AddRequired(required, FindBestLane(potionCountGroup.ToList(), trait), limit);
                    }
                }
                // MultiObjectiveDominates intentionally cannot compare nodes from different
                // combat/control/pile cohorts. Looking at the whole ranked pool therefore did
                // O(n^2) fingerprint checks at large turn boundaries (tens of thousands of
                // ended candidates) even though nearly every pair was incomparable.
                Dictionary<(
                    StateFingerprint EnemyCombat,
                    StateFingerprint EnemyControl,
                    StateFingerprint UnorderedPile), List<SearchNode>> paretoCohorts = [];
                foreach (SearchNode node in ranked)
                {
                    var cohortKey = (
                        node.Snapshot.EnemyCombatDistributionKey,
                        node.Snapshot.EnemyControlDistributionKey,
                        node.Snapshot.UnorderedPileKey);
                    if (!paretoCohorts.TryGetValue(cohortKey, out List<SearchNode>? cohort))
                    {
                        cohort = [];
                        paretoCohorts.Add(cohortKey, cohort);
                    }
                    cohort.Add(node);
                }

                List<SearchNode> pareto = new(3);
                foreach (SearchNode candidate in ranked)
                {
                    bool dominated = false;
                    var cohortKey = (
                        candidate.Snapshot.EnemyCombatDistributionKey,
                        candidate.Snapshot.EnemyControlDistributionKey,
                        candidate.Snapshot.UnorderedPileKey);
                    foreach (SearchNode other in paretoCohorts[cohortKey])
                    {
                        if (!MultiObjectiveDominates(other, candidate))
                            continue;
                        dominated = true;
                        break;
                    }
                    if (dominated)
                        continue;
                    int insertIndex = 0;
                    while (insertIndex < pareto.Count
                           && (pareto[insertIndex].Score > candidate.Score
                               || pareto[insertIndex].Score.Equals(candidate.Score)
                                   && pareto[insertIndex].ActionCount <= candidate.ActionCount))
                    {
                        insertIndex++;
                    }
                    if (insertIndex >= 3)
                        continue;
                    pareto.Insert(insertIndex, candidate);
                    if (pareto.Count > 3)
                        pareto.RemoveAt(3);
                }
                foreach (SearchNode candidate in pareto)
                    AddRequired(required, candidate, limit);
            }

            List<SearchNode> quotaPool = ranked.ToList();
            // 次段成员（见 SolverSearchProfile.SecondRankBand）：只由全局剪枝入口显式启用，
            // 把分数序前 effectiveLimit 位挪到队尾再截断，于是普通席位落在第 W+1 至 2W 位；挪走的
            // 一段只在后面候选不够时回填。quotaPool 仍是纯分数序，必保置换、边界多样化和药水配额照旧。
            if (_profile.SecondRankBand && useSecondRankBand)
                BeamWidthPortfolio.MoveLeadingBandToTail(ranked, effectiveLimit);
            if (ranked.Count > effectiveLimit)
                ranked.RemoveRange(effectiveLimit, ranked.Count - effectiveLimit);
            foreach (SearchNode requiredNode in required)
            {
                if (ContainsReference(ranked, requiredNode))
                    continue;
                int replaceIndex = -1;
                for (int index = ranked.Count - 1; index >= 0; index--)
                {
                    if (ContainsReference(required, ranked[index]))
                        continue;
                    replaceIndex = index;
                    break;
                }
                if (replaceIndex < 0)
                    throw new InvalidOperationException("Beam 容量不足以保留策略必需分支。");
                ranked[replaceIndex] = requiredNode;
            }
            DiversifyOrdinaryBeamBoundary(
                quotaPool,
                ranked,
                required,
                node => (
                    BeamRankScore(node),
                    node.ActionCount,
                    node.Snapshot.OffensiveProgressValue,
                    node.PotionCount,
                    IsCompleteVictory(node)),
                finalQualityFirst,
                node => new OrdinaryBeamTacticalValues(
                    node.Turn,
                    node.PotionCount,
                    node.PotionStrategicCost,
                    node.FutureSoldHp,
                    node.Snapshot.CumulativePlayerHpLost,
                    node.ActionCount,
                    node.Score,
                    node.Snapshot.ZeroCostPlayableCount,
                    node.Snapshot.ReachableHandValue,
                    node.Snapshot.HandCount,
                    HasRetainedRoutingChoice: RetainedRoutingChoice(node) != null));
            if (_potionPolicy != SolverPotionPolicy.Disabled
                && quotaPool.Any(UsesPotion)
                && quotaPool.Any(node => !UsesPotion(node)))
            {
                (int usedPotionQuota, int unusedPotionQuota) =
                    FeasiblePotionUseQuotas(limit);
                HashSet<SearchNode> quotaReservations = new(
                    required,
                    ReferenceEqualityComparer.Instance);
                ReservePotionQuotaLeaders(
                    quotaReservations,
                    quotaPool,
                    usesPotion: true,
                    usedPotionQuota);
                ReservePotionQuotaLeaders(
                    quotaReservations,
                    quotaPool,
                    usesPotion: false,
                    unusedPotionQuota);
                EnforcePotionUseQuota(
                    ranked,
                    quotaPool,
                    quotaReservations,
                    usesPotion: true,
                    usedPotionQuota);
                EnforcePotionUseQuota(
                    ranked,
                    quotaPool,
                    quotaReservations,
                    usesPotion: false,
                    unusedPotionQuota);
            }
            if (finalQualityFirst)
                ranked.Sort(FinalCandidateComparison);
            else
                SortByBeamRank(ranked);
            observe?.Invoke(new GlobalRetentionDecision(
                quotaPool, required, routingChoices, ranked, limit, effectiveLimit,
                routingChoiceQuota, RoutingChoiceLimit,
                observedRoutingSignatures, observedOptionLeaders, BeamRankScore));
            AssignRetentionRanks(ranked, required);
            return ranked;
        }



        private IEnumerable<SearchNode> BuildTeamSafetyPortfolio(
            IReadOnlyList<SearchNode> nodes)
        {
            if (nodes.Count == 0)
                yield break;

            IReadOnlyList<SearchNode> pool = nodes.Any(node => node.Snapshot.AllPlayersAlive)
                ? nodes.Where(node => node.Snapshot.AllPlayersAlive).ToArray()
                : nodes;

            SearchNode bestTeamLoss = pool
                .OrderBy(node => node.Snapshot.TeamLossRatio)
                .ThenBy(node => node.Snapshot.WorstPlayerLossRatio)
                .ThenBy(node => node.Snapshot.AliveEnemyCount)
                .ThenBy(node => node.Snapshot.EnemyHp)
                .ThenByDescending(BeamRankScore)
                .First();
            yield return bestTeamLoss;

            SearchNode bestWorstPlayer = pool
                .OrderBy(node => node.Snapshot.WorstPlayerLossRatio)
                .ThenBy(node => node.Snapshot.TeamLossRatio)
                .ThenBy(node => node.Snapshot.AliveEnemyCount)
                .ThenBy(node => node.Snapshot.EnemyHp)
                .ThenByDescending(BeamRankScore)
                .First();
            if (!ReferenceEquals(bestWorstPlayer, bestTeamLoss))
                yield return bestWorstPlayer;

            SearchNode bestEnemyProgress = pool
                .OrderBy(node => node.Snapshot.AliveEnemyCount)
                .ThenBy(node => node.Snapshot.EnemyHp)
                .ThenBy(node => node.Snapshot.TeamLossRatio)
                .ThenBy(node => node.Snapshot.WorstPlayerLossRatio)
                .ThenByDescending(BeamRankScore)
                .First();
            if (!ReferenceEquals(bestEnemyProgress, bestTeamLoss)
                && !ReferenceEquals(bestEnemyProgress, bestWorstPlayer))
            {
                yield return bestEnemyProgress;
            }
        }

        private static bool IsBetterDefensive(SearchNode candidate, SearchNode? current)
            => current == null
                || candidate.Snapshot.ProjectedPlayerHp > current.Snapshot.ProjectedPlayerHp
                || candidate.Snapshot.ProjectedPlayerHp == current.Snapshot.ProjectedPlayerHp
                    && (candidate.Snapshot.OstyHp > current.Snapshot.OstyHp
                        || candidate.Snapshot.OstyHp == current.Snapshot.OstyHp
                            && (candidate.Snapshot.OstyMaxHp > current.Snapshot.OstyMaxHp
                                || candidate.Snapshot.OstyMaxHp == current.Snapshot.OstyMaxHp
                                    && (candidate.Snapshot.PlayerBlock > current.Snapshot.PlayerBlock
                                        || candidate.Snapshot.PlayerBlock == current.Snapshot.PlayerBlock
                                            && candidate.Score > current.Score)));

        private bool IsBetterCompletedVictory(SearchNode candidate, SearchNode? current)
            => current == null || CompareFinalCandidates(candidate, current) < 0;

        private int CompareFinalCandidates(SearchNode left, SearchNode right)
        {
            SimulationSnapshot leftSnapshot = left.Snapshot;
            SimulationSnapshot rightSnapshot = right.Snapshot;
            bool leftWon = IsCompleteVictory(left);
            bool rightWon = IsCompleteVictory(right);
            int comparison = CompareMultiplayerObjective(left, right);
            if (comparison != 0)
                return comparison;
            comparison = rightWon.CompareTo(leftWon);
            if (comparison != 0)
                return comparison;
            if (!leftWon && !rightWon)
            {
                bool leftSurvives = !leftSnapshot.PlayerDead
                    && leftSnapshot.ProjectedPlayerHp > 0;
                bool rightSurvives = !rightSnapshot.PlayerDead
                    && rightSnapshot.ProjectedPlayerHp > 0;
                int survivalComparison = rightSurvives.CompareTo(leftSurvives);
                if (survivalComparison != 0)
                    return survivalComparison;
            }

            comparison = leftSnapshot.ProjectedDeathSaveUseCount.CompareTo(
                rightSnapshot.ProjectedDeathSaveUseCount);
            if (comparison != 0)
                return comparison;

            int recoveryComparison = TheftEncounterStrategy.CompareRecovery(_theftPolicy,
                leftWon, leftSnapshot.OutstandingStolenResource, rightWon, rightSnapshot.OutstandingStolenResource);
            if (recoveryComparison != 0)
                return recoveryComparison;
            comparison = SolverInterimResultOrdering.ComparePrimaryQuality(
                leftWon,
                StrategicHpDeficit(leftSnapshot, leftWon),
                leftWon ? CompletedCombatTurn(left) : null,
                rightWon,
                StrategicHpDeficit(rightSnapshot, rightWon),
                rightWon ? CompletedCombatTurn(right) : null,
                leftSnapshot.StrategyGoalHpCredit,
                rightSnapshot.StrategyGoalHpCredit,
                leftSnapshot.StrategyGoalCount,
                rightSnapshot.StrategyGoalCount,
                leftSnapshot.ProjectedDeathSaveUseCount,
                rightSnapshot.ProjectedDeathSaveUseCount);
            if (comparison != 0)
                return comparison;

            int leftOutstanding = _theftPolicy == SolverTheftPolicy.PreserveResources
                ? leftSnapshot.OutstandingStolenResource
                : 0;
            int rightOutstanding = _theftPolicy == SolverTheftPolicy.PreserveResources
                ? rightSnapshot.OutstandingStolenResource
                : 0;
            comparison = leftOutstanding.CompareTo(rightOutstanding);
            if (comparison != 0)
                return comparison;
            comparison = HealthResourceCost(leftSnapshot).CompareTo(HealthResourceCost(rightSnapshot));
            if (comparison != 0)
                return comparison;
            comparison = rightSnapshot.LongTermResourceValue.CompareTo(leftSnapshot.LongTermResourceValue);
            if (comparison != 0)
                return comparison;
            comparison = leftSnapshot.AngerCopiesGenerated.CompareTo(rightSnapshot.AngerCopiesGenerated);
            if (comparison != 0)
                return comparison;
            comparison = PolicyBoundaryRank(leftSnapshot.BoundaryReason)
                .CompareTo(PolicyBoundaryRank(rightSnapshot.BoundaryReason));
            if (comparison != 0)
                return comparison;
            comparison = ExplicitPotionUseCount(left).CompareTo(ExplicitPotionUseCount(right));
            if (comparison != 0)
                return comparison;
            comparison = left.FutureSoldHp.CompareTo(right.FutureSoldHp);
            if (comparison != 0)
                return comparison;
            comparison = leftSnapshot.EnemyHp.CompareTo(rightSnapshot.EnemyHp);
            if (comparison != 0)
                return comparison;
            comparison = right.Score.CompareTo(left.Score);
            if (comparison != 0)
                return comparison;
            comparison = left.ActionCount.CompareTo(right.ActionCount);
            if (comparison != 0)
                return comparison;
            comparison = left.StateKey.First.CompareTo(right.StateKey.First);
            return comparison != 0
                ? comparison
                : left.StateKey.Second.CompareTo(right.StateKey.Second);
        }

        private bool IsCompleteVictory(SearchNode node)
            => SolverInterimResultOrdering.IsCompleteVictory(
                node.ActionCount,
                node.Snapshot.AllEnemiesDead,
                node.Snapshot.PlayerDead,
                node.Snapshot.ProjectedPlayerHp);

        /// <summary>
        /// Route quality on the same axis the final ordering uses, so retention keeps the candidate that
        /// ordering would go on to pick.
        /// </summary>
        private int StrategicHpDeficit(SimulationSnapshot snapshot, bool completeVictory)
            => ActEndingBossPolicy.StrategicHpDeficit(
                snapshot.CumulativePlayerHpLost,
                Math.Max(0, _initialPlayerMaxHp - snapshot.PlayerMaxHp),
                snapshot.RecoveredPlayerHp
                    + ActEndingBossPolicy.RankedPostCombatRelicHeal(
                        _postCombatRelicHeal,
                        completeVictory,
                        snapshot.PlayerHp,
                        snapshot.PlayerMaxHp),
                _bossHpRelief,
                snapshot.DeathSaveHpRestored) - snapshot.StrategicHpCredit;

        private int HealthResourceCost(SimulationSnapshot snapshot)
            => _initialPlayerHp - snapshot.PlayerHp
                + _initialPlayerMaxHp - snapshot.PlayerMaxHp;

        private static int CompletedCombatTurn(SearchNode node)
            => node.Action?.Turn ?? node.Turn;

        private static bool IsBetterUtilityDefensive(SearchNode candidate, SearchNode? current)
            => current == null
                || candidate.Snapshot.ProjectedPlayerHp > current.Snapshot.ProjectedPlayerHp
                || candidate.Snapshot.ProjectedPlayerHp == current.Snapshot.ProjectedPlayerHp
                    && candidate.Score > current.Score;

        private static bool IsBetterOffensive(SearchNode candidate, SearchNode? current)
            => current == null
                || candidate.Snapshot.AliveEnemyCount < current.Snapshot.AliveEnemyCount
                || candidate.Snapshot.AliveEnemyCount == current.Snapshot.AliveEnemyCount
                    && (candidate.Snapshot.RawEnemyHp < current.Snapshot.RawEnemyHp
                        || candidate.Snapshot.RawEnemyHp == current.Snapshot.RawEnemyHp
                            && (candidate.Snapshot.EnemyHp < current.Snapshot.EnemyHp
                        || candidate.Snapshot.EnemyHp == current.Snapshot.EnemyHp
                            && (candidate.Snapshot.ProjectedPlayerHp > current.Snapshot.ProjectedPlayerHp
                                || candidate.Snapshot.ProjectedPlayerHp == current.Snapshot.ProjectedPlayerHp
                                    && candidate.Score > current.Score)));

        private static bool IsBetterResourcePreserving(SearchNode candidate, SearchNode? current)
            => current == null
                || candidate.Snapshot.OutstandingStolenResource < current.Snapshot.OutstandingStolenResource
                || candidate.Snapshot.OutstandingStolenResource == current.Snapshot.OutstandingStolenResource
                    && (candidate.Snapshot.ProjectedPlayerHp > current.Snapshot.ProjectedPlayerHp
                        || candidate.Snapshot.ProjectedPlayerHp == current.Snapshot.ProjectedPlayerHp
                            && candidate.Score > current.Score);

        private static SearchNode? FindBestEnemyStrengthControl(IEnumerable<SearchNode> nodes)
            => nodes.Aggregate(
                (SearchNode?)null,
                (best, node) => best == null
                    || node.Snapshot.EnemyStrengthSuppression > best.Snapshot.EnemyStrengthSuppression
                    || node.Snapshot.EnemyStrengthSuppression == best.Snapshot.EnemyStrengthSuppression
                        && (node.Snapshot.EnemyWeakTurns > best.Snapshot.EnemyWeakTurns
                            || node.Snapshot.EnemyWeakTurns == best.Snapshot.EnemyWeakTurns
                                && IsBetterDefensive(node, best))
                        ? node
                        : best);

        private static SearchNode? FindBestEnemyWeakControl(IEnumerable<SearchNode> nodes)
            => nodes.Aggregate(
                (SearchNode?)null,
                (best, node) => best == null
                    || node.Snapshot.EnemyWeakTurns > best.Snapshot.EnemyWeakTurns
                    || node.Snapshot.EnemyWeakTurns == best.Snapshot.EnemyWeakTurns
                        && (node.Snapshot.EnemyStrengthSuppression > best.Snapshot.EnemyStrengthSuppression
                            || node.Snapshot.EnemyStrengthSuppression == best.Snapshot.EnemyStrengthSuppression
                                && IsBetterDefensive(node, best))
                        ? node
                        : best);

        private static bool IsBetterSetup(SearchNode candidate, SearchNode? current)
        {
            if (current == null)
                return true;
            int candidateValue = SetupLaneValue(candidate.Snapshot);
            int currentValue = SetupLaneValue(current.Snapshot);
            return candidateValue > currentValue
                || candidateValue == currentValue
                    && (candidate.Snapshot.RetainedAttackValue > current.Snapshot.RetainedAttackValue
                        || candidate.Snapshot.RetainedAttackValue == current.Snapshot.RetainedAttackValue
                            && (candidate.Snapshot.ProjectedPlayerHp > current.Snapshot.ProjectedPlayerHp
                                || candidate.Snapshot.ProjectedPlayerHp == current.Snapshot.ProjectedPlayerHp
                                    && candidate.Score > current.Score));
        }

        private static SearchNode? FindBestTargetPressure(IReadOnlyList<SearchNode> nodes)
        {
            SearchNode? best = null;
            foreach (SearchNode node in nodes)
            {
                if (best == null
                    || node.Snapshot.FocusTargetPressure > best.Snapshot.FocusTargetPressure
                    || node.Snapshot.FocusTargetPressure == best.Snapshot.FocusTargetPressure
                        && (node.Snapshot.FocusTargetRemainingHp < best.Snapshot.FocusTargetRemainingHp
                            || node.Snapshot.FocusTargetRemainingHp == best.Snapshot.FocusTargetRemainingHp
                                && (node.Snapshot.FocusTargetCurrentThreat > best.Snapshot.FocusTargetCurrentThreat
                                    || node.Snapshot.FocusTargetCurrentThreat == best.Snapshot.FocusTargetCurrentThreat
                                        && (node.Snapshot.ProjectedPlayerHp > best.Snapshot.ProjectedPlayerHp
                                            || node.Snapshot.ProjectedPlayerHp == best.Snapshot.ProjectedPlayerHp
                                                && node.Score > best.Score))))
                {
                    best = node;
                }
            }
            return best;
        }

        private static SearchNode? FindBestDeckCuration(IReadOnlyList<SearchNode> nodes)
        {
            SearchNode? best = null;
            foreach (SearchNode node in nodes)
            {
                if (best == null
                    || AttackDensity(node.Snapshot) > AttackDensity(best.Snapshot)
                    || AttackDensity(node.Snapshot) == AttackDensity(best.Snapshot)
                        && (node.Snapshot.LiveDeckClutter < best.Snapshot.LiveDeckClutter
                            || node.Snapshot.LiveDeckClutter == best.Snapshot.LiveDeckClutter
                                && IsBetterSetup(node, best)))
                {
                    best = node;
                }
            }
            return best;
        }

        private static SearchNode? FindMostCompressedDeck(IReadOnlyList<SearchNode> nodes)
        {
            SearchNode? best = null;
            foreach (SearchNode node in nodes)
            {
                if (best == null
                    || node.Snapshot.LiveDeckSize < best.Snapshot.LiveDeckSize
                    || node.Snapshot.LiveDeckSize == best.Snapshot.LiveDeckSize
                        && (AttackDensity(node.Snapshot) > AttackDensity(best.Snapshot)
                            || AttackDensity(node.Snapshot) == AttackDensity(best.Snapshot)
                                && IsBetterSetup(node, best)))
                {
                    best = node;
                }
            }
            return best;
        }

        /// <summary>
        /// A multi-card choice can produce several exact states which the compressed-deck
        /// comparator genuinely cannot order. Picking the first such state makes search quality
        /// depend on parallel enumeration order. Keep a bounded, canonical and round-robin
        /// ambiguity portfolio for one layer so ordinary expansion can expose the next action's
        /// real value. It consumes only the existing routing/effective-width budget and assigns
        /// no value to a card ID.
        /// </summary>
        private List<SearchNode> BuildAmbiguousCompressedChoicePortfolio(
            IReadOnlyList<SearchNode> nodes,
            int selectionLimit)
        {
            List<(AmbiguousChoiceDecisionSignature Decision, List<SearchNode> Variants)> cohorts = [];
            foreach (IGrouping<int, SearchNode> potionGroup in nodes
                         .GroupBy(node => node.PotionCount)
                         .OrderBy(group => group.Key))
            {
                IReadOnlyList<SearchNode> group = potionGroup.ToList();
                SearchNode? winner = FindMostCompressedDeck(group);
                if (winner == null)
                    continue;

                List<(SearchNode Node, AmbiguousChoiceDecisionSignature Decision)> tied = [];
                foreach (SearchNode candidate in group)
                {
                    if (!HasEqualCompressedDeckRank(candidate, winner)
                        || !TryGetCurrentTurnRoutingChoice(
                            candidate,
                            out RoutingChoiceSignature choice,
                            out SearchNode choiceNode)
                        || !IsAmbiguousCompressedChoiceCardinality(
                            RoutingChoiceCardinality(choice))
                        || !ReferenceEquals(candidate, choiceNode)
                        || choiceNode.Parent is not { } parent)
                    {
                        continue;
                    }
                    tied.Add((
                        candidate,
                        BuildAmbiguousChoiceDecisionSignature(
                            candidate,
                            parent,
                            choice)));
                }

                foreach (IGrouping<AmbiguousChoiceDecisionSignature,
                             (SearchNode Node, AmbiguousChoiceDecisionSignature Decision)> decisionGroup in
                         tied.GroupBy(item => item.Decision))
                {
                    List<SearchNode> variants = decisionGroup
                        .GroupBy(item => item.Node.Snapshot.UnorderedPileKey)
                        .Select(outcome => outcome
                            .Select(item => item.Node)
                            .OrderBy(candidate => candidate.StateKey.First)
                            .ThenBy(candidate => candidate.StateKey.Second)
                            .First())
                        .OrderBy(candidate => candidate.StateKey.First)
                        .ThenBy(candidate => candidate.StateKey.Second)
                        .ToList();
                    if (variants.Count > 1)
                        cohorts.Add((decisionGroup.Key, variants));
                }
            }

            int limit = BoundedAmbiguousCompressedChoiceQuota(selectionLimit);
            List<(AmbiguousChoiceDecisionSignature Decision, List<SearchNode> Variants)> ordered = cohorts
                .OrderBy(cohort => cohort.Decision.PotionCount)
                .ThenBy(cohort => cohort.Decision.ParentStateKey.First)
                .ThenBy(cohort => cohort.Decision.ParentStateKey.Second)
                .ThenBy(cohort => cohort.Decision.ParentActionCount)
                .ThenBy(cohort => cohort.Decision.Turn)
                .ThenBy(cohort => cohort.Decision.SourceId, StringComparer.Ordinal)
                .ThenBy(cohort => cohort.Decision.Effect)
                .ThenBy(cohort => cohort.Decision.Pile)
                .ThenByDescending(cohort => cohort.Decision.ChoiceCount)
                .ThenBy(cohort => cohort.Decision.ContextId, StringComparer.Ordinal)
                .ToList();
            List<SearchNode> selected = new(limit);
            int round = 0;
            while (selected.Count < limit
                   && ordered.Any(cohort => round < cohort.Variants.Count))
            {
                foreach ((AmbiguousChoiceDecisionSignature _, List<SearchNode> variants) in ordered)
                {
                    if (round < variants.Count)
                        AddRoutingCandidate(selected, variants[round], limit);
                    if (selected.Count >= limit)
                        break;
                }
                round++;
            }
            return selected;
        }

        internal static int BoundedAmbiguousCompressedChoiceQuota(int beamWidth)
        {
            if (beamWidth < 0)
                throw new ArgumentOutOfRangeException(nameof(beamWidth));
            return Math.Min(AmbiguousCompressedChoiceLimit, beamWidth / 3);
        }

        /// <summary>
        /// Single-card outcomes already have an exact option identity and receive fair service
        /// from the ordinary option round-robin. The ambiguity portfolio is needed only after a
        /// multi-card decision has deliberately been collapsed to cardinality, where several
        /// selected sets can otherwise remain indistinguishable to that scheduler.
        /// </summary>
        internal static bool IsAmbiguousCompressedChoiceCardinality(int choiceCardinality)
        {
            if (choiceCardinality < 0)
                throw new ArgumentOutOfRangeException(nameof(choiceCardinality));
            return choiceCardinality > 1;
        }

        private static AmbiguousChoiceDecisionSignature
            BuildAmbiguousChoiceDecisionSignature(
                SearchNode node,
                SearchNode parent,
                RoutingChoiceSignature choice)
            => new(
                node.PotionCount,
                parent.StateKey,
                parent.ActionCount,
                choice.Turn,
                choice.SourceId,
                choice.Effect,
                choice.Pile,
                RoutingChoiceCardinality(choice),
                choice.ContextId);

        private static bool HasEqualCompressedDeckRank(
            SearchNode left,
            SearchNode right)
            => left.Snapshot.LiveDeckSize == right.Snapshot.LiveDeckSize
                && AttackDensity(left.Snapshot) == AttackDensity(right.Snapshot)
                && SetupLaneValue(left.Snapshot) == SetupLaneValue(right.Snapshot)
                && left.Snapshot.RetainedAttackValue == right.Snapshot.RetainedAttackValue
                && left.Snapshot.ProjectedPlayerHp == right.Snapshot.ProjectedPlayerHp
                && left.Score.Equals(right.Score);

        private static SearchNode? FindBestTacticalEnabler(IReadOnlyList<SearchNode> nodes)
        {
            SearchNode? best = null;
            foreach (SearchNode node in nodes)
            {
                if (best == null
                    || node.Snapshot.ZeroCostPlayableCount > best.Snapshot.ZeroCostPlayableCount
                    || node.Snapshot.ZeroCostPlayableCount == best.Snapshot.ZeroCostPlayableCount
                        && (node.Snapshot.ReachableHandValue > best.Snapshot.ReachableHandValue
                            || node.Snapshot.ReachableHandValue == best.Snapshot.ReachableHandValue
                                && (node.Snapshot.HandCount > best.Snapshot.HandCount
                                    || node.Snapshot.HandCount == best.Snapshot.HandCount
                                        && IsBetterSearchNode(node, best))))
                {
                    best = node;
                }
            }
            return best;
        }

        private static SearchNode? FindBestTurnBoundaryHand(IEnumerable<SearchNode> nodes)
            => nodes.Aggregate(
                (SearchNode?)null,
                (best, node) => best == null
                    || node.Snapshot.ProjectedPlayerHp > best.Snapshot.ProjectedPlayerHp
                    || node.Snapshot.ProjectedPlayerHp == best.Snapshot.ProjectedPlayerHp
                        && (node.Snapshot.OstyHp > best.Snapshot.OstyHp
                            || node.Snapshot.OstyHp == best.Snapshot.OstyHp
                                && (node.Snapshot.HandCount > best.Snapshot.HandCount
                                    || node.Snapshot.HandCount == best.Snapshot.HandCount
                                        && (node.Snapshot.ReachableHandValue > best.Snapshot.ReachableHandValue
                                            || node.Snapshot.ReachableHandValue == best.Snapshot.ReachableHandValue
                                                && (node.Snapshot.EnemyHp < best.Snapshot.EnemyHp
                                                    || node.Snapshot.EnemyHp == best.Snapshot.EnemyHp
                                                        && node.Score > best.Score))))
                    ? node
                    : best);

        private static SearchNode? FindBestCuratedTurnBoundaryHand(IEnumerable<SearchNode> nodes)
            => nodes.Aggregate(
                (SearchNode?)null,
                (best, node) => best == null
                    || node.Snapshot.ProjectedPlayerHp > best.Snapshot.ProjectedPlayerHp
                    || node.Snapshot.ProjectedPlayerHp == best.Snapshot.ProjectedPlayerHp
                        && (node.Snapshot.OstyHp > best.Snapshot.OstyHp
                            || node.Snapshot.OstyHp == best.Snapshot.OstyHp
                                && (node.Snapshot.ProjectedShuffleOrderValue
                                        > best.Snapshot.ProjectedShuffleOrderValue
                                    || node.Snapshot.ProjectedShuffleOrderValue
                                        == best.Snapshot.ProjectedShuffleOrderValue
                                        && (node.Snapshot.ReachableHandValue > best.Snapshot.ReachableHandValue
                                            || node.Snapshot.ReachableHandValue == best.Snapshot.ReachableHandValue
                                                && (node.Snapshot.HandCount < best.Snapshot.HandCount
                                                    || node.Snapshot.HandCount == best.Snapshot.HandCount
                                                        && (node.Snapshot.EnemyHp < best.Snapshot.EnemyHp
                                                            || node.Snapshot.EnemyHp == best.Snapshot.EnemyHp
                                                                && node.Score > best.Score)))))
                    ? node
                    : best);

        private SearchNode? FindBestCompressionAttackGrowth(IReadOnlyList<SearchNode> nodes)
        {
            SearchNode? best = null;
            foreach (SearchNode node in nodes)
            {
                if (best == null
                    || RetainedAttackGrowth(node.Snapshot) > RetainedAttackGrowth(best.Snapshot)
                    || RetainedAttackGrowth(node.Snapshot) == RetainedAttackGrowth(best.Snapshot)
                        && (node.Snapshot.Energy > best.Snapshot.Energy
                            || node.Snapshot.Energy == best.Snapshot.Energy
                                && (node.Snapshot.FutureResourceValue > best.Snapshot.FutureResourceValue
                                    || node.Snapshot.FutureResourceValue == best.Snapshot.FutureResourceValue
                                        && (node.Snapshot.FocusTargetPressure > best.Snapshot.FocusTargetPressure
                                            || node.Snapshot.FocusTargetPressure ==
                                                best.Snapshot.FocusTargetPressure
                                                && node.Score > best.Score))))
                {
                    best = node;
                }
            }
            return best;
        }

        private SearchNode? PreferMostVulnerableTargetVariant(
            IReadOnlyList<SearchNode> nodes,
            SearchNode? candidate)
        {
            if (candidate?.Action is not { TargetCombatId: not null } candidateAction)
                return candidate;
            SearchNode? preferred = nodes
                .Where(node => node.Action is { } action
                    && action.Kind == candidateAction.Kind
                    && action.CardId == candidateAction.CardId
                    && action.PotionId == candidateAction.PotionId
                    && action.TargetCombatId == node.Snapshot.MostVulnerableTargetCombatId)
                .MaxBy(BeamRankScore);
            return preferred ?? candidate;
        }

        private static long AttackDensity(SimulationSnapshot snapshot)
            => (long)snapshot.RetainedAttackValue * 1024 / Math.Max(1, snapshot.LiveDeckSize);

        private static SearchNode? FindBestTargetSetup(IReadOnlyList<SearchNode> nodes)
        {
            SearchNode? best = null;
            int bestSetup = int.MinValue;
            foreach (SearchNode node in nodes)
            {
                int setup = SetupLaneValue(node.Snapshot);
                if (best == null
                    || setup > bestSetup
                    || setup == bestSetup
                        && (node.Snapshot.RetainedAttackValue > best.Snapshot.RetainedAttackValue
                            || node.Snapshot.RetainedAttackValue == best.Snapshot.RetainedAttackValue
                                && (node.Snapshot.FocusTargetPressure > best.Snapshot.FocusTargetPressure
                                    || node.Snapshot.FocusTargetPressure == best.Snapshot.FocusTargetPressure
                                        && (node.Snapshot.ProjectedPlayerHp > best.Snapshot.ProjectedPlayerHp
                                            || node.Snapshot.ProjectedPlayerHp == best.Snapshot.ProjectedPlayerHp
                                                && node.Score > best.Score))))
                {
                    best = node;
                    bestSetup = setup;
                }
            }
            return best;
        }

        private static int SetupLaneValue(SimulationSnapshot snapshot)
            => snapshot.StrategicEffects.RetentionValue * 16
                + snapshot.LatentSetupValue * 8
                + snapshot.ReplayPotentialValue * 16
                + snapshot.FutureResourceValue;

        private static SearchNode? FindBestLane(IReadOnlyList<SearchNode> nodes, SearchRouteTraits trait)
        {
            SearchNode? best = null;
            foreach (SearchNode node in nodes)
            {
                if (!node.Traits.HasFlag(trait))
                    continue;
                int value = LaneValue(node.Snapshot, trait);
                int bestValue = best == null ? int.MinValue : LaneValue(best.Snapshot, trait);
                if (best == null
                    || value > bestValue
                    || value == bestValue && node.Snapshot.ProjectedPlayerHp > best.Snapshot.ProjectedPlayerHp
                    || value == bestValue && node.Snapshot.ProjectedPlayerHp == best.Snapshot.ProjectedPlayerHp
                        && (node.Snapshot.AliveEnemyCount < best.Snapshot.AliveEnemyCount
                            || node.Snapshot.AliveEnemyCount == best.Snapshot.AliveEnemyCount
                                && (node.Snapshot.EnemyHp < best.Snapshot.EnemyHp
                                    || node.Snapshot.EnemyHp == best.Snapshot.EnemyHp && node.Score > best.Score)))
                {
                    best = node;
                }
            }
            return best;
        }

        private static SearchNode? FindBestSetup(IEnumerable<SearchNode> nodes)
        {
            SearchNode? best = null;
            int bestValue = int.MinValue;
            foreach (SearchNode node in nodes)
            {
                int value = LaneValue(node.Snapshot, SearchRouteTraits.Scaling)
                    + LaneValue(node.Snapshot, SearchRouteTraits.Resource)
                    + LaneValue(node.Snapshot, SearchRouteTraits.Control);
                if (best == null
                    || value > bestValue
                    || value == bestValue && node.Snapshot.ProjectedPlayerHp > best.Snapshot.ProjectedPlayerHp
                    || value == bestValue && node.Snapshot.ProjectedPlayerHp == best.Snapshot.ProjectedPlayerHp
                        && node.Score > best.Score)
                {
                    best = node;
                    bestValue = value;
                }
            }
            return best;
        }

        private static int LaneValue(SimulationSnapshot snapshot, SearchRouteTraits trait)
            => trait switch
            {
                SearchRouteTraits.Scaling => SetupLaneValue(snapshot) + snapshot.DelayedDamageValue,
                SearchRouteTraits.Resource => snapshot.Energy * 16
                    + snapshot.Stars * 8
                    + snapshot.HandCount
                    + snapshot.ReachableHandValue
                    + snapshot.FutureResourceValue
                    + snapshot.OstyHp * 16
                    + snapshot.OstyMaxHp * 4,
                SearchRouteTraits.LongTermResource => snapshot.LongTermResourceValue,
                SearchRouteTraits.Control => snapshot.SandpitRemaining * 32
                    + snapshot.EnemyStrengthSuppression * 32
                    + snapshot.EnemyWeakTurns * 8
                    + snapshot.FocusTargetVulnerableTurns * 4
                        * Math.Min(SolverWeights.VulnerableAttackWindowCap, snapshot.RetainedAttackValue)
                    + Math.Max(0, snapshot.EnemyVulnerableTurns - snapshot.FocusTargetVulnerableTurns)
                        * Math.Min(SolverWeights.VulnerableAttackWindowCap, snapshot.RetainedAttackValue)
                    + snapshot.DelayedDamageValue
                    - snapshot.LiveDeckClutter * 8,
                SearchRouteTraits.RevivalWindow => snapshot.RevivingEnemyCount * 1024
                    - snapshot.RawEnemyHp * 4
                    - snapshot.MaxCurrentEnemyHp * 8,
                SearchRouteTraits.DeclinedExtraTurn => 0,
                SearchRouteTraits.ReactiveDamage => snapshot.ReactiveDamageValue,
                SearchRouteTraits.EndTurnDeckCompression => snapshot.Energy * 64
                    + snapshot.FutureResourceValue * 16
                    + (int)Math.Min(int.MaxValue, AttackDensity(snapshot))
                    + snapshot.FocusTargetPressure
                    - snapshot.LiveDeckSize * 16,
                SearchRouteTraits.HpInvestment => snapshot.StrategicEffects.RetentionValue * 16
                    + snapshot.FutureResourceValue * 8
                    + snapshot.DelayedDamageValue * 8
                    + snapshot.FocusTargetPressure,
                _ => throw new ArgumentOutOfRangeException(nameof(trait), trait, null),
            };

        private SearchNode? FindBestStandPat(
            IReadOnlyList<SearchNode> nodes,
            SearchRouteTraits trait)
        {
            const int limit = 8;
            List<SearchNode> probes = nodes
                .Where(node => node.Traits.HasFlag(trait))
                .OrderByDescending(node => node.Snapshot.ProjectedPlayerHp)
                .ThenByDescending(node => LaneValue(node.Snapshot, trait))
                .ThenByDescending(node => node.Score)
                .Take(limit)
                .ToList();

            _prepareStandPat?.Invoke(probes);
            SearchNode? best = null;
            StandPatEvaluation bestEvaluation = default;
            foreach (SearchNode node in probes)
            {
                StandPatEvaluation evaluation = _evaluateStandPat(node);
                int evaluationValue = trait == SearchRouteTraits.Resource
                    ? evaluation.ResourceValue
                    : evaluation.DelayedDamage;
                int bestEvaluationValue = trait == SearchRouteTraits.Resource
                    ? bestEvaluation.ResourceValue
                    : bestEvaluation.DelayedDamage;
                if (best == null
                    || evaluation.AllEnemiesDead && !bestEvaluation.AllEnemiesDead
                    || evaluation.AllEnemiesDead == bestEvaluation.AllEnemiesDead
                        && (evaluation.ProjectedPlayerHp > bestEvaluation.ProjectedPlayerHp
                            || evaluation.ProjectedPlayerHp == bestEvaluation.ProjectedPlayerHp
                                && (evaluationValue > bestEvaluationValue
                                    || evaluationValue == bestEvaluationValue
                                        && node.Score > best.Score)))
                {
                    best = node;
                    bestEvaluation = evaluation;
                }
            }
            return best;
        }

        private SearchNode? FindBestFreshResourceStandPat(IReadOnlyList<SearchNode> nodes)
        {
            List<SearchNode> probes = nodes.Where(node => node.Parent is { } parent
                         && (node.Snapshot.FutureResourceValue > parent.Snapshot.FutureResourceValue
                             || node.Snapshot.StrategicEffects.ResourcePotential
                                > parent.Snapshot.StrategicEffects.ResourcePotential)).ToList();
            _prepareStandPat?.Invoke(probes);
            SearchNode? best = null;
            StandPatEvaluation bestEvaluation = default;
            foreach (SearchNode node in probes)
            {
                StandPatEvaluation evaluation = _evaluateStandPat(node);
                if (best == null
                    || evaluation.AllEnemiesDead && !bestEvaluation.AllEnemiesDead
                    || evaluation.AllEnemiesDead == bestEvaluation.AllEnemiesDead
                        && (evaluation.ProjectedPlayerHp > bestEvaluation.ProjectedPlayerHp
                            || evaluation.ProjectedPlayerHp == bestEvaluation.ProjectedPlayerHp
                                && (evaluation.ResourceValue > bestEvaluation.ResourceValue
                                    || evaluation.ResourceValue == bestEvaluation.ResourceValue
                                        && node.Snapshot.CumulativePlayerHpLost
                                            < best.Snapshot.CumulativePlayerHpLost
                                    || evaluation.ResourceValue == bestEvaluation.ResourceValue
                                        && node.Snapshot.CumulativePlayerHpLost
                                            == best.Snapshot.CumulativePlayerHpLost
                                        && node.Score > best.Score)))
                {
                    best = node;
                    bestEvaluation = evaluation;
                }
            }
            return best;
        }

        private bool MultiObjectiveDominates(SearchNode left, SearchNode right)
        {
            if (ReferenceEquals(left, right))
                return false;
            if (left.Snapshot.EnemyCombatDistributionKey != right.Snapshot.EnemyCombatDistributionKey
                || left.Snapshot.EnemyControlDistributionKey != right.Snapshot.EnemyControlDistributionKey
                || left.Snapshot.UnorderedPileKey != right.Snapshot.UnorderedPileKey)
            {
                return false;
            }
            bool useTeamSafety =
                _routePolicy == SearchRoutePolicy.MultiplayerLocalCrossTurn;
            bool noWorse = left.Snapshot.ProjectedPlayerHp >= right.Snapshot.ProjectedPlayerHp
                && left.Snapshot.PlayerMaxHp >= right.Snapshot.PlayerMaxHp
                && left.Snapshot.CumulativePlayerHpLost <= right.Snapshot.CumulativePlayerHpLost
                && (!useTeamSafety
                    || (left.Snapshot.AllPlayersAlive || !right.Snapshot.AllPlayersAlive)
                        && left.Snapshot.TeamLossRatio <= right.Snapshot.TeamLossRatio
                        && left.Snapshot.WorstPlayerLossRatio <= right.Snapshot.WorstPlayerLossRatio)
                && left.Snapshot.LongTermResourceValue >= right.Snapshot.LongTermResourceValue
                && left.Snapshot.StrategicHpCredit >= right.Snapshot.StrategicHpCredit
                && (left.Snapshot.RelicCounters.SatisfiedMask & right.Snapshot.RelicCounters.SatisfiedMask)
                    == right.Snapshot.RelicCounters.SatisfiedMask
                && left.Snapshot.StrategyGoalCount >= right.Snapshot.StrategyGoalCount
                && left.Snapshot.AngerCopiesGenerated <= right.Snapshot.AngerCopiesGenerated
                && (_theftPolicy != SolverTheftPolicy.PreserveResources
                    || left.Snapshot.OutstandingStolenResource <= right.Snapshot.OutstandingStolenResource)
                && left.Snapshot.AliveEnemyCount <= right.Snapshot.AliveEnemyCount
                && left.Snapshot.EnemyHp <= right.Snapshot.EnemyHp
                && left.Snapshot.RawEnemyHp <= right.Snapshot.RawEnemyHp
                && left.Snapshot.MaxCurrentEnemyHp <= right.Snapshot.MaxCurrentEnemyHp
                && left.Snapshot.PersistentBuffValue >= right.Snapshot.PersistentBuffValue
                && left.Snapshot.LatentSetupValue >= right.Snapshot.LatentSetupValue
                && left.Snapshot.DelayedDamageValue >= right.Snapshot.DelayedDamageValue
                && left.Snapshot.ReactiveDamageValue >= right.Snapshot.ReactiveDamageValue
                && left.Snapshot.EnemyStrengthSuppression >= right.Snapshot.EnemyStrengthSuppression
                && left.Snapshot.EnemyWeakTurns >= right.Snapshot.EnemyWeakTurns
                && left.Snapshot.EnemyVulnerableTurns >= right.Snapshot.EnemyVulnerableTurns
                && left.Snapshot.FocusTargetVulnerableTurns >= right.Snapshot.FocusTargetVulnerableTurns
                && left.Snapshot.Energy >= right.Snapshot.Energy
                && left.Snapshot.Stars >= right.Snapshot.Stars
                && left.Snapshot.FutureResourceValue >= right.Snapshot.FutureResourceValue
                && left.Snapshot.OstyHp >= right.Snapshot.OstyHp
                && left.Snapshot.OstyMaxHp >= right.Snapshot.OstyMaxHp
                && RetainedAttackGrowth(left.Snapshot) >= RetainedAttackGrowth(right.Snapshot)
                && left.Snapshot.ReplayPotentialValue >= right.Snapshot.ReplayPotentialValue
                && left.Snapshot.FocusTargetPressure >= right.Snapshot.FocusTargetPressure
                && left.Snapshot.SandpitRemaining >= right.Snapshot.SandpitRemaining
                && left.Snapshot.LiveDeckClutter <= right.Snapshot.LiveDeckClutter
                && left.Snapshot.LiveDeckSize <= right.Snapshot.LiveDeckSize
                && left.PotionCount <= right.PotionCount
                && left.PotionStrategicCost <= right.PotionStrategicCost
                && left.FutureSoldHp <= right.FutureSoldHp
                && left.ActionCount <= right.ActionCount;
            bool strictlyBetter = left.Snapshot.ProjectedPlayerHp > right.Snapshot.ProjectedPlayerHp
                || left.Snapshot.PlayerMaxHp > right.Snapshot.PlayerMaxHp
                || left.Snapshot.CumulativePlayerHpLost < right.Snapshot.CumulativePlayerHpLost
                || useTeamSafety && left.Snapshot.AllPlayersAlive && !right.Snapshot.AllPlayersAlive
                || useTeamSafety && left.Snapshot.TeamLossRatio < right.Snapshot.TeamLossRatio
                || useTeamSafety
                    && left.Snapshot.WorstPlayerLossRatio < right.Snapshot.WorstPlayerLossRatio
                || left.Snapshot.LongTermResourceValue > right.Snapshot.LongTermResourceValue
                || left.Snapshot.AngerCopiesGenerated < right.Snapshot.AngerCopiesGenerated
                || _theftPolicy == SolverTheftPolicy.PreserveResources
                    && left.Snapshot.OutstandingStolenResource < right.Snapshot.OutstandingStolenResource
                || left.Snapshot.AliveEnemyCount < right.Snapshot.AliveEnemyCount
                || left.Snapshot.EnemyHp < right.Snapshot.EnemyHp
                || left.Snapshot.RawEnemyHp < right.Snapshot.RawEnemyHp
                || left.Snapshot.MaxCurrentEnemyHp < right.Snapshot.MaxCurrentEnemyHp
                || left.Snapshot.PersistentBuffValue > right.Snapshot.PersistentBuffValue
                || left.Snapshot.LatentSetupValue > right.Snapshot.LatentSetupValue
                || left.Snapshot.DelayedDamageValue > right.Snapshot.DelayedDamageValue
                || left.Snapshot.ReactiveDamageValue > right.Snapshot.ReactiveDamageValue
                || left.Snapshot.EnemyStrengthSuppression > right.Snapshot.EnemyStrengthSuppression
                || left.Snapshot.EnemyWeakTurns > right.Snapshot.EnemyWeakTurns
                || left.Snapshot.EnemyVulnerableTurns > right.Snapshot.EnemyVulnerableTurns
                || left.Snapshot.FocusTargetVulnerableTurns > right.Snapshot.FocusTargetVulnerableTurns
                || left.Snapshot.Energy > right.Snapshot.Energy
                || left.Snapshot.Stars > right.Snapshot.Stars
                || left.Snapshot.FutureResourceValue > right.Snapshot.FutureResourceValue
                || left.Snapshot.OstyHp > right.Snapshot.OstyHp
                || left.Snapshot.OstyMaxHp > right.Snapshot.OstyMaxHp
                || RetainedAttackGrowth(left.Snapshot) > RetainedAttackGrowth(right.Snapshot)
                || left.Snapshot.ReplayPotentialValue > right.Snapshot.ReplayPotentialValue
                || left.Snapshot.FocusTargetPressure > right.Snapshot.FocusTargetPressure
                || left.Snapshot.SandpitRemaining > right.Snapshot.SandpitRemaining
                || left.Snapshot.LiveDeckClutter < right.Snapshot.LiveDeckClutter
                || left.Snapshot.LiveDeckSize < right.Snapshot.LiveDeckSize
                || left.PotionCount < right.PotionCount
                || left.PotionStrategicCost < right.PotionStrategicCost
                || left.FutureSoldHp < right.FutureSoldHp
                || left.ActionCount < right.ActionCount;
            return noWorse && strictlyBetter;
        }

        private bool IsBetterSearchNode(SearchNode candidate, SearchNode current)
        {
            int objective = CompareMultiplayerObjective(candidate, current);
            if (objective != 0)
                return objective < 0;
            return candidate.Score > current.Score
                || candidate.Score.Equals(current.Score)
                    && candidate.ActionCount < current.ActionCount;
        }

        private double BeamRankScore(SearchNode node)
        {
            // 基础分成员（见 SolverSearchProfile.BaseScoreOnly）：中途排序只用基础分；未置位时下面逐位不变。
            if (_profile.BaseScoreOnly)
                return node.Score;
            int persistentBuffCap = _isActEndingBoss
                ? SolverWeights.PersistentBuffDeltaBeamCap
                : SolverWeights.StandardPersistentBuffDeltaBeamCap;
            double persistentBuffValue = _isActEndingBoss
                ? SolverWeights.PersistentBuffDeltaBeamValue
                : SolverWeights.StandardPersistentBuffDeltaBeamValue;
            bool useLatentSetup = _isActEndingBoss || _initialEnemyCount > 1;
            int strengthSuppressionHorizon = _isActEndingBoss
                ? SolverWeights.BossEnemyStrengthSuppressionHorizon
                : SolverWeights.StandardEnemyStrengthSuppressionHorizon;
            int weakExpectedHpSaved = _isActEndingBoss
                ? SolverWeights.BossEnemyWeakExpectedHpSaved
                : SolverWeights.StandardEnemyWeakExpectedHpSaved;
            return node.Score
                + Math.Min(SolverWeights.CurrentEnergyBeamCap, node.Snapshot.Energy)
                    * SolverWeights.CurrentEnergyBeamValue
                + Math.Min(
                        persistentBuffCap,
                        Math.Max(0, node.Snapshot.PersistentBuffValue - _run.InitialPersistentBuffValue))
                    * persistentBuffValue
                + (useLatentSetup
                    ? Math.Min(SolverWeights.LatentSetupBeamCap, node.Snapshot.LatentSetupValue)
                        * SolverWeights.LatentSetupBeamValue
                    : 0d)
                + (_isActEndingBoss
                    ? node.Snapshot.FutureResourceValue * SolverWeights.FutureResourceBeamValue
                    : 0d)
                + Math.Min(
                        SolverWeights.ReplayPotentialBeamCap,
                        node.Snapshot.ReplayPotentialValue)
                    * SolverWeights.ReplayPotentialBeamValue
                + RetainedAttackGrowth(node.Snapshot) * SolverWeights.RetainedAttackGrowthBeamValue
                + node.Snapshot.DelayedDamageValue * SolverWeights.DelayedDamageBeamValue
                + node.Snapshot.SandpitRemaining * SolverWeights.SandpitTurnBeamValue
                + Math.Min(
                        SolverWeights.EnemyStrengthSuppressionBeamCap,
                        Math.Max(
                            0,
                            node.Snapshot.EnemyStrengthSuppression
                            - _run.InitialEnemyStrengthSuppression))
                    * strengthSuppressionHorizon
                    * SolverWeights.Hp
                + Math.Min(
                        SolverWeights.EnemyWeakTurnsBeamCap,
                        Math.Max(0, node.Snapshot.EnemyWeakTurns - _run.InitialEnemyWeakTurns))
                    * weakExpectedHpSaved
                    * SolverWeights.Hp;
        }

        private int RetainedAttackGrowth(SimulationSnapshot snapshot)
            => Math.Min(
                SolverWeights.RetainedAttackGrowthBeamCap,
                Math.Max(0, snapshot.RetainedAttackValue - _run.InitialRetainedAttackValue));

    }

}
