namespace CombatSolver;

internal sealed class SearchDiagnosticsSink(
    Action<string> info,
    Action<string> debug,
    SearchPathObserver? pathObserver = null)
{
    public SearchPathObserver? PathObserver { get; } = pathObserver;

    public void Info(string message) => info(message);

    public void Debug(string message) => debug(message);
}

// Observation is opt-in and never participates in candidate acceptance. Both delegates may
// run on expansion workers: an injected collector owns its synchronization and output limits.
internal sealed class SearchPathObserver(
    Func<StateFingerprint, bool> wantsState,
    Action<SearchPathObservation> observe,
    Func<StateFingerprint, bool>? wantsRetentionPool = null)
{
    public bool WantsState(StateFingerprint stateKey) => wantsState(stateKey);

    public bool ObservesRetentionPools => wantsRetentionPool != null;

    public bool WantsRetentionPool(StateFingerprint stateKey)
        => wantsRetentionPool?.Invoke(stateKey) == true;

    public void Observe(SearchPathObservation observation) => observe(observation);
}

internal enum SearchPathObservationStage
{
    Root,
    Generated,
    AdmissionTransposition,
    ExpansionTransposition,
    Expanded,
    ExpansionBlocked,
    ActionAdmitted,
    PruneInput,
    PruneFinal,
    TurnInput,
    TurnAnnotated,
    TurnDropped,
    RetentionPoolInput,
    GlobalRetention,
    RetentionPoolFinal,
    StandPatProbe,
    EndTurnChoiceReplay,
    CardChoiceContinuationReplay,
    PotionChoiceContinuationReplay,
    ExecutionChoiceContinuationReplay,
}

internal readonly record struct SearchPathPolicyLabel(
    int PotionCount,
    int PotionStrategicCost,
    int FutureSoldHp,
    int CumulativePlayerHpLost,
    int ActionCount,
    double Score);

internal readonly record struct SearchPathRoutingChoiceSignature(
    int Turn,
    string SourceId,
    PlanChoiceEffect Effect,
    string Pile,
    string CardId,
    int Upgrade,
    string CardStateKey,
    int Occurrence,
    string ContextId,
    int StateContext,
    StateFingerprint EnemyCombatDistributionKey,
    StateFingerprint EnemyControlDistributionKey,
    StateFingerprint UnorderedPileKey);

internal readonly record struct SearchPathEvaluationValues(
    int Energy,
    int Stars,
    int PlayerBlock,
    int ProjectedPlayerHp,
    int HandCount,
    int ReachableHandValue,
    int ZeroCostPlayableCount,
    int LiveDeckSize,
    int LiveDeckClutter,
    int PersistentBuffValue,
    int StrategicRetentionValue,
    int LatentSetupValue,
    int RetainedAttackValue,
    int ReplayPotentialValue,
    int FutureResourceValue,
    int DelayedDamageValue,
    int ReactiveDamageValue,
    int EnemyStrengthSuppression,
    int EnemyWeakTurns,
    int EnemyVulnerableTurns,
    int SandpitRemaining,
    int FocusTargetPressure,
    int ProjectedShuffleOrderValue,
    int LongTermResourceValue);

// All indexes, including RawRank, are zero-based; null means absent or not evaluated.
// ParentRetentionRank is the immediate parent's value, not a routing-family minimum.
// An option leader may still be excluded from routing or required by their original caps.
internal sealed record SearchPathRetentionDetails(
    int? PoolIndex = null,
    int? RawRank = null,
    int? RequiredIndex = null,
    int? RoutingIndex = null,
    int? SelectedIndex = null,
    double? BeamRankScore = null,
    int? OffensiveProgressValue = null,
    int? ParentRetentionRank = null,
    int? Limit = null,
    int? EffectiveLimit = null,
    int? RoutingQuota = null,
    int? RoutingLimit = null,
    int? RawCount = null,
    int? RequiredCount = null,
    int? RoutingCount = null,
    int? SelectedCount = null,
    SearchPathRoutingChoiceSignature? RoutingChoiceSignature = null,
    bool? IsRoutingOptionLeader = null,
    SearchPathEvaluationValues? Evaluation = null);

