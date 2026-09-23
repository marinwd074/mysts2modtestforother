namespace CombatSolver;

internal enum MultiplayerScenarioEvaluationStatus
{
    Unknown = 0,
    Completed = 1,
    Terminal = 2,
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
    double EnemyDurabilityRatio);

internal readonly record struct MultiplayerScenarioEvaluation(
    MultiplayerScenarioSpec Spec,
    MultiplayerScenarioEvaluationStatus Status,
    MultiplayerScenarioOutcome? Outcome,
    int? ExpandedBranches);

internal readonly record struct MultiplayerScenarioDecisionRank(
    int ScenarioCount,
    bool AllScenariosAlive,
    bool GuaranteedVictory,
    double WorstLossEquivalent,
    double MeanLossEquivalent,
    double WorstPlayerLossRatio,
    double WorstTeamLossRatio,
    double WorstEnemyDurabilityRatio);

/// <summary>
/// Robust multiplayer scenario reranking. Scenario specs are stress-behavior rules, not
/// calibrated probabilities or concrete teammate card strings. Every compared current decision
/// must be evaluated against the same spec set; missing work is Unknown and forces the shared
/// fallback rather than being interpreted as a favorable outcome.
/// </summary>
internal static class MultiplayerScenarioReevaluationPolicy
{
    internal const int MaximumCurrentDecisions = 4;

    private static readonly MultiplayerScenarioSpec[] ScenarioSpecsValue =
    [
        new("aggressive", ShadowTeammateScenarioKind.Aggressive),
        new("defensive", ShadowTeammateScenarioKind.Defensive),
        new("conserve", ShadowTeammateScenarioKind.Conserve),
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
    internal const int MaximumExpandedBranchesPerScenario = 32;
    internal const int MaximumExpandedBranchesPerDecision =
        MaximumScenariosPerDecision * MaximumExpandedBranchesPerScenario;
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

    internal static int ExpandedBranchBudgetPerScenario(
        int reservedExpandedBranches,
        int comparedDecisionCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reservedExpandedBranches);
        if (comparedDecisionCount < 1
            || comparedDecisionCount > MaximumCurrentDecisions)
        {
            throw new ArgumentOutOfRangeException(nameof(comparedDecisionCount));
        }

        int cells = checked(comparedDecisionCount * MaximumScenariosPerDecision);
        return Math.Min(
            MaximumExpandedBranchesPerScenario,
            reservedExpandedBranches / cells);
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
