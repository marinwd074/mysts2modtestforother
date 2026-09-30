using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal readonly record struct R1TransitionHydrationRejectSample(
    string ActionIdentity,
    string Reason);

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
    int RejectedKeys,
    bool ReuseDisabled,
    IReadOnlyList<R1TransitionHydrationRejectSample> RejectSamples,
    int RetainedSimulators = 0);

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

internal sealed class R1TransitionHydrationCache(int entryLimit = 32, bool learnFromSearch = false)
{
    private sealed class Entry(
        string combatIdentity,
        string parentStateText,
        string outputStateText,
        CombatPredictionSimulator? outputSimulator,
        ForkableSet<uint>? processedEnemyDeaths,
        int turn,
        int shufflesCrossed,
        SearchBoundaryReason boundaryReason,
        StateFingerprint outputStateKey)
    {
        internal readonly object ForkGate = new();
        internal string CombatIdentity { get; } = combatIdentity;
        internal string ParentStateText { get; } = parentStateText;
        internal string OutputStateText { get; } = outputStateText;
        internal CombatPredictionSimulator? OutputSimulator { get; set; } = outputSimulator;
        internal ForkableSet<uint>? ProcessedEnemyDeaths { get; set; } = processedEnemyDeaths;
        internal int Turn { get; } = turn;
        internal int ShufflesCrossed { get; } = shufflesCrossed;
        internal SearchBoundaryReason BoundaryReason { get; } = boundaryReason;
        internal StateFingerprint OutputStateKey { get; } = outputStateKey;
    }

