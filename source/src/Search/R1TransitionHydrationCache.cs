using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal readonly record struct R1TransitionHydrationSnapshot(
    int EntryLimit,
    int Entries,
    int Stores,
    int DuplicateStores,
    int DroppedStores,
    int StoreConflicts,
    int FirstValidations,
    int ValidatedKeys,
    int IndexMisses,
    int CollisionRejects,
    int HydrationHits,
    int OutputMismatches,
    bool ReuseDisabled);

internal sealed class R1TransitionHydrationSeed(
    CombatPredictionSimulator simulator,
    ForkableSet<uint> processedEnemyDeaths,
    int turn,
    int shufflesCrossed,
    SearchBoundaryReason boundaryReason,
    StateFingerprint expectedOutputStateKey)
{
    internal CombatPredictionSimulator Simulator { get; } = simulator;
    internal ForkableSet<uint> ProcessedEnemyDeaths { get; } = processedEnemyDeaths;
    internal int Turn { get; } = turn;
    internal int ShufflesCrossed { get; } = shufflesCrossed;
    internal SearchBoundaryReason BoundaryReason { get; } = boundaryReason;
    internal StateFingerprint ExpectedOutputStateKey { get; } = expectedOutputStateKey;
}

internal sealed class R1TransitionHydrationCache(int entryLimit = 32)
{
    private sealed class Entry(
        string combatIdentity,
        string parentStateText,
        string outputStateText,
        CombatPredictionSimulator outputSimulator,
        ForkableSet<uint> processedEnemyDeaths,
        int turn,
        int shufflesCrossed,
        SearchBoundaryReason boundaryReason,
        StateFingerprint outputStateKey)
    {
        internal readonly object ForkGate = new();
        internal string CombatIdentity { get; } = combatIdentity;
        internal string ParentStateText { get; } = parentStateText;
        internal string OutputStateText { get; } = outputStateText;
        internal CombatPredictionSimulator OutputSimulator { get; } = outputSimulator;
        internal ForkableSet<uint> ProcessedEnemyDeaths { get; } = processedEnemyDeaths;
        internal int Turn { get; } = turn;
        internal int ShufflesCrossed { get; } = shufflesCrossed;
        internal SearchBoundaryReason BoundaryReason { get; } = boundaryReason;
        internal StateFingerprint OutputStateKey { get; } = outputStateKey;
    }

    private readonly object _gate = new();
    private readonly Dictionary<ReplayCacheKey, Entry> _entries = [];
    private readonly HashSet<ReplayCacheKey> _validatedKeys = [];
    private readonly int _entryLimit = entryLimit > 0
        ? entryLimit
        : throw new ArgumentOutOfRangeException(nameof(entryLimit));
    private int _frozen;
    private int _stores;
    private int _duplicateStores;
    private int _droppedStores;
    private int _storeConflicts;
    private int _firstValidations;
    private int _validatedKeysCount;
    private int _indexMisses;
    private int _collisionRejects;
    private int _hydrationHits;
    private int _outputMismatches;
    private int _reuseDisabled;

    internal int EntryCount
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    internal void FreezeStores()
    {
        lock (_gate)
            Volatile.Write(ref _frozen, 1);
    }

    internal bool MayContain(ReplayCacheKey key)
    {
        if (Volatile.Read(ref _reuseDisabled) != 0)
            return false;
        lock (_gate)
            return _entries.ContainsKey(key);
    }

    internal void Store(
        ReplayCacheKey key,
        string combatIdentity,
        string parentStateText,
        string outputStateText,
        SimulationSnapshot output)
    {
        if (Volatile.Read(ref _frozen) != 0
            || Volatile.Read(ref _reuseDisabled) != 0
            || !output.HasSimulator
            || output.BoundaryReason != SearchBoundaryReason.None
            || output.PlayerDead
            || output.AllEnemiesDead
            || output.HasRisk
            || output.PredictionGaps.Any(static gap => !gap.Compensated))
        {
            return;
        }

        lock (_gate)
        {
            if (_frozen != 0 || _reuseDisabled != 0)
                return;

            if (_entries.TryGetValue(key, out Entry? existing))
            {
                if (string.Equals(existing.CombatIdentity, combatIdentity, StringComparison.Ordinal)
                    && string.Equals(existing.ParentStateText, parentStateText, StringComparison.Ordinal)
                    && existing.OutputStateKey == output.StateKey
                    && string.Equals(existing.OutputStateText, outputStateText, StringComparison.Ordinal))
                {
                    _duplicateStores++;
                }
                else
                {
                    _storeConflicts++;
                    Volatile.Write(ref _reuseDisabled, 1);
                }
                return;
            }

            if (_entries.Count >= _entryLimit)
            {
                _droppedStores++;
                return;
            }

            CombatPredictionSimulator outputSimulator =
                ((CombatPredictionSimulator)output.Simulator).Fork();
            ForkableSet<uint> processedEnemyDeaths =
                ((ForkableSet<uint>)output.ProcessedEnemyDeaths).Fork();
            _entries.Add(key, new Entry(
                combatIdentity,
                parentStateText,
                outputStateText,
                outputSimulator,
                processedEnemyDeaths,
                output.Turn,
                output.ShufflesCrossed,
                output.BoundaryReason,
                output.StateKey));
            _stores++;
        }
    }

