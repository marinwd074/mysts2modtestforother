using System.Diagnostics;

namespace CombatSolver;

internal enum P4CombatFingerprintPhase
{
    Powers,
    TurnState,
    Lifecycle,
    RelicPotion,
    MonsterAi,
    MonsterState,
    DeathAndOtherLifecycle,
    StolenResource,
}

internal readonly record struct P4FingerprintMeasurement(long Timestamp, long AllocatedBytes)
{
    public static P4FingerprintMeasurement Disabled => default;
}

internal readonly record struct P4FingerprintPhaseSnapshot(
    string Phase,
    double ElapsedMs,
    long AllocatedBytes);

internal static class P4CombatFingerprintProfiler
{
    private static bool _enabled;
    private static readonly long[] Ticks =
        new long[Enum.GetValues<P4CombatFingerprintPhase>().Length];
    private static readonly long[] Allocated =
        new long[Enum.GetValues<P4CombatFingerprintPhase>().Length];

    public static void Reset(bool enabled)
    {
        _enabled = enabled;
        Array.Clear(Ticks);
        Array.Clear(Allocated);
    }

    public static P4FingerprintMeasurement Begin()
        => _enabled
            ? new(Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread())
            : P4FingerprintMeasurement.Disabled;

    public static void End(
        P4CombatFingerprintPhase phase,
        P4FingerprintMeasurement measurement)
    {
        if (!_enabled)
            return;
        int index = (int)phase;
        Ticks[index] += Stopwatch.GetTimestamp() - measurement.Timestamp;
        Allocated[index] += GC.GetAllocatedBytesForCurrentThread() - measurement.AllocatedBytes;
    }

    public static P4FingerprintPhaseSnapshot[] Snapshot()
    {
        P4CombatFingerprintPhase[] phases = Enum.GetValues<P4CombatFingerprintPhase>();
        P4FingerprintPhaseSnapshot[] result = new P4FingerprintPhaseSnapshot[phases.Length];
        for (int index = 0; index < phases.Length; index++)
        {
            result[index] = new(
                phases[index].ToString(),
                Stopwatch.GetElapsedTime(0, Ticks[index]).TotalMilliseconds,
                Allocated[index]);
        }
        return result;
    }
}
