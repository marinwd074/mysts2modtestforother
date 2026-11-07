namespace CombatSolver;

// Counts snapshot-owned simulator references, not distinct simulators or heap bytes.
// Retain only counters so observation cannot keep a snapshot or its parent chain alive.
internal sealed class SearchSnapshotOwnership
{
    private long _created;
    private int _active;
    private int _peak;

    internal void Acquire()
    {
        Interlocked.Increment(ref _created);
        int active = Interlocked.Increment(ref _active);
        int peak = Volatile.Read(ref _peak);
        while (active > peak)
        {
            int previous = Interlocked.CompareExchange(ref _peak, active, peak);
            if (previous == peak)
                break;
            peak = previous;
        }
    }

    internal void Release() => Interlocked.Decrement(ref _active);

    // Read after all member sessions have drained.
    internal (long Created, int Active, int Peak) Capture() => (_created, _active, _peak);
}
