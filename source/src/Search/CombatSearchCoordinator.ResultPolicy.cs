using System.Diagnostics;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

internal static partial class CombatSearchCoordinator
{
    private static SolverInterimResult BuildInterimResult(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
        => new(
            Won: IsCompleteVictory(result),
            OutstandingStolenResource: result.OutstandingStolenResource,
            ProjectedBattleHpLost: result.ProjectedBattleHpLost,
            StrategicHpDeficit: StrategicHpDeficit(root, policy, result),
            PotionStrategicCost: SmartPotionHpRequired(root, policy, result),
            ProjectedBattlePotionCount: result.ProjectedBattlePotionCount,
            CombatEndedTurn: result.CombatEndedTurn,
            EnemyHp: result.Snapshot.EnemyHp,
            Score: result.BestNode.Score)
        {
            GrowthHpCredit = result.Snapshot.StrategyGoalHpCredit,
            EvaluationWindow = result.EvaluationWindow,
            TheftPolicy = policy.TheftPolicy,
            GrowthRewardCount = result.Snapshot.StrategyGoalCount,
            Survives = !result.Snapshot.PlayerDead && result.Snapshot.ProjectedPlayerHp > 0,
            SurvivesEvaluationWindow = !result.Snapshot.PlayerDead && result.Snapshot.PlayerHp > 0,
            DeathSaveUseCount = result.Snapshot.ProjectedDeathSaveUseCount,
            RollingHorizonLossFirst =
                SearchEvaluationPolicy.UsesRawLossFirst(
                    policy.RoutePolicy),
        };


    private static bool IsBetterCompletedResult(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult candidate,
        SolverResult current)
    {
        int? primaryQuality = CompareCompletedResultPrimaryQuality(root, policy, candidate, current);
        if (primaryQuality == null)
            return false;
        if (primaryQuality != 0)
            return primaryQuality < 0;
        if (candidate.PotionCount != current.PotionCount)
            return candidate.PotionCount < current.PotionCount;
        int threat = ComparePublicThreat(policy, candidate, current);
        if (threat != 0)
            return threat > 0;
        if (PreferPlayableCurrentTurnRoute(root, policy, candidate, current))
            return true;
        if (PreferPlayableCurrentTurnRoute(root, policy, current, candidate))
            return false;
        return candidate.BestNode.Score > current.BestNode.Score;
    }

    private static bool IsBetterPotionPolicyResult(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult candidate,
        SolverResult current)
    {
        SolverInterimResult candidateInterim = BuildInterimResult(root, policy, candidate);
        SolverInterimResult currentInterim = BuildInterimResult(root, policy, current);
        int? quality = ComparePotionPolicyQuality(
            policy.TheftPolicy,
            candidateInterim,
            currentInterim);
        if (quality == null)
            return false;
        if (quality != 0)
            return quality < 0;
        int threat = ComparePublicThreat(policy, candidate, current);
        if (threat != 0)
            return threat > 0;
        if (PreferPlayableCurrentTurnRoute(root, policy, candidate, current))
            return true;
        if (PreferPlayableCurrentTurnRoute(root, policy, current, candidate))
            return false;
        return candidate.BestNode.Score > current.BestNode.Score;
    }

    private static int ComparePublicThreat(SearchPolicySnapshot policy, SolverResult candidate, SolverResult current)
    {
        if (!policy.UseLocalCorePublicThreat || candidate.PublicThreat is not { } threat
            || current.PublicThreat is not { } previous
            || candidate.Snapshot.HasRisk || current.Snapshot.HasRisk
            || !RouteEvaluationWindow.CanCompare(candidate.EvaluationWindow, current.EvaluationWindow)
            || candidate.Snapshot.PlayerMaxHp != current.Snapshot.PlayerMaxHp
            || candidate.Snapshot.PlayerHp != current.Snapshot.PlayerHp
            || candidate.Snapshot.GrowthRewards != current.Snapshot.GrowthRewards
            || candidate.Snapshot.RelicCounters != current.Snapshot.RelicCounters
            || candidate.Snapshot.RealizedLongTermResourceValue != current.Snapshot.RealizedLongTermResourceValue
            || candidate.Snapshot.LongTermResourceValue != current.Snapshot.LongTermResourceValue
            || candidate.OutstandingStolenResource != current.OutstandingStolenResource)
            return 0;
        return threat.CompareTo(previous);
    }

    internal static bool IsBetterPotionPolicyResult(
        SolverTheftPolicy? theftPolicy,
        SolverInterimResult candidate,
        SolverInterimResult current)
    {
        int? quality = ComparePotionPolicyQuality(theftPolicy, candidate, current);
        if (quality == null)
            return false;
        if (quality != 0)
            return quality < 0;
        return candidate.Score > current.Score;
    }

    private static int? ComparePotionPolicyQuality(
        SolverTheftPolicy? theftPolicy,
        SolverInterimResult candidate,
        SolverInterimResult current)
    {
        bool rollingHorizonLossFirst =
            candidate.RollingHorizonLossFirst && current.RollingHorizonLossFirst;
        int coverage = SolverInterimResultOrdering.CompareCompletedCoverage(candidate, current);
        if (coverage != 0)
            return coverage;
        if (candidate.Survives == current.Survives
            && !RouteEvaluationWindow.CanCompareQuality(candidate.EvaluationWindow, current.EvaluationWindow))
            return null;
        int outcomeComparison = SolverInterimResultOrdering.CompareOutcome(candidate, current, rollingHorizonLossFirst);
        if (outcomeComparison != 0)
            return outcomeComparison;
        int recovery = TheftEncounterStrategy.CompareRecovery(theftPolicy,
            candidate.OutstandingStolenResource, current.OutstandingStolenResource);
        if (recovery != 0)
            return recovery;
        // Opportunity cost participates before the single-player HP/turn tie-break.
        // The production multiplayer raw-loss-first contract retains its primary axes.
        if (!rollingHorizonLossFirst && candidate.Won && current.Won)
        {
            int opportunity = ((long)candidate.StrategicHpDeficit + candidate.PotionStrategicCost)
                .CompareTo((long)current.StrategicHpDeficit + current.PotionStrategicCost);
            if (opportunity != 0) return opportunity;
        }
        int primaryQuality = SolverInterimResultOrdering.ComparePrimaryQuality(
            candidate.Won,
            candidate.StrategicHpDeficit,
            candidate.CombatEndedTurn,
            current.Won,
            current.StrategicHpDeficit,
            current.CombatEndedTurn,
            candidate.GrowthHpCredit,
            current.GrowthHpCredit,
            candidate.GrowthRewardCount,
            current.GrowthRewardCount,
            candidate.DeathSaveUseCount,
            current.DeathSaveUseCount,
            rollingHorizonLossFirst,
            candidate.ProjectedBattleHpLost,
            current.ProjectedBattleHpLost);
        if (primaryQuality != 0)
            return primaryQuality;
        if (candidate.ProjectedBattlePotionCount != current.ProjectedBattlePotionCount)
        {
            return candidate.ProjectedBattlePotionCount.CompareTo(current.ProjectedBattlePotionCount);
        }
        return 0;
    }

    private static bool PreferPlayableCurrentTurnRoute(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult candidate,
        SolverResult current)
        => MultiplayerLocalCrossTurnContracts.PreferCurrentTurnPlayableRoute(
            MultiplayerLocalCrossTurnContracts.HasActiveMultiplayerRouteSemantics(
                policy.RoutePolicy,
                root.PlayerCount),
            HasCurrentTurnCardAction(candidate),
            HasCurrentTurnCardAction(current));

    private static bool HasCurrentTurnCardAction(SolverResult result)
        => result.BestNode.Actions.Any(action =>
            action.Turn == result.StartTurnNumber
            && action.Kind == PlanActionKind.PlayCard);

    private static int? CompareCompletedResultPrimaryQuality(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult candidate,
        SolverResult current)
    {
        bool candidateWon = IsCompleteVictory(candidate);
        bool currentWon = IsCompleteVictory(current);
        bool rollingHorizonLossFirst =
            SearchEvaluationPolicy.UsesRawLossFirst(
                policy.RoutePolicy);
        bool candidateSurvives = !candidate.Snapshot.PlayerDead
            && candidate.Snapshot.ProjectedPlayerHp > 0;
        bool currentSurvives = !current.Snapshot.PlayerDead
            && current.Snapshot.ProjectedPlayerHp > 0;
        if (SearchEvaluationPolicy.UsesLocalEvaluationWindows(policy.RoutePolicy))
        {
            int coverage = RouteEvaluationWindow.CompletedCoverageRank(candidate.EvaluationWindow,
                    !candidate.Snapshot.PlayerDead && candidate.Snapshot.PlayerHp > 0)
                .CompareTo(RouteEvaluationWindow.CompletedCoverageRank(current.EvaluationWindow,
                    !current.Snapshot.PlayerDead && current.Snapshot.PlayerHp > 0));
            if (coverage != 0)
                return coverage;
            if (candidateSurvives == currentSurvives
                && !RouteEvaluationWindow.CanCompareQuality(candidate.EvaluationWindow, current.EvaluationWindow))
                return null;
        }
        int outcomeComparison = SolverInterimResultOrdering.OutcomeRank(candidateWon, candidateSurvives,
            candidate.Snapshot.ProjectedDeathSaveUseCount, rollingHorizonLossFirst)
            .CompareTo(SolverInterimResultOrdering.OutcomeRank(currentWon, currentSurvives,
                current.Snapshot.ProjectedDeathSaveUseCount, rollingHorizonLossFirst));
        if (outcomeComparison != 0)
            return outcomeComparison;
        int recovery = TheftEncounterStrategy.CompareRecovery(policy.TheftPolicy,
            candidate.OutstandingStolenResource, current.OutstandingStolenResource);
        if (recovery != 0)
            return recovery;
        return SolverInterimResultOrdering.ComparePrimaryQuality(
            candidateWon,
            StrategicHpDeficit(root, policy, candidate),
            candidate.CombatEndedTurn,
            currentWon,
            StrategicHpDeficit(root, policy, current),
            current.CombatEndedTurn,
            candidate.Snapshot.StrategyGoalHpCredit,
            current.Snapshot.StrategyGoalHpCredit,
            candidate.Snapshot.StrategyGoalCount,
            current.Snapshot.StrategyGoalCount,
            candidate.Snapshot.ProjectedDeathSaveUseCount,
            current.Snapshot.ProjectedDeathSaveUseCount,
            rollingHorizonLossFirst,
            candidate.ProjectedBattleHpLost,
            current.ProjectedBattleHpLost);
    }

    private static bool IsCompleteVictory(SolverResult result)
        => SolverInterimResultOrdering.IsCompleteVictory(
            result.BestNode.ActionCount,
            result.Snapshot.AllEnemiesDead,
            result.Snapshot.PlayerDead,
            result.Snapshot.ProjectedPlayerHp);

    internal static bool HasReachedAcceptableBattleHpLoss(
        SearchPolicySnapshot policy,
        SolverResult result)
        => policy.GrowthTargetSatisfied(result.Snapshot.GrowthRewards)
            && policy.RelicTargetsSatisfied(result.Snapshot.RelicCounters)
            && TheftEncounterStrategy.RecoverySatisfied(policy.TheftPolicy, result.OutstandingStolenResource)
            && result.Snapshot.ProjectedDeathSaveUseCount == 0
            && result.PotionCount == policy.MinimumRequiredPotionUses(result.BattlePotionsUsedSoFar)
            && policy.PotionStrategy.EvaluateForcedUses(result.PotionUses).AllForcedUsesSatisfied
            && HasReachedAcceptableBattleHpLoss(
            IsCompleteVictory(result),
            result.ProjectedBattleHpLost,
            policy.AcceptableBattleHpLoss);

    internal static bool HasReachedAcceptableBattleHpLoss(
        bool completeVictory,
        int projectedBattleHpLost,
        int acceptableBattleHpLoss)
        => completeVictory && projectedBattleHpLost <= acceptableBattleHpLoss;

    private static bool HasReachedProvablePrimaryQualityLowerBound(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
        => !policy.EffectiveHasGrowthTargets
            && policy.RelicTargets.Count == 0
            && !result.Snapshot.HasRisk && result.Snapshot.ProjectedDeathSaveUseCount == 0
            && TheftEncounterStrategy.RecoverySatisfied(policy.TheftPolicy, result.OutstandingStolenResource)
            && HasReachedProvablePrimaryQualityLowerBound(
            IsCompleteVictory(result),
            StrategicHpDeficit(root, policy, result),
            result.CombatEndedTurn,
            root.StartTurnNumber,
            ProvableStrategicHpFloor(root, StrategicBossHpRelief(root, policy)));

    internal static bool HasReachedProvablePrimaryQualityLowerBound(
        bool completeVictory,
        int strategicHpDeficit,
        int? combatEndedTurn,
        int? earliestPossibleCombatEndedTurn,
        int provableStrategicHpFloor)
    {
        if (earliestPossibleCombatEndedTurn is not { } earliestTurn)
            return false;
        return SolverInterimResultOrdering.ComparePrimaryQuality(
            completeVictory,
            strategicHpDeficit,
            combatEndedTurn,
            currentCompleteVictory: true,
            currentStrategicHpDeficit: provableStrategicHpFloor,
            currentCombatEndedTurn: earliestTurn) <= 0;
    }

    private static PrimarySearchIncumbent? BuildPrimarySearchIncumbent(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
    {
        if (SearchEvaluationPolicy.UsesRawLossFirst(policy.RoutePolicy)
            || policy.EffectiveHasGrowthTargets
            || policy.RelicTargets.Count > 0
            || result.Snapshot.HasRisk || result.Snapshot.ProjectedDeathSaveUseCount > 0
            || !IsCompleteVictory(result)
            || result.CombatEndedTurn is not { } combatEndedTurn)
            return null;
        return new PrimarySearchIncumbent(
            StrategicHpDeficit(root, policy, result),
            combatEndedTurn);
    }

    private static int SmartPotionHpRequired(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
    {
        var remainingUses = result.PotionUses.Where(use => use.Turn >= result.StartTurnNumber);
        ForcedPotionUseEvaluation forced = policy.PotionStrategy.EvaluateForcedUses(remainingUses);
        return PotionUsePolicy.SmartAuditHpRequired(
            result.PotionStrategicCostByTurn.Where(turn => turn.Key >= result.StartTurnNumber).Sum(turn => turn.Value),
            remainingUses.Count(use => use.IsPaidAmbergris),
            forced,
            root.InitialPlayerMaxHp,
            StrategicBossHpRelief(root, policy));
    }

    private static int StrategicHpDeficit(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        SolverResult result)
        => ActEndingBossPolicy.StrategicHpDeficit(
            result.Snapshot.CumulativePlayerHpLost,
            Math.Max(0, root.InitialPlayerMaxHp - result.Snapshot.PlayerMaxHp),
            result.Snapshot.RecoveredPlayerHp
                + ActEndingBossPolicy.RankedPostCombatRelicHeal(
                    root.PostCombatRelicHeal,
                    SolverInterimResultOrdering.IsCompleteVictory(
                        result.BestNode.ActionCount,
                        result.Snapshot.AllEnemiesDead,
                        result.Snapshot.PlayerDead,
                        result.Snapshot.ProjectedPlayerHp),
                    result.Snapshot.PlayerHp,
                    result.Snapshot.PlayerMaxHp),
            StrategicBossHpRelief(root, policy),
            result.Snapshot.DeathSaveHpRestored) - result.Snapshot.StrategicHpCredit;

    /// <summary>
    /// Best strategic HP result any route could still reach from this root.
    /// </summary>
    /// <remarks>
    /// Once healing counts, zero is no longer the floor. Current HP is capped by max HP, so a route can at most
    /// heal back to full, which puts the floor at the HP the player was already missing when the fight started.
    /// Treating zero as the floor while a wounded player holds a heal would declare a route provably optimal
    /// when a strictly better one exists, and stop the extra searches that would have found it.
    /// </remarks>
    private static int ProvableStrategicHpFloor(
        CombatRootSnapshot root,
        BossHpRelief relief,
        int recoveryUpperBound = int.MaxValue)
        => -ActEndingBossPolicy.PersistentValueOfRecoveredHp(
            Math.Min(recoveryUpperBound, Math.Max(0, root.InitialPlayerMaxHp - root.InitialPlayerHp)),
            relief);

    internal static bool CanAnySmartPotionQualify(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        bool potionFreeWon,
        int potionFreeHpDeficit)
        => MaximumSmartPotionUses(root, policy, potionFreeWon, potionFreeHpDeficit) > 0;

    internal static int MaximumSmartPotionUses(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        bool potionFreeWon,
        int potionFreeHpDeficit)
    {
        SearchablePotionSlotSnapshot[] allowedPotions = root.SearchablePotions
            .Where(potion => policy.PotionStrategy.AllowsExplicitUse(
                potion.Slot,
                potion.PotionId,
                SolverPotionPolicy.Smart,
                forceAllDisabled: false)
                && policy.PotionStrategy.Resolve(potion.Slot, potion.PotionId)
                    != SolverPotionDirective.Force)
            .ToArray();
        // These are action-count seats within the existing request budget, not
        // additional paid bottles. Root Force bottles already belong to baseline.
        int forcedBrews = root.SearchablePotions.Count(potion => potion.PotionId == "ENTROPIC_BREW"
            && policy.PotionStrategy.Resolve(potion.Slot, potion.PotionId) == SolverPotionDirective.Force);
        int generatedCapacity = checked(root.PotionSlotCount * (forcedBrews
            + allowedPotions.Count(potion => potion.PotionId == "ENTROPIC_BREW")));
        if (!potionFreeWon || policy.TheftPolicy == SolverTheftPolicy.PreserveResources)
            return checked(allowedPotions.Length + generatedCapacity);
        bool legacyAdmission = !policy.UseUpstreamSearchOptimizations
            || SearchEvaluationPolicy.UsesRawLossFirst(policy.RoutePolicy);
        BossHpRelief relief = StrategicBossHpRelief(root, policy);
        bool useCertifiedRecoveryBound = !legacyAdmission && root.HasPinnedHealingCertificate
            && !policy.EffectiveHasGrowthTargets && policy.RelicTargets.Count == 0
            && !policy.UseMultiplayerTeamObjective && !policy.PotionStrategy.HasForcedDirectives;
        // On a closed root, damage taken later cannot create net healing. At most
        // the certified post-combat heal can recover HP already missing at capture.
        int recoveryUpperBound = useCertifiedRecoveryBound
            ? PinnedHealingBound.RankedPostCombatHealingUpperBound(root)
            : int.MaxValue;
        long savingRoom = legacyAdmission ? Math.Max(0, potionFreeHpDeficit)
            : Math.Max(0L, (long)potionFreeHpDeficit - ProvableStrategicHpFloor(root, relief, recoveryUpperBound));
        int qualifiedGeneratedCapacity = checked(root.PotionSlotCount * (forcedBrews
            + allowedPotions.Count(potion => potion.PotionId == "ENTROPIC_BREW"
                && PotionUsePolicy.SmartRequiredHpSaved(potion.StrategicHpCost, relief) <= savingRoom)));
        if (legacyAdmission)
        {
            int paidPotionHpRequired = PotionUsePolicy.SmartRequiredHpSaved(
                SolverWeights.PotionMinimumHpSaved, relief);
            int paidPotionCapacity = paidPotionHpRequired >= int.MaxValue / 4
                ? 0 : Math.Max(0, potionFreeHpDeficit) / paidPotionHpRequired;
            return checked(Math.Min(allowedPotions.Length,
                allowedPotions.Count(potion => potion.StrategicHpCost == 0) + paidPotionCapacity)
                + qualifiedGeneratedCapacity);
        }
        // Sum the cheapest actual eligible slots. Potential recovery of the root's
        // missing HP remains available; unknown healing is never assumed absent.
        long minimumCost = 0;
        int maximum = 0, count = 0;
        foreach (var potion in allowedPotions.OrderBy(potion => potion.StrategicHpCost))
        {
            minimumCost += Math.Max(0, potion.StrategicHpCost);
            count++;
            int required = PotionUsePolicy.SmartRequiredHpSaved((int)Math.Min(int.MaxValue, minimumCost), relief);
            if (required <= savingRoom) maximum = count;
        }
        return checked(maximum + qualifiedGeneratedCapacity);
    }

    private static BossHpRelief StrategicBossHpRelief(
        CombatRootSnapshot root,
        SearchPolicySnapshot policy)
        => ActEndingBossPolicy.ResolveStrategicHpRelief(
            root.BossHpRelief,
            policy.ActTransitionBossHpStrategy,
            policy.FinalBossHpStrategy);

}
