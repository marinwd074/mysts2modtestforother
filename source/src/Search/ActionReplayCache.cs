using System.Runtime.CompilerServices;

namespace CombatSolver;

/// <summary>
/// Battle-scoped shadow validation for ordinary action replay reuse.
/// It never skips simulation and never authorizes deployment; it only proves that an exact
/// parent/action/policy key reproduces the same immutable output before reuse is enabled.
/// </summary>
internal sealed class ActionReplayCache
{
    internal const int CurrentContractVersion = 1;
    private const int MaximumEntries = 4096;

    private static readonly ConditionalWeakTable<CombatTransitionMemo, ActionReplayCache> Owners = new();

    private readonly object _gate = new();
    private readonly Dictionary<ReplayCacheKey, Entry> _entries = [];
    private string? _combatIdentity;
    private int _validatedHits;
    private int _collisionRejects;
    private int _outputMismatches;
    private int _droppedStores;

    private sealed record Entry(string ParentStateText, ReplayCacheObservation Output);

    internal static ActionReplayCache For(CombatTransitionMemo owner)
        => Owners.GetValue(owner, static _ => new ActionReplayCache());

    internal int Count { get { lock (_gate) return _entries.Count; } }
    internal int ValidatedHits { get { lock (_gate) return _validatedHits; } }
    internal int CollisionRejects { get { lock (_gate) return _collisionRejects; } }
    internal int OutputMismatches { get { lock (_gate) return _outputMismatches; } }
    internal int DroppedStores { get { lock (_gate) return _droppedStores; } }

    internal void BindCombat(string combatIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(combatIdentity);
        lock (_gate)
        {
            if (string.Equals(_combatIdentity, combatIdentity, StringComparison.Ordinal))
                return;

            _combatIdentity = combatIdentity;
            _entries.Clear();
            _validatedHits = 0;
            _collisionRejects = 0;
            _outputMismatches = 0;
            _droppedStores = 0;
        }
    }

    internal ReplayCacheValidationResult Observe(
        ReplayCacheKey key,
        string parentStateText,
        ReplayCacheObservation output)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out Entry? existing))
            {
                if (!string.Equals(existing.ParentStateText, parentStateText, StringComparison.Ordinal))
                {
                    _collisionRejects++;
                    return ReplayCacheValidationResult.CollisionRejected;
                }

                if (existing.Output == output)
                {
                    _validatedHits++;
                    return ReplayCacheValidationResult.ValidatedHit;
                }

                _outputMismatches++;
                return ReplayCacheValidationResult.OutputMismatch;
            }

            if (_entries.Count >= MaximumEntries)
            {
                _droppedStores++;
                return ReplayCacheValidationResult.DroppedStore;
            }

            _entries.Add(key, new Entry(parentStateText, output));
            return ReplayCacheValidationResult.Stored;
        }
    }
}

internal sealed class ShadowReplaySamplingBudget
{
    private readonly int _limit;
    private int _used;
    private int _observations;
    private int _stores;
    private int _validatedHits;
    private int _collisionRejects;
    private int _outputMismatches;
    private int _droppedStores;
    private int _capped;
    private long _validationTicks;
    private long _potentialSavedTicks;

    internal ShadowReplaySamplingBudget(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        _limit = limit;
    }

    internal int Limit => _limit;

    internal bool TryAcquire()
    {
        while (true)
        {
            int observed = Volatile.Read(ref _used);
            if (observed >= _limit)
            {
                Volatile.Write(ref _capped, 1);
                return false;
            }
            if (Interlocked.CompareExchange(ref _used, observed + 1, observed) == observed)
                return true;
        }
    }

    internal void Record(
        ReplayCacheValidationResult validation,
        long validationTicks,
        long potentialSavedTicks)
    {
        Interlocked.Increment(ref _observations);
        switch (validation)
        {
            case ReplayCacheValidationResult.Stored:
                Interlocked.Increment(ref _stores);
                break;
            case ReplayCacheValidationResult.ValidatedHit:
                Interlocked.Increment(ref _validatedHits);
                break;
            case ReplayCacheValidationResult.CollisionRejected:
                Interlocked.Increment(ref _collisionRejects);
                break;
            case ReplayCacheValidationResult.OutputMismatch:
                Interlocked.Increment(ref _outputMismatches);
                break;
            case ReplayCacheValidationResult.DroppedStore:
                Interlocked.Increment(ref _droppedStores);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(validation), validation, null);
        }
        Interlocked.Add(ref _validationTicks, validationTicks);
        if (potentialSavedTicks > 0)
            Interlocked.Add(ref _potentialSavedTicks, potentialSavedTicks);
    }

    internal ShadowReplaySamplingSnapshot Capture()
        => new(
            Limit: _limit,
            Used: Math.Min(_limit, Volatile.Read(ref _used)),
            Observations: Volatile.Read(ref _observations),
            Stores: Volatile.Read(ref _stores),
            ValidatedHits: Volatile.Read(ref _validatedHits),
            CollisionRejects: Volatile.Read(ref _collisionRejects),
            OutputMismatches: Volatile.Read(ref _outputMismatches),
            DroppedStores: Volatile.Read(ref _droppedStores),
            Capped: Volatile.Read(ref _capped) != 0,
            ValidationTicks: Interlocked.Read(ref _validationTicks),
            PotentialSavedTicks: Interlocked.Read(ref _potentialSavedTicks));
}

internal readonly record struct ShadowReplaySamplingSnapshot(
    int Limit,
    int Used,
    int Observations,
    int Stores,
    int ValidatedHits,
    int CollisionRejects,
    int OutputMismatches,
    int DroppedStores,
    bool Capped,
    long ValidationTicks,
    long PotentialSavedTicks);

internal readonly record struct ReplayCacheKey(
    StateFingerprint ParentState,
    string ActionIdentity,
    string PolicyIdentity,
    int ContractVersion);

internal readonly record struct ReplayCacheObservation(
    StateFingerprint OutputState,
    string OutputStateText,
    double Score,
    SearchBoundaryReason BoundaryReason,
    int Turn,
    bool PlayerDead,
    bool AllEnemiesDead,
    bool HasRisk,
    int PredictionGapCount);

internal enum ReplayCacheValidationResult
{
    Stored,
    ValidatedHit,
    CollisionRejected,
    OutputMismatch,
    DroppedStore,
}
