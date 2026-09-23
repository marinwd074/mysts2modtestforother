namespace CombatSolver;

// Lightweight contract-test surface for compiling the production current-decision identity
// without loading STS2 runtime assemblies. Keep only fields read by
// MultiplayerChanceDecisionIdentity.
internal enum PlanActionKind
{
    PlayCard,
    UsePotion,
    EndTurn,
    TeammateForecast,
}

internal enum PlanChoiceEffect
{
    None,
}

internal enum PileType
{
    None,
}

internal enum PlanChoiceTiming
{
    Action,
}

internal sealed record PlanCardToken(
    string CardId,
    int UpgradeLevel,
    string StateKey,
    int SourceOccurrence,
    int OptionOccurrence);

internal sealed record PlanCardChoice(
    PlanChoiceEffect Effect,
    PileType SourcePile,
    IReadOnlyList<PlanCardToken> Cards,
    string SourceId = "",
    string ContextId = "",
    PlanChoiceTiming Timing = PlanChoiceTiming.Action);

internal sealed record ShadowForecastPlan(
    IReadOnlyList<object> Actions,
    ShadowTeammateScenarioKind ScenarioKind = ShadowTeammateScenarioKind.Unspecified);

internal sealed record PlanAction(
    PlanActionKind Kind,
    int Turn,
    string CardId = "",
    int CardOccurrence = 0,
    uint? TargetCombatId = null,
    PlanCardChoice? Choice = null,
    IReadOnlyList<PlanCardChoice>? NestedChoices = null,
    int NestedChoicesBeforePrimary = 0,
    int PotionSlot = -1,
    string PotionId = "",
    IReadOnlyList<PlanCardChoice>? TurnStartChoices = null,
    int CardStateOccurrence = 0,
    string CardStateKey = "",
    bool EndsPlayerTurn = false,
    int CardUpgradeLevel = 0,
    string CardEnchantmentId = "",
    ShadowForecastPlan? ShadowForecast = null);

internal sealed class SearchNode
{
    public SearchNode? Parent { get; init; }
    public PlanAction? Action { get; init; }
    public IReadOnlyList<PlanAction> Actions { get; init; } = [];
}
