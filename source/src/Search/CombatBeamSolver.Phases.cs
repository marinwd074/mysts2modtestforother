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

internal enum SearchStepStatus
{
    Yielded,
    Completed,
    Canceled,
    BudgetExhausted,
}

internal readonly record struct SearchWorkAllowance
{
    public SearchWorkAllowance(int maxParentCommits)
    {
        if (maxParentCommits <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxParentCommits));
        MaxParentCommits = maxParentCommits;
    }

    public int MaxParentCommits { get; }

    public static SearchWorkAllowance SingleParent { get; } = new(1);

    public static SearchWorkAllowance Unlimited { get; } = new(int.MaxValue);
}

internal readonly record struct SearchStepResult(
    SearchStepStatus Status,
    int CommittedParents,
    int TotalCommittedParents);

internal interface IResumableSearch : IDisposable
{
    SearchStepResult Step(SearchWorkAllowance allowance, CancellationToken token);
}

internal sealed partial class CombatBeamSolver
{
    private bool _executionSessionCreated;

    public SolverResult Solve()
    {
        using SearchMemberExecutionSession session = CreateExecutionSession();
        while (true)
        {
            SearchStepResult step = session.Step(
                SearchWorkAllowance.Unlimited,
                cancellationToken);
            if (step.Status == SearchStepStatus.Completed)
            {
                return session.Result
                    ?? throw new InvalidOperationException("搜索成员已完成但没有结果。");
            }
            if (step.Status == SearchStepStatus.Canceled)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            }
            if (step.Status == SearchStepStatus.BudgetExhausted)
            {
                throw new InvalidOperationException(
                    "成员级搜索会话不应在没有最终结果时报告 BudgetExhausted。");
            }
        }
    }

    internal SearchMemberExecutionSession CreateExecutionSession()
    {
        if (_executionSessionCreated)
            throw new InvalidOperationException("同一个 CombatBeamSolver 只能建立一个成员执行会话。");
        _executionSessionCreated = true;
        return new SearchMemberExecutionSession(
            this,
            policy.RequestWorkTotals,
            cancellationToken);
    }

    private void CompleteExecutionLifecycle(
        SearchRequestWorkTotals? requestWorkTotals,
        TimeSpan exclusiveElapsed,
        long exclusiveAllocatedBytes,
        int gen0Collections,
        int gen1Collections,
        int gen2Collections,
        TimeSpan gcPauseDuration)
    {
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] ROUTING_CHOICE_SUMMARIES scope=solver " +
            $"builds={_run.RoutingChoiceSummaryBuilds} hits={_run.RoutingChoiceSummaryHits} " +
            $"bypasses={_run.RoutingChoiceSummaryBypasses}");
        HookLayoutCacheStatistics hookLayouts = root.HookLayoutCacheStatistics;
        HookListenerSegmentStatistics hookSegments = root.HookListenerSegmentStatistics;
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] HOOK_LISTENER_SEGMENTS scope=root_cumulative " +
            $"prefix_reuses={hookSegments.PrefixReuses} prefix_builds={hookSegments.PrefixBuilds} " +
            $"split_builds={hookSegments.SplitBuilds} whole_builds={hookSegments.WholeBuilds} " +
            $"effective_prefix_reuses={hookSegments.EffectivePrefixReuses} " +
            $"effective_prefix_builds={hookSegments.EffectivePrefixBuilds}");
        policy.Diagnostics.Info(
            $"[CombatSolver/Test] HOOK_LAYOUT_CACHE scope=root_cumulative " +
            $"hits={hookLayouts.Hits} misses={hookLayouts.Misses} " +
            $"collisions={hookLayouts.Collisions} bypasses={hookLayouts.Bypasses}");
        if (_run.PotionStrategicCosts.Misses > 0)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] POTION_POLICY_LOOKUP " +
                $"hits={_run.PotionStrategicCosts.Hits} misses={_run.PotionStrategicCosts.Misses} " +
                $"entries={_run.PotionStrategicCosts.Count}");
        }
        if (requestWorkTotals != null)
        {
            requestWorkTotals.Record(new SearchSolverWorkContribution(
                _run.Expanded,
                _run.TransitionCount,
                _run.ChoiceBranchesEvaluated,
                exclusiveElapsed,
                Math.Max(0, exclusiveAllocatedBytes),
                Math.Max(0, gen0Collections),
                Math.Max(0, gen1Collections),
                Math.Max(0, gen2Collections),
                gcPauseDuration < TimeSpan.Zero ? TimeSpan.Zero : gcPauseDuration,
                _run.WorkPacer.MaxObservedGcPause));
        }
        CompleteSearchEfficiencyMember();
    }

    private IEnumerable<SearchStepResult> SolveCoreSteps(SearchMemberExecutionState execution)
    {
        SearchMemberExecutionState member = execution;
        cancellationToken.ThrowIfCancellationRequested();
        if (policy.VerifyIncrementalSearch)
            VerifyResumableParentExpansionSessionForTesting();
        if (policy.Diagnostics.PathObserver != null)
            _run.PathDiagnosticsSolverId = Guid.NewGuid();
        if (_minimumPotionUses < 0
            || _maximumPotionUses is { } maximumPotionUses
                && _minimumPotionUses > maximumPotionUses)
        {
            throw new ArgumentOutOfRangeException(
                nameof(_minimumPotionUses),
                "最少用药数必须非负且不能超过最多用药数。");
        }
        if (root.PlayerCount != 1 && !root.AllowsLocalPlayerOnlySearch)
            throw new NotSupportedException("第一版只支持单人战斗。");
        if (root.Enemies.Count > 64)
            throw new NotSupportedException("单场战斗超过 64 个敌人，无法编码路线存活位图。");
        PlayerTurnPhase requiredPhase = _includeTurnSetup
            ? PlayerTurnPhase.Start
            : PlayerTurnPhase.Play;
        if (root.CurrentSide != CombatSide.Player || root.PlayerPhase != requiredPhase)
        {
            throw new InvalidOperationException(
                _includeTurnSetup
                    ? "回合准备选牌搜索只能在玩家回合准备阶段计算。"
                    : "只能在玩家出牌阶段计算。");
        }

        int expansionParallelism = _detailedDiagnostics || policy.VerifyIncrementalSearch
            ? 1
            : Math.Clamp(
                policy.MaxDegreeOfParallelism,
                1,
                Math.Max(1, Environment.ProcessorCount));
        if (!policy.MemoryPressureSignal.IsEnabled
            && policy.MemoryPressureSignal.ConservativeParallelismRequired)
        {
            // A requested NoGC region that could not be established means system/runtime
            // headroom is already constrained. Do not allocate idle worker lanes above the
            // same two-way cap used by fallback parent/action/choice microbatches.
            expansionParallelism = Math.Min(2, expansionParallelism);
        }

        long allocatedBytesAtStart = GC.GetAllocatedBytesForCurrentThread();
        int gen0AtStart = GC.CollectionCount(0);
        int gen1AtStart = GC.CollectionCount(1);
        int gen2AtStart = GC.CollectionCount(2);
        TimeSpan gcPauseAtStart = GC.GetTotalPauseDuration();
        Stopwatch stopwatch = Stopwatch.StartNew();
        using ParallelExpansionExecutor? parallelExpansionExecutor = expansionParallelism > 1
            ? new ParallelExpansionExecutor(this, expansionParallelism)
            : null;
        long lastProgressMs = -100;
        member.LastRoutePreviewAt =
            System.Environment.TickCount64 - SolverWeights.ProgressUiIntervalMilliseconds;
        int initialHp = root.InitialPlayerHp;

        SolverInterimResult SummarizeCandidate(SearchNode node, bool won)
        {

            int ambergrisCount = node.Actions.Count(action =>
                action.Kind == PlanActionKind.UsePotion
                && string.Equals(action.PotionId, "AMBERGRIS", StringComparison.Ordinal));
            return new SolverInterimResult(
                Won: won,
                OutstandingStolenResource: node.Snapshot.OutstandingStolenResource,
                ProjectedBattleHpLost: battleDamage.HpLostSoFar
                    + node.Snapshot.CumulativePlayerHpLost,
                StrategicHpDeficit: ActEndingBossPolicy.StrategicHpDeficit(
                    node.Snapshot.CumulativePlayerHpLost,
                    Math.Max(0, root.InitialPlayerMaxHp - node.Snapshot.PlayerMaxHp),
                    node.Snapshot.RecoveredPlayerHp
                        + ActEndingBossPolicy.RankedPostCombatRelicHeal(
                            root.PostCombatRelicHeal, won, node.Snapshot.PlayerHp, node.Snapshot.PlayerMaxHp),
                    _strategicBossHpRelief,
                    node.Snapshot.DeathSaveHpRestored) - node.Snapshot.StrategicHpCredit,
                PotionStrategicCost: PotionUsePolicy.EffectiveStrategicHpCost(
                    node.PotionStrategicCost,
                    ambergrisCount,
                    root.InitialPlayerMaxHp),
                ProjectedBattlePotionCount: battleDamage.PotionsUsedSoFar + node.PotionCount,
                EnemyHp: node.Snapshot.EnemyHp,
                Score: node.Score,
                CombatEndedTurn: won ? node.Snapshot.CombatEndedTurn : null)
            {
                GrowthHpCredit = node.Snapshot.StrategyGoalHpCredit,
                TheftPolicy = _theftPolicy,
                GrowthRewardCount = node.Snapshot.StrategyGoalCount,
                Survives = !node.Snapshot.PlayerDead && node.Snapshot.ProjectedPlayerHp > 0,
                DeathSaveUseCount = node.Snapshot.ProjectedDeathSaveUseCount,
            };
        }



        IReadOnlyList<SolverFrontierTurn>? BuildFrontierTurns(SearchNode candidate)
        {
            if (candidate.ActionCount == 0)
                return null;
            Dictionary<int, (TurnOutcome Outcome, bool CombatEnded)> outcomesByTurn = [];
            for (SearchNode? node = candidate; node != null; node = node.Parent)
            {
                if (node.Outcome is { } outcome)
                    outcomesByTurn.TryAdd(outcome.Turn, (outcome, node.Snapshot.AllEnemiesDead));
            }
            if (outcomesByTurn.Count == 0)
                return null;

            List<SolverFrontierTurn> turns = new(outcomesByTurn.Count);
            foreach (IGrouping<int, PlanAction> actions in candidate.Actions.GroupBy(action => action.Turn))
            {
                if (!outcomesByTurn.TryGetValue(actions.Key, out var materialized))
                    continue;
                turns.Add(new SolverFrontierTurn(
                    actions.Key,
                    actions.Select(WithDisplayNames).ToArray(),
                    materialized.Outcome.HpLost,
                    materialized.Outcome.HpRecovered,
                    materialized.Outcome.EnemyHpLost,
                    materialized.Outcome.EnergyLeft,
                    materialized.CombatEnded)
                {
                    TurnStartChoices = TurnStartChoicePreviewPolicy.ChoicesForTurn(
                        actions.Key,
                        _startTurnNumber,
                        candidate.GetTurnSetupChoices(),
                        candidate.Actions)
                        .Select(WithDisplayNames)
                        .ToArray(),
                });
            }
            turns.Sort((a, b) => a.Turn.CompareTo(b.Turn));
            return turns.Count == 0 ? null : turns;
        }

        static bool FrontierTurnsEqual(
            IReadOnlyList<SolverFrontierTurn>? current,
            IReadOnlyList<SolverFrontierTurn>? next)
        {
            if (ReferenceEquals(current, next))
                return true;
            if (current == null || next == null || current.Count != next.Count)
                return false;
            for (int i = 0; i < current.Count; i++)
            {
                SolverFrontierTurn a = current[i];
                SolverFrontierTurn b = next[i];
                if (a.Turn != b.Turn || a.HpLost != b.HpLost || a.EnemyHpLost != b.EnemyHpLost
                    || a.EnergyLeft != b.EnergyLeft || a.CombatEnded != b.CombatEnded
                    || !a.Actions.SequenceEqual(b.Actions)
                    || !a.TurnStartChoices.SequenceEqual(b.TurnStartChoices))
                {
                    return false;
                }
            }
            return true;
        }

        SearchNode? FindCurrentTurnBoundary(SearchNode node)
        {
            for (SearchNode? current = node; current?.Parent != null; current = current.Parent)
            {
                if (current.Outcome?.Turn == _startTurnNumber)
                    return current;
            }
            return null;
        }

        int requiredPotionUses = Math.Max(_minimumPotionUses,
            _potionPolicy == SolverPotionPolicy.RequireAtLeastOne ? 1 : 0);
        int earlyStopPotionUses = policy.MinimumRequiredPotionUses(battleDamage.PotionsUsedSoFar);
        bool IsEligibleCompleteVictory(SearchNode node)
            => ExplicitPotionUseCount(node) >= requiredPotionUses
                && (!_enforcePotionDirectives
                    || _potionStrategy.EvaluateForcedUses(
                            node.Actions,
                            root.HasRenewablePotionShapedRock)
                        .AllForcedUsesSatisfied)
                && SolverInterimResultOrdering.IsCompleteVictory(
                    node.ActionCount,
                    node.Snapshot.AllEnemiesDead,
                    node.Snapshot.PlayerDead,
                    node.Snapshot.ProjectedPlayerHp);

        bool MeetsHpTarget(SearchNode node)
            => policy.GrowthTargetSatisfied(node.Snapshot.GrowthRewards)
                && policy.RelicTargetsSatisfied(node.Snapshot.RelicCounters)
                && TheftEncounterStrategy.RecoverySatisfied(_theftPolicy, node.Snapshot.OutstandingStolenResource)
                && IsEligibleCompleteVictory(node)
                && node.Snapshot.ProjectedDeathSaveUseCount == 0
                && ExplicitPotionUseCount(node) <= earlyStopPotionUses
                && battleDamage.HpLostSoFar + node.Snapshot.CumulativePlayerHpLost <= _acceptableBattleHpLoss;

        void ConsiderCompleteVictory(SearchNode node)
        {
            if (!IsEligibleCompleteVictory(node))
                return;

            EnsureCandidateOrigin(node);
            SolverInterimResult candidate = SummarizeCandidate(node, won: true);
            if (MeetsHpTarget(node))
            {
                member.AcceptableBattleHpLossReached = true;
                policy.Diagnostics.Info(
                    $"[CombatSolver/Test] ACCEPTABLE_BATTLE_HP_LOSS_REACHED " +
                    $"projected_battle_hp_lost={candidate.ProjectedBattleHpLost} " +
                    $"threshold={_acceptableBattleHpLoss} " +
                    $"turn={candidate.CombatEndedTurn?.ToString() ?? "-"}");
            }
            if (member.CurrentBestResult != null
                && !SolverInterimResultOrdering.IsBetter(candidate, member.CurrentBestResult))
            {
                return;
            }
            member.CurrentBestResult = candidate;
            member.CurrentBestNode = node;
        }

        void ConsiderCurrentTurnCandidate(SearchNode node)
        {
            SearchNode? boundary = FindCurrentTurnBoundary(node);
            if (boundary == null
                || ExplicitPotionUseCount(boundary) < _minimumPotionUses
                || _enforcePotionDirectives
                    && !_potionStrategy.EvaluateForcedUses(
                            boundary.Actions,
                            root.HasRenewablePotionShapedRock)
                        .AllForcedUsesSatisfied
                || !IsCurrentTurnCandidate(
                    boundary.ActionCount,
                    turnBoundaryReached: boundary.Action is { } boundaryAction
                        && (boundaryAction.Kind == PlanActionKind.EndTurn
                            || boundaryAction.EndsPlayerTurn
                            || boundary.Snapshot.AllEnemiesDead),
                    boundary.Snapshot.PlayerDead,
                    boundary.Snapshot.ProjectedPlayerHp))
            {
                return;
            }

            EnsureCandidateOrigin(boundary);
            SolverInterimResult candidate = SummarizeCandidate(
                boundary,
                boundary.Snapshot.AllEnemiesDead);
            if (member.CurrentTurnCandidateResult != null
                && !SolverInterimResultOrdering.IsBetter(candidate, member.CurrentTurnCandidateResult))
            {
                if (ReferenceEquals(boundary, member.CurrentTurnCandidateNode)
                    && node.Outcome is { } nodeOutcome
                    && nodeOutcome.Turn > (member.CurrentTurnPreviewNode?.Outcome?.Turn ?? int.MinValue))
                {
                    member.CurrentTurnPreviewNode = node;
                }
                return;
            }
            member.CurrentTurnCandidateResult = candidate;
            member.CurrentTurnCandidateNode = boundary;
            member.CurrentTurnPreviewNode = node;
        }

        bool HasFutureTurnActions(SearchNode? node)
            => node?.Actions.Any(action => action.Turn > _startTurnNumber) == true;

        void RefreshCurrentTurnPreview()
        {
            SearchNode? candidate = member.CurrentBestNode
                ?? member.CurrentTurnPreviewNode
                ?? member.CurrentTurnCandidateNode;
            SearchNode? boundary = candidate == null
                ? null
                : FindCurrentTurnBoundary(candidate);
            if (candidate == null || boundary?.Outcome is not { } outcome)
                return;
            PlanAction[] actions = candidate.Actions
                .Where(action => action.Turn == _startTurnNumber)
                .ToArray();
            bool combatEnded = boundary.Snapshot.AllEnemiesDead;
            IReadOnlyList<SolverFrontierTurn>? frontierTurns = BuildFrontierTurns(candidate);
            if (member.PublishedCurrentTurnActions != null
                && member.PublishedCurrentTurnActions.SequenceEqual(actions)
                && member.CurrentTurnPreview is { } published
                && published.HpLost == outcome.HpLost
                && published.EnemyHpLost == outcome.EnemyHpLost
                && published.EnergyLeft == outcome.EnergyLeft
                && published.CombatEnded == combatEnded
                && FrontierTurnsEqual(published.FrontierTurns, frontierTurns))
            {
                return;
            }

            member.PublishedCurrentTurnActions = actions;
            member.CurrentTurnPreview = new SolverCurrentTurnPreview(
                ++member.CurrentTurnPreviewVersion,
                _startTurnNumber,
                actions.Select(WithDisplayNames).ToArray(),
                outcome.HpLost,
                outcome.HpRecovered,
                outcome.EnemyHpLost,
                outcome.EnergyLeft,
                combatEnded,
                frontierTurns)
            {
                TurnStartChoices = candidate.GetTurnSetupChoices()
                    .Select(WithDisplayNames)
                    .ToArray(),
            };
        }



        SolverResult MaterializeSelectedRoute(
            FinalPlanSelection ordering,
            bool onlyDeathRoutesFound,
            SolverResultScope resultScope,
            int candidateSearchedTurnLayers,
            bool candidateTimeBudgetReached,
            bool candidateNodeBudgetReached,
            string evaluationContextId,
            IReadOnlyList<PlanAction>? routeAdoptionActions = null)
        {
            long e0MaterializationStarted = Stopwatch.GetTimestamp();
            long e0FinalReplayTicks = 0;
            SearchMeasurement finalMeasurement = _run.Performance.Begin();
            FinalPlanCandidate publishedCandidate = ordering.Candidate;
            EnsureCandidateOrigin(publishedCandidate.Node);
            SearchNode materializedNode = publishedCandidate.Node.Snapshot.HasSimulator
                ? publishedCandidate.Node
                : RefreshReleasedFallback(publishedCandidate.Node);
            PropagateCandidateOrigin(publishedCandidate.Node, materializedNode);
            RouteAnnotations materializedAnnotations = BuildRouteAnnotations(materializedNode);
            BlockPotionInsertion? blockPotionInsertion = TryInsertBlockPotion(
                materializedNode,
                materializedAnnotations,
                resultScope);
            if (blockPotionInsertion != null)
            {
                materializedNode.Snapshot.ReleaseSimulator();
                materializedNode = blockPotionInsertion.Node;
                materializedAnnotations = blockPotionInsertion.Annotations;
            }
            EnsureCandidateOrigin(materializedNode);
            RecordCandidateEvaluated(materializedNode, evaluationContextId);
            RecordCandidateSelected(materializedNode, evaluationContextId);
            FinalPlanCandidate selectedCandidate = publishedCandidate with
            {
                Node = materializedNode,
                Snapshot = materializedNode.Snapshot,
                Features = SearchFeatures.Capture(materializedNode),
                FutureSold = materializedNode.FutureSoldHp,
                BattleSold = battleDamage.SoldHpCommitted + materializedNode.FutureSoldHp,
                PotionCount = materializedNode.PotionCount,
                Score = blockPotionInsertion == null
                    ? publishedCandidate.Score
                    : materializedNode.Score,
            };
            int potionBranchesRejected = ordering.PotionBranchesRejected;
            int potionHpSaved = blockPotionInsertion?.HpSaved ?? ordering.PotionHpSaved;
            int potionHpRequired = blockPotionInsertion == null
                ? ordering.PotionHpRequired
                : SolverWeights.PotionMinimumHpSaved;
            int annotatedFutureSold = materializedAnnotations.SoldHpByTurn.Values.Sum();
            if (annotatedFutureSold != selectedCandidate.FutureSold)
            {
                throw new InvalidOperationException(
                    $"卖血路径状态不一致：节点累计 {selectedCandidate.FutureSold}，逐回合累计 {annotatedFutureSold}。");
            }
            SearchNode best = selectedCandidate.Node with { Score = selectedCandidate.Score };
            PropagateCandidateOrigin(selectedCandidate.Node, best);

            SimulationSnapshot finalSnapshot = selectedCandidate.Snapshot;
            RouteAnnotations annotations = materializedAnnotations;
            IReadOnlyList<CachedContinuation> continuations = BuildContinuations(best);
            int searchedTurns = Math.Max(1, best.Actions
                .Select(action => action.Turn)
                .DefaultIfEmpty(_startTurnNumber)
                .Max() - _startTurnNumber + 1);
            SearchBoundaryReason boundary = finalSnapshot.BoundaryReason;
            if (resultScope != SolverResultScope.RouteAdoption)
            {
                if (boundary == SearchBoundaryReason.None && candidateTimeBudgetReached)
                    boundary = SearchBoundaryReason.TimeLimit;
                else if (boundary == SearchBoundaryReason.None
                         && candidateNodeBudgetReached)
                    boundary = SearchBoundaryReason.NodeLimit;
                else if (boundary == SearchBoundaryReason.None
                         && policy.VerifyIncrementalSearch
                         && candidateSearchedTurnLayers >= SolverWeights.IncrementalVerificationMaxTurns)
                    boundary = SearchBoundaryReason.TurnLimit;
            }
            int futureHpLost = finalSnapshot.CumulativePlayerHpLost;
            int futureUnavoidableHpLost = annotations.HpLostByTurn.Sum(item =>
                Math.Max(0, item.Value - annotations.SoldHpByTurn.GetValueOrDefault(item.Key)));
            int battleUnavoidableHpLost = Math.Max(0, battleDamage.HpLostSoFar - battleDamage.SoldHpCommitted)
                + futureUnavoidableHpLost;
            ActionRelicTriggerRecorder relicTriggerRecorder = new();
            SearchReplayEvidence replayEvidence = new(best);
            SimulationSnapshot? annotationRoot = _includeTurnSetup
                ? ReplayTurnSetup(best.GetTurnSetupChoices())
                : null;
            SimulationSnapshot annotationReplay;
            bool replayFailed = true;
            long e0FinalReplayStarted = Stopwatch.GetTimestamp();
            try
            {
                annotationReplay = Replay(best.Actions, annotationRoot, _startTurnNumber,
                    priorActionCount: 0, triggerRecorder: relicTriggerRecorder, replayEvidence: replayEvidence);
                replayFailed = false;
            }
            finally
            {
                e0FinalReplayTicks += Math.Max(0, Stopwatch.GetTimestamp() - e0FinalReplayStarted);
                if (replayFailed)
                    replayEvidence.Publish(policy.Diagnostics,
                        cancellationToken.IsCancellationRequested ? "replay_cancelled" : "replay_failed", relicTriggerRecorder);
                annotationRoot?.ReleaseSimulator();
            }
            if (annotationReplay.StateKey != finalSnapshot.StateKey
                || annotationReplay.PlayerHp != finalSnapshot.PlayerHp
                || annotationReplay.EnemyHp != finalSnapshot.EnemyHp
                || annotationReplay.BoundaryReason != finalSnapshot.BoundaryReason)
            {
                ContinuationStamp expectedStamp = ContinuationStamp.CapturePredicted(
                    _player,
                    (CombatPredictionSimulator)finalSnapshot.Simulator,
                    finalSnapshot.Turn,
                    _forecast,
                    _startTurnNumber);
                ContinuationStamp replayStamp = ContinuationStamp.CapturePredicted(
                    _player,
                    (CombatPredictionSimulator)annotationReplay.Simulator,
                    annotationReplay.Turn,
                    _forecast,
                    _startTurnNumber);
                string difference = expectedStamp.DescribeFirstDifference(replayStamp);
                replayEvidence.Publish(policy.Diagnostics, "final_state_mismatch", relicTriggerRecorder,
                    expectedStamp.StateText, replayStamp.StateText);
                annotationReplay.ReleaseSimulator();
                throw new InvalidOperationException(
                    $"最终路线的遗物标注回放与选中状态不一致：{difference}；" +
                    $"hp={finalSnapshot.PlayerHp}/{annotationReplay.PlayerHp} " +
                    $"enemy_hp={finalSnapshot.EnemyHp}/{annotationReplay.EnemyHp} " +
                    $"boundary={finalSnapshot.BoundaryReason}/{annotationReplay.BoundaryReason}。");
            }
            RouteAnnotations replayAnnotations = BuildRouteAnnotations(best, relicTriggerRecorder);
            replayEvidence.Publish(policy.Diagnostics, "selected_route", relicTriggerRecorder);
            annotations = annotations with { KillsAfterAction = replayAnnotations.KillsAfterAction };
            annotationReplay.ReleaseSimulator();
            IReadOnlyList<PlanAction> annotatedActions = resultScope == SolverResultScope.RouteAdoption
                && routeAdoptionActions != null
                    ? routeAdoptionActions
                    : best.Actions
                        .Select((action, actionIndex) => WithDisplayNames(action) with
                        {
                            RelicEffects = relicTriggerRecorder.ForAction(actionIndex)
                                .Select(trigger => new PlanRelicEffect(
                                    trigger.RelicId,
                                    displayNames.Relic(trigger.RelicId),
                                    trigger.Summary))
                                .ToArray(),
                        })
                        .ToArray();
            _run.Performance.End(SearchMetricPhase.FinalSelection, finalMeasurement);
            _run.WorkPacer.ObserveGcPause();
            stopwatch.Stop();
            long workerAllocatedBytes =
                GC.GetAllocatedBytesForCurrentThread() - allocatedBytesAtStart
                + _run.OffThreadAllocatedBytes;
            int gen0Collections = GC.CollectionCount(0) - gen0AtStart;
            int gen1Collections = GC.CollectionCount(1) - gen1AtStart;
            int gen2Collections = GC.CollectionCount(2) - gen2AtStart;
            TimeSpan gcPauseDuration = GC.GetTotalPauseDuration() - gcPauseAtStart;
            // 必须在返回前把节点链和模拟器图压平成运行时真正需要的数据。
            // Coordinator 会在深化期间保留短搜结果；这里若返回 SearchNode/SimulationSnapshot，
            // 短搜的全部父链和每步模拟器都会成为长寿命 GC 根。
            SelectedSearchPlan selectedPlan = new(
                annotatedActions,
                best.ActionCount,
                best.Score);
            SolverSnapshot selectedSnapshot = new(
                finalSnapshot.HasRisk,
                finalSnapshot.PlayerDead,
                finalSnapshot.AllEnemiesDead,
                finalSnapshot.PlayerHp,
                finalSnapshot.PlayerMaxHp,
                finalSnapshot.CumulativePlayerHpLost,
                finalSnapshot.RecoveredPlayerHp,
                finalSnapshot.DeathSaveRelicHpRestored,
                finalSnapshot.LongTermResourceValue,
                finalSnapshot.AngerCopiesGenerated,
                finalSnapshot.ProjectedPlayerHp,
                finalSnapshot.PlayerBlock,
                finalSnapshot.EnemyHp,
                finalSnapshot.AliveEnemyCount,
                finalSnapshot.Energy,
                finalSnapshot.Stars,
                finalSnapshot.HandCount,
                finalSnapshot.OutstandingStolenResource,
                finalSnapshot.Turn,
                finalSnapshot.ShufflesCrossed,
                finalSnapshot.BoundaryReason,
                finalSnapshot.PredictionGaps.ToArray())
            {
                DeathSavePotionHpRestored = finalSnapshot.DeathSavePotionHpRestored,
                DeathSaveUseCount = finalSnapshot.DeathSaveUseCount,
                ProjectedDeathSaveUseCount = finalSnapshot.ProjectedDeathSaveUseCount,
                AllPlayersAlive = finalSnapshot.AllPlayersAlive,
                TeamCumulativeHpLost = finalSnapshot.TeamCumulativeHpLost,
                TeamLossRatio = finalSnapshot.TeamLossRatio,
                WorstPlayerLossRatio = finalSnapshot.WorstPlayerLossRatio,
                GrowthHpCredit = finalSnapshot.GrowthHpCredit,
                RelicCounters = finalSnapshot.RelicCounters,
                GrowthRewards = finalSnapshot.GrowthRewards,
                UnrecoveredGold = finalSnapshot.UnrecoveredGold,
                UnrecoveredCards = finalSnapshot.UnrecoveredCards,
            };
            ValidateOrderedMutationAdmissionLedger(_run);
            SolverResult result = new()
            {
                ResultScope = resultScope,
                SearchEfficiencyOrigin = TryGetCandidateOrigin(best),
                SearchEfficiencyEvaluationContextId = evaluationContextId,
                MultiplayerScope = policy.RoutePolicy switch
                {
                    SearchRoutePolicy.MultiplayerCurrentTurnOnly
                        => MultiplayerSearchResultScope.CurrentTurnOnly,
                    SearchRoutePolicy.MultiplayerLocalCrossTurn
                        => annotations.CombatEndedTurn.HasValue
                            ? MultiplayerSearchResultScope.CompleteLocalBattleProjection
                            : MultiplayerSearchResultScope.PartialLocalCrossTurnProjection,
                    _ => MultiplayerSearchResultScope.NotMultiplayer,
                },
                DeterministicBlockPotionInserted = blockPotionInsertion != null,
                TotalSearchElapsed = stopwatch.Elapsed,
                TotalWorkerAllocatedBytes = workerAllocatedBytes,
                TotalGen0Collections = gen0Collections,
                TotalGen1Collections = gen1Collections,
                TotalGen2Collections = gen2Collections,
                TotalGcPauseDuration = gcPauseDuration,
                ForkMetric = _run.Performance.Snapshot(SearchMetricPhase.Fork),
                ActionMetric = _run.Performance.Snapshot(SearchMetricPhase.Action),
                ExecutionChoiceResumeMetric = _run.Performance.Snapshot(SearchMetricPhase.ExecutionChoiceResume),
                CardExecutionMetric = _run.Performance.Snapshot(SearchMetricPhase.CardExecution),
                CardPostProcessingMetric = _run.Performance.Snapshot(SearchMetricPhase.CardPostProcessing),
                PotionExecutionMetric = _run.Performance.Snapshot(SearchMetricPhase.PotionExecution),
                RoundAdvanceMetric = _run.Performance.Snapshot(SearchMetricPhase.RoundAdvance),
                RoundPlayerEndMetric = _run.Performance.Snapshot(SearchMetricPhase.RoundPlayerEnd),
                RoundEndSimulationMetric = _run.Performance.Snapshot(SearchMetricPhase.RoundEndSimulation),
                RoundFlushMetric = _run.Performance.Snapshot(SearchMetricPhase.RoundFlush),
                RoundPlayerEndPowersMetric = _run.Performance.Snapshot(SearchMetricPhase.RoundPlayerEndPowers),
                RoundEnemyTurnMetric = _run.Performance.Snapshot(SearchMetricPhase.RoundEnemyTurn),
                RoundEnemyStartMetric = _run.Performance.Snapshot(SearchMetricPhase.RoundEnemyStart),
                RoundEnemyMovesMetric = _run.Performance.Snapshot(SearchMetricPhase.RoundEnemyMoves),
                RoundEnemyEndPowersMetric = _run.Performance.Snapshot(SearchMetricPhase.RoundEnemyEndPowers),
                RoundPlayerStartMetric = _run.Performance.Snapshot(SearchMetricPhase.RoundPlayerStart),
                RoundDrawMetric = _run.Performance.Snapshot(SearchMetricPhase.RoundDraw),
                SnapshotMetric = _run.Performance.Snapshot(SearchMetricPhase.Snapshot),
                ThreatProjectionMetric = _run.Performance.Snapshot(SearchMetricPhase.ThreatProjection),
                FingerprintMetric = _run.Performance.Snapshot(SearchMetricPhase.Fingerprint),
                ProjectedShuffleMetric = _run.Performance.Snapshot(SearchMetricPhase.ProjectedShuffle),
                PileFingerprintMetric = _run.Performance.Snapshot(SearchMetricPhase.PileFingerprint),
                PileFingerprintMissMetric = _run.Performance.Snapshot(SearchMetricPhase.PileFingerprintMiss),
                CardFingerprintMissMetric = _run.Performance.Snapshot(SearchMetricPhase.CardFingerprintMiss),
                CombatFingerprintMetric = _run.Performance.Snapshot(SearchMetricPhase.CombatFingerprint),
                PruneMetric = _run.Performance.Snapshot(SearchMetricPhase.Prune),
                FinalSelectionMetric = _run.Performance.Snapshot(SearchMetricPhase.FinalSelection),
                StartTurnNumber = _startTurnNumber,
                TurnSetupChoices = best.GetTurnSetupChoices().Select(WithDisplayNames).ToArray(),
                TurnSetupPlayState = best.GetTurnSetupPlayState(),
                BestNode = selectedPlan,
                MultiplayerReplayCandidates = blockPotionInsertion == null
                    ? ordering.ReplayCandidates
                    : [],
                Snapshot = selectedSnapshot,
                Forecast = _forecast,
                ExpandedNodes = _run.Expanded,
                TotalExpandedNodes = _run.Expanded,
                NoveltySearch = _run.Novelty?.Describe(),
                DominatedActionsPruned = _run.DominatedActionsPruned,
                TopQueueActionsDropped = _run.TopQueueActionsDropped,
                ActionAdmissionRepresentativesProtected = _run.ActionAdmissionRepresentativesProtected,
                DuplicateCardBranchesPruned = _run.DuplicateCardBranchesPruned,
                ChoiceBranchesEvaluated = _run.ChoiceBranchesEvaluated,
                TotalChoiceBranchesEvaluated = _run.ChoiceBranchesEvaluated,
                ChoiceReplayAttempts = _run.ChoiceReplayAttempts,
                ChoiceReplayBudgetExhaustions = _run.ChoiceReplayBudgetExhaustions,
                ChoiceBranchesDroppedByBudget = _run.ChoiceBranchesDroppedByBudget,
                ShuffleBranchesPruned = _run.ShuffleBranchesPruned,
                SoldHpBranchesPruned = _run.SoldHpBranchesPruned,
                HpInvestmentBranchesProtected = _run.HpInvestmentBranchesProtected,
                ReplayCount = _run.ReplayCount,
                ForkCount = _run.ForkCount,
                RoundReplayPrefixCaptures = _run.RoundReplayPrefixCaptures,
                RoundReplayPrefixReuses = _run.RoundReplayPrefixReuses,
                ExecutionChoiceCaptures = _run.ExecutionChoiceCaptures,
                ExecutionChoiceReuses = _run.ExecutionChoiceReuses,
                CardChoicePrefixAttempts = _run.CardChoicePrefixAttempts,
                CardChoicePrefixCaptures = _run.CardChoicePrefixCaptures,
                CardChoicePrefixReuses = _run.CardChoicePrefixReuses,
                CardChoicePrefixFallbacks = _run.CardChoicePrefixFallbacks,
                PotionChoicePrefixForks = _run.PotionChoicePrefixForks,
                PotionChoicePrefixCaptures = _run.PotionChoicePrefixCaptures,
                PotionChoicePrefixReuses = _run.PotionChoicePrefixReuses,
                PotionChoicePrefixFallbacks = _run.PotionChoicePrefixFallbacks,
                TransitionCount = _run.TransitionCount,
                TotalTransitionCount = _run.TransitionCount,
                ReusedNodeSnapshots = _run.ReusedNodeSnapshots,
                TranspositionBranchesPruned = _run.TranspositionBranchesPruned,
                RepeatableNoProgressBranchesPruned = _run.RepeatableNoProgressBranchesPruned,
                CycleShapesDetected = _run.CycleShapesDetected,
                CycleProbeContinuationsExpanded = _run.CycleProbeContinuationsExpanded,
                CycleCandidatesProtected = _run.CycleCandidatesProtected,
                CycleContinuationsStopped = _run.CycleContinuationsStopped,
                CycleRegionsDetected = _run.CycleRegionsDetected,
                CycleRegionCandidatesConsidered = _run.CycleRegionCandidatesConsidered,
                CycleRegionCandidatesAdmitted = _run.CycleRegionCandidatesAdmitted,
                CycleRegionCandidatesDropped = _run.CycleRegionCandidatesDropped,
                CycleRegionProgressEpochs = _run.CycleRegionProgressEpochs,
                CycleRegionProbeCandidatesAdmitted =
                    _run.CycleRegionProbeCandidatesAdmitted,
                CycleRegionProgressCandidatesAdmitted =
                    _run.CycleRegionProgressCandidatesAdmitted,
                CycleRegionMaxActionFamilies = _run.CycleRegionMaxActionFamilies,
                OrderedMutationCandidatesAdmitted =
                    _run.OrderedMutationPortfolioNodesConsumed,
                OrderedMutationLeaseExpiredBudget =
                    _run.OrderedMutationLeaseExpiredBudget,
                OrderedMutationOrdinaryFallbacks =
                    _run.OrderedMutationOrdinaryFallbacks,
                OrderedMutationColdAtomicCommitted =
                    _run.OrderedMutationColdAtomicCommitted,
                OrderedMutationColdAtomicRejected =
                    _run.OrderedMutationColdAtomicRejected,
                CrossTurnCandidatesProtected = _run.CrossTurnCandidatesProtected,
                CrossTurnContinuationsStopped = _run.CrossTurnContinuationsStopped,
                PrimaryIncumbentBranchesPruned = _run.PrimaryIncumbentBranchesPruned,
                PrimaryIncumbentUpdates = _run.PrimaryIncumbentUpdates,
                StandPatProbes = _run.StandPatProbes,
                ParallelExpansionWaves = _run.ParallelExpansionWaves,
                ParallelExpansionWorkItems = _run.ParallelExpansionWorkItems,
                MaxParallelExpansionConcurrency = _run.MaxParallelExpansionConcurrency,
                ParallelActionReplayWaves = _run.ParallelActionReplayWaves,
                ParallelActionReplayWorkItems = _run.ParallelActionReplayWorkItems,
                MaxParallelActionReplayConcurrency = _run.MaxParallelActionReplayConcurrency,
                DeferredRoundChoiceActions = _run.DeferredRoundChoiceActions,
                DeferredRoundChoiceLayerWidthTotal = _run.DeferredRoundChoiceLayerWidthTotal,
                MaxDeferredRoundChoiceLayerWidth = _run.MaxDeferredRoundChoiceLayerWidth,
                DeferredRoundChoiceFiniteQuotaFallbacks =
                    _run.DeferredRoundChoiceFiniteQuotaFallbacks,
                DeferredRoundChoiceFinitePrimaryLayers =
                    _run.DeferredRoundChoiceFinitePrimaryLayers,
                DeferredRoundChoiceFinitePendingFallbacks =
                    _run.DeferredRoundChoiceFinitePendingFallbacks,
                ParallelRoundChoiceReplayWaves = _run.ParallelRoundChoiceReplayWaves,
                ParallelRoundChoiceReplayWorkItems = _run.ParallelRoundChoiceReplayWorkItems,
                MaxParallelRoundChoiceReplayConcurrency =
                    _run.MaxParallelRoundChoiceReplayConcurrency,
                NodeLimitSnapshotsReleased = _run.NodeLimitSnapshotsReleased,
                TransitionCacheHits = 0,
                WorkerAllocatedBytes = workerAllocatedBytes,
                Gen0Collections = gen0Collections,
                Gen1Collections = gen1Collections,
                Gen2Collections = gen2Collections,
                GcPauseDuration = gcPauseDuration,
                MaxObservedGcPause = _run.WorkPacer.MaxObservedGcPause,
                WorkerYieldCount = _run.WorkPacer.YieldCount,
                FrameRecoveryWaitCount = _run.WorkPacer.FrameRecoveryWaitCount,
                FrameRecoveryWaitDuration = _run.WorkPacer.FrameRecoveryWaitDuration,
                SearchedTurns = searchedTurns,
                BoundaryReason = boundary,
                UnavoidableHpLost = battleUnavoidableHpLost,
                SoldHp = selectedCandidate.BattleSold,
                FutureSoldHp = selectedCandidate.FutureSold,
                BattleHpLostSoFar = battleDamage.HpLostSoFar,
                ProjectedBattleHpLost = battleDamage.HpLostSoFar + futureHpLost,
                BattlePotionsUsedSoFar = battleDamage.PotionsUsedSoFar,
                PotionCount = selectedCandidate.PotionCount,
                ExplicitPotionCount = annotatedActions.Count(action =>
                    action.Kind == PlanActionKind.UsePotion),
                PotionHpSaved = potionHpSaved,
                PotionHpRequired = potionHpRequired,
                PotionBranchesRejected = potionBranchesRejected,
                TheftPolicy = _theftPolicy,
                OutstandingStolenResource = finalSnapshot.OutstandingStolenResource,
                SoldHpByTurn = annotations.SoldHpByTurn,
                HpLostByTurn = annotations.HpLostByTurn,
                HpRecoveredByTurn = annotations.HpRecoveredByTurn,
                PostCombatRelicHeal = SolverInterimResultOrdering.IsCompleteVictory(
                    best.ActionCount,
                    finalSnapshot.AllEnemiesDead,
                    finalSnapshot.PlayerDead,
                    finalSnapshot.ProjectedPlayerHp)
                    ? root.PostCombatRelicHeal.HealFor(
                        finalSnapshot.PlayerHp,
                        finalSnapshot.PlayerMaxHp)
                    : 0,
                EnemyHpLostByTurn = annotations.EnemyHpLostByTurn,
                MaxBlockByTurn = annotations.MaxBlockByTurn,
                ActualBlockByTurn = annotations.ActualBlockByTurn,
                EnergyLeftByTurn = annotations.EnergyLeftByTurn,
                PotionCountByTurn = annotations.PotionCountByTurn,
                PotionStrategicCostByTurn = annotations.PotionStrategicCostByTurn,
                KillsAfterAction = annotations.KillsAfterAction,
                CombatEndedTurn = annotations.CombatEndedTurn,
                DeathTurn = annotations.DeathTurn,
                OnlyDeathRoutesFound = onlyDeathRoutesFound,
                IsActEndingBoss = _isActEndingBoss,
                BossHpRelief = _bossHpRelief,
                Elapsed = stopwatch.Elapsed,
                // Current-turn takeover is a deployment scope, not permission to discard a
                // trusted local cross-turn projection. CurrentTurnOnly and single-player
                // takeover still publish no future route; local-cross-turn takeover keeps only
                // continuations actually materialized from a future-bearing preview node.
                Continuations = resultScope == SolverResultScope.CurrentTurnAdoption
                    && policy.RoutePolicy != SearchRoutePolicy.MultiplayerLocalCrossTurn
                        ? []
                        : continuations,
            };
            finalSnapshot.ReleaseSimulator();
            policy.PortfolioTelemetry?.RecordExclusivePhase(
                _searchEfficiencyMemberId,
                "materialization_replay",
                e0FinalReplayTicks);
            RecordSearchEfficiencyPhase(
                "materialization",
                e0MaterializationStarted,
                e0FinalReplayTicks);
            return result;
        }

        SolverSpeculativeRoutePreview BuildRoutePreview(
            FinalPlanSelection selection,
            bool onlyDeathRoutesFound,
            int candidateVersion)
        {
            FinalPlanCandidate selected = selection.Candidate;
            RouteAnnotations annotations = BuildRouteAnnotations(selected.Node);
            List<SearchNode> path = [];
            for (SearchNode? node = selected.Node; node?.Parent != null; node = node.Parent)
                path.Add(node);
            path.Reverse();

            SolverFrontierTurn[] turns = path
                .GroupBy(node => node.Action!.Turn)
                .OrderBy(group => group.Key)
                .Select(group =>
                {
                    SearchNode[] nodes = group.ToArray();
                    SearchNode first = nodes[0];
                    SearchNode last = nodes[^1];
                    int hpLost = annotations.HpLostByTurn.TryGetValue(
                        group.Key,
                        out int annotatedHpLost)
                            ? annotatedHpLost
                            : Math.Max(
                                0,
                                last.Snapshot.CumulativePlayerHpLost
                                - first.Parent!.Snapshot.CumulativePlayerHpLost);
                    int enemyHpLost = annotations.EnemyHpLostByTurn.TryGetValue(
                        group.Key,
                        out int annotatedEnemyHpLost)
                            ? annotatedEnemyHpLost
                            : last.CumulativeEnemyHpLost - first.Parent!.CumulativeEnemyHpLost;
                    int energyLeft = annotations.EnergyLeftByTurn.TryGetValue(
                        group.Key,
                        out int annotatedEnergyLeft)
                            ? annotatedEnergyLeft
                            : last.Snapshot.Energy;
                    return new SolverFrontierTurn(
                        group.Key,
                        nodes.Select(node => WithDisplayNames(node.Action!)).ToArray(),
                        hpLost,
                        annotations.HpRecoveredByTurn.GetValueOrDefault(group.Key),
                        enemyHpLost,
                        energyLeft,
                        annotations.CombatEndedTurn == group.Key)
                    {
                        TurnStartChoices = TurnStartChoicePreviewPolicy.ChoicesForTurn(
                            group.Key,
                            _startTurnNumber,
                            selected.Node.GetTurnSetupChoices(),
                            selected.Node.Actions)
                            .Select(WithDisplayNames)
                            .ToArray(),
                    };
                })
                .ToArray();
            return new SolverSpeculativeRoutePreview(
                candidateVersion,
                _startTurnNumber,
                battleDamage.PotionsUsedSoFar + selected.Node.PotionCount,
                battleDamage.HpLostSoFar + selected.Snapshot.CumulativePlayerHpLost,
                annotations.CombatEndedTurn.HasValue,
                onlyDeathRoutesFound,
                selected.Snapshot.HasRisk,
                turns);
        }

        void PublishRoutePreview(
            IReadOnlyList<SearchNode> retained,
            IReadOnlyList<SearchNode>? additional = null,
            bool force = false)
        {
            if (progressCallback == null)
                return;
            long now = System.Environment.TickCount64;
            if (!force && now - member.LastRoutePreviewAt < SolverWeights.ProgressUiIntervalMilliseconds)
                return;
            IEnumerable<SearchNode> pool = additional == null
                ? retained
                : retained.Concat(additional);
            List<SearchNode> viable = pool
                .Where(node => node.ActionCount > 0 && node.Snapshot.HasSimulator)
                .DistinctBy(node => node.Snapshot)
                .ToList();
            if (viable.Count == 0)
                return;
            (SearchNode Node, int RetentionRank)[] savedRanks = viable
                .Select(node => (node, node.RetentionRank))
                .ToArray();
            List<SearchNode> candidates;
            try
            {
                candidates = Retention.RankFinal(viable);
            }
            finally
            {
                foreach ((SearchNode node, int retentionRank) in savedRanks)
                    node.RetentionRank = retentionRank;
            }
            foreach (SearchNode candidate in candidates)
                EnsureCandidateOrigin(candidate);
            List<(SearchNode Node, SimulationSnapshot Snapshot)> evaluated = candidates
                .Select(node => (Node: node, Snapshot: node.Snapshot))
                .ToList();
            string evaluationContextId = SearchEfficiencyEvaluationContextId(
                scenarioReevaluation: false,
                completion: "preview");
            FinalPlanSelection ordering;
            try
            {
                ordering = FinalOrdering.Select(
                    evaluated,
                    root.InitialPlayerHp,
                    emitDiagnostics: false);
            }
            catch (PotionPolicyUnsatisfiedException)
            {
                return;
            }
            RecordCandidateEvaluated(ordering.Candidate.Node, evaluationContextId);
            RecordCandidateSelected(ordering.Candidate.Node, evaluationContextId);
            member.SpeculativeRouteOrigin = EnsureCandidateOrigin(ordering.Candidate.Node);
            member.SpeculativeRouteEvaluationContextId = evaluationContextId;
            bool onlyDeathRoutesFound = evaluated.All(candidate =>
                candidate.Snapshot.PlayerDead || candidate.Snapshot.ProjectedPlayerHp <= 0);
            int candidateVersion = ++member.RoutePreviewVersion;
            member.SpeculativeRoutePreview = BuildRoutePreview(
                ordering,
                onlyDeathRoutesFound,
                candidateVersion);
            int candidateSearchedTurnLayers = member.SearchedTurnLayers;
            PlanAction[] adoptionActions = member.SpeculativeRoutePreview.Turns
                .SelectMany(turn => turn.Actions)
                .ToArray();
            member.RouteAdoptionSeed = new SolverRouteAdoptionSeed(
                candidateVersion,
                adoptionActions,
                () => MaterializeSelectedRoute(
                    ordering,
                    onlyDeathRoutesFound,
                    SolverResultScope.RouteAdoption,
                    candidateSearchedTurnLayers,
                    candidateTimeBudgetReached: false,
                    candidateNodeBudgetReached: false,
                    evaluationContextId,
                    routeAdoptionActions: adoptionActions));
            member.LastRoutePreviewAt = System.Environment.TickCount64;
        }

        void PublishProgress(
            int currentTurn,
            int completedTurns,
            int playDepth,
            int frontierNodes,
            int endedNodes,
            string phase,
            bool force = false)
        {
            long elapsedMs = stopwatch.ElapsedMilliseconds;
            if (!force && elapsedMs - lastProgressMs < 100)
                return;
            lastProgressMs = elapsedMs;
            if (progressCallback == null)
                return;
            RecordCandidatePublished(
                member.SpeculativeRouteOrigin,
                member.SpeculativeRouteEvaluationContextId);
            progressCallback(new SolverProgress(
                _startTurnNumber,
                currentTurn,
                completedTurns,
                playDepth,
                _run.Expanded,
                _run.Expanded,
                _totalExpandedNodeBudget,
                frontierNodes,
                endedNodes,
                elapsedMs,
                _progressPhaseOverride
                ?? $"搜索·{phase}",
                member.CurrentBestResult,
                member.CurrentTurnPreview,
                member.SpeculativeRoutePreview,
                member.RouteAdoptionSeed));
        }

        PublishProgress(_startTurnNumber, 0, 0, 1, 0, "初始化", force: true);
        IReadOnlyList<(IReadOnlyList<PlanCardChoice> Choices, SimulationSnapshot Snapshot)> rootCandidates =
            _includeTurnSetup
                ? BuildTurnSetupRoots()
                : [([], Replay([]))];
        if (rootCandidates.Count == 0)
            throw new InvalidOperationException("回合准备阶段没有生成可搜索状态。");
        _run.InitialPersistentBuffValue = _includeTurnSetup
            ? 0
            : rootCandidates[0].Snapshot.PersistentBuffValue;
        _run.InitialEnemyStrengthSuppression = _includeTurnSetup
            ? 0
            : rootCandidates[0].Snapshot.EnemyStrengthSuppression;
        _run.InitialEnemyWeakTurns = _includeTurnSetup
            ? 0
            : rootCandidates[0].Snapshot.EnemyWeakTurns;
        _run.InitialRetainedAttackValue = _includeTurnSetup
            ? 0
            : rootCandidates[0].Snapshot.RetainedAttackValue;
        member.Frontier = new List<SearchNode>(rootCandidates.Count);
        foreach ((IReadOnlyList<PlanCardChoice> choices, SimulationSnapshot snapshot) in rootCandidates)
        {
            ContinuationStamp? turnSetupPlayState = _includeTurnSetup
                ? ContinuationStamp.CapturePredicted(
                    _player,
                    snapshot.Simulator,
                    _startTurnNumber,
                    _forecast,
                    _startTurnNumber)
                : null;
            SearchNode root = new(
                null,
                0,
                snapshot.PotionUseCount,
                snapshot.PotionStrategicCost,
                _startTurnNumber,
                SearchRouteTraits.None,
                0,
                snapshot.Score,
                snapshot.StateKey,
                snapshot.HasRisk,
                snapshot.BoundaryReason,
                snapshot.PlayerDead
                    || snapshot.AllEnemiesDead
                    || snapshot.BoundaryReason != SearchBoundaryReason.None,
                null,
                snapshot,
                CombatProgressState.Capture(snapshot),
                TurnSetupChoices: choices,
                TurnSetupPlayState: turnSetupPlayState);
            // Setup roots are observed only after their existing choice budget selected them.
            // This hook does not claim coverage of the initial Start-phase choice enumeration.
            ObserveSearchPath(root, SearchPathObservationStage.Root,
                _includeTurnSetup ? "turn_setup_root_after_choice_budget" : "play_root");
            SearchNode? compatibleRoot = ApplyFixedPrefix(root);
            if (compatibleRoot == null)
                continue;
            root = compatibleRoot;
            member.Frontier.Add(root);
            if (_run.Transpositions.TryGetValue(root.StateKey, out TranspositionFrontier? existing))
                existing.TryAccept(new TranspositionLabel(
                    root.PotionCount,
                    root.PotionStrategicCost,
                    root.FutureSoldHp,
                    root.Snapshot.CumulativePlayerHpLost,
                    root.Snapshot.AllPlayersAlive,
                    root.Snapshot.TeamLossRatio,
                    root.Snapshot.WorstPlayerLossRatio,
                    root.ActionCount,
                    root.Score,
                    root.Traits,
                    root.HasNonPotionAction,
                    root.BoundaryReason,
                    root.Snapshot.PlayerDead,
                    root.Snapshot.AllEnemiesDead,
                    root.Snapshot.PredictionGaps,
                    root.CombatProgress));
            else
                _run.Transpositions.Add(
                    root.StateKey,
                    new TranspositionFrontier(new TranspositionLabel(
                        root.PotionCount,
                        root.PotionStrategicCost,
                        root.FutureSoldHp,
                        root.Snapshot.CumulativePlayerHpLost,
                        root.Snapshot.AllPlayersAlive,
                        root.Snapshot.TeamLossRatio,
                        root.Snapshot.WorstPlayerLossRatio,
                        root.ActionCount,
                        root.Score,
                        root.Traits,
                        root.HasNonPotionAction,
                        root.BoundaryReason,
                        root.Snapshot.PlayerDead,
                        root.Snapshot.AllEnemiesDead,
                        root.Snapshot.PredictionGaps,
                        root.CombatProgress)));
        }
        if (member.Frontier.Count == 0)
            throw new InvalidOperationException("固定搜索前缀与全部回合准备选牌分支都不相容。");

        member.Completed = [];
        member.Fallback = member.Frontier.MaxBy(static node => node.Score)!;
        member.PotionFreeBoundaryFallback = null;
        member.PotionFreeBoundaryFallbackScore = double.NegativeInfinity;
        member.PotionBoundaryFallback = null;
        member.PotionBoundaryFallbackScore = double.NegativeInfinity;
        // A cheap first parent is not a safe predictor for the rest of a later play depth.
        // Retain the largest observed parent for the whole search so a new depth cannot
        // immediately rematerialize a wide wave that exceeds the No-GC allocation budget.
        // Keep the cold estimate separate: after observing complete parents, it is
        // additional burst headroom for the wave, not a permanent per-parent floor.
        member.ParentAllocatedHighWater = 0;
        // Keep each metadata interval indivisible, with checkpoints only at drained
        // ranking/probe boundaries. Estimate it separately using measured probe bytes;
        // never require the sum of every transient probe to fit a single No-GC region.
        const long pruneAllocationFloorBytes = 64L * 1024 * 1024;
        member.PruneAllocatedBytesPerInputHighWater = 0;
        member.PruneHighWaterInterval = "cold";
        member.PruneHighWaterInputCount = 0;
        member.PruneHighWaterAllocatedBytes = 0;

        long ParentAllocationReserve()
            => SearchWaveMemoryPolicy.SingleParentReserve(member.ParentAllocatedHighWater);

        long PruneAllocationReserve(int inputCount)
            => PredictScaledPruneAllocationReserve(
                pruneAllocationFloorBytes,
                member.PruneAllocatedBytesPerInputHighWater,
                inputCount);

        void ObserveParentAllocation(long allocatedBytes)
        {
            if (allocatedBytes > member.ParentAllocatedHighWater)
                member.ParentAllocatedHighWater = allocatedBytes;
        }

        void ObservePruneAllocation(long allocatedBytes, int inputCount, string interval)
        {
            if (inputCount <= 0)
                return;
            long bytesPerInput = ObservePruneAllocationBytesPerInput(
                allocatedBytes,
                inputCount);
            if (bytesPerInput > member.PruneAllocatedBytesPerInputHighWater)
            {
                member.PruneAllocatedBytesPerInputHighWater = bytesPerInput;
                member.PruneHighWaterInterval = interval;
                member.PruneHighWaterInputCount = inputCount;
                member.PruneHighWaterAllocatedBytes = allocatedBytes;
            }
        }

        void ReclaimAtCommittedBoundary(
            string reason,
            int playDepth,
            int frontierNodes,
            int endedNodes)
        {
            SearchMemoryPressureSignal signal = policy.MemoryPressureSignal;
            long allocated = signal.AllocatedBytes;
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SEARCH_MEMORY_CHECKPOINT " +
                $"reason={reason} allocated={allocated} " +
                $"limit={signal.AllocationLimitBytes} " +
                $"projected_memory_load={signal.ProjectedMemoryLoadBytes} " +
                $"system_memory_limit={signal.SystemMemoryLimitBytes} " +
                $"system_pressure_dominates={signal.SystemPressureDominates.ToString().ToLowerInvariant()} " +
                $"parent_reserve={ParentAllocationReserve()} " +
                $"prune_floor={pruneAllocationFloorBytes} " +
                $"prune_bytes_per_input={member.PruneAllocatedBytesPerInputHighWater} " +
                $"prune_interval={member.PruneHighWaterInterval} prune_sample_inputs={member.PruneHighWaterInputCount} " +
                $"prune_sample_allocated={member.PruneHighWaterAllocatedBytes} " +
                $"expanded={_run.Expanded} " +
                $"turn_layer={member.SearchedTurnLayers} play_depth={playDepth}");
            PublishProgress(
                _startTurnNumber + member.SearchedTurnLayers,
                member.SearchedTurnLayers,
                playDepth,
                frontierNodes,
                endedNodes,
                "内存压力较高，正在整理内存",
                force: true);
            _run.ResetReclaimableCaches();
            parallelExpansionExecutor?.ResetRebuildableCaches();
            try
            {
                signal.ReclaimAndContinue(cancellationToken, reason);
            }
            finally
            {
                _run.WorkPacer.ObserveGcPause(signal.LastReclaimMaxObservedGcPause);
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SEARCH_MEMORY_RESUMED " +
                $"reason={reason} checkpoint={signal.ReclaimCount} " +
                $"frontier={frontierNodes} ended={endedNodes} expanded={_run.Expanded} " +
                $"turn_layer={member.SearchedTurnLayers} play_depth={playDepth}");
            PublishProgress(
                _startTurnNumber + member.SearchedTurnLayers,
                member.SearchedTurnLayers,
                playDepth,
                frontierNodes,
                endedNodes,
                "继续搜索",
                force: true);
        }

        bool EnsureMemoryForNextCommit(
            long reservedBytes,
            string reason,
            int playDepth,
            int frontierNodes,
            int endedNodes)
        {
            SearchMemoryPressureSignal signal = policy.MemoryPressureSignal;
            if (policy.VerifyIncrementalSearch)
                return true;

            signal.TryRecoverNoGc(reservedBytes, cancellationToken);
            bool reclaimAttempted = false;
            if (signal.HasUnexpectedNoGcLoss())
            {
                ReclaimAtCommittedBoundary(
                    "unexpected_no_gc_loss",
                    playDepth,
                    frontierNodes,
                    endedNodes);
                reclaimAttempted = true;
                signal.TryRecoverNoGc(reservedBytes, cancellationToken);
            }

            MemoryCommitPreparation preparation = ResolveMemoryCommitPreparation(
                signal.IsEnabled,
                reservedBytes,
                signal.AllocationLimitBytes,
                signal.RemainingBytes,
                signal.AllocatedBytes,
                reclaimAttempted);
            if (preparation == MemoryCommitPreparation.Reclaim)
            {
                ReclaimAtCommittedBoundary(reason, playDepth, frontierNodes, endedNodes);
                preparation = ResolveMemoryCommitPreparation(
                    signal.IsEnabled,
                    reservedBytes,
                    signal.AllocationLimitBytes,
                    signal.RemainingBytes,
                    signal.AllocatedBytes,
                    reclaimAttempted: true);
            }
            return preparation == MemoryCommitPreparation.Ready;
        }

        void EnsureMemoryForIndivisibleCommit(
            long reservedBytes,
            string reason,
            int playDepth,
            int frontierNodes,
            int endedNodes)
        {
            if (EnsureMemoryForNextCommit(
                    reservedBytes,
                    reason,
                    playDepth,
                    frontierNodes,
                    endedNodes))
            {
                return;
            }

            SearchMemoryPressureSignal signal = policy.MemoryPressureSignal;
            if (!signal.IsEnabled)
                return;
            bool systemHeadroomConstrained = signal.SystemPressureDominates;
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SEARCH_MEMORY_DEFAULT_GC_FALLBACK " +
                $"reason={reason} reserved={reservedBytes} " +
                $"allocated={signal.AllocatedBytes} limit={signal.AllocationLimitBytes} " +
                $"remaining={signal.RemainingBytes} " +
                $"system_pressure_dominates={systemHeadroomConstrained.ToString().ToLowerInvariant()} " +
                $"expanded={_run.Expanded} turn_layer={member.SearchedTurnLayers} play_depth={playDepth}");
            PublishProgress(
                _startTurnNumber + member.SearchedTurnLayers,
                member.SearchedTurnLayers,
                playDepth,
                frontierNodes,
                endedNodes,
                "单次内存需求超出 NoGC 余量，正在切换常规 GC",
                force: true);
            _run.ResetReclaimableCaches();
            parallelExpansionExecutor?.ResetRebuildableCaches();
            signal.UseDefaultGcAndContinue(cancellationToken);
            if (signal.IsEnabled)
            {
                throw new InvalidOperationException(
                    "不可分割的搜索提交在 NoGC 回退后仍受分配上限约束。");
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SEARCH_MEMORY_RESUMED " +
                $"reason={reason}_default_gc checkpoint={signal.ReclaimCount} " +
                $"frontier={frontierNodes} ended={endedNodes} expanded={_run.Expanded} " +
                $"turn_layer={member.SearchedTurnLayers} play_depth={playDepth}");
            PublishProgress(
                _startTurnNumber + member.SearchedTurnLayers,
                member.SearchedTurnLayers,
                playDepth,
                frontierNodes,
                endedNodes,
                "已切换常规 GC，继续搜索",
                force: true);
        }
        List<SearchNode> PruneAtMemoryBoundary(
            IEnumerable<SearchNode> nodes,
            int inputCount,
            string reason,
            int playDepth,
            int endedNodes)
        {
            if (_run.EnsurePruneMemory != null)
                throw new InvalidOperationException("剪枝内存边界不能嵌套。");
            long nonProbeReserve = PruneAllocationReserve(inputCount);
            if (inputCount > 0)
                EnsureMemoryForIndivisibleCommit(
                    nonProbeReserve, reason, playDepth, inputCount, endedNodes);
            _run.EnsurePruneMemory = probeReserve => EnsureMemoryForIndivisibleCommit(
                probeReserve,
                "within_prune_stand_pat", playDepth, inputCount, endedNodes);
            // Region counters reset at a checkpoint, while process samples also include
            // unrelated threads and allocation quanta. Use coordinator + merged lane bytes,
            // then subtract the probe intervals measured with the same ownership scope.
            long allocatedBefore = OwnedSearchAllocatedBytes();
            long probesBefore = _run.StandPatBatchAllocatedBytes;
            string metadataInterval = "global_rank";
            void ObserveMetadataInterval()
            {
                long allocated = OwnedSearchAllocatedBytes() - allocatedBefore;
                long probes = _run.StandPatBatchAllocatedBytes - probesBefore;
                ObservePruneAllocation(Math.Max(0, allocated - probes), inputCount, metadataInterval);
            }
            _run.CheckpointPruneMetadata = nextInterval =>
            {
                ObserveMetadataInterval();
                metadataInterval = nextInterval;
                // Use the same conservative high-water policy for each independently drained
                // interval. Do not reserve the sum of global ranking and the metadata tail.
                long metadataReserve = PruneAllocationReserve(inputCount);
                if (inputCount > 0)
                    EnsureMemoryForIndivisibleCommit(
                        metadataReserve,
                        "within_prune_metadata", playDepth, inputCount, endedNodes);
                allocatedBefore = OwnedSearchAllocatedBytes();
                probesBefore = _run.StandPatBatchAllocatedBytes;
            };
            try
            {
                return Prune(nodes);
            }
            finally
            {
                ObserveMetadataInterval();
                _run.CheckpointPruneMetadata = null;
                _run.EnsurePruneMemory = null;
            }
        }

        int reservedTurnLayers = policy.CurrentTurnOnly
            ? 1
            : root.EncounterRoomType == RoomType.Boss
                ? SolverWeights.BossEnemyStrengthSuppressionHorizon
                : SolverWeights.StandardEnemyStrengthSuppressionHorizon;

        if (policy.NoveltySearch != null)
        {
            long noveltyParentAllocatedAtStart = 0;
            bool BeforeNoveltyParent(SearchNode node, int openCount, int completedCount)
            {
                SearchTakeoverRequest? request = _interaction?.CurrentTakeoverRequest;
                if (request?.Kind == SearchTakeoverKind.AdoptRoute && request.RouteAdoptionSeed != null)
                {
                    member.RequestedRouteAdoptionSeed = request.RouteAdoptionSeed;
                    return false;
                }
                if (request?.Kind == SearchTakeoverKind.ApplyCurrentTurn
                    && (member.CurrentBestNode != null || member.CurrentTurnCandidateNode != null))
                {
                    member.AdoptionReached = true;
                    member.CurrentTurnAdoptionReached = member.CurrentBestNode == null;
                    return false;
                }
                EnsureMemoryForIndivisibleCommit(ParentAllocationReserve(),
                    "before_novelty_parent", node.ActionCount, openCount + 1, completedCount);
                noveltyParentAllocatedAtStart = policy.MemoryPressureSignal.AllocatedBytes;
                return true;
            }
            void ObserveNoveltyBoundary(SearchNode node)
            {
                ConsiderCompleteVictory(node);
                ConsiderCurrentTurnCandidate(node);
            }
            void AfterNoveltyParent(SearchNode node, int openCount, int completedCount)
            {
                ObserveParentAllocation(Math.Max(0,
                    policy.MemoryPressureSignal.AllocatedBytes - noveltyParentAllocatedAtStart));
                if (progressCallback != null && stopwatch.ElapsedMilliseconds - lastProgressMs >= 100)
                {
                    RefreshCurrentTurnPreview();
                    PublishRoutePreview(member.Completed);
                }
                PublishProgress(node.Turn, Math.Max(0, node.Turn - _startTurnNumber),
                    node.ActionCount, openCount, completedCount, "探索不同路线");
            }
            SearchNode noveltyFallback = member.Fallback;
            member.TimeBudgetReached = RunNoveltyOpen(
                member.Frontier,
                member.Completed,
                stopwatch,
                ref noveltyFallback,
                MeetsHpTarget,
                BeforeNoveltyParent,
                AfterNoveltyParent,
                ObserveNoveltyBoundary,
                out bool noveltyAcceptableBattleHpLossReached,
                out int noveltySearchedTurnLayers);
            member.Fallback = noveltyFallback;
            member.AcceptableBattleHpLossReached = noveltyAcceptableBattleHpLossReached;
            member.SearchedTurnLayers = noveltySearchedTurnLayers;
        }

        while (member.Frontier.Count > 0
            && (!policy.VerifyIncrementalSearch
                || member.SearchedTurnLayers < SolverWeights.IncrementalVerificationMaxTurns)
            && _run.Expanded < _profile.MaxExpandedNodes
            && !member.TimeBudgetReached)
        {
            cancellationToken.ThrowIfCancellationRequested();
            member.Active = member.Frontier.Where(node => !node.IsTerminal).ToList();
            foreach (SearchNode terminal in member.Frontier.Where(node => node.IsTerminal))
                member.Completed.Add(terminal);
            if (member.Active.Count == 0)
            {
                List<SearchNode> rankedCompleted = Retention.RankFinal(member.Completed);
                ReleaseDroppedSnapshots(member.Completed, rankedCompleted);
                member.Completed = rankedCompleted;
                break;
            }

            member.Ended = [];
            member.TurnLayerStartedMs = stopwatch.ElapsedMilliseconds;
            int remainingReservedLayers = Math.Max(1, reservedTurnLayers - member.SearchedTurnLayers);
            long remainingSearchMs = Math.Max(
                1,
                _profile.SoftTimeBudgetMilliseconds - member.TurnLayerStartedMs);
            member.TurnLayerBudgetMs = Math.Max(250, remainingSearchMs / remainingReservedLayers);
            // 节点预算按回合层分配，口径和上面的时间预算一样。
            //
            // 以前只有时间按层分，节点是全局的，于是一个回合层可以合法地把整份节点预算吃光：
            // 手牌 0 费一类的引擎（干瘪之手、真言转神格、抽牌循环）在同一个回合里能一直出牌，
            // 每一步都真的产出一点资源，所以「无进展」那套判据永远不触发。实测一个包里回合层
            // 停在 2、play_depth 从 173 涨到 248、member.Ended 逼近 10 万，整份节点预算烧完也没推进到
            // 下一回合；期间分配到 16 GB，机器换页，主线程再没恢复过来。
            //
            // 时间那一侧之所以没兜住：deep 软预算 180 秒、保留 4 层，一层能分到 90 秒，而节点
            // 上限早在那之前就到了，for 循环直接退出、走不到下面这个切层分支。
            member.TurnLayerStartedExpanded = _run.Expanded;
            int remainingExpandedNodes = Math.Max(
                1,
                _profile.MaxExpandedNodes - member.TurnLayerStartedExpanded);
            member.TurnLayerNodeBudget = Math.Max(
                SolverWeights.MinimumTurnLayerExpandedNodes,
                remainingExpandedNodes / remainingReservedLayers);
            PublishProgress(member.Active.Min(node => node.Turn), member.SearchedTurnLayers, 0, member.Active.Count, 0,
                "展开回合", force: true);
            for (member.PlayDepth = 0;
                 member.Active.Count > 0 && _run.Expanded < _profile.MaxExpandedNodes;
                 member.PlayDepth++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BeginCyclePlanningLayer();
                SearchTakeoverRequest? takeover = _interaction?.CurrentTakeoverRequest;
                if (takeover?.Kind == SearchTakeoverKind.AdoptRoute
                    && takeover.RouteAdoptionSeed != null)
                {
                    member.RequestedRouteAdoptionSeed = takeover.RouteAdoptionSeed;
                    member.InterruptedActive = member.Active;
                    member.TimeBudgetReached = true;
                    break;
                }
                SearchNode? adoptableNode = member.CurrentBestNode ?? member.CurrentTurnCandidateNode;
                if (takeover?.Kind == SearchTakeoverKind.ApplyCurrentTurn && adoptableNode != null)
                {
                    member.AdoptionReached = true;
                    member.CurrentTurnAdoptionReached = member.CurrentBestNode == null;
                    member.TimeBudgetReached = true;
                    break;
                }
                long turnLayerElapsedMs = stopwatch.ElapsedMilliseconds - member.TurnLayerStartedMs;
                int turnLayerExpanded = _run.Expanded - member.TurnLayerStartedExpanded;
                // Boss setup chains use the existing per-layer node share. A local wall-clock
                // slice otherwise cuts different action depths under JIT/GC load, even when
                // the request has ample time left. The global time and node limits still apply.
                bool turnLayerTimeSpent = !policy.Act3BossStrategy
                    && turnLayerElapsedMs >= member.TurnLayerBudgetMs;
                bool turnLayerNodesSpent = turnLayerExpanded >= member.TurnLayerNodeBudget;
                if (!policy.VerifyIncrementalSearch
                    && member.SearchedTurnLayers < reservedTurnLayers - 1
                    && member.PlayDepth > 0
                    && member.Ended.Count > 0
                    && (turnLayerTimeSpent || turnLayerNodesSpent))
                {
                    int forcedEndTurnCandidates = 0;
                    foreach (SearchNode node in member.Active)
                    {
                        foreach (SearchNode endNode in BuildAcceptedEndTurnNodes(node))
                        {
                            member.Ended.Add(endNode);
                            forcedEndTurnCandidates++;
                        }
                        node.Snapshot.ReleaseSimulator();
                    }
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] TURN_LAYER_BUDGET " +
                        $"reason={(turnLayerTimeSpent ? "time" : "nodes")} " +
                        $"completed_turns={member.SearchedTurnLayers} play_depth={member.PlayDepth} " +
                        $"elapsed_ms={turnLayerElapsedMs} budget_ms={member.TurnLayerBudgetMs} " +
                        $"expanded={turnLayerExpanded} node_budget={member.TurnLayerNodeBudget} " +
                        $"forced_end_turn={forcedEndTurnCandidates}");
                    member.Active = [];
                    break;
                }
                if (!policy.VerifyIncrementalSearch
                    && (policy.MemoryPressureSignal.HasUnexpectedNoGcLoss()
                        || policy.MemoryPressureSignal.IsLimitReached()))
                {
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] SEARCH_MEMORY_CHECKPOINT " +
                        $"allocated={policy.MemoryPressureSignal.AllocatedBytes} " +
                        $"limit={policy.MemoryPressureSignal.AllocationLimitBytes} " +
                        $"projected_memory_load={policy.MemoryPressureSignal.ProjectedMemoryLoadBytes} " +
                        $"system_memory_limit={policy.MemoryPressureSignal.SystemMemoryLimitBytes} " +
                        $"system_pressure_dominates={policy.MemoryPressureSignal.SystemPressureDominates.ToString().ToLowerInvariant()} " +
                        $"expanded={_run.Expanded} turn_layer={member.SearchedTurnLayers} play_depth={member.PlayDepth}");
                    PublishProgress(
                        _startTurnNumber + member.SearchedTurnLayers,
                        member.SearchedTurnLayers,
                        member.PlayDepth,
                        member.Active.Count,
                        member.Ended.Count,
                        "内存压力较高，正在整理内存",
                        force: true);
                    // Preserve coordinator transposition order across a GC-only safe point.
                    // Rebuilding it from a mid-search subset would change which later branches win.
                    _run.ResetReclaimableCaches();
                    parallelExpansionExecutor?.ResetRebuildableCaches();
                    try
                    {
                        policy.MemoryPressureSignal.ReclaimAndContinue(cancellationToken, "before_play_depth");
                    }
                    finally
                    {
                        _run.WorkPacer.ObserveGcPause(
                            policy.MemoryPressureSignal.LastReclaimMaxObservedGcPause);
                    }
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] SEARCH_MEMORY_RESUMED " +
                        $"checkpoint={policy.MemoryPressureSignal.ReclaimCount} " +
                        $"frontier={member.Active.Count} ended={member.Ended.Count} expanded={_run.Expanded} " +
                        $"turn_layer={member.SearchedTurnLayers} play_depth={member.PlayDepth}");
                    PublishProgress(
                        _startTurnNumber + member.SearchedTurnLayers,
                        member.SearchedTurnLayers,
                        member.PlayDepth,
                        member.Active.Count,
                        member.Ended.Count,
                        "继续搜索",
                        force: true);
                }
                if (!policy.VerifyIncrementalSearch
                    && member.PlayDepth > 0
                    && stopwatch.ElapsedMilliseconds >= _profile.SoftTimeBudgetMilliseconds)
                {
                    member.TimeBudgetReached = true;
                    int forcedEndTurnCandidates = 0;
                    foreach (SearchNode node in member.Active)
                    {
                        foreach (SearchNode endNode in BuildAcceptedEndTurnNodes(node))
                        {
                            member.Ended.Add(endNode);
                            forcedEndTurnCandidates++;
                        }
                        node.Snapshot.ReleaseSimulator();
                    }
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Test] SEARCH_TIME_BUDGET " +
                        $"completed_turns={member.SearchedTurnLayers} play_depth={member.PlayDepth} " +
                        $"elapsed_ms={stopwatch.ElapsedMilliseconds} " +
                        $"budget_ms={_profile.SoftTimeBudgetMilliseconds} " +
                        $"forced_end_turn={forcedEndTurnCandidates}");
                    member.Active = [];
                    break;
                }
                member.NextPlays = [];
                void AcceptExpandedChild(SearchNode node, SearchNode child)
                {
                    ObserveSearchPath(child, SearchPathObservationStage.ActionAdmitted, "expansion_commit");
                    if (child.Score > member.Fallback.Score)
                        member.Fallback = child;
                    if (child.IsTerminal || child.Turn > node.Turn)
                    {
                        int explicitPotionUses = ExplicitPotionUseCount(child);
                        if (explicitPotionUses == 0 && child.Score > member.PotionFreeBoundaryFallbackScore)
                        {
                            member.PotionFreeBoundaryFallback = child;
                            member.PotionFreeBoundaryFallbackScore = child.Score;
                        }
                        else if (explicitPotionUses > 0 && child.Score > member.PotionBoundaryFallbackScore)
                        {
                            member.PotionBoundaryFallback = child;
                            member.PotionBoundaryFallbackScore = child.Score;
                        }
                        member.Ended.Add(child);
                        member.AcceptableBattleHpLossReached |= MeetsHpTarget(child);
                    }
                    else
                        member.NextPlays.Add(child);
                }

                void FinishExpandedParent(SearchNode node)
                {
                    node.Snapshot.ReleaseSimulator();
                    PublishProgress(node.Turn, member.SearchedTurnLayers, member.PlayDepth, member.Active.Count + member.NextPlays.Count,
                        member.Ended.Count, "展开出牌序列");
                }

                void ReleaseNodeLimitSnapshot(SearchNode node)
                {
                    if (!node.Snapshot.HasSimulator)
                        return;
                    node.Snapshot.ReleaseSimulator();
                    _run.NodeLimitSnapshotsReleased++;
                }

                member.ActiveIndex = 0;
                int maximumQueuedParents = parallelExpansionExecutor?.MaximumQueuedParents
                    ?? expansionParallelism;
                int parallelWaveCapacity = policy.MemoryPressureSignal.ConservativeParallelismRequired
                    ? Math.Min(2, expansionParallelism)
                    : maximumQueuedParents;

                long ParallelWaveAllocationReserve(int parentCount)
                    => SearchWaveMemoryPolicy.ParentWaveReserve(member.ParentAllocatedHighWater, parentCount);

                int MemorySafeParallelWaveCapacity(int desiredCapacity)
                {
                    SearchMemoryPressureSignal signal = policy.MemoryPressureSignal;
                    if (!signal.IsEnabled)
                    {
                        return signal.ConservativeParallelismRequired
                            ? Math.Min(2, desiredCapacity)
                            : desiredCapacity;
                    }
                    return SearchWaveMemoryPolicy.ParentWaveCapacity(
                        desiredCapacity, member.ParentAllocatedHighWater, signal.RemainingBytes);
                }

                void ReclaimAfterCommittedWork(string reason)
                {
                    SearchMemoryPressureSignal signal = policy.MemoryPressureSignal;
                    bool hasMoreParents = member.ActiveIndex < member.Active.Count
                        && !member.AcceptableBattleHpLossReached
                        && _run.Expanded < _profile.MaxExpandedNodes;
                    // With no further parent admission, prune first. Even an unexpected region
                    // exit can be handled at that smaller graph before the next search layer.
                    if (!hasMoreParents)
                        return;
                    if (!policy.VerifyIncrementalSearch && signal.HasUnexpectedNoGcLoss())
                    {
                        ReclaimAtCommittedBoundary(
                            "unexpected_no_gc_loss",
                            member.PlayDepth,
                            Math.Max(0, member.Active.Count - member.ActiveIndex) + member.NextPlays.Count,
                            member.Ended.Count);
                        return;
                    }
                    if (policy.VerifyIncrementalSearch || !signal.IsEnabled)
                        return;
                    long reserve = ParentAllocationReserve();
                    bool reserveCanEverFit = reserve <= signal.AllocationLimitBytes;
                    if (signal.IsLimitReached()
                        || (reserveCanEverFit && !signal.CanReachCommit(reserve)))
                    {
                        ReclaimAtCommittedBoundary(
                            reason,
                            member.PlayDepth,
                            Math.Max(0, member.Active.Count - member.ActiveIndex) + member.NextPlays.Count,
                            member.Ended.Count);
                    }
                }

                void ExpandNextSerially()
                {
                    SearchMemoryPressureSignal signal = policy.MemoryPressureSignal;
                    EnsureMemoryForIndivisibleCommit(
                        ParentAllocationReserve(),
                        "before_serial_parent",
                        member.PlayDepth,
                        Math.Max(0, member.Active.Count - member.ActiveIndex) + member.NextPlays.Count,
                        member.Ended.Count);
                    long allocatedBefore = signal.AllocatedBytes;
                    SearchNode node = member.Active[member.ActiveIndex];
                    foreach (SearchNode child in Expand(node))
                    {
                        AcceptExpandedChild(node, child);
                        if (_run.Expanded >= _profile.MaxExpandedNodes)
                            break;
                    }
                    FinishExpandedParent(node);
                    member.ActiveIndex++;
                    ObserveParentAllocation(Math.Max(0, signal.AllocatedBytes - allocatedBefore));
                    ReclaimAfterCommittedWork("after_serial_parent");
                }

                if (expansionParallelism == 1)
                {
                    while (member.ActiveIndex < member.Active.Count
                           && !member.AcceptableBattleHpLossReached
                           && _run.Expanded < _profile.MaxExpandedNodes)
                    {
                        member.StepCancellationToken.ThrowIfCancellationRequested();
                        ExpandNextSerially();
                        if (member.ObserveCommittedParents(1))
                        {
                            yield return new SearchStepResult(
                                SearchStepStatus.Yielded,
                                member.CommittedParentsInCurrentStep,
                                member.TotalCommittedParents);
                        }
                    }
                }
                else
                {
                    while (member.ActiveIndex < member.Active.Count
                           && !member.AcceptableBattleHpLossReached
                           && _run.Expanded < _profile.MaxExpandedNodes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int remainingBudget = _profile.MaxExpandedNodes - _run.Expanded;
                        if (remainingBudget <= 1)
                        {
                            // The legacy iterator intentionally yields only the first child from the
                            // final budget slot. Keep that edge case out of the materialized worker path.
                            while (member.ActiveIndex < member.Active.Count)
                            {
                                member.StepCancellationToken.ThrowIfCancellationRequested();
                                ExpandNextSerially();
                                if (member.ObserveCommittedParents(1))
                                {
                                    yield return new SearchStepResult(
                                        SearchStepStatus.Yielded,
                                        member.CommittedParentsInCurrentStep,
                                        member.TotalCommittedParents);
                                }
                                if (_run.Expanded >= _profile.MaxExpandedNodes)
                                    break;
                            }
                            break;
                        }
                        int desiredCapacity = Math.Min(
                            parallelWaveCapacity,
                            Math.Min(remainingBudget - 1, member.Active.Count - member.ActiveIndex));
                        desiredCapacity = Math.Min(
                            desiredCapacity,
                            member.RemainingParentCommitsBeforeYield);
                        // Reserve only one parent before considering a reclaim. A smaller wave
                        // can use the tail of this region without collecting a full candidate graph.
                        bool materializedParentFits = EnsureMemoryForNextCommit(
                            ParentAllocationReserve(),
                            "before_parallel_parent",
                            member.PlayDepth,
                            Math.Max(0, member.Active.Count - member.ActiveIndex) + member.NextPlays.Count,
                            member.Ended.Count);
                        if (!materializedParentFits)
                        {
                            member.StepCancellationToken.ThrowIfCancellationRequested();
                            ExpandNextSerially();
                            if (member.ObserveCommittedParents(1))
                            {
                                yield return new SearchStepResult(
                                    SearchStepStatus.Yielded,
                                    member.CommittedParentsInCurrentStep,
                                    member.TotalCommittedParents);
                            }
                            continue;
                        }
                        int acceptedCapacity = MemorySafeParallelWaveCapacity(desiredCapacity);
                        if (acceptedCapacity == 0)
                        {
                            // Other process threads can allocate between the admission samples.
                            // The serial path rechecks one parent at its own committed boundary.
                            member.StepCancellationToken.ThrowIfCancellationRequested();
                            ExpandNextSerially();
                            if (member.ObserveCommittedParents(1))
                            {
                                yield return new SearchStepResult(
                                    SearchStepStatus.Yielded,
                                    member.CommittedParentsInCurrentStep,
                                    member.TotalCommittedParents);
                            }
                            continue;
                        }

                        List<(SearchNode Node, int WorkerIndex)> entries = [];
                        List<SearchNode> workerNodes = new(acceptedCapacity);
                        while (member.ActiveIndex < member.Active.Count
                               && workerNodes.Count < acceptedCapacity
                               && entries.Count < member.RemainingParentCommitsBeforeYield)
                        {
                            SearchNode node = member.Active[member.ActiveIndex];
                            member.ActiveIndex++;
                            int workerIndex = -1;
                            if (TryPrepareParallelExpansion(node))
                            {
                                workerIndex = workerNodes.Count;
                                workerNodes.Add(node);
                            }
                            entries.Add((node, workerIndex));
                        }

                        ExpansionWorkerOutcome[]? outcomes = null;
                        int finishedEntryCount = 0;
                        int rawCandidateCount = 0;
                        long waveAllocatedBefore = policy.MemoryPressureSignal.AllocatedBytes;
                        long waveRemainingBefore = policy.MemoryPressureSignal.RemainingBytes;
                        TimeSpan wavePauseBefore = GC.GetTotalPauseDuration();
                        try
                        {
                            outcomes = parallelExpansionExecutor!.Evaluate(
                                workerNodes,
                                commitOrdered: (workerIndex, batch) =>
                                {
                                    rawCandidateCount += batch.Cards.Count + batch.Potions.Count + batch.EndTurns.Count;
                                    while (entries[finishedEntryCount].WorkerIndex < 0)
                                    {
                                        FinishExpandedParent(entries[finishedEntryCount].Node);
                                        finishedEntryCount++;
                                    }
                                    (SearchNode node, int expectedWorker) = entries[finishedEntryCount];
                                    if (expectedWorker != workerIndex)
                                        throw new InvalidOperationException("并行展开提交顺序与父节点顺序不一致。");
                                    CommitExpansionBatch(
                                        node,
                                        batch,
                                        child => AcceptExpandedChild(node, child));
                                    batch.Dispose();
                                    FinishExpandedParent(node);
                                    finishedEntryCount++;
                                });
                            while (finishedEntryCount < entries.Count)
                            {
                                FinishExpandedParent(entries[finishedEntryCount].Node);
                                finishedEntryCount++;
                            }
                        }
                        finally
                        {
                            if (outcomes != null)
                            {
                                foreach (ExpansionWorkerOutcome outcome in outcomes)
                                    outcome.Batch?.Dispose();
                            }
                            for (int index = finishedEntryCount; index < entries.Count; index++)
                                entries[index].Node.Snapshot.ReleaseSimulator();
                        }
                        long waveAllocated = Math.Max(
                            0,
                            policy.MemoryPressureSignal.AllocatedBytes - waveAllocatedBefore);
                        long reservedWaveBytes = ParallelWaveAllocationReserve(workerNodes.Count);
                        bool waveStayedWithinReserve = waveAllocated <= reservedWaveBytes;
                        if (workerNodes.Count > 0)
                            ObserveParentAllocation(waveAllocated / workerNodes.Count);
                        if (outcomes != null)
                        {
                            foreach (ExpansionWorkerOutcome outcome in outcomes)
                                ObserveParentAllocation(outcome.AllocatedBytes);
                        }
                        if (workerNodes.Count > 1)
                        {
                            // Multiplicative increase / multiplicative decrease. Collapsing straight
                            // back to two lanes after a single heavy wave left most of the user's
                            // requested lanes idle for the following waves.
                            parallelWaveCapacity = waveStayedWithinReserve
                                ? SearchWaveMemoryPolicy.GrowCapacity(
                                    parallelWaveCapacity,
                                    maximumQueuedParents)
                                : Math.Max(
                                    Math.Min(2, expansionParallelism),
                                    parallelWaveCapacity / 2);
                        }
                        if (policy.MeasurePhasePerformance)
                        {
                            policy.Diagnostics.Info(
                                $"[CombatSolver/Test] SEARCH_WAVE_MEMORY " +
                                $"turn_layer={member.SearchedTurnLayers} play_depth={member.PlayDepth} " +
                                $"desired_parents={desiredCapacity} admitted_parents={workerNodes.Count} " +
                                $"signal_enabled={policy.MemoryPressureSignal.IsEnabled.ToString().ToLowerInvariant()} " +
                                $"remaining_before={waveRemainingBefore} process_allocated_bytes={waveAllocated} " +
                                $"reserved_bytes={reservedWaveBytes} raw_candidates={rawCandidateCount} " +
                                $"pending_plays={member.NextPlays.Count} ended={member.Ended.Count} " +
                                $"gc_pause_ms={(GC.GetTotalPauseDuration() - wavePauseBefore).TotalMilliseconds:F3}");
                        }
                        ReclaimAfterCommittedWork("after_parallel_wave");
                        if (member.ObserveCommittedParents(entries.Count))
                        {
                            yield return new SearchStepResult(
                                SearchStepStatus.Yielded,
                                member.CommittedParentsInCurrentStep,
                                member.TotalCommittedParents);
                        }
                    }
                }
                for (; member.ActiveIndex < member.Active.Count; member.ActiveIndex++)
                {
                    if (member.AcceptableBattleHpLossReached)
                        member.Active[member.ActiveIndex].Snapshot.ReleaseSimulator();
                    else
                        ReleaseNodeLimitSnapshot(member.Active[member.ActiveIndex]);
                }
                if (policy.Act3BossStrategy && member.SearchedTurnLayers == 0 && !member.AcceptableBattleHpLossReached)
                {
                    // Settle a fetched, payable power's next decision before ranking the
                    // intermediate selection. Expand all legal successors using the normal
                    // transposition and node accounting; ordinary alternatives remain legal.
                    SearchNode[] commitments = member.NextPlays.Where(HasPlayableFetchedPower).ToArray();
                    foreach (SearchNode commitment in commitments)
                    {
                        if (_run.Expanded >= _profile.MaxExpandedNodes || member.AcceptableBattleHpLossReached
                            || !policy.VerifyIncrementalSearch
                                && stopwatch.ElapsedMilliseconds >= _profile.SoftTimeBudgetMilliseconds)
                            break;
                        EnsureMemoryForIndivisibleCommit(ParentAllocationReserve(),
                            "before_fetched_power_followup", member.PlayDepth, member.NextPlays.Count, member.Ended.Count);
                        long allocatedBefore = policy.MemoryPressureSignal.AllocatedBytes;
                        foreach (SearchNode successor in Expand(commitment))
                        {
                            AcceptExpandedChild(commitment, successor);
                            if (_run.Expanded >= _profile.MaxExpandedNodes || member.AcceptableBattleHpLossReached)
                                break;
                        }
                        member.NextPlays.Remove(commitment);
                        commitment.Snapshot.ReleaseSimulator();
                        ObserveParentAllocation(Math.Max(0, policy.MemoryPressureSignal.AllocatedBytes - allocatedBefore));
                        ReclaimAfterCommittedWork("after_fetched_power_followup");
                    }
                }
                if (member.AcceptableBattleHpLossReached)
                {
                    foreach (SearchNode pending in member.NextPlays)
                        pending.Snapshot.ReleaseSimulator();
                    member.Active = [];
                    break;
                }
                List<SearchNode> prunedPlays = PruneAtMemoryBoundary(
                    member.NextPlays, member.NextPlays.Count, "before_play_prune", member.PlayDepth, member.Ended.Count);
                ReleaseDroppedSnapshots(member.NextPlays, prunedPlays);
                member.NextPlays.Clear();
                member.Active = prunedPlays;
                member.ActiveIndex = 0;
                if (!policy.VerifyIncrementalSearch
                    && member.Active.Count > 0
                    && _run.Expanded < _profile.MaxExpandedNodes
                    && (policy.MemoryPressureSignal.HasUnexpectedNoGcLoss()
                        || policy.MemoryPressureSignal.IsLimitReached()))
                    ReclaimAtCommittedBoundary("after_prune", member.PlayDepth, member.Active.Count, member.Ended.Count);
                PublishRoutePreview(member.Completed, member.Active);
                if (_detailedDiagnostics && member.SearchedTurnLayers == 0)
                {
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Debug] ROOT_DEPTH_POTIONS depth={member.PlayDepth + 1} " +
                        $"frontier={SummarizePotionCandidates(member.Active)} " +
                        $"routes={SummarizeDiagnosticRoutes(member.Active, 24)}");
                }
                PublishProgress(_startTurnNumber + member.SearchedTurnLayers, member.SearchedTurnLayers, member.PlayDepth,
                    member.Active.Count, member.Ended.Count, "剪枝候选", force: true);
            }
            if (member.AdoptionReached || member.RequestedRouteAdoptionSeed != null)
                break;

            if (_run.Expanded >= _profile.MaxExpandedNodes)
            {
                foreach (SearchNode node in member.Active)
                {
                    if (!node.Snapshot.HasSimulator)
                        continue;
                    node.Snapshot.ReleaseSimulator();
                    _run.NodeLimitSnapshotsReleased++;
                }
                member.Active = [];
            }

            List<SearchNode> unannotatedEnded = member.Ended;
            member.Ended = AnnotateTurnOutcomes(unannotatedEnded);
            ReleaseDroppedSnapshots(unannotatedEnded, member.Ended);

            List<SearchNode> completedCandidates =
                [.. member.Completed, .. member.Ended.Where(node => node.IsTerminal)];
            if (member.AcceptableBattleHpLossReached)
            {
                List<SearchNode> reached = completedCandidates.Where(MeetsHpTarget).ToList();
                ReleaseDroppedSnapshots(completedCandidates, reached);
                completedCandidates = reached;
            }
            List<SearchNode> rankedCompletedCandidates = Retention.RankFinal(completedCandidates);
            ReleaseDroppedSnapshots(completedCandidates, rankedCompletedCandidates);
            member.Completed = rankedCompletedCandidates;
            // Only complete victories can tighten this incumbent, and every terminal candidate
            // is already represented by `member.Completed`. Publish the new bound before turn pruning
            // so ordered/cycle-region ledgers commit exactly once against the actual member.Frontier.
            _ = TightenPrimarySearchIncumbentAtTurnLayer(
                member.Completed,
                member.SearchedTurnLayers + 1);
            int turnPruneCandidateCount = 0;
            foreach (SearchNode candidate in member.Ended)
            {
                if (!candidate.IsTerminal)
                    turnPruneCandidateCount++;
            }
            member.Frontier = member.AcceptableBattleHpLossReached
                ? []
                : PruneAtMemoryBoundary(member.Ended.Where(node => !node.IsTerminal),
                    turnPruneCandidateCount, "before_turn_prune", playDepth: 0, member.Ended.Count);
            foreach (SearchNode node in member.Frontier)
                CaptureContinuation(node);
            List<SearchNode> retainedAfterRound = [.. member.Completed, .. member.Frontier];
            ReleaseDroppedSnapshots(member.Ended, retainedAfterRound);
            foreach (SearchNode candidate in retainedAfterRound)
            {
                ConsiderCompleteVictory(candidate);
                ConsiderCurrentTurnCandidate(candidate);
            }
            RefreshCurrentTurnPreview();
            PublishRoutePreview(retainedAfterRound, force: true);
            member.SearchedTurnLayers++;
            if (_detailedDiagnostics)
            {
                policy.Diagnostics.Info(
                    $"[CombatSolver/Debug] TURN_LAYER_POTIONS completed_turns={member.SearchedTurnLayers} " +
                    $"frontier={SummarizePotionCandidates(member.Frontier)} " +
                    $"completed={SummarizePotionCandidates(member.Completed)} " +
                    $"opening_lineages={SummarizeOpeningLineages(member.Frontier)} " +
                    $"frontier_routes={SummarizeDiagnosticRoutes(member.Frontier, 24)} " +
                    $"touch_choices={SummarizePotionChoiceTargets(member.Frontier, "TOUCH_OF_INSANITY")}");
                if (member.SearchedTurnLayers == 2)
                {
                    foreach (SearchNode candidate in member.Frontier.Where(node => node.PotionCount == 2))
                    {
                        policy.Diagnostics.Info(
                            $"[CombatSolver/Debug] TURN2_DUAL_POTION hp={candidate.Snapshot.PlayerHp} " +
                            $"projected_hp={candidate.Snapshot.ProjectedPlayerHp} " +
                            $"enemy_hp={candidate.Snapshot.EnemyHp} " +
                            $"actions={string.Join(',', candidate.Actions.Select(PolicyActionToken))}");
                    }
                }
            }
            PublishProgress(_startTurnNumber + member.SearchedTurnLayers, member.SearchedTurnLayers, 0,
                member.Frontier.Count, member.Completed.Count, "回合层完成", force: true);
            if (policy.CurrentTurnOnly)
            {
                // Current-turn multiplayer routes are useful as advice even when no
                // complete-victory route exists. Stop after this layer so a future
                // teammate turn can never enter the immutable local search result.
                member.AdoptionReached = true;
                member.CurrentTurnAdoptionReached = true;
                member.TimeBudgetReached = true;
                break;
            }
            SearchTakeoverRequest? layerTakeover = _interaction?.CurrentTakeoverRequest;
            if (layerTakeover?.Kind == SearchTakeoverKind.AdoptRoute
                && layerTakeover.RouteAdoptionSeed != null)
            {
                member.RequestedRouteAdoptionSeed = layerTakeover.RouteAdoptionSeed;
                member.TimeBudgetReached = true;
                break;
            }
            if (layerTakeover?.Kind == SearchTakeoverKind.ApplyCurrentTurn
                && (member.CurrentBestNode != null || member.CurrentTurnCandidateNode != null))
            {
                member.AdoptionReached = true;
                member.CurrentTurnAdoptionReached = member.CurrentBestNode == null;
                member.TimeBudgetReached = true;
                break;
            }
            if (member.AcceptableBattleHpLossReached)
            {
                foreach (SearchNode node in member.Frontier)
                    node.Snapshot.ReleaseSimulator();
                member.Frontier = [];
                break;
            }
            if (!_hasGrowthTargets && member.Completed.Any(node =>
                    node.Snapshot.AllEnemiesDead
                    && ExplicitPotionUseCount(node) == 0
                    && node.FutureSoldHp == 0
                    && node.Snapshot.CumulativePlayerHpLost == 0
                    && node.Snapshot.PlayerMaxHp >= root.InitialPlayerMaxHp
                    // Zero damage is only provably best once there is nothing left to heal. A wounded
                    // player holding a heal can still end the fight strictly higher, so stopping here
                    // would discard the better route before it is ever expanded.
                    && node.Snapshot.PlayerHp >= node.Snapshot.PlayerMaxHp))
            {
                foreach (SearchNode node in member.Frontier)
                    node.Snapshot.ReleaseSimulator();
                member.Frontier = [];
                break;
            }
        }

        if (member.RequestedRouteAdoptionSeed != null)
        {
            foreach (SearchNode candidate in member.Completed)
                candidate.Snapshot.ReleaseSimulator();
            foreach (SearchNode candidate in member.Frontier)
                candidate.Snapshot.ReleaseSimulator();
            if (member.InterruptedActive != null)
            {
                foreach (SearchNode candidate in member.InterruptedActive)
                    candidate.Snapshot.ReleaseSimulator();
            }
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SEARCH_ROUTE_ADOPTION_CHECKPOINT " +
                $"candidate_version={member.RequestedRouteAdoptionSeed.CandidateVersion} " +
                $"expanded={_run.Expanded}");
            execution.Result = member.RequestedRouteAdoptionSeed.Materialize();
            yield break;
        }

        List<SearchNode> finalPool;
        bool retainCrossTurnTakeoverRoute = member.CurrentTurnAdoptionReached
            && policy.RoutePolicy == SearchRoutePolicy.MultiplayerLocalCrossTurn
            && HasFutureTurnActions(member.CurrentTurnPreviewNode);
        SearchNode? adoptedNode = policy.CurrentTurnOnly
            ? member.CurrentTurnCandidateNode ?? member.CurrentBestNode
            : member.CurrentBestNode
                ?? (retainCrossTurnTakeoverRoute
                    ? member.CurrentTurnPreviewNode
                    : member.CurrentTurnCandidateNode);
        if (member.AdoptionReached && adoptedNode != null)
        {
            SearchNode adopted = RefreshReleasedFallback(adoptedNode);
            List<SearchNode> remaining = [.. member.Completed, .. member.Frontier];
            ReleaseDroppedSnapshots(remaining, [adopted]);
            finalPool = [adopted];
            SolverInterimResult adoptedSummary = policy.CurrentTurnOnly
                ? member.CurrentTurnCandidateResult ?? member.CurrentBestResult!
                : member.CurrentBestResult ?? member.CurrentTurnCandidateResult!;
            policy.Diagnostics.Info(
                $"[CombatSolver/Test] SEARCH_CHECKPOINT_ADOPTED " +
                $"scope={(member.CurrentTurnAdoptionReached ? "current_turn" : "complete_victory")} " +
                $"route_candidate={(retainCrossTurnTakeoverRoute ? "future_preview" : "current_turn_candidate")} " +
                $"deployment_actions={adopted.Actions.Count(action => action.Turn == _startTurnNumber)} " +
                $"future_actions={adopted.Actions.Count(action => action.Turn > _startTurnNumber)} " +
                $"potions={adoptedSummary.ProjectedBattlePotionCount} " +
                $"projected_battle_hp_lost={adoptedSummary.ProjectedBattleHpLost} " +
                $"expanded={_run.Expanded}");
        }
        else
        {
            finalPool = member.Completed.Count == 0 && member.Frontier.Count == 0
                ? [RefreshReleasedFallback(member.Fallback)]
                : [.. member.Completed, .. member.Frontier];
        }
        if (!member.AdoptionReached
            && !finalPool.Any(node => ExplicitPotionUseCount(node) == 0)
            && member.PotionFreeBoundaryFallback != null)
        {
            finalPool.Add(RefreshReleasedFallback(member.PotionFreeBoundaryFallback));
        }
        if (!member.AdoptionReached
            && !finalPool.Any(node => ExplicitPotionUseCount(node) > 0)
            && member.PotionBoundaryFallback != null)
        {
            finalPool.Add(RefreshReleasedFallback(member.PotionBoundaryFallback));
        }
        if (member.AcceptableBattleHpLossReached)
        {
            List<SearchNode> reached = finalPool.Where(MeetsHpTarget).ToList();
            ReleaseDroppedSnapshots(finalPool, reached);
            finalPool = reached;
        }
        List<SearchNode> finalCandidates = Retention.RankFinal(finalPool);
        ReleaseDroppedSnapshots(finalPool, finalCandidates);
        ValidateHistoricalSimulatorsReleased(finalCandidates);
        PublishProgress(_startTurnNumber + member.SearchedTurnLayers, member.SearchedTurnLayers, 0,
            finalCandidates.Count, member.Completed.Count, "复核最终候选", force: true);
        foreach (SearchNode candidate in finalCandidates)
            EnsureCandidateOrigin(candidate);
        List<(SearchNode Node, SimulationSnapshot Snapshot)> evaluated = finalCandidates
            .Select(node => (Node: node, Snapshot: node.Snapshot))
            .ToList();
        bool onlyDeathRoutesFound = evaluated.All(candidate =>
            candidate.Snapshot.PlayerDead || candidate.Snapshot.ProjectedPlayerHp <= 0);
        _run.ReusedNodeSnapshots += evaluated.Count;
        // Freeze the main-search stop reason before U3 spends its reserved reevaluation work.
        // Whether the main search exhausted its allocation must not depend on how much of the
        // separate U3 reserve the final candidate matrix later consumes.
        bool nodeBudgetReached = _run.Expanded >= _profile.MaxExpandedNodes;
        bool scenarioReevaluation = !member.TimeBudgetReached
            && policy.UseMultiplayerScenarioReevaluation
            && policy.UseMultiplayerTeamObjective
            && _useMultiplayerRouteSemantics;
        string finalEvaluationContextId = SearchEfficiencyEvaluationContextId(
            scenarioReevaluation,
            member.TimeBudgetReached ? "final_time_budget" : "final");
        FinalPlanSelection ordering = FinalOrdering.Select(
            evaluated,
            initialHp,
            emitDiagnostics: true,
            reevaluateScenarios: !member.TimeBudgetReached,
            allowScenarioRerank: !member.TimeBudgetReached);
        RecordCandidateEvaluated(ordering.Candidate.Node, finalEvaluationContextId);
        RecordCandidateSelected(ordering.Candidate.Node, finalEvaluationContextId);
        if (_run.Expanded > _totalExpandedNodeBudget)
        {
            throw new InvalidOperationException(
                $"U3 reevaluation exceeded the total expanded-work budget: " +
                $"{_run.Expanded}/{_totalExpandedNodeBudget}.");
        }
        SolverResult result = MaterializeSelectedRoute(
            ordering,
            onlyDeathRoutesFound,
            member.CurrentTurnAdoptionReached || policy.CurrentTurnOnly
                ? SolverResultScope.CurrentTurnAdoption
                : SolverResultScope.SearchCompletion,
            member.SearchedTurnLayers,
            member.TimeBudgetReached,
            nodeBudgetReached,
            finalEvaluationContextId);
        foreach (SearchNode candidate in finalCandidates)
            candidate.Snapshot.ReleaseSimulator();
        execution.Result = result;
        yield break;
    }

    private SearchNode? ApplyFixedPrefix(SearchNode seed)
        => ApplyFixedPrefix(seed, _fixedPrefixActions);

    private SearchNode? ApplyFixedPrefix(SearchNode seed, IReadOnlyList<PlanAction> prefix)
    {
        SearchNode node = seed;
        foreach (PlanAction action in prefix)
        {
            if (action.Kind == PlanActionKind.EndTurn
                || action.EndsPlayerTurn
                || action.Turn != node.Turn)
            {
                throw new InvalidOperationException(
                    $"固定搜索前缀动作无效：kind={action.Kind} actionTurn={action.Turn} " +
                    $"nodeTurn={node.Turn} endsPlayerTurn={action.EndsPlayerTurn} " +
                    $"card={(string.IsNullOrEmpty(action.CardId) ? "-" : action.CardId)} " +
                    $"potion={(string.IsNullOrEmpty(action.PotionId) ? "-" : action.PotionId)}。");
            }

            if (!CanApplyFixedPrefixAction(node, action))
            {
                node.Snapshot.ReleaseSimulator();
                return null;
            }

            SimulationSnapshot snapshot = Replay(
                [action],
                node.Snapshot,
                node.Turn,
                node.ActionCount);
            bool terminal = snapshot.PlayerDead
                || snapshot.AllEnemiesDead
                || snapshot.BoundaryReason != SearchBoundaryReason.None;
            SearchRouteTraits traits = action.Kind == PlanActionKind.UsePotion
                ? ClassifyPotionTraits(node.Traits, node.Snapshot, snapshot)
                : node.Traits;
            node = new SearchNode(
                action,
                node.ActionCount + 1,
                snapshot.PotionUseCount,
                snapshot.PotionStrategicCost,
                node.Turn,
                traits,
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
            node = AttachOrderedMutationLineage(node);
            node.Parent!.Snapshot.ReleaseSimulator();
        }
        return node;
    }

    private bool CanApplyFixedPrefixAction(SearchNode node, PlanAction action)
    {
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)node.Snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        if (action.Kind == PlanActionKind.UsePotion)
        {
            PotionModel? potion = combat.GetPotionAtSlot(_player, action.PotionSlot);
            return potion != null
                && string.Equals(potion.Id.Entry, action.PotionId, StringComparison.Ordinal)
                && combat.IsPotionAvailable(_player, action.PotionSlot);
        }

        SimPlayerCombatState player = simulator.State.GetPlayerCombatState(_player);
        PredictedCard? card = FindCardForReplay(player.Hand.Cards, action);
        return card != null
            && CanConsiderCardAction(card)
            && combat.CanPlayCard(simulator, card);
    }

    private PlanAction WithDisplayNames(PlanAction action)
        => action with
        {
            Choice = action.Choice == null ? null : WithDisplayNames(action.Choice),
            NestedChoices = action.NestedChoices?.Select(WithDisplayNames).ToArray(),
            TurnStartChoices = action.TurnStartChoices?.Select(WithDisplayNames).ToArray(),
        };

    private PlanCardChoice WithDisplayNames(PlanCardChoice choice)
        => choice with
        {
            Cards = choice.Cards
                .Select(card => card with
                {
                    Title = displayNames.Card(card.CardId, card.UpgradeLevel),
                })
                .ToArray(),
        };

    internal static bool IsCurrentTurnCandidate(
        int actionCount,
        bool turnBoundaryReached,
        bool playerDead,
        int projectedPlayerHp)
        => actionCount > 0
            && turnBoundaryReached
            && !playerDead
            && projectedPlayerHp > 0;

    private static long BufferedAllocationReserve(long observedHighWater)
    {
        if (observedHighWater < 0)
            throw new ArgumentOutOfRangeException(nameof(observedHighWater));
        return observedHighWater >= long.MaxValue / 3 * 2
            ? long.MaxValue
            : observedHighWater + observedHighWater / 2;
    }

    private static long ObservePruneAllocationBytesPerInput(
        long allocatedBytes,
        int inputCount)
    {
        if (allocatedBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(allocatedBytes));
        if (inputCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputCount));
        return allocatedBytes == 0
            ? 0
            : 1 + (allocatedBytes - 1) / inputCount;
    }

    private static long PredictScaledPruneAllocationReserve(
        long fixedFloorBytes,
        long bytesPerInputHighWater,
        int inputCount)
    {
        if (fixedFloorBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(fixedFloorBytes));
        if (bytesPerInputHighWater < 0)
            throw new ArgumentOutOfRangeException(nameof(bytesPerInputHighWater));
        if (inputCount < 0)
            throw new ArgumentOutOfRangeException(nameof(inputCount));

        long scaledBytes = bytesPerInputHighWater == 0 || inputCount == 0
            ? 0
            : bytesPerInputHighWater > long.MaxValue / inputCount
                ? long.MaxValue
                : bytesPerInputHighWater * inputCount;
        long predictedBytes = Math.Max(fixedFloorBytes, scaledBytes);
        return BufferedAllocationReserve(predictedBytes);
    }

    private enum SearchMemberExecutionPhase
    {
        Created,
        Running,
        Yielded,
        Completed,
        Canceled,
        Disposed,
    }

    private sealed class SearchMemberExecutionState
    {
        public SolverResult? Result { get; set; }
        public SearchMemberExecutionPhase Phase { get; set; } = SearchMemberExecutionPhase.Created;
        public CancellationToken StepCancellationToken { get; set; }
        public int RemainingParentCommitsBeforeYield { get; private set; } = int.MaxValue;
        public int CommittedParentsInCurrentStep { get; private set; }
        public int TotalCommittedParents { get; private set; }

        public void BeginStep(SearchWorkAllowance allowance, CancellationToken token)
        {
            StepCancellationToken = token;
            RemainingParentCommitsBeforeYield = allowance.MaxParentCommits;
            CommittedParentsInCurrentStep = 0;
            Phase = SearchMemberExecutionPhase.Running;
        }

        public bool ObserveCommittedParents(int count)
        {
            if (count <= 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            checked
            {
                TotalCommittedParents += count;
                CommittedParentsInCurrentStep += count;
            }
            if (RemainingParentCommitsBeforeYield == int.MaxValue)
                return false;
            RemainingParentCommitsBeforeYield = Math.Max(
                0,
                RemainingParentCommitsBeforeYield - count);
            return RemainingParentCommitsBeforeYield == 0;
        }

        public SolverInterimResult? CurrentBestResult { get; set; }
        public SearchNode? CurrentBestNode { get; set; }
        public SolverInterimResult? CurrentTurnCandidateResult { get; set; }
        public SearchNode? CurrentTurnCandidateNode { get; set; }
        public SearchNode? CurrentTurnPreviewNode { get; set; }
        public SolverCurrentTurnPreview? CurrentTurnPreview { get; set; }
        public IReadOnlyList<PlanAction>? PublishedCurrentTurnActions { get; set; }
        public int CurrentTurnPreviewVersion { get; set; }
        public SolverSpeculativeRoutePreview? SpeculativeRoutePreview { get; set; }
        public CandidateOrigin? SpeculativeRouteOrigin { get; set; }
        public string? SpeculativeRouteEvaluationContextId { get; set; }
        public SolverRouteAdoptionSeed? RouteAdoptionSeed { get; set; }
        public SolverRouteAdoptionSeed? RequestedRouteAdoptionSeed { get; set; }
        public IReadOnlyList<SearchNode>? InterruptedActive { get; set; }
        public int RoutePreviewVersion { get; set; }
        public long LastRoutePreviewAt { get; set; }
        public bool AdoptionReached { get; set; }
        public bool CurrentTurnAdoptionReached { get; set; }
        public int SearchedTurnLayers { get; set; }
        public bool TimeBudgetReached { get; set; }
        public bool AcceptableBattleHpLossReached { get; set; }
        public long TurnLayerStartedMs { get; set; }
        public int TurnLayerStartedExpanded { get; set; }
        public long TurnLayerBudgetMs { get; set; }
        public int TurnLayerNodeBudget { get; set; }
        public int PlayDepth { get; set; }

        public List<SearchNode> Frontier { get; set; } = [];
        public List<SearchNode> Completed { get; set; } = [];
        public SearchNode Fallback { get; set; } = null!;
        public SearchNode? PotionFreeBoundaryFallback { get; set; }
        public double PotionFreeBoundaryFallbackScore { get; set; } = double.NegativeInfinity;
        public SearchNode? PotionBoundaryFallback { get; set; }
        public double PotionBoundaryFallbackScore { get; set; } = double.NegativeInfinity;
        public long ParentAllocatedHighWater { get; set; }
        public long PruneAllocatedBytesPerInputHighWater { get; set; }
        public string PruneHighWaterInterval { get; set; } = "cold";
        public int PruneHighWaterInputCount { get; set; }
        public long PruneHighWaterAllocatedBytes { get; set; }
        public List<SearchNode> Active { get; set; } = [];
        public List<SearchNode> Ended { get; set; } = [];
        public List<SearchNode> NextPlays { get; set; } = [];
        public int ActiveIndex { get; set; }

        public bool HasLiveSimulators()
            => CollectOwnedNodes().Any(node => node.Snapshot.HasSimulator);

        public void ReleaseLiveSimulators()
        {
            foreach (SearchNode node in CollectOwnedNodes())
            {
                if (node.Snapshot.HasSimulator)
                    node.Snapshot.ReleaseSimulator();
            }
        }

        private HashSet<SearchNode> CollectOwnedNodes()
        {
            HashSet<SearchNode> nodes = new(ReferenceEqualityComparer.Instance);
            void Add(IEnumerable<SearchNode>? source)
            {
                if (source == null)
                    return;
                foreach (SearchNode node in source)
                    nodes.Add(node);
            }

            Add(Frontier);
            Add(Completed);
            Add(Active);
            Add(Ended);
            Add(NextPlays);
            Add(InterruptedActive);
            if (Fallback != null)
                nodes.Add(Fallback);
            if (PotionFreeBoundaryFallback != null)
                nodes.Add(PotionFreeBoundaryFallback);
            if (PotionBoundaryFallback != null)
                nodes.Add(PotionBoundaryFallback);
            if (CurrentBestNode != null)
                nodes.Add(CurrentBestNode);
            if (CurrentTurnCandidateNode != null)
                nodes.Add(CurrentTurnCandidateNode);
            if (CurrentTurnPreviewNode != null)
                nodes.Add(CurrentTurnPreviewNode);
            return nodes;
        }
    }

    internal sealed class SearchMemberExecutionSession : IResumableSearch
    {
        private readonly CombatBeamSolver _owner;
        private readonly CancellationToken _ownerCancellationToken;
        private readonly SearchMemberExecutionState _state = new();
        private readonly IEnumerator<SearchStepResult> _steps;
        private readonly SearchRequestWorkTotals? _requestWorkTotals;
        private TimeSpan _exclusiveElapsed;
        private long _exclusiveAllocatedBytes;
        private int _gen0Collections;
        private int _gen1Collections;
        private int _gen2Collections;
        private TimeSpan _gcPauseDuration;
        private bool _finalized;
        private bool _disposed;

        internal SearchMemberExecutionSession(
            CombatBeamSolver owner,
            SearchRequestWorkTotals? requestWorkTotals,
            CancellationToken ownerCancellationToken)
        {
            _owner = owner;
            _ownerCancellationToken = ownerCancellationToken;
            _owner.BeginSearchEfficiencyMember();
            _requestWorkTotals = requestWorkTotals;
            _steps = owner.SolveCoreSteps(_state).GetEnumerator();
        }

        public SolverResult? Result => _state.Result;
        internal int TotalCommittedParentsForTesting => _state.TotalCommittedParents;
        internal bool HasLiveSimulatorsForTesting => _state.HasLiveSimulators();
        internal long ExpandedNodesForScheduling => _owner._run.Expanded;
        internal long TransitionCountForScheduling => _owner._run.TransitionCount;
        internal TimeSpan ExclusiveElapsedForScheduling => _exclusiveElapsed;
        internal long AllocatedBytesForScheduling => _exclusiveAllocatedBytes;
        internal SolverInterimResult? CurrentBestResultForScheduling => _state.CurrentBestResult;

        public SearchStepResult Step(SearchWorkAllowance allowance, CancellationToken token)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SearchMemberExecutionSession));
            if (_state.Phase == SearchMemberExecutionPhase.Completed)
            {
                return new SearchStepResult(
                    SearchStepStatus.Completed,
                    0,
                    _state.TotalCommittedParents);
            }
            if (token.IsCancellationRequested)
            {
                Cancel();
                return new SearchStepResult(
                    SearchStepStatus.Canceled,
                    0,
                    _state.TotalCommittedParents);
            }

            _state.BeginStep(allowance, token);
            try
            {
                bool yielded = MoveNextMeasured();
                if (!yielded)
                {
                    if (_state.Result == null)
                        throw new InvalidOperationException("搜索成员结束时没有生成最终结果。");
                    _state.Phase = SearchMemberExecutionPhase.Completed;
                    _steps.Dispose();
                    FinalizeLifecycle();
                    return new SearchStepResult(
                        SearchStepStatus.Completed,
                        _state.CommittedParentsInCurrentStep,
                        _state.TotalCommittedParents);
                }

                _state.Phase = SearchMemberExecutionPhase.Yielded;
                return new SearchStepResult(
                    SearchStepStatus.Yielded,
                    _state.CommittedParentsInCurrentStep,
                    _state.TotalCommittedParents);
            }
            catch (OperationCanceledException)
                when (token.IsCancellationRequested || _ownerCancellationToken.IsCancellationRequested)
            {
                Cancel();
                return new SearchStepResult(
                    SearchStepStatus.Canceled,
                    _state.CommittedParentsInCurrentStep,
                    _state.TotalCommittedParents);
            }
            catch
            {
                DisposeCore(releaseLiveSimulators: true);
                throw;
            }
        }

        private bool MoveNextMeasured()
        {
            long startedTimestamp = Stopwatch.GetTimestamp();
            long allocatedBytesAtStart = GC.GetAllocatedBytesForCurrentThread();
            long offThreadAllocatedBytesAtStart = _owner._run.OffThreadAllocatedBytes;
            int gen0AtStart = GC.CollectionCount(0);
            int gen1AtStart = GC.CollectionCount(1);
            int gen2AtStart = GC.CollectionCount(2);
            TimeSpan gcPauseAtStart = GC.GetTotalPauseDuration();
            try
            {
                using IDisposable notificationIsolation = SimulationNotificationIsolation.Enter();
                return _steps.MoveNext();
            }
            finally
            {
                _exclusiveElapsed += Stopwatch.GetElapsedTime(startedTimestamp);
                _exclusiveAllocatedBytes = checked(
                    _exclusiveAllocatedBytes
                    + Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBytesAtStart)
                    + Math.Max(0, _owner._run.OffThreadAllocatedBytes - offThreadAllocatedBytesAtStart));
                _gen0Collections = checked(_gen0Collections + Math.Max(0, GC.CollectionCount(0) - gen0AtStart));
                _gen1Collections = checked(_gen1Collections + Math.Max(0, GC.CollectionCount(1) - gen1AtStart));
                _gen2Collections = checked(_gen2Collections + Math.Max(0, GC.CollectionCount(2) - gen2AtStart));
                TimeSpan pause = GC.GetTotalPauseDuration() - gcPauseAtStart;
                if (pause > TimeSpan.Zero)
                    _gcPauseDuration += pause;
            }
        }

        private void Cancel()
        {
            _state.Phase = SearchMemberExecutionPhase.Canceled;
            DisposeCore(releaseLiveSimulators: true);
        }

        private void FinalizeLifecycle()
        {
            if (_finalized)
                return;
            _finalized = true;
            _owner.CompleteExecutionLifecycle(
                _requestWorkTotals,
                _exclusiveElapsed,
                _exclusiveAllocatedBytes,
                _gen0Collections,
                _gen1Collections,
                _gen2Collections,
                _gcPauseDuration);
        }

        private void DisposeCore(bool releaseLiveSimulators)
        {
            if (_disposed)
                return;
            if (releaseLiveSimulators)
                _state.ReleaseLiveSimulators();
            _steps.Dispose();
            _disposed = true;
            FinalizeLifecycle();
        }

        public void Dispose()
        {
            if (_state.Phase is not SearchMemberExecutionPhase.Completed
                and not SearchMemberExecutionPhase.Canceled)
            {
                _state.Phase = SearchMemberExecutionPhase.Disposed;
            }
            DisposeCore(releaseLiveSimulators: _state.Phase != SearchMemberExecutionPhase.Completed);
        }
    }

    private sealed class ParentExpansionSession : IResumableSearch
    {
        private readonly int _parentCount;
        private readonly Func<int, bool> _canCommitParent;
        private readonly Action<int> _commitParent;
        private bool _disposed;

        public ParentExpansionSession(
            int parentCount,
            Func<int, bool> canCommitParent,
            Action<int> commitParent)
        {
            if (parentCount < 0)
                throw new ArgumentOutOfRangeException(nameof(parentCount));
            ArgumentNullException.ThrowIfNull(canCommitParent);
            ArgumentNullException.ThrowIfNull(commitParent);
            _parentCount = parentCount;
            _canCommitParent = canCommitParent;
            _commitParent = commitParent;
        }

        public int NextParentIndex { get; private set; }

        public SearchStepResult Step(SearchWorkAllowance allowance, CancellationToken token)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ParentExpansionSession));

            int committed = 0;
            while (NextParentIndex < _parentCount)
            {
                if (token.IsCancellationRequested)
                {
                    return new SearchStepResult(
                        SearchStepStatus.Canceled,
                        committed,
                        NextParentIndex);
                }
                if (!_canCommitParent(NextParentIndex))
                {
                    return new SearchStepResult(
                        SearchStepStatus.BudgetExhausted,
                        committed,
                        NextParentIndex);
                }

                int parentIndex = NextParentIndex;
                _commitParent(parentIndex);
                NextParentIndex++;
                committed++;

                if (NextParentIndex >= _parentCount)
                {
                    return new SearchStepResult(
                        SearchStepStatus.Completed,
                        committed,
                        NextParentIndex);
                }
                if (committed >= allowance.MaxParentCommits)
                {
                    return new SearchStepResult(
                        SearchStepStatus.Yielded,
                        committed,
                        NextParentIndex);
                }
            }

            return new SearchStepResult(
                SearchStepStatus.Completed,
                committed,
                NextParentIndex);
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }

    private static void VerifyResumableParentExpansionSessionForTesting()
    {
        const int parentCount = 5;
        List<int> continuousOrder = [];
        using (ParentExpansionSession continuous = new(
                   parentCount,
                   _ => true,
                   index => continuousOrder.Add(index)))
        {
            SearchStepResult result = continuous.Step(
                SearchWorkAllowance.Unlimited,
                CancellationToken.None);
            if (result.Status != SearchStepStatus.Completed
                || result.CommittedParents != parentCount
                || result.TotalCommittedParents != parentCount)
            {
                throw new InvalidOperationException(
                    "连续父节点会话没有一次完成固定工作量。");
            }
        }

        List<int> resumedOrder = [];
        using (ParentExpansionSession resumed = new(
                   parentCount,
                   _ => true,
                   index => resumedOrder.Add(index)))
        {
            for (int expectedTotal = 1; expectedTotal <= parentCount; expectedTotal++)
            {
                SearchStepResult result = resumed.Step(
                    SearchWorkAllowance.SingleParent,
                    CancellationToken.None);
                SearchStepStatus expectedStatus = expectedTotal == parentCount
                    ? SearchStepStatus.Completed
                    : SearchStepStatus.Yielded;
                if (result.Status != expectedStatus
                    || result.CommittedParents != 1
                    || result.TotalCommittedParents != expectedTotal)
                {
                    throw new InvalidOperationException(
                        "暂停恢复父节点会话重置了累计工作量或越过安全点。");
                }
            }
        }
        if (!continuousOrder.SequenceEqual(resumedOrder))
        {
            throw new InvalidOperationException(
                "暂停恢复父节点会话改变了确定性父节点顺序。");
        }

        int budgetedCommits = 0;
        using (ParentExpansionSession budgeted = new(
                   parentCount,
                   index => index < 2,
                   _ => budgetedCommits++))
        {
            SearchStepResult first = budgeted.Step(
                SearchWorkAllowance.SingleParent,
                CancellationToken.None);
            SearchStepResult second = budgeted.Step(
                SearchWorkAllowance.SingleParent,
                CancellationToken.None);
            SearchStepResult exhausted = budgeted.Step(
                SearchWorkAllowance.SingleParent,
                CancellationToken.None);
            if (first.Status != SearchStepStatus.Yielded
                || second.Status != SearchStepStatus.Yielded
                || exhausted.Status != SearchStepStatus.BudgetExhausted
                || exhausted.TotalCommittedParents != 2
                || budgetedCommits != 2)
            {
                throw new InvalidOperationException(
                    "暂停恢复父节点会话在分片之间重置了工作预算。");
            }
        }

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        int canceledCommits = 0;
        using ParentExpansionSession canceled = new(
            parentCount,
            _ => true,
            _ => canceledCommits++);
        SearchStepResult canceledResult = canceled.Step(
            SearchWorkAllowance.SingleParent,
            cancellation.Token);
        if (canceledResult.Status != SearchStepStatus.Canceled
            || canceledResult.TotalCommittedParents != 0
            || canceledCommits != 0)
        {
            throw new InvalidOperationException(
                "取消的可恢复父节点会话仍提交了新工作。");
        }
    }

    private enum MemoryCommitPreparation
    {
        Ready,
        Reclaim,
        UseDefaultGc,
    }

    private static MemoryCommitPreparation ResolveMemoryCommitPreparation(
        bool signalEnabled,
        long reservedBytes,
        long allocationLimitBytes,
        long remainingBytes,
        long allocatedBytes,
        bool reclaimAttempted)
    {
        if (reservedBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(reservedBytes));
        if (allocationLimitBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(allocationLimitBytes));
        if (remainingBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(remainingBytes));
        if (allocatedBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(allocatedBytes));
        if (!signalEnabled || reservedBytes <= remainingBytes)
            return MemoryCommitPreparation.Ready;
        if (reservedBytes > allocationLimitBytes || reclaimAttempted || allocatedBytes == 0)
            return MemoryCommitPreparation.UseDefaultGc;
        return MemoryCommitPreparation.Reclaim;
    }

    internal static void VerifyPruneMemoryCheckpointPolicyForTesting()
    {
        if (ResolveStandPatBatchSize(100, 600, 100) != 6
            || ResolveStandPatBatchSize(3, 600, 100) != 3
            || ResolveStandPatBatchSize(100, 99, 100) != 1
            || ResolveStandPatBatchSize(100, 0, 100) != 1
            || ResolveStandPatBatchSize(100, long.MaxValue, 100) != 100
            || ResolveStandPatBatchSize(int.MaxValue, long.MaxValue - 1, 1) != int.MaxValue)
            throw new InvalidOperationException("待命评估批次没有按可用内存分批或正确处理单项/无上限边界。");
        SearchRunContext checkpointRun = new(false, new SearchFramePressureSignal());
        StateFingerprint sentinelKey = new(123, 456);
        StandPatEvaluation sentinel = new(true, 7, 8, 9);
        checkpointRun.StandPatCache.Add(sentinelKey, sentinel);
        checkpointRun.EnsurePruneMemory = _ => { };
        checkpointRun.ResetReclaimableCaches();
        if (!checkpointRun.StandPatCache.TryGetValue(sentinelKey, out StandPatEvaluation preserved)
            || preserved != sentinel)
            throw new InvalidOperationException("剪枝内回收丢失了准备阶段已跳过的缓存代表。");
        checkpointRun.EnsurePruneMemory = null;
        checkpointRun.ResetReclaimableCaches();
        if (checkpointRun.StandPatCache.Count != 0)
            throw new InvalidOperationException("离开剪枝后没有恢复正常的缓存释放边界。");

        const long fixedFloorBytes = 64L * 1024 * 1024;
        if (PredictScaledPruneAllocationReserve(
                fixedFloorBytes,
                bytesPerInputHighWater: 0,
                inputCount: 16_384) != 96L * 1024 * 1024)
        {
            throw new InvalidOperationException(
                "剪枝内存检查点没有为首次剪枝保留 50% 的分配余量。");
        }
        long bytesPerInput = ObservePruneAllocationBytesPerInput(
            allocatedBytes: 192L * 1024 * 1024,
            inputCount: 4);
        if (bytesPerInput != 48L * 1024 * 1024
            || PredictScaledPruneAllocationReserve(
                fixedFloorBytes,
                bytesPerInput,
                inputCount: 4) != 288L * 1024 * 1024
            || PredictScaledPruneAllocationReserve(
                fixedFloorBytes,
                bytesPerInput,
                inputCount: 8) != 576L * 1024 * 1024)
        {
            throw new InvalidOperationException(
                "剪枝内存检查点没有按输入规模扩大已观测的变动分配高水位。");
        }
        if (ObservePruneAllocationBytesPerInput(
                allocatedBytes: 10,
                inputCount: 3) != 4)
        {
            throw new InvalidOperationException(
                "剪枝每输入分配观测没有向上取整，可能系统性低估下一次提交。");
        }
        long amplifiedBytesPerInput = ObservePruneAllocationBytesPerInput(
            allocatedBytes: 70L * 1024 * 1024,
            inputCount: 4);
        if (PredictScaledPruneAllocationReserve(
                fixedFloorBytes,
                amplifiedBytesPerInput,
                inputCount: 40) != 1_050L * 1024 * 1024)
        {
            throw new InvalidOperationException(
                "剪枝内存检查点在输入规模放大时减掉了并不存在的固定开销。");
        }
        if (PredictScaledPruneAllocationReserve(
                fixedFloorBytes,
                bytesPerInputHighWater: long.MaxValue,
                inputCount: 2) != long.MaxValue
            || BufferedAllocationReserve(long.MaxValue) != long.MaxValue)
        {
            throw new InvalidOperationException(
                "剪枝内存检查点的饱和计算可能溢出分配余量。");
        }

        if (ResolveMemoryCommitPreparation(
                signalEnabled: true,
                reservedBytes: 513,
                allocationLimitBytes: 512,
                remainingBytes: 512,
                allocatedBytes: 0,
                reclaimAttempted: false) != MemoryCommitPreparation.UseDefaultGc)
        {
            throw new InvalidOperationException(
                "永久装不下 NoGC 分配上限的剪枝没有切换 CLR 常规 GC。");
        }

        if (ResolveMemoryCommitPreparation(
                signalEnabled: true,
                reservedBytes: 256,
                allocationLimitBytes: 512,
                remainingBytes: 32,
                allocatedBytes: 480,
                reclaimAttempted: false) != MemoryCommitPreparation.Reclaim
            || ResolveMemoryCommitPreparation(
                signalEnabled: true,
                reservedBytes: 256,
                allocationLimitBytes: 512,
                remainingBytes: 512,
                allocatedBytes: 0,
                reclaimAttempted: true) != MemoryCommitPreparation.Ready)
        {
            throw new InvalidOperationException(
                "普通可回收剪枝没有在一次回收后继续使用 NoGC 区域。");
        }

        if (ResolveMemoryCommitPreparation(
                signalEnabled: true,
                reservedBytes: 256,
                allocationLimitBytes: 512,
                remainingBytes: 128,
                allocatedBytes: 384,
                reclaimAttempted: true) != MemoryCommitPreparation.UseDefaultGc)
        {
            throw new InvalidOperationException(
                "剪枝回收后仍装不下时没有切换 CLR 常规 GC。");
        }
    }
}