// Arrays/choices are detached value copies. This must not acquire a SearchNode, snapshot,
// simulator, model, ledger, lazy enumerable, or callback that retains one of those objects.
internal sealed record SearchPathObservation(
    Guid SolverId,
    int BeamWidth,
    SearchPathObservationStage Stage,
    string Reason,
    int BoundaryId,
    StateFingerprint StateKey,
    StateFingerprint? ParentStateKey,
    int Turn,
    int ActionCount,
    SearchPathPolicyLabel PolicyLabel,
    SearchPathPolicyLabel? ParentPolicyLabel,
    SearchRouteTraits Traits,
    TurnOutcome? Outcome,
    SearchBoundaryReason BoundaryReason,
    bool IsTerminal,
    bool HasPredictionRisk,
    int PlayerHp,
    int PlayerMaxHp,
    int EnemyHp,
    int ShufflesCrossed,
    int CumulativeEnemyHpLost,
    IReadOnlyList<PlanAction> Actions,
    IReadOnlyList<PlanCardChoice> RootTurnSetupChoices)
{
    public SearchPathRetentionDetails? Retention { get; init; }

    public int PotionCount => PolicyLabel.PotionCount;
    public int PotionStrategicCost => PolicyLabel.PotionStrategicCost;
    public int FutureSoldHp => PolicyLabel.FutureSoldHp;
    public int CumulativePlayerHpLost => PolicyLabel.CumulativePlayerHpLost;
    public double Score => PolicyLabel.Score;
}


internal readonly record struct R1EvaluationShadowSnapshot(
    int EntryLimit,
    int Entries,
    int StoreAttempts,
    int DuplicateStores,
    int StoreConflicts,
    int ValidationObservations,
    int StateKeyMisses,
    int FullKeyMisses,
    int ValidatedHits,
    int OutputMismatches,
    bool Capped);

