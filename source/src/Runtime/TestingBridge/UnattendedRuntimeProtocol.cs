using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Rooms;

namespace CombatSolver;

// The production API only needs the small wire contract used to start an
// isolated pre-combat worker and read its result. The full unattended request,
// assertions and fixtures remain test-only in src/Testing.
internal static class UnattendedRuntimeProtocol
{
    internal const string RequestUri = "user://combat_solver_test_request.json";
    internal const string RunningUri = "user://combat_solver_test_running.json";
    internal const string ResultUri = "user://combat_solver_test_result.json";
    internal const string ReadyUri = "user://combat_solver_test_ready.json";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static string GlobalPath(string uri) => ProjectSettings.GlobalizePath(uri);
}

internal sealed class UnattendedRuntimeRequest
{
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public string ScenarioId { get; init; } = "PRECOMBAT-API-V1";
    public string CharacterId { get; init; } = "IRONCLAD";
    public string EncounterId { get; init; } = "FUZZY_WURM_CRAWLER_WEAK";
    public string Seed { get; init; } = "COMBATSOLVER";
    public string? RunSnapshotPath { get; init; }
    public bool LoadRunSnapshotDirectly { get; init; }
    public int? TargetActFloor { get; init; }
    public int? TargetMapColumn { get; init; }
    public RoomType TargetRoomType { get; init; } = RoomType.Monster;
    public MapPointType TargetMapPointType { get; init; } = MapPointType.Unassigned;
    public string[] ExpectedLoadedMods { get; init; } = [];
    public int Ascension { get; init; }
    public int ActIndexForTest { get; init; }
    public bool MarkEncounterAsSecondBossForTest { get; init; }
    public int EnemyCurrentHp { get; init; } = 1;
    public JsonElement[] Cards { get; init; } = [];
    public bool FixedSearchBudget { get; init; }
    public int? SearchBudgetOverrideMilliseconds { get; init; }
    public int? SearchMaxDegreeOfParallelismForTest { get; init; }
    public int? PreCombatPlayerCurrentHpOverride { get; init; }
    public ulong? PreCombatSimulationSeed { get; init; }
    public UnattendedRuntimePreCombatMapStep[] PreCombatInterveningMapPoints { get; init; } = [];
    public bool EnableNoGcRegionForTest { get; init; }
    public SolverDeploymentFastMode? HeadlessFastModeForTest { get; init; }
    public bool StopAfterInitialSolverResultAssertion { get; init; }
    public double TimeoutSeconds { get; init; } = 120;
    public bool ExitOnComplete { get; init; } = true;
}

internal sealed record UnattendedRuntimePreCombatMapStep
{
    public MapCoord Coordinate { get; init; }
    public RoomType RoomType { get; init; }
    public MapPointType MapPointType { get; init; }
}

internal sealed class UnattendedRuntimeResult
{
    public int ProcessId { get; init; } = System.Environment.ProcessId;
    public string RunId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Stage { get; init; } = string.Empty;
    public double ElapsedMilliseconds { get; init; }
    public UnattendedRuntimeSolverMetrics? SolverMetrics { get; init; }
    public string[] CompletedChecks { get; init; } = [];
    public string? Error { get; init; }
}

internal sealed class UnattendedRuntimeSolverMetrics
{
    public SearchBoundaryReason Boundary { get; init; }
    public int ProjectedBattleHpLost { get; init; }
    public bool OnlyDeathRoutes { get; init; }
    public int FinalHp { get; init; }
    public int? CombatEndedTurn { get; init; }
    public double TotalElapsedMilliseconds { get; init; }
    public UnattendedRuntimePotionUse[] PotionUses { get; init; } = [];
}

internal sealed class UnattendedRuntimePotionUse
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public int Turn { get; init; }
    public int Slot { get; init; }
}
