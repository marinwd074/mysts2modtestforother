namespace CombatSolver;

internal readonly record struct R1FrontierShadowKey(
    int Turn,
    int ActionCount,
    int NodeCount);

internal readonly record struct R1FrontierShadowSnapshot(
    int SignatureLimit,
    int StoredSignatures,
    int DuplicateStores,
    int DroppedStores,
    int BaselineObservations,
    int KeyMisses,
    int ValidatedHits,
    int SignatureMismatches,
    int SkippedFrontiers);

internal sealed class R1FrontierShadowCache(int signatureLimit = 16)
{
    private readonly object _gate = new();
    private readonly Dictionary<R1FrontierShadowKey, List<string>> _signatures = [];
    private readonly int _signatureLimit = signatureLimit > 0
        ? signatureLimit
        : throw new ArgumentOutOfRangeException(nameof(signatureLimit));
    private int _storedSignatures;
    private int _duplicateStores;
    private int _droppedStores;
    private int _baselineObservations;
    private int _keyMisses;
    private int _validatedHits;
    private int _signatureMismatches;
    private int _skippedFrontiers;
    private int _frozen;

    internal void FreezeStores()
    {
        lock (_gate)
            _frozen = 1;
    }

    internal bool TryBeginBaselineObservation(R1FrontierShadowKey key)
    {
        if (Volatile.Read(ref _frozen) == 0)
            return false;
        Interlocked.Increment(ref _baselineObservations);
        lock (_gate)
        {
            if (_signatures.ContainsKey(key))
                return true;
            _keyMisses++;
            return false;
        }
    }

    internal void RecordSkipped()
        => Interlocked.Increment(ref _skippedFrontiers);

    internal void Observe(
        R1FrontierShadowKey key,
        string signature,
        bool r1Probe)
    {
        if (r1Probe)
        {
            Store(key, signature);
            return;
        }
        Validate(key, signature);
    }

    private void Store(R1FrontierShadowKey key, string signature)
    {
        lock (_gate)
        {
            if (_frozen != 0)
                return;
            if (!_signatures.TryGetValue(key, out List<string>? bucket))
            {
                if (_storedSignatures >= _signatureLimit)
                {
                    _droppedStores++;
                    return;
                }
                bucket = [];
                _signatures.Add(key, bucket);
            }
            if (bucket.Contains(signature, StringComparer.Ordinal))
            {
                _duplicateStores++;
                return;
            }
            if (_storedSignatures >= _signatureLimit)
            {
                _droppedStores++;
                return;
            }
            bucket.Add(signature);
            _storedSignatures++;
        }
    }

    private void Validate(R1FrontierShadowKey key, string signature)
    {
        lock (_gate)
        {
            if (!_signatures.TryGetValue(key, out List<string>? bucket))
                return;
            if (bucket.Contains(signature, StringComparer.Ordinal))
                _validatedHits++;
            else
                _signatureMismatches++;
        }
    }

    internal R1FrontierShadowSnapshot Capture()
        => new(
            _signatureLimit,
            Volatile.Read(ref _storedSignatures),
            Volatile.Read(ref _duplicateStores),
            Volatile.Read(ref _droppedStores),
            Volatile.Read(ref _baselineObservations),
            Volatile.Read(ref _keyMisses),
            Volatile.Read(ref _validatedHits),
            Volatile.Read(ref _signatureMismatches),
            Volatile.Read(ref _skippedFrontiers));

    internal static bool VerifyShadowGateForTesting()
    {
        R1FrontierShadowKey key = new(3, 4, 2);
        R1FrontierShadowCache cache = new(signatureLimit: 2);
        cache.Observe(key, "frontier-a", r1Probe: true);
        cache.FreezeStores();
        if (!cache.TryBeginBaselineObservation(key))
            return false;
        cache.Observe(key, "frontier-a", r1Probe: false);
        if (!cache.TryBeginBaselineObservation(key))
            return false;
        cache.Observe(key, "frontier-b", r1Probe: false);
        if (cache.TryBeginBaselineObservation(new R1FrontierShadowKey(3, 5, 2)))
            return false;

        R1FrontierShadowSnapshot snapshot = cache.Capture();
        return snapshot.StoredSignatures == 1
            && snapshot.BaselineObservations == 3
            && snapshot.KeyMisses == 1
            && snapshot.ValidatedHits == 1
            && snapshot.SignatureMismatches == 1;
    }
}
