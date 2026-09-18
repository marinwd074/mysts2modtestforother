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
    internal void VerifyNarrowOrderedPileCapacityForTesting(IReadOnlyList<SimulationSnapshot> snapshots)
    {
        List<SearchNode> nodes = snapshots.Select(snapshot => new SearchNode(
            new PlanAction(PlanActionKind.EndTurn, snapshot.Turn - 1), 5,
            snapshot.PotionUseCount, snapshot.PotionStrategicCost, snapshot.Turn,
            SearchRouteTraits.None, 0, snapshot.Score, snapshot.StateKey,
            snapshot.HasRisk, snapshot.BoundaryReason, false, null,
            snapshot, CombatProgressState.Capture(snapshot))).ToList();
        List<SearchNode> narrow = Retention.RankBest(nodes, 6, preserveDefensiveRoute: true);
        if (narrow.Count is < 6 or > 7
            || narrow.Distinct(ReferenceEqualityComparer.Instance).Count() != narrow.Count
            || narrow.Any(node => !nodes.Contains(node)))
            throw new InvalidOperationException("Narrow ordered-pile retention exceeded its capacity or lost candidate identity.");
        List<SearchNode> full = Retention.RankBest(nodes, nodes.Count, preserveDefensiveRoute: true);
        if (full.Count != nodes.Count)
            throw new InvalidOperationException("An unsaturated retention channel lost candidates.");
    }

    internal static void VerifyRoutingChoicePortfolioBoundsForTesting()
    {
        if (BeamRetentionPolicy.BoundedRoutingChoiceQuota(0) != 0
            || BeamRetentionPolicy.BoundedRoutingChoiceQuota(42) != 42
            || BeamRetentionPolicy.BoundedRoutingChoiceQuota(200) != 96
            || BeamRetentionPolicy.BoundedAmbiguousCompressedChoiceQuota(0) != 0
            || BeamRetentionPolicy.BoundedAmbiguousCompressedChoiceQuota(6) != 2
            || BeamRetentionPolicy.BoundedAmbiguousCompressedChoiceQuota(12) != 4
            || BeamRetentionPolicy.BoundedAmbiguousCompressedChoiceQuota(60) != 20
            || BeamRetentionPolicy.BoundedAmbiguousCompressedChoiceQuota(135) != 45
            || BeamRetentionPolicy.BoundedAmbiguousCompressedChoiceQuota(300) != 48
            || BeamRetentionPolicy.IsAmbiguousCompressedChoiceCardinality(0)
            || BeamRetentionPolicy.IsAmbiguousCompressedChoiceCardinality(1)
            || !BeamRetentionPolicy.IsAmbiguousCompressedChoiceCardinality(2)
            || !BeamRetentionPolicy.IsAmbiguousCompressedChoiceCardinality(3))
        {
            throw new InvalidOperationException(
                "选牌歧义 portfolio 没有排除已有精确 option 身份的单卡选择，" +
                "或没有保留多卡三分之一公平子配额及硬上界。");
        }

        bool rejectedNegativeCardinality = false;
        try
        {
            BeamRetentionPolicy.IsAmbiguousCompressedChoiceCardinality(-1);
        }
        catch (ArgumentOutOfRangeException)
        {
            rejectedNegativeCardinality = true;
        }
        if (!rejectedNegativeCardinality)
            throw new InvalidOperationException("选牌歧义 portfolio 接受了负 cardinality。");

        IReadOnlyList<IReadOnlyList<int>> optionContexts =
        [
            [10, 11, 12, 13],
            [20],
            [30, 31],
        ];
        int[] interleaved =
            [.. BeamRetentionPolicy.InterleaveRoutingChoiceContexts(optionContexts)];
        int[] repeated =
            [.. BeamRetentionPolicy.InterleaveRoutingChoiceContexts(
                optionContexts)];
        if (!interleaved.SequenceEqual([10, 11, 12, 13, 20, 30, 31])
            || !repeated.SequenceEqual(interleaved)
            || interleaved.Length != optionContexts.Sum(group => group.Count)
            || interleaved.Distinct().Count() != interleaved.Length)
        {
            throw new InvalidOperationException(
                "routing context 的 8-wide 分块调度不完整、不确定，或重复/遗漏了 context。");
        }

        IReadOnlyList<IReadOnlyList<int>> saturatedOptions = Enumerable.Range(0, 12)
            .Select(option => (IReadOnlyList<int>)Enumerable.Range(0, 12)
                .Select(context => option * 100 + context)
                .ToList())
            .ToList();
        int hardLimit = BeamRetentionPolicy.BoundedRoutingChoiceQuota(
            saturatedOptions.Sum(group => group.Count));
        int[] saturatedSchedule =
            [.. BeamRetentionPolicy.InterleaveRoutingChoiceContexts(saturatedOptions)];
        int[] boundedPrefix =
        [
            .. saturatedSchedule.Take(hardLimit),
        ];
        if (hardLimit != 96
            || boundedPrefix.Length != hardLimit
            || Enumerable.Range(0, 12).Any(option =>
                boundedPrefix.Count(value => value / 100 == option) != 8)
            || Enumerable.Range(0, 12).Any(option =>
                saturatedSchedule.Skip(hardLimit).Count(value => value / 100 == option) != 4))
        {
            throw new InvalidOperationException(
                "routing context 分块没有保持每轮每 option 至多 8 个，" +
                "或 12x12 输入在 96 硬上限内没有给每个 option 留出代表。");
        }
    }

    internal static void VerifyPotionQuotaReservationPolicyForTesting()
    {
        if (BeamRetentionPolicy.FeasiblePotionUseQuotas(1) != (1, 0)
            || BeamRetentionPolicy.FeasiblePotionUseQuotas(2) != (1, 1)
            || BeamRetentionPolicy.FeasiblePotionUseQuotas(3) != (1, 2)
            || BeamRetentionPolicy.FeasiblePotionUseQuotas(4) != (2, 2)
            || BeamRetentionPolicy.FeasiblePotionUseQuotas(5) != (2, 3)
            || BeamRetentionPolicy.FeasiblePotionUseQuotas(6) != (2, 4)
            || BeamRetentionPolicy.FeasiblePotionUseQuotas(135) != (45, 90))
        {
            throw new InvalidOperationException(
                "小 Beam 的双侧药水 quota 不可行，或标准三分之一分区发生变化。");
        }

        static SearchNode Candidate(int identity, bool usesPotion)
            => new(
                null,
                0,
                usesPotion ? 1 : 0,
                0,
                1,
                SearchRouteTraits.None,
                0,
                1000d - identity,
                new StateFingerprint((ulong)identity, 0),
                false,
                SearchBoundaryReason.None,
                false,
                null,
                null!,
                null!);

        List<SearchNode> used = Enumerable.Range(1, 6)
            .Select(identity => Candidate(identity, usesPotion: true))
            .ToList();
        List<SearchNode> unused = Enumerable.Range(101, 6)
            .Select(identity => Candidate(identity, usesPotion: false))
            .ToList();
        List<SearchNode> pool = [.. used, .. unused];
        List<SearchNode> oneSlot = [unused[0]];
        HashSet<SearchNode> oneSlotReservations = new(ReferenceEqualityComparer.Instance);
        (int oneUsedQuota, int oneUnusedQuota) =
            BeamRetentionPolicy.FeasiblePotionUseQuotas(1);
        BeamRetentionPolicy.ReservePotionQuotaLeaders(
            oneSlotReservations,
            pool,
            usesPotion: true,
            oneUsedQuota);
        BeamRetentionPolicy.ReservePotionQuotaLeaders(
            oneSlotReservations,
            pool,
            usesPotion: false,
            oneUnusedQuota);
        BeamRetentionPolicy.EnforcePotionUseQuota(
            oneSlot,
            pool,
            oneSlotReservations,
            usesPotion: true,
            oneUsedQuota);
        if (oneSlot.Count != 1 || oneSlot[0].PotionCount == 0)
        {
            throw new InvalidOperationException(
                "单槽 Beam 的零 quota 侧错误预约，阻止了可行的用药最低保障。");
        }

        List<SearchNode> selected = [.. used];
        HashSet<SearchNode> reservations = new(ReferenceEqualityComparer.Instance);
        BeamRetentionPolicy.ReservePotionQuotaLeaders(
            reservations,
            pool,
            usesPotion: true,
            quota: 2);
        BeamRetentionPolicy.ReservePotionQuotaLeaders(
            reservations,
            pool,
            usesPotion: false,
            quota: 4);
        BeamRetentionPolicy.EnforcePotionUseQuota(
            selected,
            pool,
            reservations,
            usesPotion: true,
            quota: 2);
        BeamRetentionPolicy.EnforcePotionUseQuota(
            selected,
            pool,
            reservations,
            usesPotion: false,
            quota: 4);
        if (selected.Count(node => node.PotionCount > 0) != 2
            || selected.Count(node => node.PotionCount == 0) != 4
            || selected.Distinct(ReferenceEqualityComparer.Instance).Count() != selected.Count
            || !selected.Contains(used[0], ReferenceEqualityComparer.Instance)
            || !selected.Contains(used[1], ReferenceEqualityComparer.Instance))
        {
            throw new InvalidOperationException(
                "双侧药水 quota 没有同时保护两类质量最高的最低保障集合。");
        }

        List<SearchNode> reverseSelected = [.. unused];
        BeamRetentionPolicy.EnforcePotionUseQuota(
            reverseSelected,
            pool,
            reservations,
            usesPotion: false,
            quota: 4);
        BeamRetentionPolicy.EnforcePotionUseQuota(
            reverseSelected,
            pool,
            reservations,
            usesPotion: true,
            quota: 2);
        if (reverseSelected.Count(node => node.PotionCount > 0) != 2
            || reverseSelected.Count(node => node.PotionCount == 0) != 4
            || reverseSelected.Distinct(ReferenceEqualityComparer.Instance).Count()
                != reverseSelected.Count
            || !reverseSelected.Contains(used[0], ReferenceEqualityComparer.Instance)
            || !reverseSelected.Contains(used[1], ReferenceEqualityComparer.Instance)
            || !reverseSelected.Contains(unused[0], ReferenceEqualityComparer.Instance)
            || !reverseSelected.Contains(unused[1], ReferenceEqualityComparer.Instance)
            || !reverseSelected.Contains(unused[2], ReferenceEqualityComparer.Instance)
            || !reverseSelected.Contains(unused[3], ReferenceEqualityComparer.Instance))
        {
            throw new InvalidOperationException(
                "双侧药水 quota 的结果依赖补齐调用顺序或删除了反侧预约路线。");
        }

        SearchNode requiredUsed = used[^1];
        List<SearchNode> constrained = [.. used];
        HashSet<SearchNode> constrainedReservations = new(
            [requiredUsed],
            ReferenceEqualityComparer.Instance);
        BeamRetentionPolicy.ReservePotionQuotaLeaders(
            constrainedReservations,
            pool,
            usesPotion: true,
            quota: 2);
        BeamRetentionPolicy.ReservePotionQuotaLeaders(
            constrainedReservations,
            pool,
            usesPotion: false,
            quota: 4);
        BeamRetentionPolicy.EnforcePotionUseQuota(
            constrained,
            pool,
            constrainedReservations,
            usesPotion: false,
            quota: 4);
        if (!constrained.Contains(requiredUsed, ReferenceEqualityComparer.Instance))
        {
            throw new InvalidOperationException(
                "不可同时满足药水 quota 时删除了更高优先级的 required 路线。");
        }
    }

    internal static void VerifyPotionUseLineageKeyForTesting()
    {
        static SearchNode Root() => new(null, 0, 0, 0, 1, default, 0, 0,
            default, false, SearchBoundaryReason.None, false, null, null!, null!);
        static SearchNode Append(SearchNode parent, PlanActionKind kind, string? id = null)
            => new(new PlanAction(kind, parent.Turn, PotionId: id!), parent.ActionCount + 1,
                parent.PotionCount + (kind == PlanActionKind.UsePotion ? 1 : 0),
                0, parent.Turn, default, 0, 0, default, false,
                SearchBoundaryReason.None, false, parent, null!, null!);
        static void Verify(SearchNode node)
        {
            string actual = BeamRetentionPolicy.PotionUseLineageKey(node);
            if (node.HasMaterializedActionsForTesting)
                throw new InvalidOperationException("药水分组不应物化完整动作链。");
            string expected = string.Join(',', node.Actions
                .Where(action => action.Kind == PlanActionKind.UsePotion)
                .Select(action => action.PotionId
                    ?? throw new InvalidOperationException("用药动作缺少药水 ID。"))
                .OrderBy(static id => id, StringComparer.Ordinal));
            if (!string.Equals(actual, expected, StringComparison.Ordinal)
                || BeamRetentionPolicy.PotionUseLineageKey(node) != expected)
                throw new InvalidOperationException("药水谱系键与原完整历史算法不同。");
        }
        Verify(Root());
        foreach (string[] ids in new string[][] { ["Z"], ["Z", "A", "Z"], ["", "a", "A", "药水", ","] })
        {
            SearchNode node = Root();
            foreach (string id in ids)
            {
                for (int index = 0; index < 257; index++)
                    node = Append(node, index % 2 == 0 ? PlanActionKind.PlayCard : PlanActionKind.EndTurn);
                node = Append(node, PlanActionKind.UsePotion, id);
            }
            Verify(node);
        }
        SearchNode shared = Append(Root(), PlanActionKind.UsePotion, "ROOT");
        Verify(Append(shared, PlanActionKind.UsePotion, "LEFT"));
        Verify(Append(shared, PlanActionKind.UsePotion, "RIGHT"));
        if (shared.HasMaterializedActionsForTesting)
            throw new InvalidOperationException("药水分组不应物化共享父链。");
        try
        {
            BeamRetentionPolicy.PotionUseLineageKey(Append(Root(), PlanActionKind.UsePotion));
        }
        catch (InvalidOperationException exception) when (exception.Message == "用药动作缺少药水 ID。")
        {
            return;
        }
        throw new InvalidOperationException("药水 ID 缺失必须显式失败。");
    }

    internal void VerifyFinalPolicyQualificationRetentionForTesting(string potionId, int forcedSlot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(potionId);
        const int cohortHp = 37;
        if (BeamRetentionPolicy.FinalPolicyOptionalAmbergrisPlayerHpCohort(
                optionalAmbergrisCount: 1,
                playerHp: cohortHp) != cohortHp
            || BeamRetentionPolicy.FinalPolicyOptionalAmbergrisPlayerHpCohort(
                optionalAmbergrisCount: 0,
                playerHp: cohortHp) != int.MinValue
            || !BeamRetentionPolicy.FinalPolicyTheftEscapeEligible(
                SolverTheftPolicy.PreserveResources,
                potionCount: 1,
                outstandingStolenResource: 2,
                potionFreeOutstandingResource: 3)
            || BeamRetentionPolicy.FinalPolicyTheftEscapeEligible(
                SolverTheftPolicy.PreserveResources,
                potionCount: 1,
                outstandingStolenResource: 3,
                potionFreeOutstandingResource: 3)
            || BeamRetentionPolicy.FinalPolicyTheftEscapeEligible(
                theftPolicy: null,
                potionCount: 1,
                outstandingStolenResource: 2,
                potionFreeOutstandingResource: 3))
        {
            throw new InvalidOperationException(
                "最终策略资格的 Ambergris 或偷窃分组不符合终局政策。 ");
        }
        using IDisposable notificationIsolation = SimulationNotificationIsolation.Enter();
        SimulationSnapshot snapshot = Replay([]);
        try
        {
            int limit = checked(_profile.BeamWidth * 4);
            int potionCount = checked(snapshot.PotionUseCount + 1);
            CombatProgressState combatProgress = CombatProgressState.Capture(snapshot);
            bool terminal = snapshot.PlayerDead
                || snapshot.AllEnemiesDead
                || snapshot.BoundaryReason != SearchBoundaryReason.None;
            SearchNode rootNode = new(
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
                terminal,
                null,
                snapshot,
                combatProgress);

            SearchNode MakeNode(
                SearchNode parent,
                PlanAction action,
                int actionCount,
                int candidatePotionCount)
                => new(
                    action,
                    actionCount,
                    candidatePotionCount,
                    snapshot.PotionStrategicCost,
                    _startTurnNumber,
                    SearchRouteTraits.None,
                    0,
                    snapshot.Score,
                    snapshot.StateKey,
                    snapshot.HasRisk,
                    snapshot.BoundaryReason,
                    terminal,
                    parent,
                    snapshot,
                    combatProgress);

            SearchNode sharedPrefix = MakeNode(
                rootNode,
                new PlanAction(
                    PlanActionKind.PlayCard,
                    _startTurnNumber,
                    CardId: "TEST.FINAL_POLICY_PREFIX"),
                1,
                snapshot.PotionUseCount);
            int ordinarySlot = forcedSlot == 0 ? 1 : 0;
            List<SearchNode> candidates = new(limit + 2);
            for (int index = 0; index <= limit; index++)
            {
                candidates.Add(MakeNode(
                    sharedPrefix,
                    new PlanAction(
                        PlanActionKind.UsePotion,
                        _startTurnNumber,
                        PotionSlot: ordinarySlot,
                        PotionId: potionId),
                    2,
                    potionCount));
            }
            SearchNode forcedPrefix = MakeNode(
                sharedPrefix,
                new PlanAction(
                    PlanActionKind.PlayCard,
                    _startTurnNumber,
                    CardId: "TEST.FINAL_POLICY_DELAY"),
                2,
                snapshot.PotionUseCount);
            SearchNode forcedCandidate = MakeNode(
                forcedPrefix,
                new PlanAction(
                    PlanActionKind.UsePotion,
                    _startTurnNumber,
                    PotionSlot: forcedSlot,
                    PotionId: potionId),
                3,
                potionCount);
            candidates.Add(forcedCandidate);

            List<SearchNode> ordinaryTop = Retention.RankBest(
                candidates,
                limit,
                finalQualityFirst: true);
            if (ordinaryTop.Any(node => ReferenceEquals(node, forcedCandidate)))
            {
                throw new InvalidOperationException(
                    "最终策略历史保留回归的普通 Top-N 截断前置条件没有成立。");
            }

            List<SearchNode> retained = Retention.RankFinal(candidates);
            if (!retained.Any(node => ReferenceEquals(node, forcedCandidate)))
            {
                throw new InvalidOperationException(
                    "最终候选截断丢失了同药水不同槽位的策略历史代表。");
            }
            if (retained.Count > limit + 2)
            {
                throw new InvalidOperationException(
                    "未满足的强制用药历史没有折叠为有界资格分组。");
            }

            PotionStrategySnapshot forcedStrategy = new(
                SolverPotionPolicy.Smart,
                [new PotionSlotDirective(forcedSlot, potionId, SolverPotionDirective.Force)]);
            if (!forcedStrategy.EvaluateForcedUses(
                    forcedCandidate.Actions,
                    renewablePotionShapedRock: false).AllForcedUsesSatisfied
                || forcedStrategy.EvaluateForcedUses(
                    candidates[0].Actions,
                    renewablePotionShapedRock: false).AllForcedUsesSatisfied)
            {
                throw new InvalidOperationException(
                    "最终策略历史保留回归没有维持精确槽位的强制用药资格。");
            }
        }
        finally
        {
            snapshot.ReleaseSimulator();
        }
    }
}

