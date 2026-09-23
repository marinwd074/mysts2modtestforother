namespace CombatSolver;

internal enum MultiplayerScenarioEvaluationStatus
{
    Unknown = 0,
    Completed = 1,
    Terminal = 2,
}

internal enum MultiplayerScenarioRiskStrategy
{
    Robust = 0,
    NominalReference = 1,
    BoundedRisk = 2,
}

internal readonly record struct MultiplayerScenarioSpec(
    string Id,
    ShadowTeammateScenarioKind Kind);

internal readonly record struct MultiplayerScenarioOutcome(
    ShadowTeammateScenarioKind Kind,
    bool CompleteVictory,
    bool AllPlayersAlive,
    double LossEquivalent,
    double WorstPlayerLossRatio,
    double TeamLossRatio,
    double EnemyDurabilityRatio,
    double TeamRemainingHpRatio = double.NaN,
    double WorstPlayerRemainingHpRatio = double.NaN);

internal readonly record struct MultiplayerScenarioEvaluation(
    MultiplayerScenarioSpec Spec,
    MultiplayerScenarioEvaluationStatus Status,
    MultiplayerScenarioOutcome? Outcome,
    int? ExpandedBranches);

internal sealed record MultiplayerScenarioDecisionEvaluation(
    string DecisionKey,
    IReadOnlyList<MultiplayerScenarioEvaluation> Scenarios,
    int SharedExpandedBranches = 0)
{
    internal bool CompleteCoverage =>
        Scenarios.Count == MultiplayerScenarioReevaluationPolicy.MaximumScenariosPerDecision
        && Scenarios.All(evaluation =>
            evaluation.Status != MultiplayerScenarioEvaluationStatus.Unknown
            && evaluation.Outcome.HasValue)
        && MultiplayerScenarioReevaluationPolicy.HasCompleteCoverage(
            Scenarios
                .Where(evaluation => evaluation.Status != MultiplayerScenarioEvaluationStatus.Unknown)
                .Select(evaluation => evaluation.Spec.Kind));

    internal int ExpandedBranches =>
        SharedExpandedBranches
        + Scenarios.Sum(evaluation => evaluation.ExpandedBranches ?? 0);
}

internal readonly record struct MultiplayerScenarioDecisionRank(
    int ScenarioCount,
    bool AllScenariosAlive,
    bool GuaranteedVictory,
    double WorstLossEquivalent,
    double MeanLossEquivalent,
    double WorstPlayerLossRatio,
    double WorstTeamLossRatio,
    double WorstEnemyDurabilityRatio);

internal readonly record struct MultiplayerScenarioRiskMetrics(
    int ScenarioCount,
    double NominalReferenceLossEquivalent,
    double RobustLossEquivalent,
    double ConservatismGap,
    double MeanTeamLossRatio,
    double WorstTeamLossRatio,
    double MeanTeamRemainingHpRatio,
    double WorstTeamRemainingHpRatio,
    double MeanWorstPlayerRemainingHpRatio,
    double WorstPlayerRemainingHpRatio,
    double CooperativeMeanTeamLossRatio,
    double NoActionTeamLossRatio,
    double CooperationTeamLossBenefit,
    double CooperativeMeanEnemyDurabilityRatio,
    double NoActionEnemyDurabilityRatio,
    double CooperationProgressBenefit);

/// <summary>
/// Robust multiplayer scenario reranking. Scenario specs are stress-behavior rules, not
/// calibrated probabilities or concrete teammate card strings. Every compared current decision
/// must be evaluated against the same spec set; missing work is Unknown and forces the shared
/// fallback rather than being interpreted as a favorable outcome.
/// </summary>
internal static class MultiplayerScenarioReevaluationPolicy
{
    internal const int MaximumCurrentDecisions = 4;
    internal const double BoundedRiskWorstGapWeight = 0.5d;
    internal const string NoActionScopeDiagnosticValue = "current_joint_forecast_only";