    internal void ValidateRealReplay(
        ReplayCacheKey key,
        string combatIdentity,
        string parentStateText,
        StateFingerprint outputStateKey,
        string outputStateText)
    {
        if (Volatile.Read(ref _frozen) == 0
            || Volatile.Read(ref _reuseDisabled) != 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_reuseDisabled != 0
                || _validatedKeys.Contains(key)
                || !_entries.TryGetValue(key, out Entry? entry))
            {
                return;
            }

            _firstValidations++;
            if (!string.Equals(entry.CombatIdentity, combatIdentity, StringComparison.Ordinal)
                || !string.Equals(entry.ParentStateText, parentStateText, StringComparison.Ordinal))
            {
                _collisionRejects++;
                _reuseDisabled = 1;
                return;
            }
            if (entry.OutputStateKey != outputStateKey
                || !string.Equals(entry.OutputStateText, outputStateText, StringComparison.Ordinal))
            {
                _outputMismatches++;
                _reuseDisabled = 1;
                return;
            }

            _validatedKeys.Add(key);
            _validatedKeysCount++;
        }
    }

    internal bool TryFork(
        ReplayCacheKey key,
        string combatIdentity,
        string parentStateText,
        out R1TransitionHydrationSeed seed)
    {
        seed = null!;
        if (Volatile.Read(ref _frozen) == 0
            || Volatile.Read(ref _reuseDisabled) != 0)
        {
            return false;
        }

        Entry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry))
            {
                _indexMisses++;
                return false;
            }
            if (!IsReusableKeyNoLock(key))
                return false;
            if (!string.Equals(entry.CombatIdentity, combatIdentity, StringComparison.Ordinal)
                || !string.Equals(entry.ParentStateText, parentStateText, StringComparison.Ordinal))
            {
                _collisionRejects++;
                _reuseDisabled = 1;
                return false;
            }
        }

        lock (entry.ForkGate)
        {
            if (Volatile.Read(ref _reuseDisabled) != 0)
                return false;
            seed = new R1TransitionHydrationSeed(
                entry.OutputSimulator.Fork(),
                entry.ProcessedEnemyDeaths.Fork(),
                entry.Turn,
                entry.ShufflesCrossed,
                entry.BoundaryReason,
                entry.OutputStateKey);
        }
        return true;
    }

    internal void RecordHydrationHit()
        => Interlocked.Increment(ref _hydrationHits);

    internal void RecordOutputMismatch()
    {
        Interlocked.Increment(ref _outputMismatches);
        Volatile.Write(ref _reuseDisabled, 1);
    }

    private bool IsReusableKeyNoLock(ReplayCacheKey key)
        => _frozen != 0
            && _reuseDisabled == 0
            && _validatedKeys.Contains(key)
            && _entries.ContainsKey(key);

    internal bool IsReusableKeyForTesting(ReplayCacheKey key)
    {
        lock (_gate)
            return IsReusableKeyNoLock(key);
    }

    internal void Release()
    {
        lock (_gate)
        {
            _entries.Clear();
            _validatedKeys.Clear();
            Volatile.Write(ref _reuseDisabled, 1);
        }
    }

    internal R1TransitionHydrationSnapshot Capture()
    {
        int entries;
        lock (_gate)
            entries = _entries.Count;
        return new R1TransitionHydrationSnapshot(
            _entryLimit,
            entries,
            Volatile.Read(ref _stores),
            Volatile.Read(ref _duplicateStores),
            Volatile.Read(ref _droppedStores),
            Volatile.Read(ref _storeConflicts),
            Volatile.Read(ref _firstValidations),
            Volatile.Read(ref _validatedKeysCount),
            Volatile.Read(ref _indexMisses),
            Volatile.Read(ref _collisionRejects),
            Volatile.Read(ref _hydrationHits),
            Volatile.Read(ref _outputMismatches),
            Volatile.Read(ref _reuseDisabled) != 0);
    }

    internal static bool VerifyExactReuseGateForTesting()
    {
        ReplayCacheKey key = new(
            new StateFingerprint(1, 2),
            "PlayCard:TEST",
            "policy",
            ActionReplayCache.CurrentContractVersion);

        R1TransitionHydrationCache accepted = new(entryLimit: 1);
        accepted._entries.Add(key, new Entry(
            "combat",
            "parent",
            "output",
            outputSimulator: null!,
            processedEnemyDeaths: [],
            turn: 3,
            shufflesCrossed: 0,
            boundaryReason: SearchBoundaryReason.None,
            outputStateKey: new StateFingerprint(3, 4)));

        if (accepted.IsReusableKeyForTesting(key))
            return false;
        accepted.FreezeStores();
        if (accepted.IsReusableKeyForTesting(key))
            return false;
        accepted.ValidateRealReplay(
            key,
            "combat",
            "parent",
            new StateFingerprint(3, 4),
            "output");
        if (!accepted.IsReusableKeyForTesting(key))
            return false;

        R1TransitionHydrationSnapshot acceptedSnapshot = accepted.Capture();
        if (acceptedSnapshot.FirstValidations != 1
            || acceptedSnapshot.ValidatedKeys != 1
            || acceptedSnapshot.ReuseDisabled)
        {
            return false;
        }

        R1TransitionHydrationCache rejected = new(entryLimit: 1);
        rejected._entries.Add(key, new Entry(
            "combat",
            "parent",
            "output",
            outputSimulator: null!,
            processedEnemyDeaths: [],
            turn: 3,
            shufflesCrossed: 0,
            boundaryReason: SearchBoundaryReason.None,
            outputStateKey: new StateFingerprint(3, 4)));
        rejected.FreezeStores();
        rejected.ValidateRealReplay(
            key,
            "combat",
            "parent",
            new StateFingerprint(9, 9),
            "different");
        return rejected.Capture().ReuseDisabled
            && rejected.Capture().OutputMismatches == 1
            && !rejected.IsReusableKeyForTesting(key);
    }
}
