namespace CombatSolver;

internal sealed record PlanCardChoice(string SourceId);

internal sealed record PlanAction(
    int Turn,
    IReadOnlyList<PlanCardChoice>? TurnStartChoices = null);