    private static readonly MultiplayerScenarioSpec[] ScenarioSpecsValue =
    [
        new("aggressive", ShadowTeammateScenarioKind.Aggressive),
        new("defensive", ShadowTeammateScenarioKind.Defensive),
        new("conserve", ShadowTeammateScenarioKind.Conserve),
        // NoAction means no teammate action in this one Joint forecast window only.
        // It never means the teammate is assumed idle for the remainder of combat.
        new("no_action", ShadowTeammateScenarioKind.NoAction),
    ];

    internal static IReadOnlyList<MultiplayerScenarioSpec> ScenarioSpecs =>
        ScenarioSpecsValue;

    internal const int MaximumScenariosPerDecision = 4;
    internal const int MaximumCoverageCandidates =
        MaximumCurrentDecisions * MaximumScenariosPerDecision;

    // U3 final reevaluation never receives an unbounded hidden work allowance. The reserve is
    // carved from the caller's existing node budget in a later integration step; these helpers
    // only define the deterministic split and are intentionally independent of candidate order.
    internal const int MaximumExpandedBranchesPerDecision = 128;
    internal const int MaximumReservedExpandedBranches =
        MaximumCurrentDecisions * MaximumExpandedBranchesPerDecision;
    private const int MinimumTotalBudgetForReevaluation = 64;
    private const int ReevaluationBudgetDivisor = 8;

