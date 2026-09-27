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
    internal const int CurrentLocalCoreContractVersion = 1;
    private const int MaximumEntries = 4096;

    private static readonly ConditionalWeakTable<CombatTransitionMemo, ActionReplayCache> Owners = new();
    private static readonly ConditionalWeakTable<CombatTransitionMemo, ActionReplayCache> LocalCoreOwners = new();

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

    internal static ActionReplayCache ForLocalCoreShadow(CombatTransitionMemo owner)
        => LocalCoreOwners.GetValue(owner, static _ => new ActionReplayCache());

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

internal enum ShadowReplaySampleClass
{
    CurrentTurn,
    FutureTurn,
}

internal sealed class ShadowReplaySamplingBudget
{
    private readonly int _limit;
    private readonly int _futureTurnReserve;
    private readonly object _acquireGate = new();
    private int _used;
    private int _currentTurnUsed;
    private int _futureTurnUsed;
    private int _currentTurnLimited;
    private int _observations;
    private int _stores;
    private int _validatedHits;
    private int _collisionRejects;
    private int _outputMismatches;
    private int _droppedStores;
    private int _localCoreObservations;
    private int _localCoreStores;
    private int _localCoreValidatedHits;
    private int _localCoreCollisionRejects;
    private int _localCoreOutputMismatches;
    private int _localCoreDroppedStores;
    private int _capped;
    private long _validationTicks;
    private long _potentialSavedTicks;

    internal ShadowReplaySamplingBudget(int limit, int futureTurnReserve = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(futureTurnReserve);
        if (futureTurnReserve > limit)
            throw new ArgumentOutOfRangeException(nameof(futureTurnReserve));
        _limit = limit;
        _futureTurnReserve = futureTurnReserve;
    }

    internal int Limit => _limit;
    internal int FutureTurnReserve => _futureTurnReserve;
    internal int CurrentTurnUsed => Volatile.Read(ref _currentTurnUsed);
    internal int FutureTurnUsed => Volatile.Read(ref _futureTurnUsed);
    internal bool CurrentTurnLimited => Volatile.Read(ref _currentTurnLimited) != 0;

    internal bool TryAcquire()
        => TryAcquireCore(sampleClass: null);

    internal bool TryAcquire(ShadowReplaySampleClass sampleClass)
        => TryAcquireCore(sampleClass);

    private bool TryAcquireCore(ShadowReplaySampleClass? sampleClass)
    {
        lock (_acquireGate)
        {
            if (_used >= _limit)
            {
                Volatile.Write(ref _capped, 1);
                return false;
            }
            if (sampleClass == ShadowReplaySampleClass.CurrentTurn
                && _currentTurnUsed >= _limit - _futureTurnReserve)
            {
                Volatile.Write(ref _currentTurnLimited, 1);
                return false;
            }

            _used++;
            if (sampleClass == ShadowReplaySampleClass.CurrentTurn)
                _currentTurnUsed++;
            else if (sampleClass == ShadowReplaySampleClass.FutureTurn)
                _futureTurnUsed++;
            return true;
        }
    }

    internal void Record(
        ReplayCacheValidationResult validation,
        long validationTicks,
        long potentialSavedTicks,
        ReplayCacheValidationResult? localCoreValidation = null)
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
        if (localCoreValidation is { } localValidation)
        {
            Interlocked.Increment(ref _localCoreObservations);
            switch (localValidation)
            {
                case ReplayCacheValidationResult.Stored:
                    Interlocked.Increment(ref _localCoreStores);
                    break;
                case ReplayCacheValidationResult.ValidatedHit:
                    Interlocked.Increment(ref _localCoreValidatedHits);
                    break;
                case ReplayCacheValidationResult.CollisionRejected:
                    Interlocked.Increment(ref _localCoreCollisionRejects);
                    break;
                case ReplayCacheValidationResult.OutputMismatch:
                    Interlocked.Increment(ref _localCoreOutputMismatches);
                    break;
                case ReplayCacheValidationResult.DroppedStore:
                    Interlocked.Increment(ref _localCoreDroppedStores);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(localCoreValidation), localValidation, null);
            }
        }
        Interlocked.Add(ref _validationTicks, validationTicks);
        if (potentialSavedTicks > 0)
            Interlocked.Add(ref _potentialSavedTicks, potentialSavedTicks);
    }

    internal ShadowReplaySamplingSnapshot Capture()
        => new(
            Limit: _limit,
            Used: Math.Min(_limit, Volatile.Read(ref _used)),
            FutureTurnReserve: _futureTurnReserve,
            CurrentTurnUsed: Volatile.Read(ref _currentTurnUsed),
            FutureTurnUsed: Volatile.Read(ref _futureTurnUsed),
            CurrentTurnLimited: Volatile.Read(ref _currentTurnLimited) != 0,
            Observations: Volatile.Read(ref _observations),
            Stores: Volatile.Read(ref _stores),
            ValidatedHits: Volatile.Read(ref _validatedHits),
            CollisionRejects: Volatile.Read(ref _collisionRejects),
            OutputMismatches: Volatile.Read(ref _outputMismatches),
            DroppedStores: Volatile.Read(ref _droppedStores),
            LocalCoreObservations: Volatile.Read(ref _localCoreObservations),
            LocalCoreStores: Volatile.Read(ref _localCoreStores),
            LocalCoreValidatedHits: Volatile.Read(ref _localCoreValidatedHits),
            LocalCoreCollisionRejects: Volatile.Read(ref _localCoreCollisionRejects),
            LocalCoreOutputMismatches: Volatile.Read(ref _localCoreOutputMismatches),
            LocalCoreDroppedStores: Volatile.Read(ref _localCoreDroppedStores),
            Capped: Volatile.Read(ref _capped) != 0,
            ValidationTicks: Interlocked.Read(ref _validationTicks),
            PotentialSavedTicks: Interlocked.Read(ref _potentialSavedTicks));
}

internal readonly record struct ShadowReplaySamplingSnapshot(
    int Limit,
    int Used,
    int FutureTurnReserve,
    int CurrentTurnUsed,
    int FutureTurnUsed,
    bool CurrentTurnLimited,
    int Observations,
    int Stores,
    int ValidatedHits,
    int CollisionRejects,
    int OutputMismatches,
    int DroppedStores,
    int LocalCoreObservations,
    int LocalCoreStores,
    int LocalCoreValidatedHits,
    int LocalCoreCollisionRejects,
    int LocalCoreOutputMismatches,
    int LocalCoreDroppedStores,
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
