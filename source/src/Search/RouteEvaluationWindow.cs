namespace CombatSolver;

/// <summary>
/// Value-only coverage of a local-core projection. Phase is the captured PlayerTurnPhase
/// value, never a read from a retained simulator. A key permits cost comparison within
/// one request; it is not a state-equivalence or execution-authority key.
/// </summary>
internal readonly record struct RouteEvaluationWindow(
    int StartTurn, int TargetTurn, int EvaluatedTurn, int PlayerPhase,
    bool CompleteVictory, bool Resolved, bool AtPlayerDecisionPoint)
{
    internal readonly record struct CostWindow(int StartTurn, int TargetTurn, int Turn, int Phase);

    // Victory is absorbing. Otherwise only the actual settled point is comparable;
    // an unmodelled boundary or a missing tail must not become zero future cost.
    internal CostWindow? ComparisonKey => !Resolved || TargetTurn <= StartTurn
        || EvaluatedTurn < StartTurn || EvaluatedTurn > TargetTurn
            ? null
            : CompleteVictory || EvaluatedTurn == TargetTurn && AtPlayerDecisionPoint
                ? new(StartTurn, TargetTurn, TargetTurn, -1)
                : new(StartTurn, TargetTurn, EvaluatedTurn, PlayerPhase);

    internal static bool CanCompare(RouteEvaluationWindow? left, RouteEvaluationWindow? right)
        => left?.ComparisonKey is { } key && right?.ComparisonKey == key;

    internal RouteEvaluationWindow? ForTurn(int turn)
        => turn < StartTurn || turn > EvaluatedTurn ? null
            : this with { StartTurn = turn, TargetTurn = checked(turn + TargetTurn - StartTurn) };
}
