namespace CombatSolver;

internal readonly record struct R1FrontierShadowKey(
    int Turn,
    int ActionCount,
    int NodeCount);

internal readonly record struct R1FrontierDepthKey(
    int Turn,
    int ActionCount);

internal enum R1FrontierMissKind
{
    Turn,
    ActionCount,
    NodeCount,
}

internal readonly record struct R1FrontierMissSample(
    R1FrontierMissKind Kind,
    R1FrontierShadowKey Baseline,
    R1FrontierShadowKey NearestProbe);

internal readonly record struct R1FrontierSubsetSample(
    R1FrontierDepthKey Depth,
    int ProbeNodes,
    int BaselineNodes,
    int IntersectionNodes,
    bool ProbeSubsetOfBaseline,
    bool ExactSetMatch);

internal readonly record struct R1FrontierShadowSnapshot(
    int SignatureLimit,
    int StoredSignatures,
    int DuplicateStores,
    int DroppedStores,
    int BaselineObservations,
    int KeyMisses,
    int TurnMisses,
    int ActionCountMisses,
    int NodeCountMisses,
    int ValidatedHits,
    int SignatureMismatches,
    int SkippedFrontiers,
    int SubsetObservations,
    int ProbeSubsetHits,
    int ExactSetHits,
    int PartialOverlapHits,
    int ZeroOverlapHits,
    IReadOnlyList<R1FrontierMissSample> MissSamples,
    IReadOnlyList<R1FrontierSubsetSample> SubsetSamples);

internal sealed class R1FrontierShadowCache(int signatureLimit = 16)
{
    private const int SampleLimit = 16;

    private readonly object _gate = new();
    private readonly Dictionary<R1FrontierShadowKey, List<string>> _signatures = [];
    private readonly Dictionary<R1FrontierDepthKey, List<HashSet<StateFingerprint>>> _nodeSets = [];
    private readonly List<R1FrontierMissSample> _missSamples = [];
    private readonly List<R1FrontierSubsetSample> _subsetSamples = [];
    private readonly int _signatureLimit = signatureLimit > 0
        ? signatureLimit
        : throw new ArgumentOutOfRangeException(nameof(signatureLimit));
    private int _storedSignatures;
    private int _duplicateStores;
    private int _droppedStores;
    private int _baselineObservations;
    private int _keyMisses;
    private int _turnMisses;
    private int _actionCountMisses;
    private int _nodeCountMisses;
    private int _validatedHits;
    private int _signatureMismatches;
    private int _skippedFrontiers;
    private int _subsetObservations;
    private int _probeSubsetHits;
    private int _exactSetHits;
    private int _partialOverlapHits;
    private int _zeroOverlapHits;
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
            R1FrontierMissKind kind = ClassifyMiss(key);
            switch (kind)
            {
                case R1FrontierMissKind.Turn:
                    _turnMisses++;
                    break;
                case R1FrontierMissKind.ActionCount:
                    _actionCountMisses++;
                    break;
                case R1FrontierMissKind.NodeCount:
                    _nodeCountMisses++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }

