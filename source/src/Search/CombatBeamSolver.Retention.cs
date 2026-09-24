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
    private readonly record struct CycleProbeFamilyKey(
        int Turn,
        StateFingerprint ShapeKey,
        StateFingerprint SequenceKey,
        int PeriodActions,
        int HealthRiskBucket,
        CycleProbeTracker? Tracker);

    private readonly record struct CycleStartupRetentionKey(
        int HealthRiskBucket,
        long HealthRisk,
        int PotionStrategicCost,
        int LiveDeckClutter,
        int LiveDeckSize,
        long SetupValue,
        StateFingerprint StableFingerprint);

    private sealed record CycleStartupRetentionTestCandidate(
        int Identity,
        int ProjectedPlayerHp,
        CycleStartupRetentionKey Key);

    [InlineArray(5)]
    private struct CycleStartupNodeBuckets
    {
        private SearchNode? _element0;
    }

    [InlineArray(5)]
    private struct CycleStartupKeyBuckets
    {
        private CycleStartupRetentionKey _element0;
    }

    private readonly record struct CycleExitProbeFamilyKey(
        StateFingerprint OriginShapeKey,
        StateFingerprint OriginSequenceKey,
        int OriginPeriodActions,
        int OriginPhaseIndex,
        CycleProbeTracker OriginTracker,
        long OriginGeneration,
        StateFingerprint ExitActionKey);

    private readonly record struct CycleExitProbeTicketKey(
        CycleProbeTracker OriginTracker,
        int OriginPhaseIndex,
        StateFingerprint ExitActionKey,
        long OriginGeneration);

    private SearchNode RefreshReleasedFallback(SearchNode fallback)
    {
        if (fallback.Snapshot.HasSimulator)
            return fallback;
        SimulationSnapshot? turnSetupRoot = _includeTurnSetup
            ? ReplayTurnSetup(fallback.GetTurnSetupChoices())
            : null;
        SimulationSnapshot snapshot;
        try
        {
            snapshot = Replay(
                fallback.Actions,
                turnSetupRoot,
                _startTurnNumber,
                priorActionCount: 0);
        }
        finally
        {
            turnSetupRoot?.ReleaseSimulator();
        }
        return fallback with
        {
            Score = snapshot.Score,
            StateKey = snapshot.StateKey,
            HasPredictionRisk = snapshot.HasRisk,
            BoundaryReason = snapshot.BoundaryReason,
            IsTerminal = snapshot.PlayerDead
                || snapshot.AllEnemiesDead
                || snapshot.BoundaryReason != SearchBoundaryReason.None,
            Snapshot = snapshot,
        };
    }

    private List<SearchNode> RankLongTermResourceWithAncestorRanks(
        List<SearchNode> pool,
        List<SearchNode> global)
    {
        // Uniform pools have no independent resource route. Determine that before staging
        // ancestor ranks: the resource selector never consumes those ranks in this case.
        var maximum = BeamRetentionPolicy.GetLongTermResourceMaximum(pool);
        if (maximum.Count == pool.Count)
            return [];
        // RankBest 的返回表按引用去重，保存/还原名次只需要一条与它同序的并行数组，
        // 还原顺序与原来按插入序枚举字典完全一致。
        int[] globalRetentionRanks = new int[global.Count];
        for (int index = 0; index < global.Count; index++)
            globalRetentionRanks[index] = global[index].RetentionRank;
        Dictionary<SearchNode, int> ancestorRetentionRanks = new(ReferenceEqualityComparer.Instance);
        foreach (SearchNode candidate in pool)
        {
            for (SearchNode? ancestor = candidate.Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                // The first visit records this ancestor and its complete parent chain.
                // A repeated ancestor therefore proves every remaining parent is recorded too.
                if (!ancestorRetentionRanks.TryAdd(ancestor, ancestor.RetentionRank))
                    break;
                if (ancestor.LongTermResourceRetentionRank != int.MaxValue)
                    ancestor.RetentionRank = ancestor.LongTermResourceRetentionRank;
            }
        }
        List<SearchNode> longTermResource = Retention.RankLongTermResource(pool, _profile.BeamWidth, maximum);
        foreach (SearchNode candidate in longTermResource)
            candidate.LongTermResourceRetentionRank = candidate.RetentionRank;
        foreach ((SearchNode ancestor, int retentionRank) in ancestorRetentionRanks)
            ancestor.RetentionRank = retentionRank;
        for (int index = 0; index < global.Count; index++)
            global[index].RetentionRank = globalRetentionRanks[index];
        return longTermResource;
    }

    private List<SearchNode> Prune(IEnumerable<SearchNode> nodes)
    {
        SearchMeasurement measurement = _run.Performance.Begin();
        try
        {
            // Rank the complete candidate pool before applying the incumbent. Filtering first
            // backfills the beam with weaker branches and changes which exact lineages win later
            // transposition races; an incumbent is a bound, not a request to refill every lane.
            List<SearchNode> pool = nodes as List<SearchNode> ?? nodes.ToList();
            int pathBoundaryId = ObserveSearchPathBoundaryInput(
                pool, SearchPathObservationStage.PruneInput, "prune_input");
            Action<GlobalRetentionDecision>? observeGlobalRetention =
                CreateGlobalRetentionObserver(pool, pathBoundaryId);
            List<SearchNode> global = Retention.RankBest(
                pool,
                _profile.BeamWidth,
                preserveDefensiveRoute: true,
                useSecondRankBand: true,
                observe: observeGlobalRetention);
            // RankBest has drained its lanes and published its ordered result. The rest of
            // retention is a separate allocation interval while the complete pool stays rooted.
            _run.CheckpointPruneMetadata?.Invoke("resource_routes");
            List<SearchNode> selected = [.. global];
            HashSet<SearchNode> selectedSet = new(global, ReferenceEqualityComparer.Instance);
            List<SearchNode> longTermResource = RankLongTermResourceWithAncestorRanks(pool, global);
            foreach (SearchNode candidate in longTermResource
                         .OrderBy(node => node.RetentionRank)
                         .ThenByDescending(node => node.Score))
            {
                if (!selectedSet.Add(candidate))
                    continue;
                selected.Add(candidate);
            }
            _run.CheckpointPruneMetadata?.Invoke("opening_routes");
            bool hasCyclePortfolioWork = false;
            bool hasCycleExitWork = false;
            bool hasCrossTurnWork = false;
            bool hasCycleRegionWork = false;
            bool hasOrderedMutationWork = false;
            foreach (SearchNode candidate in pool)
            {
                hasCyclePortfolioWork |= candidate.CycleProbeLease != null
                    || RequiresBoundedCyclePlanning(candidate);
                hasCycleExitWork |= candidate.CycleExitProbe != null;
                hasCrossTurnWork |= candidate.CrossTurnProbe != null
                    || BeamRetentionPolicy.RequiresCrossTurnPlanning(candidate);
                hasCycleRegionWork |= !IsCycleRegionBudgetExempt(candidate)
                    && (candidate.CycleExitProbe != null
                        || candidate.CycleProbeLease != null
                        || candidate.Cycle != null);
                hasOrderedMutationWork |= candidate.OrderedMutationLineage != null
                    || candidate.OrderedMutationBoundaryLineage != null
                    || candidate.OrderedMutationRetentionLease != null
                    || candidate.OrderedMutationActivationTicket != null
                    || candidate.OrderedMutationLeaseTransitionPending
                    || candidate.OrderedMutationAdmissionPending
                    || candidate.OrderedMutationContinuationHandoff
                    || candidate.OrderedMutationContinuationBridge
                    || candidate.OrderedMutationObservationRequested
                    || candidate.OrderedMutationObservationDebtSettlementPending
                    || candidate.OrderedMutationObservationStepsRemaining > 0;
            }
            if (hasCyclePortfolioWork)
                Retention.AddCyclePortfolio(pool, selected, selectedSet);
            if (hasCycleExitWork)
                Retention.AddCycleExitPortfolio(pool, selected, selectedSet);
            if (hasCrossTurnWork)
                Retention.AddCrossTurnPortfolio(pool, selected, selectedSet);
            // Every independent retention channel must finish before the ordered coordinator.
            // In particular a late opening-channel winner with an inherited lease must pay this
            // layer's ordered admission (or lose only that lease) before CycleRegion arbitration.
            if (pool.Count > _profile.BeamWidth
                && root.HasUnusedCardReplayAllocator)
            {
                int channelWidth = Math.Clamp(_profile.BeamWidth / 12, 6, 12);
                List<List<SearchNode>> openingChannels = pool
                    .Select(node => (Node: node, Opening: FindOpeningCardNode(node)))
                    .Where(item => item.Opening?.Parent is { } parent
                        && (item.Opening.Snapshot.PersistentBuffValue
                                > parent.Snapshot.PersistentBuffValue
                            || item.Opening.Snapshot.StrategicEffects.RetentionValue
                                > parent.Snapshot.StrategicEffects.RetentionValue))
                    .GroupBy(item => (
                        item.Node.PotionCount,
                        FirstCardId: item.Opening!.Action!.CardId))
                    .OrderByDescending(group => group.Max(item =>
                        item.Opening!.Snapshot.StrategicEffects.RetentionValue))
                    .ThenByDescending(group => group.Max(item => item.Node.Score))
                    .Take(8)
                    .Select(group => Retention.RankBest(
                        group.Select(item => item.Node),
                        channelWidth,
                        preserveDefensiveRoute: true))
                    .ToList();
                int expandedLimit = Math.Min(
                    pool.Count,
                    checked(selected.Count + Math.Max(12, _profile.BeamWidth / 3)));
                for (int round = 0;
                     selected.Count < expandedLimit
                         && openingChannels.Any(channel => round < channel.Count);
                     round++)
                {
                    foreach (IReadOnlyList<SearchNode> channel in openingChannels)
                    {
                        if (round >= channel.Count || !selectedSet.Add(channel[round]))
                            continue;
                        selected.Add(channel[round]);
                        if (selected.Count >= expandedLimit)
                            break;
                    }
                }
            }

            _run.CheckpointPruneMetadata?.Invoke("ordered_routes");
            CycleRegionRetentionTransaction? cycleRegionTransaction = null;
            if (hasOrderedMutationWork)
                Retention.AddOrderedMutationPortfolio(pool, selected, selectedSet);
            _run.CheckpointPruneMetadata?.Invoke("finalize_routes");
            if (hasCycleRegionWork)
            {
                cycleRegionTransaction = ApplyCycleRegionRetention(
                    pool,
                    selected);
            }
            SortRetained(selected);
            List<SearchNode> finalized = FinalizePrunedSelection(
                pool,
                selected,
                hasOrderedMutationWork,
                hasCycleExitWork,
                cycleRegionTransaction);
            List<SearchNode> bounded = ApplyPrimaryIncumbentBound(finalized);
            // Emit all watched final aliases, after every portfolio and the incumbent.
            // The paired value events avoid equating a `with` clone with a dropped route.
            ObserveSearchPathBoundary(
                bounded, SearchPathObservationStage.PruneFinal, "after_incumbent", pathBoundaryId);
            if (WantsSearchPathRetentionPool(pool))
            {
                ObserveSearchPathRetentionPool(
                    bounded, SearchPathObservationStage.RetentionPoolFinal, "outer_prune_final", pathBoundaryId);
            }
            return bounded;
        }
        finally
        {
            _run.Performance.End(SearchMetricPhase.Prune, measurement);
        }
    }

    private List<SearchNode> FinalizePrunedSelection(
        IReadOnlyList<SearchNode> pool,
        List<SearchNode> selected,
        bool hasOrderedMutationWork,
        bool hasCycleExitWork,
        CycleRegionRetentionTransaction? cycleRegionTransaction = null)
    {
        // Ordered settlement may remove only its own exempt pending work; ordinary CycleRegion
        // survivors are stable and its provisional slots therefore cannot develop backfill holes.
        // The primary incumbent is applied by the caller after every transaction has settled, so
        // removed candidates deliberately leave holes instead of changing the ranked population.
        List<SearchNode> bounded = selected;
        if (hasOrderedMutationWork)
        {
            FinalizeOrderedMutationPortfolio(bounded);
            Retention.ArmOrderedMutationObservationBridges(pool, bounded);
        }
        else if (_run.PendingOrderedMutationHandoffSourceByNode.Count != 0)
        {
            throw new InvalidOperationException(
                "无 ordered-mutation frontier 时遗留了 pending source admission。");
        }
        List<SearchNode> finalized = hasCycleExitWork
            ? FinalizePrunedCycleExitProbeTickets(pool, bounded)
            : bounded;
        FinalizeCycleRegionRetention(cycleRegionTransaction, finalized);
        return finalized;
    }

    private List<SearchNode> ApplyPrimaryIncumbentBound(List<SearchNode> retained)
    {
        // Per-event growth can repeat; the HP-only floor is not a bound on this objective.
        if (_hasGrowthTargets || _theftPolicy == SolverTheftPolicy.PreserveResources || _primaryIncumbent is not { } incumbent)
            return retained;

        List<SearchNode> bounded = ApplyPrimaryIncumbentBound(
            retained,
            incumbent,
            out int pruned,
            _strategicBossHpRelief);
        _run.PrimaryIncumbentBranchesPruned += pruned;
        return bounded;
    }

    internal static List<SearchNode> ApplyPrimaryIncumbentBound(
        List<SearchNode> retained,
        PrimarySearchIncumbent incumbent,
        out int pruned,
        BossHpRelief bossHpRelief = BossHpRelief.None)
    {
        pruned = 0;
        List<SearchNode>? bounded = null;
        for (int index = 0; index < retained.Count; index++)
        {
            SearchNode node = retained[index];
            if (ShouldPruneByPrimaryIncumbent(
                    StrategicHpLowerBound(node.Snapshot, bossHpRelief),
                    node.Turn,
                    incumbent))
            {
                if (bounded == null)
                {
                    bounded = new List<SearchNode>(retained.Count);
                    if (index > 0)
                        bounded.AddRange(retained.GetRange(0, index));
                }
                pruned++;
                continue;
            }
            bounded?.Add(node);
        }
        return bounded ?? retained;
    }

    /// <summary>
    /// Best strategic HP result an unfinished node could still reach, so the incumbent bound never prunes a
    /// branch that could still overtake it.
    /// </summary>
    /// <remarks>
    /// Future damage cannot help: every point of it raises cumulative loss and can at most be healed back, so
    /// it cancels out. What is left is the HP the node is currently missing, which a heal could still restore.
    /// Max HP is deliberately excluded for the same reason the caller excludes it: it may still recover.
    ///
    /// Post-combat relic healing needs no term of its own here. It can never exceed the HP the route ends up
    /// missing, and that headroom is already credited in full, so this stays a valid lower bound.
    /// </remarks>
    private static int StrategicHpLowerBound(SimulationSnapshot snapshot, BossHpRelief bossHpRelief)
        => ActEndingBossPolicy.StrategicHpDeficit(
            snapshot.CumulativePlayerHpLost,
            maxHpDeficit: 0,
            snapshot.RecoveredPlayerHp + Math.Max(0, snapshot.PlayerMaxHp - snapshot.PlayerHp),
            bossHpRelief,
            snapshot.DeathSaveHpRestored);

    internal static bool ShouldPruneByPrimaryIncumbent(
        int strategicHpLowerBound,
        int turn,
        PrimarySearchIncumbent incumbent)
        => strategicHpLowerBound > incumbent.StrategicHpDeficit
            || strategicHpLowerBound == incumbent.StrategicHpDeficit
                && turn > incumbent.CombatEndedTurn;

    internal static bool TryTightenPrimarySearchIncumbent(
        PotionFreePolicyBaseline? auditedPotionFreeBaseline,
        int minimumPotionUses,
        int? maximumPotionUses,
        bool candidateCompleteVictory,
        bool candidateSatisfiesHardRules,
        int candidateExplicitPotionUses,
        int candidateStrategicHpDeficit,
        int? candidateCombatEndedTurn,
        ref PrimarySearchIncumbent? incumbent,
        SolverPotionPolicy? effectivePotionPolicy = null,
        int candidateDeathSaveUseCount = 0)
    {
        if (!candidateCompleteVictory
            || !candidateSatisfiesHardRules
            || candidateDeathSaveUseCount > 0
            || candidateExplicitPotionUses != minimumPotionUses
            || candidateCombatEndedTurn is not { } combatEndedTurn)
        {
            return false;
        }

        // A complete, hard-policy-compliant victory without explicit potion use is
        // already eligible under Disabled/Smart. It needs no separate potion audit.
        // Positive exact layers retain their stricter, audited eligibility proof.
        bool eligiblePotionFreeVictory = minimumPotionUses == 0
            && effectivePotionPolicy is SolverPotionPolicy.Disabled or SolverPotionPolicy.Smart;
        bool eligibleExactPotionVictory = auditedPotionFreeBaseline is { } baseline
            && minimumPotionUses > 0
            && maximumPotionUses == minimumPotionUses
            && SolverInterimResultOrdering.ComparePrimaryQuality(
                candidateCompleteVictory: true,
                candidateStrategicHpDeficit,
                candidateCombatEndedTurn,
                currentCompleteVictory: baseline.Won,
                currentStrategicHpDeficit: baseline.HpDeficit,
                currentCombatEndedTurn: baseline.CombatEndedTurn,
                candidateDeathSaveUseCount: candidateDeathSaveUseCount,
                currentDeathSaveUseCount: baseline.DeathSaveUseCount) < 0;
        if (!eligiblePotionFreeVictory && !eligibleExactPotionVictory)
        {
            return false;
        }

        PrimarySearchIncumbent candidate = new(
            candidateStrategicHpDeficit,
            combatEndedTurn);
        if (incumbent is { } current
            && SolverInterimResultOrdering.ComparePrimaryQuality(
                candidateCompleteVictory: true,
                candidate.StrategicHpDeficit,
                candidate.CombatEndedTurn,
                currentCompleteVictory: true,
                currentStrategicHpDeficit: current.StrategicHpDeficit,
                currentCombatEndedTurn: current.CombatEndedTurn) >= 0)
        {
            return false;
        }

        incumbent = candidate;
        return true;
    }

    private bool TightenPrimarySearchIncumbentAtTurnLayer(
        IReadOnlyList<SearchNode> retained,
        int completedTurnLayers)
    {
        if (_hasGrowthTargets || _theftPolicy == SolverTheftPolicy.PreserveResources)
            return false;
        bool canEstablishPotionFreeIncumbent = _minimumPotionUses == 0
            && _potionPolicy is SolverPotionPolicy.Disabled or SolverPotionPolicy.Smart;
        // The strict-primary escape in FinalPlanOrdering is guaranteed to make an
        // exact-layer victory policy-eligible only when every explicit use is optional.
        // Smart-gradient exact layers use a policy override and therefore do not enforce
        // per-slot directives here. Future forced-directive exact solvers must prove their
        // optional-use facts separately before they may tighten this bound.
        bool canEstablishExactPotionIncumbent = _potionFreePolicyBaseline != null
            && _minimumPotionUses > 0
            && _maximumPotionUses == _minimumPotionUses
            && !_enforcePotionDirectives;
        if (!canEstablishPotionFreeIncumbent && !canEstablishExactPotionIncumbent)
        {
            return false;
        }

        PrimarySearchIncumbent? tightened = _primaryIncumbent;
        foreach (SearchNode node in retained)
        {
            int explicitPotionUses = ExplicitPotionUseCount(node);
            bool completeVictory = SolverInterimResultOrdering.IsCompleteVictory(
                node.ActionCount,
                node.Snapshot.AllEnemiesDead,
                node.Snapshot.PlayerDead,
                node.Snapshot.ProjectedPlayerHp);
            if (!completeVictory
                || node.Snapshot.ProjectedDeathSaveUseCount > 0
                || explicitPotionUses != _minimumPotionUses
                || _enforcePotionDirectives
                    && !_potionStrategy.EvaluateForcedUses(
                            node.Actions,
                            root.HasRenewablePotionShapedRock)
                        .AllForcedUsesSatisfied)
            {
                continue;
            }

            // PlayerMaxHp is part of the incumbent only after combat has actually ended.
            // ApplyPrimaryIncumbentBound deliberately keeps using a looser lower bound for
            // incomplete nodes because max HP may still recover and HP may still be healed.
            int strategicHpDeficit = ActEndingBossPolicy.StrategicHpDeficit(
                node.Snapshot.CumulativePlayerHpLost,
                Math.Max(0, root.InitialPlayerMaxHp - node.Snapshot.PlayerMaxHp),
                node.Snapshot.RecoveredPlayerHp
                    + ActEndingBossPolicy.RankedPostCombatRelicHeal(
                        root.PostCombatRelicHeal, true, node.Snapshot.PlayerHp, node.Snapshot.PlayerMaxHp),
                _strategicBossHpRelief,
                node.Snapshot.DeathSaveHpRestored);
            TryTightenPrimarySearchIncumbent(
                _potionFreePolicyBaseline,
                _minimumPotionUses,
                _maximumPotionUses,
                candidateCompleteVictory: true,
                candidateSatisfiesHardRules: true,
                explicitPotionUses,
                strategicHpDeficit,
                node.Snapshot.CombatEndedTurn,
                ref tightened,
                effectivePotionPolicy: _potionPolicy,
                candidateDeathSaveUseCount: node.Snapshot.ProjectedDeathSaveUseCount);
        }

        if (Nullable.Equals(tightened, _primaryIncumbent))
            return false;

        PrimarySearchIncumbent? previous = _primaryIncumbent;
        _primaryIncumbent = tightened;
        _run.PrimaryIncumbentUpdates++;
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] PRIMARY_INCUMBENT_UPDATE " +
            $"source={(canEstablishPotionFreeIncumbent ? "no_explicit_potion" : "exact_potion_layer")} " +
            $"completed_turns={completedTurnLayers} " +
            $"previous_deficit={previous?.StrategicHpDeficit.ToString() ?? "-"} " +
            $"previous_turn={previous?.CombatEndedTurn.ToString() ?? "-"} " +
            $"deficit={tightened!.Value.StrategicHpDeficit} " +
            $"turn={tightened.Value.CombatEndedTurn}");
        return true;
    }

    private static void SortRetained(List<SearchNode> selected)
        => selected.Sort(CompareRetainedOrder);

    private static int CompareRetainedOrder(SearchNode left, SearchNode right)
    {
        int leftRank = Math.Min(
            left.RetentionRank,
            Math.Min(
                left.LongTermResourceRetentionRank,
                Math.Min(
                    left.CycleRetentionRank,
                    Math.Min(left.CycleExitRetentionRank, left.CrossTurnRetentionRank))));
        int rightRank = Math.Min(
            right.RetentionRank,
            Math.Min(
                right.LongTermResourceRetentionRank,
                Math.Min(
                    right.CycleRetentionRank,
                    Math.Min(right.CycleExitRetentionRank, right.CrossTurnRetentionRank))));
        int byRetention = leftRank.CompareTo(rightRank);
        if (byRetention != 0)
            return byRetention;
        int byScore = right.Score.CompareTo(left.Score);
        return byScore != 0
            ? byScore
            : CompareCycleCandidateDeterministicFingerprints(left, right);
    }


    private static long CycleHealthRisk(SearchNode node, int referenceMaxHp)
        => (long)node.Snapshot.CumulativePlayerHpLost
            + node.FutureSoldHp
            + Math.Max(0, referenceMaxHp - node.Snapshot.PlayerMaxHp);

    private long CycleStartupHealthRisk(SearchNode node)
        => CycleHealthRisk(node, root.InitialPlayerMaxHp);

    private int CycleStartupHealthRiskBucket(SearchNode node)
        => CycleStartupHealthRiskBucket(
            root.InitialPlayerHp,
            CycleStartupHealthRisk(node));

    private static int CycleStartupHealthRiskBucket(
        int initialPlayerHp,
        long healthRisk)
    {
        if (healthRisk <= 0)
            return 0;
        long survivableRisk = Math.Max(1L, (long)initialPlayerHp - 1);
        long quartile = Math.Min(3L, checked((healthRisk - 1) * 4) / survivableRisk);
        return checked(1 + (int)quartile);
    }

    private static bool CanOccupyCycleStartupReserve(int projectedPlayerHp)
        => projectedPlayerHp > 0;

    private static StateFingerprint BuildCycleStartupStableFingerprint(
        StateFingerprint state,
        StateFingerprint action,
        StateFingerprint parent)
        => BeamRetentionPolicy.BuildCycleStartupStableFingerprint(state, action, parent);

    private static T? SelectCycleStartupBucketRepresentative<T>(
        IEnumerable<T> candidates,
        int healthRiskBucket,
        Func<T, bool> isEligible,
        Func<T, CycleStartupRetentionKey> keySelector)
        where T : class
        => BeamRetentionPolicy.SelectCycleStartupBucketRepresentative(
            candidates,
            healthRiskBucket,
            isEligible,
            keySelector);

    private static T? SelectCyclePurificationAnchor<T>(
        IEnumerable<T> candidates,
        Func<T, bool> isEligible,
        Func<T, CycleStartupRetentionKey> keySelector)
        where T : class
        => BeamRetentionPolicy.SelectCyclePurificationAnchor(
            candidates,
            isEligible,
            keySelector);

    private static CycleExitProbeFamilyKey BuildCycleExitProbeFamilyKey(SearchNode node)
        => BeamRetentionPolicy.BuildCycleExitProbeFamilyKey(node);

    private static CycleExitProbeTicketKey BuildCycleExitProbeTicketKey(SearchNode node)
        => BeamRetentionPolicy.BuildCycleExitProbeTicketKey(node);

    private CycleProbeFamilyKey BuildCycleProbeFamilyKey(SearchNode node)
        => Retention.BuildCycleProbeFamilyKey(node);



    private static SearchNode? FindOpeningCardNode(SearchNode node)
    {
        SearchNode? opening = null;
        for (SearchNode? cursor = node; cursor?.Action != null; cursor = cursor.Parent)
        {
            if (cursor.Action.Kind == PlanActionKind.PlayCard)
                opening = cursor;
        }
        return opening;
    }

    private void CaptureContinuation(SearchNode node)
    {
        if (node.Action is not { } action
            || action.Kind != PlanActionKind.EndTurn && !action.EndsPlayerTurn
            || node.Snapshot.PlayerDead
            || node.Snapshot.AllEnemiesDead
            || node.Snapshot.BoundaryReason != SearchBoundaryReason.None)
        {
            return;
        }
        bool continuationComplete = node.Snapshot.Continuation != null
            && (!root.AllowsLocalPlayerOnlySearch
                || node.Snapshot.ContinuationRemoteFingerprint != null);
        if (continuationComplete)
            return;
        CombatPredictionSimulator simulator =
            (CombatPredictionSimulator)node.Snapshot.Simulator;
        StateFingerprint? remoteFingerprint = root.AllowsLocalPlayerOnlySearch
            ? MultiplayerContinuationRemoteFingerprint.CapturePredicted(
                simulator,
                _player)
            : null;
        node.Snapshot.SetContinuation(
            ContinuationStamp.CapturePredicted(
                _player,
                simulator,
                node.Turn,
                _forecast,
                _startTurnNumber),
            remoteFingerprint);
    }

    private static void ValidateHistoricalSimulatorsReleased(IReadOnlyList<SearchNode> candidates)
    {
        foreach (SearchNode candidate in candidates)
        {
            for (SearchNode? parent = candidate.Parent; parent != null; parent = parent.Parent)
            {
                if (parent.Snapshot.HasSimulator)
                    throw new InvalidOperationException("历史搜索节点仍在保留完整模拟器。");
            }
        }
    }

    private static void ReleaseDroppedSnapshots(
        IReadOnlyList<SearchNode> candidates,
        IReadOnlyList<SearchNode> retained)
    {
        // Larger retained pools otherwise require a quadratic reference scan. Keep the
        // allocation-free path for tiny pools; snapshot identity (not node identity) owns retention.
        if (retained.Count > 8)
        {
            HashSet<SimulationSnapshot> retainedSnapshots = new(
                retained.Count, ReferenceEqualityComparer.Instance);
            foreach (SearchNode survivor in retained)
                retainedSnapshots.Add(survivor.Snapshot);
            foreach (SearchNode candidate in candidates)
                if (!retainedSnapshots.Contains(candidate.Snapshot))
                    candidate.Snapshot.ReleaseSimulator();
            return;
        }

        foreach (SearchNode candidate in candidates)
        {
            bool keepSnapshot = false;
            foreach (SearchNode survivor in retained)
            {
                if (!ReferenceEquals(candidate.Snapshot, survivor.Snapshot))
                    continue;
                keepSnapshot = true;
                break;
            }
            if (!keepSnapshot)
                candidate.Snapshot.ReleaseSimulator();
        }
    }

    private static List<SearchNode> FinalizePrunedCycleExitProbeTickets(
        IReadOnlyList<SearchNode> pool,
        List<SearchNode> retained)
    {
        SettleDroppedCycleExitProbeTickets(pool, retained);
        return retained;
    }

    private static void SettleDroppedCycleExitProbeTickets(
        IReadOnlyList<SearchNode> candidates,
        IReadOnlyList<SearchNode> retained)
    {
        HashSet<SearchNode> retainedSet = new(
            retained,
            ReferenceEqualityComparer.Instance);
        HashSet<CycleExitProbeTicketKey> survivingIssuedTickets = [];
        List<CycleExitProbeTicketKey> issuedCandidates = [];

        foreach (SearchNode survivor in retained)
        {
            if (survivor.CycleExitProbe is not { } probe)
                continue;
            bool retainsPortfolioLease = survivor.CycleExitRetentionRank != int.MaxValue;
            if (probe.LeaseIssued)
            {
                CycleExitProbeTicketKey ticket = BuildCycleExitProbeTicketKey(survivor);
                if (retainsPortfolioLease)
                    survivingIssuedTickets.Add(ticket);
                else
                    issuedCandidates.Add(ticket);
            }
            if (!retainsPortfolioLease)
            {
                // Ordinary Beam/long-term retention may keep the route, but it cannot bypass
                // the bounded exit-probe portfolio merely by surviving another prune channel.
                survivor.CycleExitProbe = null;
            }
        }

        foreach (SearchNode candidate in candidates)
        {
            bool retainsPortfolioLease = retainedSet.Contains(candidate)
                && candidate.CycleExitRetentionRank != int.MaxValue;
            if (candidate.CycleExitProbe is { LeaseIssued: true })
                issuedCandidates.Add(BuildCycleExitProbeTicketKey(candidate));
            if (!retainsPortfolioLease)
                candidate.CycleExitProbe = null;
        }

        SettleAbandonedCycleExitProbeTickets(
            issuedCandidates,
            survivingIssuedTickets);
    }

    private static void SettleAbandonedCycleExitProbeTickets(
        IEnumerable<CycleExitProbeTicketKey> issuedCandidates,
        IEnumerable<CycleExitProbeTicketKey> survivingIssuedTickets)
    {
        HashSet<CycleExitProbeTicketKey> surviving = [.. survivingIssuedTickets];
        HashSet<CycleExitProbeTicketKey> settled = [];
        foreach (CycleExitProbeTicketKey ticket in issuedCandidates)
        {
            if (surviving.Contains(ticket) || !settled.Add(ticket))
                continue;
            // Settle one whole ticket, not each sibling. Losing one branch while another
            // survives must never mint duplicate generations.
            ticket.OriginTracker.RetryAbandonedExitProbe(
                ticket.OriginPhaseIndex,
                ticket.ExitActionKey,
                ticket.OriginGeneration);
        }
    }

    internal static void VerifyCycleExitTicketSettlementPolicyForTesting()
    {
        VerifyCycleStartupRetentionPolicyForTesting();

        StateFingerprint shapeKey = new(0x1001UL, 0x1002UL);
        StateFingerprint sequenceKey = new(0x2001UL, 0x2002UL);
        StateFingerprint firstActionKey = new(0x3001UL, 0x3002UL);
        StateFingerprint droppedActionKey = new(0x4001UL, 0x4002UL);
        CycleProbeTracker tracker = new(
            shapeKey,
            sequenceKey,
            [firstActionKey],
            default);
        long firstGeneration = tracker.ObserveExit(
            0,
            firstActionKey,
            default,
            out _);
        long droppedGeneration = tracker.ObserveExit(
            0,
            droppedActionKey,
            default,
            out _);
        if (!tracker.TryMarkExitProbeIssued(0, firstActionKey, firstGeneration)
            || !tracker.TryMarkExitProbeIssued(0, droppedActionKey, droppedGeneration))
        {
            throw new InvalidOperationException("循环出口测试票据无法签发。");
        }

        int activeBefore = tracker.ActiveExitProbeTicketCountForTesting;
        CycleExitProbeTicketKey survivor = new(
            tracker,
            0,
            firstActionKey,
            firstGeneration);
        CycleExitProbeTicketKey dropped = new(
            tracker,
            0,
            droppedActionKey,
            droppedGeneration);
        SettleAbandonedCycleExitProbeTickets(
            [survivor, survivor, dropped, dropped],
            [survivor]);
        long survivingPendingGeneration = tracker.ObserveExit(
            0,
            firstActionKey,
            default,
            out _);
        if (survivingPendingGeneration != 0)
        {
            throw new InvalidOperationException(
                "同一出口票据仍有 sibling 存活时被错误 rearm。");
        }
        long rearmedGeneration = tracker.ObserveExit(
            0,
            droppedActionKey,
            default,
            out _);
        if (!tracker.HasPendingExitProbe(0, droppedActionKey, rearmedGeneration)
            || tracker.ActiveExitProbeTicketCountForTesting != activeBefore)
        {
            throw new InvalidOperationException(
                "同一出口票据的全部 sibling 删除后没有唯一变回 pending，或 ActiveTickets 发生增长。");
        }
        tracker.RearmExitProbes();
        long checkpointGeneration = tracker.ObserveExit(
            0,
            firstActionKey,
            default,
            out _);
        if (checkpointGeneration <= firstGeneration
            || !tracker.HasPendingExitProbe(0, firstActionKey, checkpointGeneration)
            || !tracker.HasPendingExitProbe(0, droppedActionKey, rearmedGeneration)
            || tracker.ActiveExitProbeTicketCountForTesting != activeBefore + 1)
        {
            throw new InvalidOperationException(
                "循环检查点没有在保留旧探测的同时唯一签发新一代出口票据。");
        }
        tracker.RearmExitProbes();
        long repeatedCheckpointGeneration = tracker.ObserveExit(
            0,
            firstActionKey,
            default,
            out _);
        long repeatedRearmGeneration = tracker.ObserveExit(
            0,
            droppedActionKey,
            default,
            out _);
        if (repeatedCheckpointGeneration != checkpointGeneration
            || repeatedRearmGeneration != rearmedGeneration
            || tracker.ActiveExitProbeTicketCountForTesting != activeBefore + 1)
        {
            throw new InvalidOperationException(
                "同一循环检查点被重复处理时重复签发了出口票据。");
        }
    }

    private static void VerifyCycleStartupRetentionPolicyForTesting()
    {
        const int initialPlayerHp = 17;
        long[] risks = [0, 1, 5, 9, 13];
        int[] expectedBuckets = [0, 1, 2, 3, 4];
        for (int index = 0; index < risks.Length; index++)
        {
            int bucket = CycleStartupHealthRiskBucket(initialPlayerHp, risks[index]);
            if (bucket != expectedBuckets[index])
            {
                throw new InvalidOperationException(
                    $"循环 startup 风险桶错误：risk={risks[index]}，" +
                    $"bucket={bucket}/{expectedBuckets[index]}。");
            }
        }
        if (CanOccupyCycleStartupReserve(0)
            || CanOccupyCycleStartupReserve(-1)
            || !CanOccupyCycleStartupReserve(1))
        {
            throw new InvalidOperationException(
                "循环 startup reserve 没有严格拒绝 projected HP 非正的路线。");
        }

        static CycleStartupRetentionKey Key(
            long risk,
            int potionCost,
            int clutter,
            int size,
            long setup,
            ulong stable)
            => new(
                CycleStartupHealthRiskBucket(initialPlayerHp, risk),
                risk,
                potionCost,
                clutter,
                size,
                setup,
                new StateFingerprint(stable, stable + 100));

        CycleStartupRetentionTestCandidate[] candidates =
        [
            new(0, 1, Key(0, 0, 4, 10, 1, 10)),
            new(1, 1, Key(1, 0, 3, 10, 99, 11)),
            // The deep edge of a bucket wins before potion/deck/setup tie-breakers.
            new(2, 1, Key(4, 9, 8, 20, 0, 12)),
            // Global purification prefers the smallest clutter/size, independently of depth.
            new(3, 1, Key(5, 1, 0, 7, 2, 13)),
            new(4, 1, Key(8, 0, 5, 12, 50, 14)),
            new(5, 1, Key(9, 0, 4, 11, 3, 15)),
            new(6, 1, Key(13, 0, 4, 11, 3, 16)),
            // A numerically attractive but non-surviving startup may never own a reserve.
            new(7, 0, Key(4, 0, 0, 1, 999, 1)),
        ];
        static bool Eligible(CycleStartupRetentionTestCandidate candidate)
            => CanOccupyCycleStartupReserve(candidate.ProjectedPlayerHp);
        static CycleStartupRetentionKey SelectKey(
            CycleStartupRetentionTestCandidate candidate)
            => candidate.Key;

        static int[] SelectBucketIdentities(
            IReadOnlyList<CycleStartupRetentionTestCandidate> source)
        {
            int[] identities = new int[5];
            for (int bucket = 0; bucket <= 4; bucket++)
            {
                CycleStartupRetentionTestCandidate selected =
                    SelectCycleStartupBucketRepresentative(
                        source,
                        bucket,
                        Eligible,
                        SelectKey)
                    ?? throw new InvalidOperationException(
                        $"循环 startup 测试缺少第 {bucket} 桶代表。");
                identities[bucket] = selected.Identity;
            }
            return identities;
        }

        int[] expectedIdentities = [0, 2, 4, 5, 6];
        int[] forward = SelectBucketIdentities(candidates);
        CycleStartupRetentionTestCandidate[] reversed = [.. candidates.Reverse()];
        int[] backward = SelectBucketIdentities(reversed);
        if (!forward.SequenceEqual(expectedIdentities)
            || !backward.SequenceEqual(expectedIdentities))
        {
            throw new InvalidOperationException(
                "循环 startup 桶内深端选择错误，或结果受候选遍历顺序影响。");
        }

        StateFingerprint sharedState = new(700, 701);
        StateFingerprint sharedParent = new(800, 801);
        StateFingerprint firstRoute = BuildCycleStartupStableFingerprint(
            sharedState,
            new StateFingerprint(900, 901),
            sharedParent);
        StateFingerprint secondRoute = BuildCycleStartupStableFingerprint(
            sharedState,
            new StateFingerprint(902, 903),
            sharedParent);
        if (firstRoute == secondRoute)
            throw new InvalidOperationException("循环 startup 稳定键没有区分同状态的不同动作路线。");
        CycleStartupRetentionTestCandidate[] tiedStates =
        [
            new(30, 1, Key(1, 0, 4, 10, 1, 30) with
            {
                StableFingerprint = firstRoute,
            }),
            new(31, 1, Key(1, 0, 4, 10, 1, 31) with
            {
                StableFingerprint = secondRoute,
            }),
        ];
        CycleStartupRetentionTestCandidate tiedForward =
            SelectCycleStartupBucketRepresentative(
                tiedStates,
                1,
                Eligible,
                SelectKey)
            ?? throw new InvalidOperationException("循环 startup 同状态稳定性测试没有代表。");
        CycleStartupRetentionTestCandidate tiedBackward =
            SelectCycleStartupBucketRepresentative(
                tiedStates.Reverse(),
                1,
                Eligible,
                SelectKey)
            ?? throw new InvalidOperationException("逆序循环 startup 同状态稳定性测试没有代表。");
        if (tiedForward.Identity != tiedBackward.Identity)
        {
            throw new InvalidOperationException(
                "循环 startup 同状态路线的选择仍受候选遍历顺序影响。");
        }

        CycleStartupRetentionTestCandidate forwardAnchor = SelectCyclePurificationAnchor(
                candidates,
                Eligible,
                SelectKey)
            ?? throw new InvalidOperationException("循环 purification anchor 意外缺失。");
        CycleStartupRetentionTestCandidate reverseAnchor = SelectCyclePurificationAnchor(
                reversed,
                Eligible,
                SelectKey)
            ?? throw new InvalidOperationException("逆序循环 purification anchor 意外缺失。");
        if (forwardAnchor.Identity != 3
            || reverseAnchor.Identity != 3
            || expectedIdentities.Append(forwardAnchor.Identity).Distinct().Count() != 6)
        {
            throw new InvalidOperationException(
                "循环 purification anchor 没有稳定选择真实 deck 净化路线，或组合上限失效。");
        }

        CycleStartupRetentionTestCandidate[] uniformDeck =
        [
            new(20, 1, Key(1, 0, 4, 10, 1, 20)),
            new(21, 1, Key(4, 0, 4, 10, 99, 21)),
        ];
        if (SelectCyclePurificationAnchor(
                uniformDeck,
                Eligible,
                SelectKey) != null)
        {
            throw new InvalidOperationException(
                "候选集没有 deck clutter/size 改善时仍占用了 purification reserve。");
        }

        CycleStartupRetentionTestCandidate stableHigh =
            new(30, 1, Key(1, 0, 4, 10, 1, 30));
        CycleStartupRetentionTestCandidate stableLow =
            new(31, 1, Key(1, 0, 4, 10, 1, 29));
        foreach (CycleStartupRetentionTestCandidate[] order in
                 new[]
                 {
                     new[] { stableHigh, stableLow },
                     new[] { stableLow, stableHigh },
                 })
        {
            if (SelectCycleStartupBucketRepresentative(
                    order,
                    1,
                    Eligible,
                    SelectKey)?.Identity != stableLow.Identity)
            {
                throw new InvalidOperationException(
                    "循环 startup 完全同质候选没有按稳定 fingerprint 决胜。");
            }
        }
    }

    private static string SummarizePotionCandidates(IEnumerable<SearchNode> nodes)
    {
        string summary = string.Join(';', nodes
            .GroupBy(node => node.PotionCount)
            .OrderBy(group => group.Key)
            .Select(group =>
                $"{group.Key}:{group.Count()}:hp{group.Max(node => node.Snapshot.ProjectedPlayerHp)}:" +
                $"enemy{group.Min(node => node.Snapshot.EnemyHp)}"));
        return string.IsNullOrEmpty(summary) ? "-" : summary;
    }

    private static string SummarizeDiagnosticRoutes(
        IEnumerable<SearchNode> nodes,
        int limit)
    {
        string summary = string.Join(';', nodes
            .Take(limit)
            .Select(node =>
                $"{string.Join('>', node.Actions.Select(PolicyActionIdentityToken))}:" +
                $"score{node.Score:F0}:hp{node.Snapshot.ProjectedPlayerHp}:" +
                $"enemy{node.Snapshot.EnemyHp}:hand{node.Snapshot.HandCount}/" +
                $"{node.Snapshot.ReachableHandValue}/{node.Snapshot.ZeroCostPlayableCount}:" +
                $"traits{node.Traits}"));
        return string.IsNullOrEmpty(summary) ? "-" : summary;
    }

    private static string SummarizeOpeningLineages(IEnumerable<SearchNode> nodes)
    {
        string summary = string.Join(';', nodes
            .GroupBy(node => (
                node.PotionCount,
                FirstCardId: node.Actions.FirstOrDefault(action =>
                    action.Kind == PlanActionKind.PlayCard)?.CardId ?? "-"))
            .OrderBy(group => group.Key.PotionCount)
            .ThenBy(group => group.Key.FirstCardId, StringComparer.Ordinal)
            .Select(group =>
                $"p{group.Key.PotionCount}/{group.Key.FirstCardId}:{group.Count()}:" +
                $"hp{group.Max(node => node.Snapshot.ProjectedPlayerHp)}:" +
                $"setup{group.Max(node => node.Snapshot.StrategicEffects.RetentionValue)}:" +
                $"order{group.Max(node => node.Snapshot.ProjectedShuffleOrderValue)}"));
        return string.IsNullOrEmpty(summary) ? "-" : summary;
    }

    private static string SummarizePotionChoiceTargets(
        IEnumerable<SearchNode> nodes,
        string potionId)
    {
        string summary = string.Join(',', nodes
            .Select(node => node.Actions.LastOrDefault(action =>
                action.Kind == PlanActionKind.UsePotion
                && string.Equals(action.PotionId, potionId, StringComparison.Ordinal))?.Choice)
            .Where(choice => choice != null)
            .Select(choice => choice!.Cards.Count == 0
                ? "skip"
                : string.Join('+', choice.Cards.Select(card => card.CardId)))
            .GroupBy(cardIds => cardIds, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}:{group.Count()}"));
        return string.IsNullOrEmpty(summary) ? "-" : summary;
    }

    private static RoutingChoiceSignature? CurrentTurnRoutingChoice(SearchNode node)
        => BeamRetentionPolicy.CurrentTurnRoutingChoice(node);

    private StandPatEvaluation EvaluateStandPat(SearchNode node)
    {
        if (_run.StandPatCache.TryGetValue(node.StateKey, out StandPatEvaluation cached))
            return cached;
        _run.CheckpointPruneMetadata?.Invoke("stand_pat_single");
        _run.EnsurePruneMemory?.Invoke(StandPatProbeAllocationReserve());
        long allocatedBefore = OwnedSearchAllocatedBytes();
        long threadAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        StandPatEvaluation evaluation = ComputeStandPat(node);
        _run.StandPatBatchAllocatedBytes += Math.Max(
            0, OwnedSearchAllocatedBytes() - allocatedBefore);
        _run.StandPatProbeAllocatedHighWater = Math.Max(
            _run.StandPatProbeAllocatedHighWater,
            GC.GetAllocatedBytesForCurrentThread() - threadAllocatedBefore);
        _run.StandPatCache.Add(node.StateKey, evaluation);
        _run.StandPatProbes++;
        _run.CheckpointPruneMetadata?.Invoke("rank_after_stand_pat_single");
        return evaluation;
    }

    private StandPatEvaluation ComputeStandPat(SearchNode node)
    {
        SimulationSnapshot end = ReplayAction(node, new PlanAction(PlanActionKind.EndTurn, node.Turn));
        try
        {
            ObserveSearchPath(node, SearchPathObservationStage.StandPatProbe, "stand_pat_replayed");
            return new StandPatEvaluation(
                end.AllEnemiesDead,
                Math.Max(0, node.Snapshot.EnemyHp - end.EnemyHp),
                end.ProjectedPlayerHp,
                end.Energy * 16
                    + end.Stars * 8
                    + end.HandCount
                    + end.ReachableHandValue
                    + end.FutureResourceValue
                    + end.OstyHp * 16
                    + end.OstyMaxHp * 4);
        }
        finally
        {
            end.ReleaseSimulator();
        }
    }

    private static int PolicyBoundaryRank(SearchBoundaryReason reason)
        => reason switch
        {
            SearchBoundaryReason.None => 0,
            SearchBoundaryReason.NoCards or SearchBoundaryReason.Shuffle
                or SearchBoundaryReason.TurnLimit or SearchBoundaryReason.NodeLimit
                or SearchBoundaryReason.TimeLimit => 1,
            SearchBoundaryReason.PendingChoice => 2,
            SearchBoundaryReason.UnsupportedEffect => 3,
            SearchBoundaryReason.EventDefeat => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
        };

    private static string PolicyActionToken(PlanAction action)
        => action.Kind switch
        {
            PlanActionKind.PlayCard => action.Choice == null
                ? $"{action.Turn}:C:{action.CardId}"
                : $"{action.Turn}:C:{action.CardId}[{string.Join(',', action.Choice.Cards.Select(card => card.CardId))}]",
            PlanActionKind.UsePotion => action.Choice == null
                ? $"{action.Turn}:P:{action.PotionId}"
                : $"{action.Turn}:P:{action.PotionId}[{string.Join(',', action.Choice.Cards.Select(card => card.CardId))}]",
            PlanActionKind.EndTurn => action.TurnStartChoices is not { Count: > 0 }
                ? $"{action.Turn}:E"
                : $"{action.Turn}:E:" + string.Join(';', action.TurnStartChoices.Select(choice =>
                    $"{choice.SourceId}={string.Join(',', choice.Cards.Select(card => card.CardId))}")),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action.Kind, null),
        };

    private static string PolicyActionIdentityToken(PlanAction action)
    {
        string token = PolicyActionToken(action);
        if (action.Kind == PlanActionKind.PlayCard)
        {
            token += $"#card{action.CardOccurrence}/state{action.CardStateOccurrence}";
        }
        if (action.Choice != null)
            token += $"#primary={PolicyChoiceIdentityToken(action.Choice)}";
        if (action.NestedChoices is { Count: > 0 })
        {
            token += $"#nested_before={action.NestedChoicesBeforePrimary}:" +
                string.Join(',', action.NestedChoices.Select(PolicyChoiceIdentityToken));
        }
        return token;
    }

    private static string PolicyChoiceIdentityToken(PlanCardChoice choice)
        => $"{choice.Effect}[{string.Join(',', choice.Cards.Select(card =>
            $"{card.CardId}+{card.UpgradeLevel}@src{card.SourceOccurrence}/opt{card.OptionOccurrence}"))}]";

}