internal sealed class R1EvaluationShadowCache(
    int entryLimit = 256,
    int validationObservationLimit = 4096)
{
    private readonly record struct Key(
        StateFingerprint StateKey,
        int Turn,
        int ActionCount,
        int PotionCount,
        int PotionStrategicCost,
        int FutureSoldHp,
        int CumulativePlayerHpLost,
        SearchRouteTraits Traits,
        SearchBoundaryReason BoundaryReason,
        StateFingerprint CombatProgressKey);

    private readonly record struct Value(
        double Score,
        bool IsTerminal,
        bool HasPredictionRisk,
        int PlayerHp,
        int PlayerMaxHp,
        int ProjectedPlayerHp,
        int PlayerBlock,
        int EnemyHp,
        int EnemyBlock,
        int RawEnemyHp,
        int AliveEnemyCount,
        int PersistentBuffValue,
        int StrategicRetentionValue,
        int LatentSetupValue,
        int FutureResourceValue,
        int RetainedAttackValue,
        int ReplayPotentialValue,
        int DelayedDamageValue,
        int ReactiveDamageValue,
        int EnemyStrengthSuppression,
        int EnemyWeakTurns,
        int EnemyVulnerableTurns,
        int SandpitRemaining,
        int LiveDeckClutter,
        int LiveDeckSize,
        int OutstandingStolenResource,
        int Energy,
        int Stars,
        int HandCount,
        int CumulativeEnemyHpLost);

    private readonly object _storeGate = new();
    private readonly Dictionary<Key, Value> _entries = [];
    private readonly HashSet<StateFingerprint> _stateKeys = [];
    private readonly int _entryLimit = entryLimit > 0
        ? entryLimit
        : throw new ArgumentOutOfRangeException(nameof(entryLimit));
    private readonly int _validationObservationLimit = validationObservationLimit > 0
        ? validationObservationLimit
        : throw new ArgumentOutOfRangeException(nameof(validationObservationLimit));
    private int _frozen;
    private int _storeAttempts;
    private int _duplicateStores;
    private int _storeConflicts;
    private int _validationObservations;
    private int _stateKeyMisses;
    private int _fullKeyMisses;
    private int _validatedHits;
    private int _outputMismatches;
    private int _capped;

    internal int EntryCount
    {
        get
        {
            lock (_storeGate)
                return _entries.Count;
        }
    }

    internal void ObserveRetained(SearchNode node, bool r1Probe)
    {
        if (r1Probe)
        {
            Store(node);
            return;
        }
        Validate(node);
    }

    internal void Freeze()
    {
        lock (_storeGate)
            Volatile.Write(ref _frozen, 1);
    }

    internal R1EvaluationShadowSnapshot Capture()
    {
        int entries;
        lock (_storeGate)
            entries = _entries.Count;
        return new R1EvaluationShadowSnapshot(
            _entryLimit,
            entries,
            Volatile.Read(ref _storeAttempts),
            Volatile.Read(ref _duplicateStores),
            Volatile.Read(ref _storeConflicts),
            Volatile.Read(ref _validationObservations),
            Volatile.Read(ref _stateKeyMisses),
            Volatile.Read(ref _fullKeyMisses),
            Volatile.Read(ref _validatedHits),
            Volatile.Read(ref _outputMismatches),
            Volatile.Read(ref _capped) != 0);
    }

    private void Store(SearchNode node)
    {
        if (Volatile.Read(ref _frozen) != 0 || Volatile.Read(ref _capped) != 0)
            return;

        Key key = CaptureKey(node);
        Value value = CaptureValue(node);
        lock (_storeGate)
        {
            if (_frozen != 0 || _capped != 0)
                return;
            _storeAttempts++;
            if (_entries.TryGetValue(key, out Value existing))
            {
                if (existing == value)
                    _duplicateStores++;
                else
                    _storeConflicts++;
                return;
            }
            if (_entries.Count >= _entryLimit)
            {
                _capped = 1;
                return;
            }
            _entries.Add(key, value);
            _stateKeys.Add(node.StateKey);
        }
    }

    private void Validate(SearchNode node)
    {
        if (Volatile.Read(ref _frozen) == 0)
            return;
        int observation = Interlocked.Increment(ref _validationObservations);
        if (observation > _validationObservationLimit)
            return;

        // After Freeze the dictionaries are immutable; concurrent reads from Beam workers are safe.
        if (!_stateKeys.Contains(node.StateKey))
        {
            Interlocked.Increment(ref _stateKeyMisses);
            return;
        }

        Key key = CaptureKey(node);
        if (!_entries.TryGetValue(key, out Value expected))
        {
            Interlocked.Increment(ref _fullKeyMisses);
            return;
        }

        Value actual = CaptureValue(node);
        if (expected == actual)
            Interlocked.Increment(ref _validatedHits);
        else
            Interlocked.Increment(ref _outputMismatches);
    }

    private static Key CaptureKey(SearchNode node)
        => new(
            node.StateKey,
            node.Turn,
            node.ActionCount,
            node.PotionCount,
            node.PotionStrategicCost,
            node.FutureSoldHp,
            node.Snapshot.CumulativePlayerHpLost,
            node.Traits,
            node.BoundaryReason,
            CaptureCombatProgressKey(node.CombatProgress));

    private static StateFingerprint CaptureCombatProgressKey(CombatProgressState progress)
    {
        StateFingerprintBuilder key = new();
        key.Add(progress.BestEnemyHp);
        key.Add(progress.BestEnemyDurability);
        EnemyDurabilityVector durability = progress.BestEnemyDurabilityByCombatId;
        key.Add(durability.Count);
        for (int index = 0; index < durability.Count; index++)
        {
            EnemyDurabilityEntry entry = durability[index];
            key.Add(entry.CombatId);
            key.Add(entry.Durability);
        }
        key.Add(progress.BestAliveEnemyCount);
        key.Add(progress.BestOffensiveProgressValue);
        key.Add(progress.BestPersistentBuffValue);
        key.Add(progress.BestStrategicRetentionValue);
        key.Add(progress.BestFutureResourceValue);
        key.Add(progress.BestDelayedDamageValue);
        key.Add(progress.BestReplayPotentialValue);
        key.Add(progress.BestRetainedAttackValue);
        key.Add(progress.BestPlayerMaxHp);
        key.Add(progress.BestLongTermResourceValue);
        key.Add(progress.LowestPlayerHp);
        key.Add(progress.BestPlayerHpRecovery);
        key.Add(progress.LowestProjectedPlayerHp);
        key.Add(progress.BestProjectedPlayerHpRecovery);
        key.Add(progress.BestEnemyStrengthSuppression);
        key.Add(progress.BestEnemyWeakTurns);
        key.Add(progress.BestEnemyVulnerableTurns);
        key.Add(progress.BestOstyHp);
        key.Add(progress.BestOstyMaxHp);
        key.Add(progress.BestLiveDeckClutter);
        key.Add(progress.BestLiveDeckSize);
        key.Add(progress.BestOutstandingStolenResource);
        key.Add(progress.BestSandpitRemaining);
        key.Add(progress.MostProcessedEnemyDeaths);
        key.Add(progress.TurnsWithoutProgress);
        return key.Finish();
    }

    private static Value CaptureValue(SearchNode node)
    {
        SimulationSnapshot snapshot = node.Snapshot;
        return new Value(
            node.Score,
            node.IsTerminal,
            node.HasPredictionRisk,
            snapshot.PlayerHp,
            snapshot.PlayerMaxHp,
            snapshot.ProjectedPlayerHp,
            snapshot.PlayerBlock,
            snapshot.EnemyHp,
            snapshot.EnemyBlock,
            snapshot.RawEnemyHp,
            snapshot.AliveEnemyCount,
            snapshot.PersistentBuffValue,
            snapshot.StrategicEffects.RetentionValue,
            snapshot.LatentSetupValue,
            snapshot.FutureResourceValue,
            snapshot.RetainedAttackValue,
            snapshot.ReplayPotentialValue,
            snapshot.DelayedDamageValue,
            snapshot.ReactiveDamageValue,
            snapshot.EnemyStrengthSuppression,
            snapshot.EnemyWeakTurns,
            snapshot.EnemyVulnerableTurns,
            snapshot.SandpitRemaining,
            snapshot.LiveDeckClutter,
            snapshot.LiveDeckSize,
            snapshot.OutstandingStolenResource,
            snapshot.Energy,
            snapshot.Stars,
            snapshot.HandCount,
            node.CumulativeEnemyHpLost);
    }
}
