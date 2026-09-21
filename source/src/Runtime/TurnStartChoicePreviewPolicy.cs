namespace CombatSolver;

internal static class TurnStartChoicePreviewPolicy
{
    internal static IReadOnlyList<PlanCardChoice> ChoicesForTurn(
        int turn,
        int startTurn,
        IReadOnlyList<PlanCardChoice> setupChoices,
        IReadOnlyList<PlanAction> actions)
    {
        IReadOnlyList<PlanCardChoice> preceding = actions
            .FirstOrDefault(action =>
                action.Turn == turn - 1
                && action.TurnStartChoices is { Count: > 0 })
            ?.TurnStartChoices
            ?? [];

        return turn == startTurn
            ? setupChoices.Concat(preceding).ToArray()
            : preceding;
    }
}
