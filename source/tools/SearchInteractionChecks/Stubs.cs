// Foreign search/game DTOs only. The candidate module, progress DTOs and display
// pacing are linked production code; these stubs do not implement their behavior.
namespace CombatSolver;

internal enum PlanActionKind { PlayCard, EndTurn }
internal enum SolverResultScope { SearchCompletion, CurrentTurnAdoption, RouteAdoption }
internal sealed record PlanAction(PlanActionKind Kind, int Turn, string CardId = "");
internal sealed record PlanCardChoice(string SourceId);
internal sealed record SolverTheftPolicy;
internal sealed record CandidateOrigin(int SearchMemberId, int SolverId, int Turn, int CandidateId, int ParentId);
internal readonly record struct LiveCombatStamp(int Version);
internal static class SolverWeights { public const int ProgressUiIntervalMilliseconds = 200; }
internal static class TurnStartChoicePreviewPolicy
{
    public static IReadOnlyList<PlanCardChoice> ChoicesForTurn(int turn, int start,
        IReadOnlyList<PlanCardChoice> setup, IReadOnlyList<PlanAction> actions) => [];
}
internal sealed record TestNode(IReadOnlyList<PlanAction> Actions);
internal sealed record TestSnapshot(bool HasRisk = false);
internal sealed class SolverResult
{
    public SolverResultScope ResultScope { get; set; }
    public TestNode BestNode { get; init; } = new([]);
    public TestSnapshot Snapshot { get; } = new();
    public Dictionary<int, IReadOnlyList<string>> KillsAfterAction { get; } = [];
    public Dictionary<int, int> HpLostByTurn { get; } = [];
    public Dictionary<int, int> HpRecoveredByTurn { get; } = [];
    public Dictionary<int, int> EnemyHpLostByTurn { get; } = [];
    public Dictionary<int, int> EnergyLeftByTurn { get; } = [];
    public int? CombatEndedTurn { get; init; }
    public int StartTurnNumber { get; init; } = 1;
    public bool WasReused { get; init; }
    public IReadOnlyList<PlanCardChoice> TurnSetupChoices { get; } = [];
    public int ProjectedBattlePotionCount { get; init; }
    public int ProjectedBattleHpLost { get; init; }
    public bool OnlyDeathRoutesFound { get; init; }
}