            if (_missSamples.Count < SampleLimit
                && TryFindNearestProbeKey(key, out R1FrontierShadowKey nearest))
            {
                _missSamples.Add(new R1FrontierMissSample(kind, key, nearest));
            }
            return false;
        }
    }

    internal bool WantsSubsetObservation(R1FrontierDepthKey depth)
    {
        if (Volatile.Read(ref _frozen) == 0)
            return false;
        lock (_gate)
            return _nodeSets.ContainsKey(depth);
    }

    internal void ObserveSubset(
        R1FrontierDepthKey depth,
        IReadOnlyList<StateFingerprint> baselineNodeFingerprints)
    {
        lock (_gate)
        {
            if (!_nodeSets.TryGetValue(depth, out List<HashSet<StateFingerprint>>? probeSets))
                return;

            _subsetObservations++;
            HashSet<StateFingerprint> baseline = [.. baselineNodeFingerprints];
            HashSet<StateFingerprint>? bestProbe = null;
            int bestIntersection = -1;
            foreach (HashSet<StateFingerprint> probe in probeSets)
            {
                int intersection = 0;
                foreach (StateFingerprint node in probe)
                {
                    if (baseline.Contains(node))
                        intersection++;
                }
                if (intersection > bestIntersection)
                {
                    bestIntersection = intersection;
                    bestProbe = probe;
                }
            }

            if (bestProbe == null)
                return;

            bool probeSubset = bestIntersection == bestProbe.Count;
            bool exactSet = probeSubset && bestProbe.Count == baseline.Count;
            if (exactSet)
                _exactSetHits++;
            else if (probeSubset)
                _probeSubsetHits++;
            else if (bestIntersection > 0)
                _partialOverlapHits++;
            else
                _zeroOverlapHits++;

            if (_subsetSamples.Count < SampleLimit)
            {
                _subsetSamples.Add(new R1FrontierSubsetSample(
                    depth,
                    bestProbe.Count,
                    baseline.Count,
                    Math.Max(0, bestIntersection),
                    probeSubset,
                    exactSet));
            }
        }
    }

    private R1FrontierMissKind ClassifyMiss(R1FrontierShadowKey baseline)
    {
        bool sameTurn = false;
        bool sameTurnAndActionCount = false;
        foreach (R1FrontierShadowKey probe in _signatures.Keys)
        {
            if (probe.Turn != baseline.Turn)
                continue;
            sameTurn = true;
            if (probe.ActionCount == baseline.ActionCount)
            {
                sameTurnAndActionCount = true;
                break;
            }
        }
        if (!sameTurn)
            return R1FrontierMissKind.Turn;
        return sameTurnAndActionCount
            ? R1FrontierMissKind.NodeCount
            : R1FrontierMissKind.ActionCount;
    }

    private bool TryFindNearestProbeKey(
        R1FrontierShadowKey baseline,
        out R1FrontierShadowKey nearest)
    {
        nearest = default;
        bool found = false;
        (long Turn, long Action, long Nodes) bestDistance = default;
        foreach (R1FrontierShadowKey probe in _signatures.Keys)
        {
            (long Turn, long Action, long Nodes) distance = (
                Math.Abs((long)probe.Turn - baseline.Turn),
                Math.Abs((long)probe.ActionCount - baseline.ActionCount),
                Math.Abs((long)probe.NodeCount - baseline.NodeCount));
            if (!found || CompareDistance(distance, bestDistance) < 0)
            {
                found = true;
                nearest = probe;
                bestDistance = distance;
            }
        }
        return found;
    }

    private static int CompareDistance(
        (long Turn, long Action, long Nodes) left,
        (long Turn, long Action, long Nodes) right)
    {
        int compare = left.Turn.CompareTo(right.Turn);
        if (compare != 0)
            return compare;
        compare = left.Action.CompareTo(right.Action);
        return compare != 0
            ? compare
            : left.Nodes.CompareTo(right.Nodes);
    }

    internal void RecordSkipped()
        => Interlocked.Increment(ref _skippedFrontiers);

    internal void StoreProbe(
        R1FrontierShadowKey key,
        string signature,
        IReadOnlyList<StateFingerprint> nodeFingerprints)
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
            R1FrontierDepthKey depth = new(key.Turn, key.ActionCount);
            if (!_nodeSets.TryGetValue(depth, out List<HashSet<StateFingerprint>>? sets))
            {
                sets = [];
                _nodeSets.Add(depth, sets);
            }
            sets.Add([.. nodeFingerprints]);
        }
    }

    internal void ValidateExact(R1FrontierShadowKey key, string signature)
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
    {
        lock (_gate)
        {
            return new R1FrontierShadowSnapshot(
                _signatureLimit,
                _storedSignatures,
                _duplicateStores,
                _droppedStores,
                _baselineObservations,
                _keyMisses,
                _turnMisses,
                _actionCountMisses,
                _nodeCountMisses,
                _validatedHits,
                _signatureMismatches,
                Volatile.Read(ref _skippedFrontiers),
                _subsetObservations,
                _probeSubsetHits,
                _exactSetHits,
                _partialOverlapHits,
                _zeroOverlapHits,
                _missSamples.ToArray(),
                _subsetSamples.ToArray());
        }
    }

    internal static bool VerifyShadowGateForTesting()
    {
        StateFingerprint a = new(1, 1);
        StateFingerprint b = new(2, 2);
        StateFingerprint c = new(3, 3);
        R1FrontierShadowKey exactKey = new(3, 4, 2);
        R1FrontierShadowCache cache = new(signatureLimit: 4);
        cache.StoreProbe(exactKey, "frontier-a", [a, b]);
        cache.StoreProbe(new R1FrontierShadowKey(3, 6, 1), "frontier-b", [c]);
        cache.StoreProbe(new R1FrontierShadowKey(5, 2, 1), "frontier-c", [a]);
        cache.FreezeStores();

        if (!cache.TryBeginBaselineObservation(exactKey))
            return false;
        cache.ValidateExact(exactKey, "frontier-a");
        if (!cache.TryBeginBaselineObservation(exactKey))
            return false;
        cache.ValidateExact(exactKey, "frontier-mismatch");

        if (cache.TryBeginBaselineObservation(new R1FrontierShadowKey(9, 4, 2)))
            return false;
        if (cache.TryBeginBaselineObservation(new R1FrontierShadowKey(3, 5, 2)))
            return false;
        R1FrontierShadowKey nodeCountMiss = new(3, 4, 3);
        if (cache.TryBeginBaselineObservation(nodeCountMiss))
            return false;
        if (!cache.WantsSubsetObservation(new R1FrontierDepthKey(3, 4)))
            return false;
        cache.ObserveSubset(new R1FrontierDepthKey(3, 4), [a, b, c]);

        R1FrontierShadowSnapshot snapshot = cache.Capture();
        return snapshot.StoredSignatures == 3
            && snapshot.BaselineObservations == 5
            && snapshot.KeyMisses == 3
            && snapshot.TurnMisses == 1
            && snapshot.ActionCountMisses == 1
            && snapshot.NodeCountMisses == 1
            && snapshot.ValidatedHits == 1
            && snapshot.SignatureMismatches == 1
            && snapshot.SubsetObservations == 1
            && snapshot.ProbeSubsetHits == 1
            && snapshot.ExactSetHits == 0
            && snapshot.PartialOverlapHits == 0
            && snapshot.ZeroOverlapHits == 0
            && snapshot.MissSamples.Count == 3
            && snapshot.SubsetSamples.Count == 1
            && snapshot.SubsetSamples[0].ProbeNodes == 2
            && snapshot.SubsetSamples[0].BaselineNodes == 3
            && snapshot.SubsetSamples[0].IntersectionNodes == 2
            && snapshot.SubsetSamples[0].ProbeSubsetOfBaseline;
    }
}
