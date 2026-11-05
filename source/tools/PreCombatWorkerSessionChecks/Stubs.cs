// Only foreign game shapes/defaults are stubbed. The session, public contracts,
// protocol DTOs/JSON options and real owned-process adapter are production source.
namespace Godot
{
    internal static class OS { public static string GetExecutablePath() => throw new NotSupportedException("No game startup in these tests."); }
    internal static class ProjectSettings
    {
        public static string GlobalizePath(string uri) => throw new NotSupportedException("Not used by session tests.");
    }
}
namespace MegaCrit.Sts2.Core.Map
{
    public readonly record struct MapCoord(int Row, int Col);
    public enum MapPointType { Unassigned, Monster, Elite, Boss, Ancient, Unknown }
}
namespace MegaCrit.Sts2.Core.Rooms { public enum RoomType { Monster, Elite, Boss } }
namespace CombatSolver
{
    internal enum SolverDeploymentFastMode { Instant }
    internal enum SearchBoundaryReason { None }
}
namespace CombatSolver.Api
{
    internal static class PreCombatForecastApi
    {
        public const int DefaultWorkerIdleTimeoutMilliseconds = 120_000;
        public static PreCombatForecastResult Failure(PreCombatForecastStatus status, string id, string? error, string? log = null)
            => new() { Status = status, RequestId = id, Error = error, DiagnosticLogPath = log };
    }
    internal sealed class PreCombatLiveStateSnapshot
    {
        public string GameRoot { get; init; } = "";
        public string UserDataRoot { get; init; } = "";
        public string AccountDataRoot { get; init; } = "";
        public byte[]? SerializedPlanningRun { get; init; }
        public byte[] SerializedRun { get; init; } = [];
        public IReadOnlyList<PreCombatModSnapshot> Mods { get; init; } = [];
        public byte[] SolverSettings { get; init; } = [];
        public string CharacterId { get; init; } = "IRONCLAD";
        public string Seed { get; init; } = "test";
        public int ActIndex { get; init; }
    }
    internal sealed record PreCombatModSnapshot(string Id, string Version, string SourcePath);
}

namespace MegaCrit.Sts2.Core.Modding
{
    internal enum ModLoadState { None, Loaded }
    internal sealed class Manifest { public string? id { get; init; } public string? version { get; init; } }
    internal sealed class Mod { public Manifest? manifest { get; init; } public ModLoadState state { get; init; } public string path { get; init; } = ""; }
    internal static class ModManager { public static IReadOnlyList<Mod> Mods { get; } = []; }
}
namespace CombatSolver
{
    internal static class Entry
    {
        public static bool IsPreCombatWorker => false;
        public static TestLog Logger { get; } = new();
    }
    internal sealed class TestLog
    {
        public Action<string>? ErrorSink;
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) => ErrorSink?.Invoke(message);
    }
    internal static class CombatBugReportPaths
    {
        public static void EnsureOutputDirectories() => throw new NotSupportedException("No game startup in these tests.");
        public static string PreCombatLogsDirectory => throw new NotSupportedException();
    }
}
