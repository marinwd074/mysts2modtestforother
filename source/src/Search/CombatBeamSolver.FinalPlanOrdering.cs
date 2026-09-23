using System.Text;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private sealed class FinalPlanOrdering(
        SolverPotionPolicy potionPolicy,
        PotionStrategySnapshot potionStrategy,
        bool enforcePotionDirectives,
        bool renewablePotionShapedRock,
        SolverTheftPolicy? theftPolicy,
        BossHpRelief bossHpRelief,
        PostCombatRelicHealProfile postCombatRelicHeal,
        PotionFreePolicyBaseline? potionFreePolicyBaseline,
        int initialPlayerMaxHp,
        int minimumPotionUses,
        SearchDiagnosticsSink diagnostics,
        bool detailedDiagnostics,
        SearchRoutePolicy routePolicy,
        MultiplayerCombatObjectiveStrategy multiplayerCombatObjectiveStrategy,
        double multiplayerEnemyDurabilityRatio,
        int multiplayerEnemyMaximumHp,
        int startTurnNumber,
        MultiplayerCarryRankingContext carryRankingContext,
        BattleDamageSnapshot battleDamage,
        PotionStrategicCostLookup? potionStrategicCosts = null)
    {
        private readonly PotionStrategicCostLookup _potionStrategicCosts = potionStrategicCosts ?? new();
        /// <summary>
        /// The HP a potion must save to be worth spending, scaled by how much HP is worth in this fight. When HP
        /// buys nothing, no amount of saved HP justifies a potion and only the win/lose escape in
        /// <see cref="PotionUsePolicy.IsEligible"/> can still admit one.
        /// </summary>
        private int ScalePotionCost(int strategicHpCost)
            => PotionUsePolicy.SmartRequiredHpSaved(strategicHpCost, bossHpRelief);

        private static SearchNode CarryObservationNode(
            SearchNode candidate,
            int rootTurn)
        {
            SearchNode current = candidate;
            while (current.Parent != null)
            {
                PlanAction? action = current.Action;
                if (action != null && action.Turn == rootTurn)
                {
                    bool endsPlayerTurn = action.Kind == PlanActionKind.EndTurn;
                    return MultiplayerCarryRankingContracts.IsCurrentThreatWindowAction(
                            rootTurn,
                            action.Turn,
                            endsPlayerTurn)
                        ? current
                        : current.Parent;
                }

                current = current.Parent;
            }

            return current;
        }

        private static MultiplayerCarryEvaluation EvaluateCarry(
            MultiplayerCarryRankingContext context,
            SimulationSnapshot snapshot,
            bool allEnemiesDead)
        {
            if (!context.Enabled || context.RemotePlayers.Count == 0)
            {
                return MultiplayerCarryEvaluation.Disabled(
                    context.Enabled ? "no_remote_teammate" : "disabled");
            }

            MultiplayerCarryEnemyOutcome[] enemiesAfter = new MultiplayerCarryEnemyOutcome[
                snapshot.EnemyDurabilityByCombatId.Count];
            for (int index = 0; index < enemiesAfter.Length; index++)
            {
                EnemyDurabilityEntry entry = snapshot.EnemyDurabilityByCombatId[index];
                enemiesAfter[index] = new MultiplayerCarryEnemyOutcome(
                    entry.CombatId,
                    entry.Durability);
            }

            return MultiplayerCarryRankingEvaluator.Evaluate(
                context,
                MultiplayerCarryCandidateObservation.Create(allEnemiesDead, enemiesAfter));
        }

        private sealed record ChanceDecisionSummary(
            string DecisionKey,
            MultiplayerChanceDecisionRank Rank,
            bool HasShadowChance,
            bool ProbabilityTrusted,
            int BaselineIndex);

        private static bool TryGetCurrentTurnShadowOutcome(
            SearchNode candidate,
            int rootTurn,
            out SearchNode outcomeNode,
            out ShadowForecastPlan forecast)
        {
            SearchNode? current = candidate;
            while (current?.Parent != null)
            {
                PlanAction? action = current.Action;
                if (action is
                    {
                        Kind: PlanActionKind.EndTurn,
                        ShadowForecast: not null
                    }
                    && action.Turn == rootTurn)
                {
                    outcomeNode = current;
                    forecast = action.ShadowForecast;
                    return true;
                }
                current = current.Parent;
            }

            outcomeNode = null!;
            forecast = null!;
            return false;
        }

        private static string CurrentTurnDecisionKey(
            SearchNode candidate,
            int rootTurn)
        {
            StringBuilder key = new();
            foreach (PlanAction action in candidate.Actions)
            {
                if (action.Turn != rootTurn)
                    continue;
                AppendDecisionAction(key, action);
                if (action.Kind == PlanActionKind.EndTurn)
                    break;
            }
            return key.ToString();
        }

        private static void AppendDecisionAction(
            StringBuilder key,
            PlanAction action)
        {
            key.Append((int)action.Kind).Append(':')
                .Append(action.CardId).Append(':')
                .Append(action.CardOccurrence).Append(':')
                .Append(action.CardStateKey).Append(':')
                .Append(action.CardStateOccurrence).Append(':')
                .Append(action.CardUpgradeLevel).Append(':')
                .Append(action.CardEnchantmentId).Append(':')
                .Append(action.TargetCombatId?.ToString() ?? "-").Append(':')
                .Append(action.PotionSlot).Append(':')
                .Append(action.PotionId).Append(':')
                .Append(action.EndsPlayerTurn ? '1' : '0').Append('|');
            AppendDecisionChoice(key, action.Choice);
            if (action.NestedChoices != null)
            {
                key.Append("N").Append(action.NestedChoicesBeforePrimary).Append('[');
                foreach (PlanCardChoice choice in action.NestedChoices)
                    AppendDecisionChoice(key, choice);
                key.Append(']');
            }
            if (action.TurnStartChoices != null)
            {
                key.Append("T[");
                foreach (PlanCardChoice choice in action.TurnStartChoices)
                    AppendDecisionChoice(key, choice);
                key.Append(']');
            }
            key.Append(';');
        }

        private static void AppendDecisionChoice(
            StringBuilder key,
            PlanCardChoice? choice)
        {
            if (choice == null)
            {
                key.Append('-');
                return;
            }
            key.Append((int)choice.Effect).Append(':')
                .Append((int)choice.SourcePile).Append(':')
                .Append(choice.SourceId).Append(':')
                .Append(choice.ContextId).Append(':')
                .Append((int)choice.Timing).Append('[');
            foreach (PlanCardToken card in choice.Cards)
            {
                key.Append(card.CardId).Append(':')
                    .Append(card.UpgradeLevel).Append(':')
                    .Append(card.StateKey).Append(':')
                    .Append(card.SourceOccurrence).Append(':')
                    .Append(card.OptionOccurrence).Append(',');
            }
            key.Append(']');
        }

        private MultiplayerChanceOutcome BuildChanceOutcome(
            SearchNode outcomeNode,
            double probabilityMass)
        {
            SimulationSnapshot snapshot = outcomeNode.Snapshot;
            bool completeVictory = SolverInterimResultOrdering.IsCompleteVictory(
                outcomeNode.ActionCount,
                snapshot.AllEnemiesDead,
                snapshot.PlayerDead,
                snapshot.ProjectedPlayerHp);
            double enemyDurabilityRatio =
                MultiplayerCombatObjectivePolicy.ComputeEnemyDurabilityRatio(
                    snapshot.EnemyDurabilityByCombatId,
                    multiplayerEnemyMaximumHp);
            MultiplayerCombatObjectiveRank objective =
                MultiplayerCombatObjectiveMath.BuildRank(
                    multiplayerCombatObjectiveStrategy,
                    completeVictory,
                    snapshot.AllPlayersAlive,
                    snapshot.TeamLossRatio,
                    snapshot.WorstPlayerLossRatio,
                    enemyDurabilityRatio,
                    multiplayerEnemyDurabilityRatio,
                    completeVictory ? snapshot.CombatEndedTurn : null,
                    startTurnNumber);
            return new MultiplayerChanceOutcome(
                probabilityMass,
                completeVictory,
                snapshot.AllPlayersAlive,
                objective.LossEquivalent,
                objective.WorstPlayerLossRatio,
                objective.TeamLossRatio,
                objective.EnemyDurabilityRatio);
        }

        public FinalPlanSelection Select(
            IReadOnlyList<(SearchNode Node, SimulationSnapshot Snapshot)> evaluated,
            int initialHp,
            bool emitDiagnostics)
        {
            var policyCandidates = evaluated
                .Select(candidate =>
                {
                    SearchFeatures features = SearchFeatures.Capture(candidate.Node);
                    int sold = features.FutureSoldHp;
                    int battleSold = battleDamage.SoldHpCommitted + sold;
                    int potionCount = features.PotionCount;
                    int explicitPotionCount = PotionUsePolicy.ExplicitUseCount(
                        potionCount,
                        candidate.Snapshot.AutomaticPotionUseCount);
                    int ambergrisCount = candidate.Node.Actions.Count(action =>
                        action.Kind == PlanActionKind.UsePotion
                        && string.Equals(action.PotionId, "AMBERGRIS", StringComparison.Ordinal));
                    ForcedPotionUseEvaluation forced = enforcePotionDirectives
                        ? potionStrategy.EvaluateForcedUses(
                            candidate.Node.Actions,
                            renewablePotionShapedRock,
                            _potionStrategicCosts)
                        : new ForcedPotionUseEvaluation(true, 0, 0, 0);
                    int explicitPotionStrategicCost = candidate.Node.Actions
                        .Where(action => action.Kind == PlanActionKind.UsePotion)
                        .Sum(action => _potionStrategicCosts.Get(
                            action.PotionId!,
                            renewablePotionShapedRock));
                    int optionalPotionCount = Math.Max(
                        0,
                        explicitPotionCount - forced.ForcedUseCount);
                    int optionalPotionStrategicCost = Math.Max(
                        0,
                        explicitPotionStrategicCost - forced.ForcedStrategicHpCost);
                    int optionalAmbergrisCount = Math.Max(0, ambergrisCount - forced.ForcedAmbergrisCount);
                    SolverPotionPolicy effectivePotionPolicy = potionPolicy switch
                    {
                        SolverPotionPolicy.RequireAtLeastOne when forced.ForcedUseCount > 0
                            => SolverPotionPolicy.Smart,
                        SolverPotionPolicy.Disabled when optionalPotionCount > 0
                            => SolverPotionPolicy.Smart,
                        _ => potionPolicy,
                    };
                    int hpDeficit = features.CumulativePlayerHpLost;
                    int maxHpDeficit = Math.Max(0, initialPlayerMaxHp - features.PlayerMaxHp);
                    bool completeVictory = SolverInterimResultOrdering.IsCompleteVictory(
                        candidate.Node.ActionCount,
                        features.AllEnemiesDead,
                        candidate.Snapshot.PlayerDead,
                        features.ProjectedPlayerHp);
                    double candidateEnemyDurabilityRatio =
                        MultiplayerCombatObjectivePolicy.ComputeEnemyDurabilityRatio(
                            candidate.Snapshot.EnemyDurabilityByCombatId,
                            multiplayerEnemyMaximumHp);
                    MultiplayerCombatObjectiveRank multiplayerObjective =
                        routePolicy == SearchRoutePolicy.MultiplayerLocalCrossTurn
                            ? MultiplayerCombatObjectiveMath.BuildRank(
                                multiplayerCombatObjectiveStrategy,
                                completeVictory,
                                candidate.Snapshot.AllPlayersAlive,
                                candidate.Snapshot.TeamLossRatio,
                                candidate.Snapshot.WorstPlayerLossRatio,
                                candidateEnemyDurabilityRatio,
                                multiplayerEnemyDurabilityRatio,
                                completeVictory ? candidate.Snapshot.CombatEndedTurn : null,
                                startTurnNumber)
                            : default;
                    // Relics that heal on victory pay out after the fight, so their HP never reaches
                    // RecoveredPlayerHp, yet it carries into the next fight exactly like in-combat healing.
                    int relicHeal = ActEndingBossPolicy.RankedPostCombatRelicHeal(
                        postCombatRelicHeal,
                        completeVictory,
                        features.PlayerHp,
                        features.PlayerMaxHp);
                    int strategicHpDeficit = ActEndingBossPolicy.StrategicHpDeficit(
                        hpDeficit,
                        maxHpDeficit,
                        features.RecoveredPlayerHp + relicHeal,
                        bossHpRelief,
                        features.DeathSaveHpRestored) - candidate.Snapshot.StrategicHpCredit;
                    int healthResourceCost = initialHp - features.PlayerHp
                        + initialPlayerMaxHp - features.PlayerMaxHp;
                    int strategicSold = battleSold;
                    int policyHpDeficit = strategicHpDeficit
                        + (effectivePotionPolicy == SolverPotionPolicy.RequireAtLeastOne
                            ? PotionUsePolicy.AdditionalRequiredUseStrategicHpCost(
                                optionalPotionStrategicCost)
                            : 0);
                    SearchNode carryObservationNode =
                        CarryObservationNode(candidate.Node, startTurnNumber);
                    SimulationSnapshot carryObservationSnapshot = carryObservationNode.Snapshot;
                    MultiplayerCarryEvaluation carryEvaluation = EvaluateCarry(
                        carryRankingContext,
                        carryObservationSnapshot,
                        carryObservationSnapshot.AllEnemiesDead);
                    return (candidate.Node, candidate.Snapshot, Features: features,
                        CompleteVictory: completeVictory,
                        CombatEndedTurn: completeVictory ? candidate.Snapshot.CombatEndedTurn : null,
                        FutureSold: sold, BattleSold: battleSold, PotionCount: potionCount,
                        ExplicitPotionCount: explicitPotionCount, HpDeficit: hpDeficit,
                        StrategicHpDeficit: strategicHpDeficit, PolicyHpDeficit: policyHpDeficit,
                        MaxHpDeficit: maxHpDeficit, HealthResourceCost: healthResourceCost,
                        StrategicSold: strategicSold, PotionStrategicCost: candidate.Node.PotionStrategicCost,
                        AmbergrisCount: ambergrisCount, Score: features.Score,
                        ForcedUsesSatisfied: forced.AllForcedUsesSatisfied,
                        OptionalPotionCount: optionalPotionCount,
                        OptionalPotionStrategicCost: optionalPotionStrategicCost,
                        OptionalAmbergrisCount: optionalAmbergrisCount,
                        EffectivePotionPolicy: effectivePotionPolicy,
                        MultiplayerObjective: multiplayerObjective,
                        CarryEvaluation: carryEvaluation,
                        CarryObservationActionCount: carryObservationNode.ActionCount,
                        CarryCompatibility: new MultiplayerCarryCompatibilityKey(
                            CompleteVictory: completeVictory,
                            DeadFallbackRank: !completeVictory
                                && (candidate.Snapshot.PlayerDead
                                    || candidate.Snapshot.ProjectedPlayerHp <= 0)
                                    ? 1
                                    : 0,
                            DeathSaveUseCount: candidate.Snapshot.ProjectedDeathSaveUseCount,
                            PreservedStolenResource: theftPolicy == SolverTheftPolicy.PreserveResources
                                ? features.OutstandingStolenResource
                                : 0,
                            StrategicHpDeficit: strategicHpDeficit,
                            StrategyGoalHpCredit: candidate.Snapshot.StrategyGoalHpCredit,
                            StrategyGoalCount: candidate.Snapshot.StrategyGoalCount,
                            CombatEndedTurn: completeVictory
                                ? candidate.Snapshot.CombatEndedTurn ?? int.MaxValue
                                : int.MaxValue,
                            PolicyHpDeficit: policyHpDeficit,
                            HealthResourceCost: healthResourceCost,
                            LongTermResourceValue: features.LongTermResourceValue,
                            AngerCopiesGenerated: features.AngerCopiesGenerated,
                            BoundaryRank: CombatBeamSolver.PolicyBoundaryRank(features.BoundaryReason),
                            OptionalPotionCount: optionalPotionCount,
                            StrategicSold: strategicSold,
                            EnemyHp: features.EnemyHp),
                        HasCurrentTurnCardAction: candidate.Node.Actions.Any(action =>
                            action.Turn == startTurnNumber
                            && action.Kind == PlanActionKind.PlayCard));
                })
                .ToList();
            if (emitDiagnostics && detailedDiagnostics)
            {
                foreach (var potionGroup in policyCandidates
                             .GroupBy(candidate => candidate.PotionCount)
                             .OrderBy(group => group.Key))
                {
                    var diagnostic = potionGroup
                        .OrderByDescending(candidate => candidate.CompleteVictory)
                        .ThenByDescending(candidate => candidate.Features.ProjectedPlayerHp)
                        .ThenBy(candidate => candidate.Features.EnemyHp)
                        .ThenByDescending(candidate => candidate.Score)
                        .First();
                    diagnostics.Info(
                        $"[CombatSolver/Debug] POTION_FINAL_CANDIDATE count={potionGroup.Key} " +
                        $"won={diagnostic.CompleteVictory} hp={diagnostic.Snapshot.PlayerHp} " +
                        $"projected_hp={diagnostic.Features.ProjectedPlayerHp} " +
                        $"enemy_hp={diagnostic.Features.EnemyHp} " +
                        $"actions={string.Join(',', diagnostic.Node.Actions.Select(CombatBeamSolver.PolicyActionToken))}");
                }
            }
            int potionFreeBaselineIndex = -1;
            for (int index = 0; index < policyCandidates.Count; index++)
            {
                if (policyCandidates[index].ExplicitPotionCount != 0
                    || potionFreeBaselineIndex >= 0
                        && ComparePotionFreePolicyBaselines(
                            policyCandidates[index].Node,
                            policyCandidates[potionFreeBaselineIndex].Node,
                            initialHp,
                            initialPlayerMaxHp,
                            bossHpRelief,
                            postCombatRelicHeal,
                            theftPolicy,
                            routePolicy,
                            multiplayerCombatObjectiveStrategy,
                            multiplayerEnemyDurabilityRatio,
                            multiplayerEnemyMaximumHp,
                            startTurnNumber) >= 0)
                {
                    continue;
                }
                potionFreeBaselineIndex = index;
            }
            bool hasPotionFreeBaseline = potionFreeBaselineIndex >= 0;
            bool potionFreeWon = hasPotionFreeBaseline
                && policyCandidates[potionFreeBaselineIndex].CompleteVictory;
            int potionFreeStrategicHpDeficit = hasPotionFreeBaseline
                ? policyCandidates[potionFreeBaselineIndex].StrategicHpDeficit
                : initialHp;
            int potionFreePlayerHp = hasPotionFreeBaseline
                ? policyCandidates[potionFreeBaselineIndex].Snapshot.PlayerHp
                : 0;
            int? potionFreeCombatEndedTurn = hasPotionFreeBaseline
                ? policyCandidates[potionFreeBaselineIndex].CombatEndedTurn
                : null;
            int potionFreeOutstandingResource = hasPotionFreeBaseline
                ? policyCandidates[potionFreeBaselineIndex].Features.OutstandingStolenResource
                : int.MaxValue;
            int potionFreeDeathSaveUseCount = hasPotionFreeBaseline
                ? policyCandidates[potionFreeBaselineIndex].Snapshot.ProjectedDeathSaveUseCount
                : int.MaxValue;
            if (potionFreePolicyBaseline is { } auditedBaseline)
            {
                hasPotionFreeBaseline = true;
                potionFreeWon = auditedBaseline.Won;
                potionFreeStrategicHpDeficit = auditedBaseline.HpDeficit;
                potionFreePlayerHp = auditedBaseline.PlayerHp;
                potionFreeCombatEndedTurn = auditedBaseline.CombatEndedTurn;
                potionFreeDeathSaveUseCount = auditedBaseline.DeathSaveUseCount;
            }
            bool anyRouteWon = potionFreeWon
                || policyCandidates.Any(candidate => candidate.CompleteVictory);
            if (emitDiagnostics)
            {
                if (potionFreeBaselineIndex >= 0)
                {
                    var potionFreeBaseline = policyCandidates[potionFreeBaselineIndex];
                    diagnostics.Info(
                        $"[CombatSolver/Test] POLICY_BASELINE kind=potion_free " +
                        $"won={potionFreeWon} hp_deficit={potionFreeBaseline.HpDeficit} " +
                        $"enemy_hp={potionFreeBaseline.Features.EnemyHp} " +
                        $"boundary={potionFreeBaseline.Features.BoundaryReason} " +
                        $"actions={string.Join(',', potionFreeBaseline.Node.Actions.Select(CombatBeamSolver.PolicyActionToken))}");
                }
                else
                {
                    diagnostics.Info(
                        $"[CombatSolver/Test] POLICY_BASELINE kind=potion_free missing=true " +
                        $"won=false hp_deficit={initialHp}");
                }
                if (potionFreePolicyBaseline is { } baselineOverride)
                {
                    diagnostics.Info(
                        $"[CombatSolver/Test] POLICY_BASELINE_OVERRIDE kind=potion_free " +
                        $"won={baselineOverride.Won} hp_deficit={baselineOverride.HpDeficit}");
                }
            }
            var policyEligibleCandidates = policyCandidates
                .Where(candidate =>
                {
                    bool strictPrimaryImprovement = hasPotionFreeBaseline
                        && potionPolicy != SolverPotionPolicy.Disabled
                        && candidate.OptionalPotionCount > 0
                        && SolverInterimResultOrdering.ComparePrimaryQuality(
                            candidate.CompleteVictory,
                            candidate.StrategicHpDeficit,
                            candidate.CombatEndedTurn,
                            potionFreeWon,
                            potionFreeStrategicHpDeficit,
                            potionFreeCombatEndedTurn,
                            candidateDeathSaveUseCount: candidate.Snapshot.ProjectedDeathSaveUseCount,
                            currentDeathSaveUseCount: potionFreeDeathSaveUseCount) < 0;
                    bool passesSoftPotionPolicy = PotionUsePolicy.IsEligible(
                            candidate.EffectivePotionPolicy,
                            candidate.OptionalPotionCount,
                            ScalePotionCost(candidate.OptionalPotionStrategicCost),
                            potionFreeWon,
                            potionFreeStrategicHpDeficit,
                            anyRouteWon,
                            candidate.CompleteVictory,
                            candidate.StrategicHpDeficit)
                        || strictPrimaryImprovement
                        || theftPolicy == SolverTheftPolicy.PreserveResources
                            && candidate.PotionCount > 0
                            && candidate.Features.OutstandingStolenResource
                                < potionFreeOutstandingResource;
                    bool passesAmbergrisPolicy = strictPrimaryImprovement
                        || theftPolicy == SolverTheftPolicy.PreserveResources
                            && candidate.Features.OutstandingStolenResource < potionFreeOutstandingResource
                        || PotionUsePolicy.MeetsAmbergrisRestriction(
                            hasPotionFreeBaseline,
                            candidate.OptionalAmbergrisCount,
                            candidate.OptionalPotionStrategicCost,
                            initialPlayerMaxHp,
                            potionFreePlayerHp,
                            candidate.Snapshot.PlayerHp);
                    return candidate.ForcedUsesSatisfied
                        && candidate.ExplicitPotionCount >= minimumPotionUses
                        && passesSoftPotionPolicy
                        && passesAmbergrisPolicy;
                })
                .ToList();
            bool useTeamObjective =
                routePolicy == SearchRoutePolicy.MultiplayerLocalCrossTurn;
            bool useAdaptiveLethalTempo =
                MultiplayerCombatObjectivePolicy.UsesAdaptiveLethalTempo(
                    routePolicy,
                    multiplayerCombatObjectiveStrategy);
            var selected = policyEligibleCandidates
                .OrderByDescending(candidate => candidate.CompleteVictory)
                .ThenBy(candidate => useTeamObjective
                    && !candidate.MultiplayerObjective.AllPlayersAlive ? 1 : 0)
                .ThenBy(candidate => useTeamObjective
                    ? candidate.MultiplayerObjective.LossEquivalent
                    : 0d)
                .ThenBy(candidate => useTeamObjective
                    ? candidate.MultiplayerObjective.WorstPlayerLossRatio
                    : 0d)
                .ThenBy(candidate => useTeamObjective
                    ? candidate.MultiplayerObjective.TeamLossRatio
                    : 0d)
                .ThenBy(candidate => useTeamObjective && !candidate.CompleteVictory
                    ? candidate.MultiplayerObjective.EnemyDurabilityRatio
                    : 0d)
                .ThenBy(candidate => useTeamObjective && candidate.CompleteVictory
                    ? candidate.MultiplayerObjective.CombatEndedTurn
                    : 0)
                // A live incomplete fallback is always preferable to a dead fallback. For
                // complete victories this key is uniformly zero and cannot weaken the
                // requested loss-then-duration ordering.
                .ThenBy(candidate => !candidate.CompleteVictory
                    && (candidate.Snapshot.PlayerDead
                        || candidate.Snapshot.ProjectedPlayerHp <= 0)
                        ? 1
                        : 0)
                .ThenBy(candidate => candidate.Snapshot.ProjectedDeathSaveUseCount)
                .ThenBy(candidate => theftPolicy == SolverTheftPolicy.PreserveResources
                    ? candidate.Features.OutstandingStolenResource : 0)
                // Local HP/resource policy remains a secondary tie-break after the multiplayer
                // team objective. Single-player reaches this point with no added team keys.
                // Compare HP after the requested recovery objective.
                .ThenBy(candidate => candidate.StrategicHpDeficit)
                .ThenByDescending(candidate => candidate.Snapshot.StrategyGoalHpCredit)
                .ThenByDescending(candidate => candidate.Snapshot.StrategyGoalCount)
                .ThenBy(candidate => candidate.CombatEndedTurn ?? int.MaxValue)
                .ThenBy(candidate => candidate.PolicyHpDeficit)
                .ThenBy(candidate => candidate.HealthResourceCost)
                .ThenByDescending(candidate => candidate.Features.LongTermResourceValue)
                .ThenBy(candidate =>
                    MultiplayerLocalCrossTurnContracts.DelayAngerCopyPreferenceUntilAfterEnemyHp(
                        routePolicy,
                        candidate.CompleteVictory)
                        ? 0
                        : candidate.Features.AngerCopiesGenerated)
                .ThenBy(candidate => CombatBeamSolver.PolicyBoundaryRank(candidate.Features.BoundaryReason))
                .ThenBy(candidate => candidate.OptionalPotionCount)
                .ThenBy(candidate => candidate.StrategicSold)
                .ThenBy(candidate => candidate.Features.EnemyHp)
                .ThenBy(candidate =>
                    MultiplayerLocalCrossTurnContracts.DelayAngerCopyPreferenceUntilAfterEnemyHp(
                        routePolicy,
                        candidate.CompleteVictory)
                        ? candidate.Features.AngerCopiesGenerated
                        : 0)
                // Carry is a final multiplayer tie-break after local safety, resource,
                // potion, and enemy-health ordering. It cannot outrank hard local quality.
                .ThenByDescending(candidate => candidate.CarryEvaluation.CarryPreference)
                .ThenByDescending(candidate => candidate.Score)
                // A local-cross-turn route is deployed one turn at a time. When all
                // preceding quality keys tie, keep an actual current-turn card action
                // instead of letting the shorter-action tie-break turn a playable turn
                // into an empty recommendation followed by EndTurn.
                .ThenByDescending(candidate => routePolicy == SearchRoutePolicy.MultiplayerLocalCrossTurn
                    && candidate.HasCurrentTurnCardAction)
                .ThenBy(candidate => candidate.Features.ActionCount)
                .ToList();

            ChanceDecisionSummary? selectedChanceDecision = null;
            bool chanceAggregationEnabled = false;
            if (useTeamObjective && selected.Count > 0)
            {
                Dictionary<SearchNode, int> baselineIndex =
                    new(ReferenceEqualityComparer.Instance);
                for (int index = 0; index < selected.Count; index++)
                    baselineIndex[selected[index].Node] = index;

                List<ChanceDecisionSummary> chanceDecisions = [];
                foreach (var decisionGroup in selected.GroupBy(candidate =>
                             CurrentTurnDecisionKey(candidate.Node, startTurnNumber)))
                {
                    Dictionary<StateFingerprint, MultiplayerChanceOutcome> outcomes = [];
                    bool hasShadowChance = false;
                    bool probabilityTrusted = true;
                    foreach (var candidate in decisionGroup)
                    {
                        if (TryGetCurrentTurnShadowOutcome(
                                candidate.Node,
                                startTurnNumber,
                                out SearchNode outcomeNode,
                                out ShadowForecastPlan forecast))
                        {
                            hasShadowChance = true;
                            probabilityTrusted &= forecast.ScenarioProbabilityTrusted;
                            if (!outcomes.ContainsKey(forecast.ScenarioFingerprint))
                            {
                                outcomes.Add(
                                    forecast.ScenarioFingerprint,
                                    BuildChanceOutcome(
                                        outcomeNode,
                                        forecast.ScenarioProbabilityMass));
                            }
                        }
                    }

                    if (!hasShadowChance)
                    {
                        var deterministic = decisionGroup
                            .OrderBy(candidate => baselineIndex[candidate.Node])
                            .First();
                        outcomes.Add(
                            deterministic.Node.StateKey,
                            new MultiplayerChanceOutcome(
                                1d,
                                deterministic.CompleteVictory,
                                deterministic.Snapshot.AllPlayersAlive,
                                deterministic.MultiplayerObjective.LossEquivalent,
                                deterministic.MultiplayerObjective.WorstPlayerLossRatio,
                                deterministic.MultiplayerObjective.TeamLossRatio,
                                deterministic.MultiplayerObjective.EnemyDurabilityRatio));
                    }

                    chanceDecisions.Add(new ChanceDecisionSummary(
                        decisionGroup.Key,
                        MultiplayerChanceDecisionMath.Aggregate([.. outcomes.Values]),
                        hasShadowChance,
                        probabilityTrusted,
                        decisionGroup.Min(candidate => baselineIndex[candidate.Node])));
                }

                chanceAggregationEnabled = chanceDecisions.Any(decision => decision.HasShadowChance)
                    && chanceDecisions
                        .Where(decision => decision.HasShadowChance)
                        .All(decision => decision.ProbabilityTrusted);
                if (chanceAggregationEnabled)
                {
                    selectedChanceDecision = chanceDecisions
                        .OrderBy(decision => decision.Rank.ConservativeFailureProbability)
                        .ThenBy(decision => decision.Rank.ConservativeTeamDeathProbability)
                        .ThenBy(decision => decision.Rank.ExpectedLossEquivalentUpper)
                        .ThenBy(decision => decision.Rank.ExpectedWorstPlayerLossRatioUpper)
                        .ThenBy(decision => decision.Rank.ExpectedTeamLossRatioUpper)
                        .ThenBy(decision => decision.Rank.ExpectedEnemyDurabilityRatioUpper)
                        .ThenByDescending(decision => decision.Rank.RetainedProbabilityMass)
                        .ThenBy(decision => decision.BaselineIndex)
                        .First();

                    string winningDecisionKey = selectedChanceDecision.DecisionKey;
                    selected = selected
                        .Where(candidate =>
                            CurrentTurnDecisionKey(candidate.Node, startTurnNumber)
                                == winningDecisionKey)
                        .OrderByDescending(candidate =>
                            TryGetCurrentTurnShadowOutcome(
                                candidate.Node,
                                startTurnNumber,
                                out _,
                                out ShadowForecastPlan forecast)
                                ? forecast.ScenarioProbabilityMass
                                : 1d)
                        .ThenBy(candidate => baselineIndex[candidate.Node])
                        .Concat(selected.Where(candidate =>
                            CurrentTurnDecisionKey(candidate.Node, startTurnNumber)
                                != winningDecisionKey))
                        .ToList();
                }
            }

            if (emitDiagnostics
                && routePolicy == SearchRoutePolicy.MultiplayerLocalCrossTurn
                && selected.Count > 0)
            {
                if (selectedChanceDecision != null)
                {
                    MultiplayerChanceDecisionRank chanceRank = selectedChanceDecision.Rank;
                    diagnostics.Info(
                        $"[CombatSolver/Multiplayer] MP_SHADOW_CHANCE " +
                        $"enabled={chanceAggregationEnabled.ToString().ToLowerInvariant()} " +
                        $"trusted={selectedChanceDecision.ProbabilityTrusted.ToString().ToLowerInvariant()} " +
                        $"retained_probability_mass={chanceRank.RetainedProbabilityMass:0.0000} " +
                        $"failure_upper={chanceRank.ConservativeFailureProbability:0.0000} " +
                        $"team_death_upper={chanceRank.ConservativeTeamDeathProbability:0.0000} " +
                        $"expected_loss_upper={chanceRank.ExpectedLossEquivalentUpper:0.0000} " +
                        $"expected_enemy_durability_upper={chanceRank.ExpectedEnemyDurabilityRatioUpper:0.0000}");
                }
                else
                {
                    diagnostics.Info(
                        $"[CombatSolver/Multiplayer] MP_SHADOW_CHANCE enabled=false trusted=false reason=no_trusted_shadow_chance");
                }

                diagnostics.Info(
                    $"[CombatSolver/Multiplayer] MP_OBJECTIVE strategy={multiplayerCombatObjectiveStrategy} " +
                    $"enemy_durability_ratio={multiplayerEnemyDurabilityRatio:0.000} " +
                    $"candidate_enemy_durability_ratio={selected[0].MultiplayerObjective.EnemyDurabilityRatio:0.0000} " +
                    $"objective_loss_equivalent={selected[0].MultiplayerObjective.LossEquivalent:0.0000} " +
                    $"team_loss_ratio={selected[0].Snapshot.TeamLossRatio:0.0000} " +
                    $"worst_player_loss_ratio={selected[0].Snapshot.WorstPlayerLossRatio:0.0000} " +
                    $"all_players_alive={selected[0].Snapshot.AllPlayersAlive.ToString().ToLowerInvariant()} " +
                    $"adaptive_tempo={useAdaptiveLethalTempo.ToString().ToLowerInvariant()} " +
                    $"tempo_urgency={MultiplayerCombatObjectiveMath.ComputeLethalUrgency(multiplayerEnemyDurabilityRatio):0.0000} " +
                    $"team_risk_factor={MultiplayerCombatObjectiveMath.ComputeTeamRiskFactor(selected[0].Snapshot.WorstPlayerLossRatio):0.0000} " +
                    $"loss_ratio_per_turn={MultiplayerCombatObjectiveMath.LossRatioPerTurn(multiplayerEnemyDurabilityRatio, selected[0].Snapshot.WorstPlayerLossRatio):0.0000} " +
                    $"max_loss_ratio_per_turn={MultiplayerCombatObjectiveMath.MaximumExtraLossRatioPerTurn:0.0000}");
            }
            if (selected.Count == 0)
            {
                throw new PotionPolicyUnsatisfiedException(
                    enforcePotionDirectives && potionStrategy.HasForcedDirectives
                        ? $"指定药水必须使用，但搜索没有找到可执行路线：{potionStrategy.DescribeForcedUses()}。"
                        : potionPolicy == SolverPotionPolicy.RequireAtLeastOne
                        ? "本场药水策略要求至少使用一瓶，但搜索没有找到可执行的用药路线。"
                        : "本场药水策略没有可执行路线。");
            }
            var selectedCandidate = selected[0];
            var carryFreeWinner = policyEligibleCandidates
                .Where(candidate =>
                    candidate.CarryCompatibility == selectedCandidate.CarryCompatibility)
                .OrderBy(candidate => useTeamObjective
                    && !candidate.MultiplayerObjective.AllPlayersAlive ? 1 : 0)
                .ThenBy(candidate => useTeamObjective
                    ? candidate.MultiplayerObjective.LossEquivalent : 0d)
                .ThenBy(candidate => useTeamObjective
                    ? candidate.MultiplayerObjective.WorstPlayerLossRatio : 0d)
                .ThenBy(candidate => useTeamObjective
                    ? candidate.MultiplayerObjective.TeamLossRatio : 0d)
                .ThenBy(candidate => useTeamObjective && !candidate.CompleteVictory
                    ? candidate.MultiplayerObjective.EnemyDurabilityRatio : 0d)
                .ThenBy(candidate => useTeamObjective && candidate.CompleteVictory
                    ? candidate.MultiplayerObjective.CombatEndedTurn : 0)
                .ThenByDescending(candidate => candidate.Score)
                .ThenByDescending(candidate =>
                    routePolicy == SearchRoutePolicy.MultiplayerLocalCrossTurn
                    && candidate.HasCurrentTurnCardAction)
                .ThenBy(candidate => candidate.Features.ActionCount)
                .First();
            bool selectedWouldAlreadyWinWithoutCarry =
                ReferenceEquals(selectedCandidate.Node, carryFreeWinner.Node);
            bool carryDecisive = MultiplayerCarryRankingContracts.IsDecisiveTieBreak(
                selectedCandidate.CarryCompatibility,
                selectedCandidate.CarryEvaluation.CarryPreference,
                carryFreeWinner.CarryCompatibility,
                carryFreeWinner.CarryEvaluation.CarryPreference,
                selectedWouldAlreadyWinWithoutCarry);
            if (emitDiagnostics && carryRankingContext.Enabled)
            {
                foreach (var (candidate, index) in selected.Take(3).Select((item, index) => (item, index)))
                {
                    MultiplayerCarryEvaluation carry = candidate.CarryEvaluation;
                    diagnostics.Info(
                        $"[CombatSolver/MultiplayerCarry] MP_CARRY_RANKING " +
                        $"rank={index + 1} selected={(index == 0).ToString().ToLowerInvariant()} " +
                        $"enabled={carry.Enabled.ToString().ToLowerInvariant()} " +
                        $"remoteRiskBefore={carry.RemoteRiskBefore} " +
                        $"remoteRiskAfter={carry.RemoteRiskAfter} " +
                        $"threatsRemoved={carry.ThreatsRemoved} " +
                        $"unknownRiskCount={carry.UnknownRiskCount} " +
                        $"carryPreference={carry.CarryPreference} " +
                        $"carryPreferenceReason={carry.Reason} " +
                        "carryWindow=current_turn_pre_end " +
                        $"carryObservationActionCount={candidate.CarryObservationActionCount} " +
                        $"carryDecisive={(index == 0 && carryDecisive).ToString().ToLowerInvariant()} " +
                        $"carryBaselineDifferent={(index == 0 && !selectedWouldAlreadyWinWithoutCarry).ToString().ToLowerInvariant()} " +
                        $"carryBaselinePreference={(index == 0 ? carryFreeWinner.CarryEvaluation.CarryPreference : 0)} " +
                        $"current_turn_card={candidate.HasCurrentTurnCardAction.ToString().ToLowerInvariant()} " +
                        $"actions={string.Join(',', candidate.Node.Actions.Select(CombatBeamSolver.PolicyActionToken))}");
                }
            }
            int potionBranchesRejected = policyCandidates.Count(candidate => candidate.PotionCount > 0)
                - policyEligibleCandidates.Count(candidate => candidate.PotionCount > 0);
            int potionHpSaved = selectedCandidate.PotionCount == 0
                ? 0
                : selectedCandidate.AmbergrisCount > 0
                    ? Math.Max(0, selectedCandidate.Snapshot.PlayerHp - potionFreePlayerHp)
                    : PotionUsePolicy.HpSaved(
                        potionFreeStrategicHpDeficit,
                        selectedCandidate.StrategicHpDeficit);
            int potionHpRequired = PotionUsePolicy.EffectiveStrategicHpCost(
                selectedCandidate.OptionalPotionStrategicCost,
                selectedCandidate.OptionalAmbergrisCount,
                initialPlayerMaxHp);
            if (selectedCandidate.EffectivePotionPolicy == SolverPotionPolicy.Smart
                && selectedCandidate.OptionalAmbergrisCount == 0
                && potionFreeWon)
            {
                potionHpRequired = PotionUsePolicy.SmartRequiredHpSaved(
                    potionHpRequired,
                    bossHpRelief);
            }
            if (selectedCandidate.EffectivePotionPolicy == SolverPotionPolicy.RequireAtLeastOne)
            {
                potionHpRequired = PotionUsePolicy.AdditionalRequiredUseStrategicHpCost(
                    potionHpRequired);
            }
            return new FinalPlanSelection(
                new FinalPlanCandidate(
                    selectedCandidate.Node,
                    selectedCandidate.Snapshot,
                    selectedCandidate.Features,
                    selectedCandidate.FutureSold,
                    selectedCandidate.BattleSold,
                    selectedCandidate.PotionCount,
                    selectedCandidate.Score),
                potionBranchesRejected,
                potionHpSaved,
                potionHpRequired);
        }
    }

    private static int ComparePotionFreePolicyBaselines(
        SearchNode left,
        SearchNode right,
        int initialPlayerHp,
        int initialPlayerMaxHp,
        BossHpRelief bossHpRelief,
        PostCombatRelicHealProfile postCombatRelicHeal,
        SolverTheftPolicy? theftPolicy,
        SearchRoutePolicy routePolicy,
        MultiplayerCombatObjectiveStrategy multiplayerCombatObjectiveStrategy,
        double multiplayerEnemyDurabilityRatio,
        int multiplayerEnemyMaximumHp,
        int startTurnNumber)
    {
        SimulationSnapshot leftSnapshot = left.Snapshot;
        SimulationSnapshot rightSnapshot = right.Snapshot;
        bool leftWon = SolverInterimResultOrdering.IsCompleteVictory(
            left.ActionCount,
            leftSnapshot.AllEnemiesDead,
            leftSnapshot.PlayerDead,
            leftSnapshot.ProjectedPlayerHp);
        bool rightWon = SolverInterimResultOrdering.IsCompleteVictory(
            right.ActionCount,
            rightSnapshot.AllEnemiesDead,
            rightSnapshot.PlayerDead,
            rightSnapshot.ProjectedPlayerHp);
        int comparison;
        if (routePolicy == SearchRoutePolicy.MultiplayerLocalCrossTurn)
        {
            double leftEnemyDurabilityRatio =
                MultiplayerCombatObjectivePolicy.ComputeEnemyDurabilityRatio(
                    leftSnapshot.EnemyDurabilityByCombatId,
                    multiplayerEnemyMaximumHp);
            double rightEnemyDurabilityRatio =
                MultiplayerCombatObjectivePolicy.ComputeEnemyDurabilityRatio(
                    rightSnapshot.EnemyDurabilityByCombatId,
                    multiplayerEnemyMaximumHp);
            MultiplayerCombatObjectiveRank leftObjective =
                MultiplayerCombatObjectiveMath.BuildRank(
                    multiplayerCombatObjectiveStrategy,
                    leftWon,
                    leftSnapshot.AllPlayersAlive,
                    leftSnapshot.TeamLossRatio,
                    leftSnapshot.WorstPlayerLossRatio,
                    leftEnemyDurabilityRatio,
                    multiplayerEnemyDurabilityRatio,
                    leftWon ? leftSnapshot.CombatEndedTurn : null,
                    startTurnNumber);
            MultiplayerCombatObjectiveRank rightObjective =
                MultiplayerCombatObjectiveMath.BuildRank(
                    multiplayerCombatObjectiveStrategy,
                    rightWon,
                    rightSnapshot.AllPlayersAlive,
                    rightSnapshot.TeamLossRatio,
                    rightSnapshot.WorstPlayerLossRatio,
                    rightEnemyDurabilityRatio,
                    multiplayerEnemyDurabilityRatio,
                    rightWon ? rightSnapshot.CombatEndedTurn : null,
                    startTurnNumber);
            comparison = MultiplayerCombatObjectiveMath.Compare(
                leftObjective,
                rightObjective);
            if (comparison != 0)
                return comparison;
        }
        comparison = rightWon.CompareTo(leftWon);
        if (comparison != 0)
            return comparison;
        if (!leftWon && !rightWon)
        {
            bool leftSurvives = !leftSnapshot.PlayerDead && leftSnapshot.ProjectedPlayerHp > 0;
            bool rightSurvives = !rightSnapshot.PlayerDead && rightSnapshot.ProjectedPlayerHp > 0;
            comparison = rightSurvives.CompareTo(leftSurvives);
            if (comparison != 0)
                return comparison;
        }
        comparison = leftSnapshot.ProjectedDeathSaveUseCount.CompareTo(
            rightSnapshot.ProjectedDeathSaveUseCount);
        if (comparison != 0)
            return comparison;
        comparison = TheftEncounterStrategy.CompareRecovery(theftPolicy,
            leftWon, leftSnapshot.OutstandingStolenResource, rightWon, rightSnapshot.OutstandingStolenResource);
        if (comparison != 0)
            return comparison;
        comparison = (ActEndingBossPolicy.StrategicHpDeficit(
                leftSnapshot.CumulativePlayerHpLost,
                Math.Max(0, initialPlayerMaxHp - leftSnapshot.PlayerMaxHp),
                leftSnapshot.RecoveredPlayerHp
                    + ActEndingBossPolicy.RankedPostCombatRelicHeal(
                        postCombatRelicHeal,
                        leftWon,
                        leftSnapshot.PlayerHp,
                        leftSnapshot.PlayerMaxHp),
                bossHpRelief,
                leftSnapshot.DeathSaveHpRestored) - leftSnapshot.StrategicHpCredit)
            .CompareTo(ActEndingBossPolicy.StrategicHpDeficit(
                rightSnapshot.CumulativePlayerHpLost,
                Math.Max(0, initialPlayerMaxHp - rightSnapshot.PlayerMaxHp),
                rightSnapshot.RecoveredPlayerHp
                    + ActEndingBossPolicy.RankedPostCombatRelicHeal(
                        postCombatRelicHeal,
                        rightWon,
                        rightSnapshot.PlayerHp,
                        rightSnapshot.PlayerMaxHp),
                bossHpRelief,
                rightSnapshot.DeathSaveHpRestored) - rightSnapshot.StrategicHpCredit);
        if (comparison != 0)
            return comparison;
        comparison = rightSnapshot.StrategyGoalHpCredit.CompareTo(leftSnapshot.StrategyGoalHpCredit);
        if (comparison != 0)
            return comparison;
        comparison = rightSnapshot.StrategyGoalCount.CompareTo(leftSnapshot.StrategyGoalCount);
        if (comparison != 0)
            return comparison;
        comparison = (leftWon ? leftSnapshot.CombatEndedTurn ?? int.MaxValue : int.MaxValue)
            .CompareTo(rightWon ? rightSnapshot.CombatEndedTurn ?? int.MaxValue : int.MaxValue);
        if (comparison != 0)
            return comparison;
        if (theftPolicy == SolverTheftPolicy.PreserveResources)
        {
            comparison = leftSnapshot.OutstandingStolenResource.CompareTo(
                rightSnapshot.OutstandingStolenResource);
            if (comparison != 0)
                return comparison;
        }
        comparison = (initialPlayerHp - leftSnapshot.PlayerHp
                + initialPlayerMaxHp - leftSnapshot.PlayerMaxHp)
            .CompareTo(initialPlayerHp - rightSnapshot.PlayerHp
                + initialPlayerMaxHp - rightSnapshot.PlayerMaxHp);
        if (comparison != 0)
            return comparison;
        comparison = rightSnapshot.LongTermResourceValue.CompareTo(leftSnapshot.LongTermResourceValue);
        if (comparison != 0)
            return comparison;
        bool delayAngerPreference =
            MultiplayerLocalCrossTurnContracts.DelayAngerCopyPreferenceUntilAfterEnemyHp(
                routePolicy,
                completeVictory: leftWon && rightWon);
        if (!delayAngerPreference)
        {
            comparison = leftSnapshot.AngerCopiesGenerated.CompareTo(rightSnapshot.AngerCopiesGenerated);
            if (comparison != 0)
                return comparison;
        }
        comparison = PolicyBoundaryRank(leftSnapshot.BoundaryReason)
            .CompareTo(PolicyBoundaryRank(rightSnapshot.BoundaryReason));
        if (comparison != 0)
            return comparison;
        comparison = leftSnapshot.EnemyHp.CompareTo(rightSnapshot.EnemyHp);
        if (comparison != 0)
            return comparison;
        if (delayAngerPreference)
        {
            comparison = leftSnapshot.AngerCopiesGenerated.CompareTo(rightSnapshot.AngerCopiesGenerated);
            if (comparison != 0)
                return comparison;
        }
        comparison = right.Score.CompareTo(left.Score);
        if (comparison != 0)
            return comparison;
        comparison = left.FutureSoldHp.CompareTo(right.FutureSoldHp);
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
}