    internal static int ReserveExpandedBranchBudget(
        int totalExpandedNodeBudget,
        bool enabled)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalExpandedNodeBudget);
        if (!enabled || totalExpandedNodeBudget < MinimumTotalBudgetForReevaluation)
            return 0;

        int proportional = Math.Max(
            MaximumCurrentDecisions,
            totalExpandedNodeBudget / ReevaluationBudgetDivisor);
        return Math.Min(MaximumReservedExpandedBranches, proportional);
    }

    internal static int MainSearchExpandedNodeBudget(
        int totalExpandedNodeBudget,
        bool enabled)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalExpandedNodeBudget);
        return totalExpandedNodeBudget
            - ReserveExpandedBranchBudget(totalExpandedNodeBudget, enabled);
    }

    internal static int ExpandedBranchBudgetPerDecision(
        int reservedExpandedBranches,
        int comparedDecisionCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reservedExpandedBranches);
        if (comparedDecisionCount < 1
            || comparedDecisionCount > MaximumCurrentDecisions)
        {
            throw new ArgumentOutOfRangeException(nameof(comparedDecisionCount));
        }

        return Math.Min(
            MaximumExpandedBranchesPerDecision,
            reservedExpandedBranches / comparedDecisionCount);
    }

    internal static bool IsRequiredScenario(ShadowTeammateScenarioKind kind)
    {
        for (int index = 0; index < ScenarioSpecsValue.Length; index++)
        {
            if (ScenarioSpecsValue[index].Kind == kind)
                return true;
        }
        return false;
    }

    internal static bool HasCompleteCoverage(
        IEnumerable<ShadowTeammateScenarioKind> completedKinds)
    {
        HashSet<ShadowTeammateScenarioKind> completed = [];
        foreach (ShadowTeammateScenarioKind kind in completedKinds)
        {
            if (IsRequiredScenario(kind))
                completed.Add(kind);
        }

        if (completed.Count != ScenarioSpecsValue.Length)
            return false;

        for (int index = 0; index < ScenarioSpecsValue.Length; index++)
        {
            if (!completed.Contains(ScenarioSpecsValue[index].Kind))
                return false;
        }
        return true;
    }

    internal static bool CanRerank(
        IReadOnlyList<bool> decisionCoverageComplete)
    {
        if (decisionCoverageComplete.Count < 2)
            return false;

        for (int index = 0; index < decisionCoverageComplete.Count; index++)
        {
            if (!decisionCoverageComplete[index])
                return false;
        }
        return true;
    }

    internal static MultiplayerScenarioDecisionRank Aggregate(
        IReadOnlyList<MultiplayerScenarioOutcome> outcomes)
    {
        if (outcomes.Count == 0)
        {
            return new MultiplayerScenarioDecisionRank(
                0,
                AllScenariosAlive: false,
                GuaranteedVictory: false,
                double.PositiveInfinity,
                double.PositiveInfinity,
                double.PositiveInfinity,
                double.PositiveInfinity,
                double.PositiveInfinity);
        }

        double worstLoss = double.NegativeInfinity;
        double lossSum = 0d;
        double worstPlayerLoss = double.NegativeInfinity;
        double worstTeamLoss = double.NegativeInfinity;
        double worstEnemyDurability = double.NegativeInfinity;
        bool allAlive = true;
        bool allVictory = true;
        for (int index = 0; index < outcomes.Count; index++)
        {
            MultiplayerScenarioOutcome outcome = outcomes[index];
            allAlive &= outcome.AllPlayersAlive;
            allVictory &= outcome.CompleteVictory;
            worstLoss = Math.Max(worstLoss, outcome.LossEquivalent);
            lossSum += outcome.LossEquivalent;
            worstPlayerLoss = Math.Max(
                worstPlayerLoss,
                outcome.WorstPlayerLossRatio);
            worstTeamLoss = Math.Max(worstTeamLoss, outcome.TeamLossRatio);
            worstEnemyDurability = Math.Max(
                worstEnemyDurability,
                outcome.EnemyDurabilityRatio);
        }

        return new MultiplayerScenarioDecisionRank(
            outcomes.Count,
            allAlive,
            allVictory,
            worstLoss,
            lossSum / outcomes.Count,
            worstPlayerLoss,
            worstTeamLoss,
            worstEnemyDurability);
    }

    /// <summary>
    /// U4 diagnostic-only A/B measurements over the fixed U3 stress lanes.
    /// The nominal reference is an equal-lane mean, not an expected value: teammate
    /// scenario probabilities are still uncalibrated. Positive cooperation benefits mean
    /// cooperative lanes improve the corresponding quantity versus NoAction.
    /// </summary>
    internal static MultiplayerScenarioRiskMetrics MeasureRisk(
        IReadOnlyList<MultiplayerScenarioOutcome> outcomes)
    {
        if (outcomes.Count == 0)
            throw new ArgumentException("Risk measurement requires at least one scenario.", nameof(outcomes));

        MultiplayerScenarioOutcome[] cooperative = outcomes
            .Where(outcome => outcome.Kind != ShadowTeammateScenarioKind.NoAction)
            .ToArray();
        bool hasNoAction = outcomes.Any(
            outcome => outcome.Kind == ShadowTeammateScenarioKind.NoAction);
        MultiplayerScenarioOutcome noAction = hasNoAction
            ? outcomes.First(outcome => outcome.Kind == ShadowTeammateScenarioKind.NoAction)
            : default;

        double nominalReferenceLoss = outcomes.Average(outcome => outcome.LossEquivalent);
        double robustLoss = outcomes.Max(outcome => outcome.LossEquivalent);
        double meanTeamLoss = outcomes.Average(outcome => outcome.TeamLossRatio);
        double worstTeamLoss = outcomes.Max(outcome => outcome.TeamLossRatio);
        double meanTeamRemainingHp = AverageFinite(
            outcomes.Select(outcome => outcome.TeamRemainingHpRatio));
        double worstTeamRemainingHp = MinimumFinite(
            outcomes.Select(outcome => outcome.TeamRemainingHpRatio));
        double meanWorstPlayerRemainingHp = AverageFinite(
            outcomes.Select(outcome => outcome.WorstPlayerRemainingHpRatio));
        double worstPlayerRemainingHp = MinimumFinite(
            outcomes.Select(outcome => outcome.WorstPlayerRemainingHpRatio));

        double cooperativeMeanTeamLoss = cooperative.Length > 0
            ? cooperative.Average(outcome => outcome.TeamLossRatio)
            : meanTeamLoss;
        double cooperativeMeanEnemyDurability = cooperative.Length > 0
            ? cooperative.Average(outcome => outcome.EnemyDurabilityRatio)
            : outcomes.Average(outcome => outcome.EnemyDurabilityRatio);

        double noActionTeamLoss = hasNoAction
            ? noAction.TeamLossRatio
            : meanTeamLoss;
        double noActionEnemyDurability = hasNoAction
            ? noAction.EnemyDurabilityRatio
            : outcomes.Average(outcome => outcome.EnemyDurabilityRatio);

        return new MultiplayerScenarioRiskMetrics(
            outcomes.Count,
            nominalReferenceLoss,
            robustLoss,
            Math.Max(0d, robustLoss - nominalReferenceLoss),
            meanTeamLoss,
            worstTeamLoss,
            meanTeamRemainingHp,
            worstTeamRemainingHp,
            meanWorstPlayerRemainingHp,
            worstPlayerRemainingHp,
            cooperativeMeanTeamLoss,
            noActionTeamLoss,
            noActionTeamLoss - cooperativeMeanTeamLoss,
            cooperativeMeanEnemyDurability,
            noActionEnemyDurability,
            noActionEnemyDurability - cooperativeMeanEnemyDurability);
    }

    internal static double BoundedRiskLossEquivalent(
        MultiplayerScenarioDecisionRank rank)
        => rank.MeanLossEquivalent
            + BoundedRiskWorstGapWeight
            * Math.Max(0d, rank.WorstLossEquivalent - rank.MeanLossEquivalent);

    internal static int SelectPreferredIndex(
        MultiplayerScenarioRiskStrategy strategy,
        IReadOnlyList<MultiplayerScenarioDecisionRank> ranks)
    {
        if (ranks.Count == 0)
            return -1;

        int bestIndex = 0;
        for (int index = 1; index < ranks.Count; index++)
        {
            if (CompareByRiskStrategy(strategy, ranks[index], ranks[bestIndex]) < 0)
                bestIndex = index;
        }
        return bestIndex;
    }

    /// <summary>
    /// Independent U4 tolerance experiment. It is intentionally not composed with BoundedRisk:
    /// first keep the best hard safety/terminal class, then admit candidates within a caller-owned
    /// nominal-loss tolerance and choose the lower worst-case loss inside that admitted set.
    /// Production does not call this helper and U4 does not choose a default tolerance.
    /// </summary>
    internal static int SelectNominalToleranceExperimentIndex(
        IReadOnlyList<MultiplayerScenarioDecisionRank> ranks,
        double nominalLossTolerance)
    {
        if (ranks.Count == 0)
            return -1;
        if (double.IsNaN(nominalLossTolerance)
            || double.IsInfinity(nominalLossTolerance)
            || nominalLossTolerance < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(nominalLossTolerance));
        }

        bool requireAllAlive = ranks.Any(rank => rank.AllScenariosAlive);
        bool requireGuaranteedVictory = ranks
            .Where(rank => !requireAllAlive || rank.AllScenariosAlive)
            .Any(rank => rank.GuaranteedVictory);
        int[] eligible = Enumerable.Range(0, ranks.Count)
            .Where(index =>
                (!requireAllAlive || ranks[index].AllScenariosAlive)
                && (!requireGuaranteedVictory || ranks[index].GuaranteedVictory))
            .ToArray();
        double bestNominal = eligible.Min(index => ranks[index].MeanLossEquivalent);
        int bestIndex = -1;
        foreach (int index in eligible)
        {
            if (ranks[index].MeanLossEquivalent > bestNominal + nominalLossTolerance)
                continue;
            if (bestIndex < 0
                || ranks[index].WorstLossEquivalent < ranks[bestIndex].WorstLossEquivalent
                || ranks[index].WorstLossEquivalent == ranks[bestIndex].WorstLossEquivalent
                    && ranks[index].MeanLossEquivalent < ranks[bestIndex].MeanLossEquivalent)
            {
                bestIndex = index;
            }
        }
        return bestIndex;
    }

    private static double AverageFinite(IEnumerable<double> values)
    {
        double sum = 0d;
        int count = 0;
        foreach (double value in values)
        {
            if (!double.IsFinite(value))
                continue;
            sum += value;
            count++;
        }
        return count == 0 ? double.NaN : sum / count;
    }

    private static double MinimumFinite(IEnumerable<double> values)
    {
        double minimum = double.PositiveInfinity;
        foreach (double value in values)
        {
            if (double.IsFinite(value))
                minimum = Math.Min(minimum, value);
        }
        return double.IsPositiveInfinity(minimum) ? double.NaN : minimum;
    }

    /// <summary>
    /// U4 experiment-only risk ordering. Robust exactly preserves the current production
    /// comparator. NominalReference is an equal-stress-lane mean, not a calibrated expectation.
    /// BoundedRisk prices half of the mean-to-worst gap while retaining the same survival
    /// and guaranteed-victory hard boundaries. No caller in production selection uses this yet.
    /// </summary>
    internal static int CompareByRiskStrategy(
        MultiplayerScenarioRiskStrategy strategy,
        MultiplayerScenarioDecisionRank left,
        MultiplayerScenarioDecisionRank right)
    {
        if (strategy == MultiplayerScenarioRiskStrategy.Robust)
            return Compare(left, right);

        int comparison = right.AllScenariosAlive.CompareTo(left.AllScenariosAlive);
        if (comparison != 0)
            return comparison;
        comparison = right.GuaranteedVictory.CompareTo(left.GuaranteedVictory);
        if (comparison != 0)
            return comparison;

        if (strategy == MultiplayerScenarioRiskStrategy.NominalReference)
        {
            comparison = left.MeanLossEquivalent.CompareTo(right.MeanLossEquivalent);
            if (comparison != 0)
                return comparison;
        }
        else
        {
            comparison = BoundedRiskLossEquivalent(left)
                .CompareTo(BoundedRiskLossEquivalent(right));
            if (comparison != 0)
                return comparison;
        }

        comparison = left.WorstPlayerLossRatio.CompareTo(right.WorstPlayerLossRatio);
        if (comparison != 0)
            return comparison;
        comparison = left.WorstLossEquivalent.CompareTo(right.WorstLossEquivalent);
        if (comparison != 0)
            return comparison;
        comparison = left.MeanLossEquivalent.CompareTo(right.MeanLossEquivalent);
        if (comparison != 0)
            return comparison;
        comparison = left.WorstTeamLossRatio.CompareTo(right.WorstTeamLossRatio);
        if (comparison != 0)
            return comparison;
        comparison = left.WorstEnemyDurabilityRatio.CompareTo(
            right.WorstEnemyDurabilityRatio);
        if (comparison != 0)
            return comparison;
        return right.ScenarioCount.CompareTo(left.ScenarioCount);
    }

    /// <summary>Negative means left is preferred.</summary>
    internal static int Compare(
        MultiplayerScenarioDecisionRank left,
        MultiplayerScenarioDecisionRank right)
    {
        int comparison = right.AllScenariosAlive.CompareTo(left.AllScenariosAlive);
        if (comparison != 0)
            return comparison;
        comparison = right.GuaranteedVictory.CompareTo(left.GuaranteedVictory);
        if (comparison != 0)
            return comparison;
        comparison = left.WorstLossEquivalent.CompareTo(right.WorstLossEquivalent);
        if (comparison != 0)
            return comparison;
        comparison = left.WorstPlayerLossRatio.CompareTo(right.WorstPlayerLossRatio);
        if (comparison != 0)
            return comparison;
        comparison = left.MeanLossEquivalent.CompareTo(right.MeanLossEquivalent);
        if (comparison != 0)
            return comparison;
        comparison = left.WorstTeamLossRatio.CompareTo(right.WorstTeamLossRatio);
        if (comparison != 0)
            return comparison;
        comparison = left.WorstEnemyDurabilityRatio.CompareTo(
            right.WorstEnemyDurabilityRatio);
        if (comparison != 0)
            return comparison;
        return right.ScenarioCount.CompareTo(left.ScenarioCount);
    }
}
