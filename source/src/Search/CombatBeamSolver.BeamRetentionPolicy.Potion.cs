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
    private sealed partial class BeamRetentionPolicy
    {
        private readonly record struct FinalPolicyQualificationFacts(
            bool ForcedUsesSatisfied,
            int ExplicitPotionUseCount,
            SolverPotionPolicy EffectivePotionPolicy,
            int OptionalPotionUseCount,
            int OptionalPotionStrategicCost,
            int OptionalAmbergrisCount);
        private readonly record struct FinalPolicyQualificationSignature(
            bool ForcedUsesSatisfied,
            int ExplicitPotionUseCount,
            SolverPotionPolicy EffectivePotionPolicy,
            int OptionalPotionUseCount,
            int OptionalPotionStrategicCost,
            int OptionalAmbergrisCount,
            bool TheftEscapeEligible,
            int OptionalAmbergrisFinalPlayerHpCohort);

        private FinalPolicyQualificationFacts BuildFinalPolicyQualificationFacts(SearchNode node)
        {
            int explicitPotionStrategicCost = 0;
            int explicitAmbergrisCount = 0;
            for (SearchNode? cursor = node; cursor?.Action is { } action; cursor = cursor.Parent)
            {
                if (action.Kind != PlanActionKind.UsePotion)
                    continue;
                if (string.IsNullOrEmpty(action.PotionId))
                    throw new InvalidOperationException("用药动作缺少药水 ID。");
                explicitPotionStrategicCost += _run.PotionStrategicCosts.Get(
                    action.PotionId,
                    _renewablePotionShapedRock);
                if (string.Equals(action.PotionId, "AMBERGRIS", StringComparison.Ordinal))
                    explicitAmbergrisCount++;
            }

            int forcedUseCount = 0;
            int forcedStrategicHpCost = 0;
            int forcedAmbergrisCount = 0;
            bool forcedUsesSatisfied = true;
            if (_enforcePotionDirectives)
            {
                foreach (PotionSlotDirective directive in _potionStrategy.Directives)
                {
                    if (directive.Directive != SolverPotionDirective.Force)
                        continue;
                    bool used = false;
                    for (SearchNode? cursor = node; cursor?.Action is { } action; cursor = cursor.Parent)
                    {
                        if (action.Kind != PlanActionKind.UsePotion
                            || action.PotionSlot != directive.Slot
                            || !string.Equals(
                                action.PotionId,
                                directive.PotionId,
                                StringComparison.Ordinal))
                        {
                            continue;
                        }
                        used = true;
                        break;
                    }
                    if (!used)
                    {
                        forcedUsesSatisfied = false;
                        continue;
                    }
                    forcedUseCount++;
                    forcedStrategicHpCost += _run.PotionStrategicCosts.Get(
                        directive.PotionId,
                        _renewablePotionShapedRock);
                    if (string.Equals(directive.PotionId, "AMBERGRIS", StringComparison.Ordinal))
                        forcedAmbergrisCount++;
                }
            }

            int explicitPotionUseCount = ExplicitPotionUseCount(node);
            int optionalPotionUseCount = Math.Max(0, explicitPotionUseCount - forcedUseCount);
            int optionalPotionStrategicCost = Math.Max(
                0,
                explicitPotionStrategicCost - forcedStrategicHpCost);
            int optionalAmbergrisCount = Math.Max(0, explicitAmbergrisCount - forcedAmbergrisCount);
            SolverPotionPolicy effectivePotionPolicy = _potionPolicy switch
            {
                SolverPotionPolicy.RequireAtLeastOne when forcedUseCount > 0
                    => SolverPotionPolicy.Smart,
                SolverPotionPolicy.Disabled when optionalPotionUseCount > 0
                    => SolverPotionPolicy.Smart,
                _ => _potionPolicy,
            };
            return new FinalPolicyQualificationFacts(
                forcedUsesSatisfied,
                explicitPotionUseCount,
                effectivePotionPolicy,
                optionalPotionUseCount,
                optionalPotionStrategicCost,
                optionalAmbergrisCount);
        }

        private FinalPolicyQualificationSignature BuildFinalPolicyQualificationSignature(
            FinalPolicyQualificationFacts facts,
            SearchNode candidate,
            int potionFreeOutstandingResource)
        {
            if (!facts.ForcedUsesSatisfied)
            {
                // Every partial forced-use history is rejected by the same hard rule.
                return new FinalPolicyQualificationSignature(
                    false,
                    0,
                    default,
                    0,
                    0,
                    0,
                    false,
                    int.MinValue);
            }

            bool theftEscapeEligible = FinalPolicyTheftEscapeEligible(
                _theftPolicy,
                candidate.PotionCount,
                candidate.Snapshot.OutstandingStolenResource,
                potionFreeOutstandingResource);
            return new FinalPolicyQualificationSignature(
                true,
                facts.ExplicitPotionUseCount,
                facts.EffectivePotionPolicy,
                facts.OptionalPotionUseCount,
                facts.OptionalPotionStrategicCost,
                facts.OptionalAmbergrisCount,
                theftEscapeEligible,
                FinalPolicyOptionalAmbergrisPlayerHpCohort(
                    facts.OptionalAmbergrisCount,
                    candidate.Snapshot.PlayerHp));
        }

        internal static int FinalPolicyOptionalAmbergrisPlayerHpCohort(
            int optionalAmbergrisCount,
            int playerHp)
            => optionalAmbergrisCount > 0 ? playerHp : int.MinValue;

        internal static bool FinalPolicyTheftEscapeEligible(
            SolverTheftPolicy? theftPolicy,
            int potionCount,
            int outstandingStolenResource,
            int potionFreeOutstandingResource)
            => theftPolicy == SolverTheftPolicy.PreserveResources
                && potionCount > 0
                && outstandingStolenResource < potionFreeOutstandingResource;

        private static int CompareFinalPolicyQualificationSignatures(
            FinalPolicyQualificationSignature left,
            FinalPolicyQualificationSignature right)
        {
            int comparison = right.ForcedUsesSatisfied.CompareTo(left.ForcedUsesSatisfied);
            if (comparison != 0)
                return comparison;
            comparison = left.ExplicitPotionUseCount.CompareTo(right.ExplicitPotionUseCount);
            if (comparison != 0)
                return comparison;
            comparison = left.EffectivePotionPolicy.CompareTo(right.EffectivePotionPolicy);
            if (comparison != 0)
                return comparison;
            comparison = left.OptionalPotionUseCount.CompareTo(right.OptionalPotionUseCount);
            if (comparison != 0)
                return comparison;
            comparison = left.OptionalPotionStrategicCost.CompareTo(right.OptionalPotionStrategicCost);
            if (comparison != 0)
                return comparison;
            comparison = left.OptionalAmbergrisCount.CompareTo(right.OptionalAmbergrisCount);
            if (comparison != 0)
                return comparison;
            comparison = right.TheftEscapeEligible.CompareTo(left.TheftEscapeEligible);
            return comparison != 0
                ? comparison
                : left.OptionalAmbergrisFinalPlayerHpCohort.CompareTo(
                    right.OptionalAmbergrisFinalPlayerHpCohort);
        }

        internal static void ReservePotionQuotaLeaders(
            HashSet<SearchNode> reservations,
            IReadOnlyList<SearchNode> rankedPool,
            bool usesPotion,
            int quota)
        {
            if (quota < 0)
                throw new ArgumentOutOfRangeException(nameof(quota));
            if (quota == 0)
                return;
            int reserved = 0;
            foreach (SearchNode candidate in rankedPool)
            {
                if (UsesPotion(candidate) != usesPotion)
                    continue;
                reservations.Add(candidate);
                reserved++;
                if (reserved >= quota)
                    return;
            }
        }

        internal static (int Used, int Unused) FeasiblePotionUseQuotas(int limit)
        {
            if (limit < 1)
                throw new ArgumentOutOfRangeException(nameof(limit));
            int used = limit < 4
                ? 1
                : Math.Max(2, limit / 3);
            return (used, limit - used);
        }

        internal static void EnforcePotionUseQuota(
            List<SearchNode> selected,
            IReadOnlyList<SearchNode> pool,
            IReadOnlySet<SearchNode> protectedNodes,
            bool usesPotion,
            int quota)
        {
            int retained = selected.Count(node => UsesPotion(node) == usesPotion);
            if (retained >= quota)
                return;

            foreach (SearchNode candidate in pool.Where(node => UsesPotion(node) == usesPotion))
            {
                if (retained >= quota)
                    return;
                if (ContainsReference(selected, candidate))
                    continue;
                int replaceIndex = selected.FindLastIndex(node =>
                    UsesPotion(node) != usesPotion
                    && !protectedNodes.Contains(node));
                if (replaceIndex < 0)
                    return;
                selected[replaceIndex] = candidate;
                retained++;
            }
        }

        internal static string PotionUseLineageKey(SearchNode node)
        {
            // Only the potion multiset participates in this key. Materializing Actions
            // would retain an array for the entire route on every grouped candidate.
            List<string>? potionIds = null;
            int actionCount = 0;
            for (SearchNode? current = node; current?.Action is { } action; current = current.Parent)
            {
                actionCount++;
                if (action.Kind == PlanActionKind.UsePotion)
                {
                    (potionIds ??= []).Add(action.PotionId
                        ?? throw new InvalidOperationException("用药动作缺少药水 ID。"));
                }
            }
            if (actionCount != node.ActionCount)
                throw new InvalidOperationException("搜索节点动作链长度不一致。");
            if (potionIds == null)
                return string.Empty;
            potionIds.Sort(StringComparer.Ordinal);
            return string.Join(',', potionIds);
        }

        private static SearchNode? FindBestPotionLineage(IEnumerable<SearchNode> nodes)
            => nodes.Aggregate(
                (SearchNode?)null,
                (best, node) => best == null
                    || node.Snapshot.AllEnemiesDead && !best.Snapshot.AllEnemiesDead
                    || node.Snapshot.AllEnemiesDead == best.Snapshot.AllEnemiesDead
                        && (node.Snapshot.ProjectedPlayerHp > best.Snapshot.ProjectedPlayerHp
                            || node.Snapshot.ProjectedPlayerHp == best.Snapshot.ProjectedPlayerHp
                                && (node.Snapshot.EnemyHp < best.Snapshot.EnemyHp
                                    || node.Snapshot.EnemyHp == best.Snapshot.EnemyHp
                                        && node.Score > best.Score))
                        ? node
                        : best);

        private static bool UsesPotion(SearchNode node)
            => node.PotionCount > 0;
    }
}
