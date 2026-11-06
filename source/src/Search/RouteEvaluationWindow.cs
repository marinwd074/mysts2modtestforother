namespace CombatSolver;

/// <summary>
/// Value-only coverage of a search projection. A null target means battle completion.
/// Phase is the captured PlayerTurnPhase
/// value, never a read from a retained simulator. A key permits cost comparison within
/// one request; it is not a state-equivalence or execution-authority key.
/// </summary>
internal readonly record struct RouteEvaluationWindow(
    int StartTurn, int? TargetTurn, int EvaluatedTurn, int PlayerPhase,
    bool CompleteVictory, bool Resolved, bool AtPlayerDecisionPoint)
{
    internal readonly record struct CostWindow(int StartTurn, int? TargetTurn, int Turn, int Phase);

    // Victory is absorbing. Otherwise only the actual settled point is comparable;
    // an unmodelled boundary or a missing tail must not become zero future cost.
    internal CostWindow? ComparisonKey => !Resolved || EvaluatedTurn < StartTurn
        || TargetTurn is { } target && (target <= StartTurn || EvaluatedTurn > target)
            ? null
            : CompleteVictory || EvaluatedTurn == TargetTurn && AtPlayerDecisionPoint
                ? new(StartTurn, TargetTurn, TargetTurn ?? int.MaxValue, -1)
                : new(StartTurn, TargetTurn, EvaluatedTurn, PlayerPhase);

    internal static bool CanCompare(RouteEvaluationWindow? left, RouteEvaluationWindow? right)
        => left?.ComparisonKey is { } key && right?.ComparisonKey == key;

    // Scalar-only callers predate window metadata. Once either side carries coverage,
    // missing or unresolved coverage on the other side cannot prove equal cost scope.
    internal static bool CanCompareQuality(RouteEvaluationWindow? left, RouteEvaluationWindow? right)
        => left == null && right == null || CanCompare(left, right);

    // Group scopes before ordering quality. The preferred scope is supplied by the
    // committed search frontier; the remaining order is deterministic, not a quality rank.
    internal static int CompareScopes(RouteEvaluationWindow? left, RouteEvaluationWindow? right,
        RouteEvaluationWindow? preferred = null)
    {
        CostWindow? leftKey = left?.ComparisonKey;
        CostWindow? rightKey = right?.ComparisonKey;
        if (leftKey == rightKey) return 0;
        if (preferred?.ComparisonKey is { } target)
        {
            int preference = (leftKey == target ? 0 : 1).CompareTo(rightKey == target ? 0 : 1);
            if (preference != 0) return preference;
        }
        if (leftKey is not { } l) return 1;
        if (rightKey is not { } r) return -1;
        return (l.StartTurn, l.TargetTurn ?? int.MaxValue, l.Turn, l.Phase)
            .CompareTo((r.StartTurn, r.TargetTurn ?? int.MaxValue, r.Turn, r.Phase));
    }

    internal bool CoversTarget => ComparisonKey is { }
        && (CompleteVictory || TargetTurn == EvaluatedTurn && AtPlayerDecisionPoint);

    // A total eligibility key, not a pairwise depth preference. Missing/partial
    // coverage cannot outrank a surviving route evaluated to the requested target.
    internal static int CompletedCoverageRank(RouteEvaluationWindow? window, bool survives)
        => survives && window is { CoversTarget: true } ? 0 : 1;

    internal RouteEvaluationWindow? ForTurn(int turn)
        => turn < StartTurn || turn > EvaluatedTurn ? null
            : this with { StartTurn = turn,
                TargetTurn = TargetTurn is { } target ? checked(turn + target - StartTurn) : null };
}