    private readonly object _gate = new();
    private readonly Dictionary<ReplayCacheKey, Entry> _entries = [];
    private readonly HashSet<ReplayCacheKey> _validatedKeys = [];
    private readonly HashSet<ReplayCacheKey> _rejectedKeys = [];
    private readonly List<R1TransitionHydrationRejectSample> _rejectSamples = [];
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
    private int _rejectedKeysCount;
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
            return _entries.ContainsKey(key) && !_rejectedKeys.Contains(key);
    }

    internal bool CanObserveSearchReplay(ReplayCacheKey key)
    {
        lock (_gate)
        {
            if (!learnFromSearch || _reuseDisabled != 0 || _rejectedKeys.Contains(key))
                return false;
            if (_entries.ContainsKey(key) || _entries.Count < _entryLimit)
                return true;
            _droppedStores++;
            return false;
        }
    }

    internal void ObserveSearchReplay(
        ReplayCacheKey key, string combatIdentity, string parentStateText,
        string outputStateText, SimulationSnapshot output)
    {
        lock (_gate)
        {
            if (!learnFromSearch || _reuseDisabled != 0 || _rejectedKeys.Contains(key))
                return;
            if (!IsSafeOutput(output))
            {
                if (_entries.ContainsKey(key))
                {
                    _outputMismatches++;
                    RejectKeyNoLock(key, "unsafe_output");
                }
                return;
            }
            if (_entries.TryGetValue(key, out Entry? entry))
            {
                ValidateRealReplay(key, combatIdentity, parentStateText, output.StateKey, outputStateText);
                // Cold requests retain only value metadata until the first independent replay
                // validates a repeat. One-off actions never retain a simulator graph.
                if (IsReusableKeyNoLock(key) && entry.OutputSimulator == null)
                {
                    entry.OutputSimulator = output.Simulator.Fork();
                    entry.ProcessedEnemyDeaths = ((ForkableSet<uint>)output.ProcessedEnemyDeaths).Fork();
                }
            }
            else
                Store(key, combatIdentity, parentStateText, outputStateText, output);
        }
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
            || !IsSafeOutput(output))
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
                    RejectKeyNoLock(key, "store_conflict");
                }
                return;
            }

            if (_entries.Count >= _entryLimit)
            {
                _droppedStores++;
                return;
            }

            CombatPredictionSimulator? outputSimulator = learnFromSearch ? null : output.Simulator.Fork();
            ForkableSet<uint>? processedEnemyDeaths = learnFromSearch ? null
                : ((ForkableSet<uint>)output.ProcessedEnemyDeaths).Fork();
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
        if ((!learnFromSearch && Volatile.Read(ref _frozen) == 0)
            || Volatile.Read(ref _reuseDisabled) != 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_reuseDisabled != 0
                || _rejectedKeys.Contains(key)
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
                RejectKeyNoLock(key, "parent_collision");
                return;
            }
            if (entry.OutputStateKey != outputStateKey
                || !string.Equals(entry.OutputStateText, outputStateText, StringComparison.Ordinal))
            {
                _outputMismatches++;
                RejectKeyNoLock(
                    key,
                    entry.OutputStateKey != outputStateKey
                        ? "output_state_key"
                        : "output_state_text");
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
        if ((!learnFromSearch && Volatile.Read(ref _frozen) == 0)
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
                RejectKeyNoLock(key, "fork_parent_collision");
                return false;
            }
        }

        lock (entry.ForkGate)
        {
            lock (_gate)
                if (!IsReusableKeyNoLock(key) || entry.OutputSimulator == null || entry.ProcessedEnemyDeaths == null)
                    return false;
            seed = new R1TransitionHydrationSeed(
                entry.OutputSimulator!.Fork(),
                entry.ProcessedEnemyDeaths!.Fork(),
                entry.Turn,
                entry.ShufflesCrossed,
                entry.BoundaryReason,
                entry.OutputStateKey);
        }
        return true;
    }

    internal void RecordHydrationHit()
        => Interlocked.Increment(ref _hydrationHits);

    private static bool IsSafeOutput(SimulationSnapshot output)
        => output.HasSimulator && output.BoundaryReason == SearchBoundaryReason.None
            && !output.PlayerDead && !output.AllEnemiesDead && !output.HasRisk
            && output.PredictionGaps.All(static gap => gap.Compensated);

    internal void RecordOutputMismatch(ReplayCacheKey key)
    {
        lock (_gate)
        {
            _outputMismatches++;
            RejectKeyNoLock(key, "hydrated_state_key");
        }
    }

    private void RejectKeyNoLock(ReplayCacheKey key, string reason)
    {
        if (_rejectedKeys.Add(key))
        {
            _rejectedKeysCount++;
            if (_rejectSamples.Count < 8)
                _rejectSamples.Add(new R1TransitionHydrationRejectSample(
                    key.ActionIdentity,
                    reason));
        }
        _validatedKeys.Remove(key);
    }

    private bool IsReusableKeyNoLock(ReplayCacheKey key)
        => (learnFromSearch || _frozen != 0)
            && _reuseDisabled == 0
            && !_rejectedKeys.Contains(key)
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
            _rejectedKeys.Clear();
            _rejectSamples.Clear();
            Volatile.Write(ref _reuseDisabled, 1);
        }
    }

    internal R1TransitionHydrationSnapshot Capture()
    {
        lock (_gate)
            return new R1TransitionHydrationSnapshot(
                _entryLimit,
                _entries.Count,
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
                Volatile.Read(ref _rejectedKeysCount),
                Volatile.Read(ref _reuseDisabled) != 0,
                _rejectSamples.ToArray(),
                _entries.Values.Count(static entry => entry.OutputSimulator != null));
    }

    internal static bool VerifyExactReuseGateForTesting(bool learnFromSearch = false)
    {
        ReplayCacheKey goodKey = new(
            new StateFingerprint(1, 2),
            "PlayCard:GOOD",
            "policy",
            ActionReplayCache.CurrentContractVersion);
        ReplayCacheKey badKey = new(
            new StateFingerprint(5, 6),
            "PlayCard:BAD",
            "policy",
            ActionReplayCache.CurrentContractVersion);

        R1TransitionHydrationCache cache = new(entryLimit: 2, learnFromSearch: learnFromSearch);
        cache._entries.Add(goodKey, new Entry(
            "combat",
            "parent-good",
            "output-good",
            outputSimulator: null!,
            processedEnemyDeaths: [],
            turn: 3,
            shufflesCrossed: 0,
            boundaryReason: SearchBoundaryReason.None,
            outputStateKey: new StateFingerprint(3, 4)));
        cache._entries.Add(badKey, new Entry(
            "combat",
            "parent-bad",
            "output-bad",
            outputSimulator: null!,
            processedEnemyDeaths: [],
            turn: 3,
            shufflesCrossed: 0,
            boundaryReason: SearchBoundaryReason.None,
            outputStateKey: new StateFingerprint(7, 8)));

        if (cache.IsReusableKeyForTesting(goodKey) || cache.IsReusableKeyForTesting(badKey))
            return false;
        if (!learnFromSearch)
            cache.FreezeStores();
        cache.ValidateRealReplay(
            goodKey,
            "combat",
            "parent-good",
            new StateFingerprint(3, 4),
            "output-good");
        cache.ValidateRealReplay(
            badKey,
            "combat",
            "parent-bad",
            new StateFingerprint(9, 9),
            "different");

        R1TransitionHydrationSnapshot snapshot = cache.Capture();
        return cache.IsReusableKeyForTesting(goodKey)
            && !cache.IsReusableKeyForTesting(badKey)
            && snapshot.FirstValidations == 2
            && snapshot.ValidatedKeys == 1
            && snapshot.OutputMismatches == 1
            && snapshot.RejectedKeys == 1
            && !snapshot.ReuseDisabled;
    }
}
