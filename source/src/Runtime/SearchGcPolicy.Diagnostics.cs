using System.Diagnostics;
using System.Runtime;

namespace CombatSolver;

internal static partial class SearchGcPolicy
{
    private static readonly bool ForcedDetailedGcDiagnostics =
        string.Equals(
            Environment.GetEnvironmentVariable("COMBATSOLVER_GC_DIAGNOSTICS"),
            "1",
            StringComparison.Ordinal)
        || File.Exists(Path.Combine(
            Path.GetDirectoryName(typeof(SearchGcPolicy).Assembly.Location) ?? string.Empty,
            "performance-recording.json"));

    private static bool DetailedGcDiagnosticsEnabled
        => ForcedDetailedGcDiagnostics || UnattendedTestRunner.IsActive;

    private static string DescribeProcessMemory()
    {
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return $"working_set={process.WorkingSet64} private_bytes={process.PrivateMemorySize64} " +
               $"managed_live={GC.GetTotalMemory(forceFullCollection: false)} " +
               $"managed_heap={memory.HeapSizeBytes} fragmented={memory.FragmentedBytes} managed_committed={memory.TotalCommittedBytes} " +
               $"memory_load={memory.MemoryLoadBytes} high_memory_threshold={memory.HighMemoryLoadThresholdBytes} " +
               $"total_available={memory.TotalAvailableMemoryBytes} " +
               $"gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)} " +
               $"latency={GCSettings.LatencyMode} tick_ms={Environment.TickCount64}";
    }
}
