using System.Diagnostics;

namespace CombatSolver;

internal enum P4FingerprintSection
{
    Powers,
    TurnStateMaps,
    PowerStates,
    CardLifecycle,
    RelicAndModelState,
    Potions,
    MonsterAi,
    MonsterState,
    Lifecycle,
    StolenResource,
}

internal readonly record struct P4FingerprintSectionMetric(
    string Name,
    double ElapsedMilliseconds,
    long AllocatedBytes);

internal static class P4FingerprintProfiler
{
    private static readonly long[] Ticks = new long[Enum.GetValues<P4FingerprintSection>().Length];
    private static readonly long[] Allocated = new long[Enum.GetValues<P4FingerprintSection>().Length];

    internal static bool Enabled { get; set; }

    internal static SearchMeasurement Begin()
        => Enabled
            ? new SearchMeasurement(Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread())
            : SearchMeasurement.Disabled;

    internal static void End(P4FingerprintSection section, SearchMeasurement measurement)
    {
        if (!Enabled)
            return;
        int index = (int)section;
        Ticks[index] += Stopwatch.GetTimestamp() - measurement.Timestamp;
        Allocated[index] += GC.GetAllocatedBytesForCurrentThread() - measurement.AllocatedBytes;
    }

    internal static void Reset()
    {
        Array.Clear(Ticks);
        Array.Clear(Allocated);
    }

    internal static P4FingerprintSectionMetric[] Snapshot()
    {
        P4FingerprintSection[] sections = Enum.GetValues<P4FingerprintSection>();
        P4FingerprintSectionMetric[] result = new P4FingerprintSectionMetric[sections.Length];
        for (int index = 0; index < sections.Length; index++)
        {
            result[index] = new P4FingerprintSectionMetric(
                sections[index].ToString(),
                Stopwatch.GetElapsedTime(0, Ticks[index]).TotalMilliseconds,
                Allocated[index]);
        }
        return result;
    }
}
