using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal sealed record CompleteProjectionBaseline(
    int StartTurnNumber,
    int ProjectedBattleHpLost,
    string StateDifference);

internal sealed record ManualProjectionBaseline(
    int StartTurnNumber,
    int ProjectedBattleHpLost,
    string StateDifference,
    string? OriginalCheckpointId = null);

internal sealed record ManualProjectionComparison(
    int OriginalTurnNumber,
    int CurrentTurnNumber,
    int PreviousProjectedBattleHpLost,
    int CurrentProjectedBattleHpLost,
    string StateDifference,
    string? OriginalCheckpointId = null,
    string? CurrentCheckpointId = null)
{
    public int Difference => CurrentProjectedBattleHpLost - PreviousProjectedBattleHpLost;
}

internal readonly record struct SearchMemoryUsageSnapshot(
    long ProcessWorkingSetBytes,
    long PhysicalMemoryUsedBytes,
    long ConfiguredMemoryBudgetBytes,
    bool SearchActive,
    long SearchAllocatedBytes,
    long SearchAllocationLimitBytes,
    long ProjectedSystemMemoryLoadBytes,
    long SystemMemoryLimitBytes,
    bool Reclaiming,
    bool BackgroundReclaiming)
{
    public long PhysicalMemoryTotalBytes { get; init; }
    public bool HasPhysicalMemorySample => PhysicalMemoryTotalBytes > 0;
    public long? PhysicalMemoryAvailableBytes => HasPhysicalMemorySample
        ? Math.Clamp(PhysicalMemoryTotalBytes - PhysicalMemoryUsedBytes, 0, PhysicalMemoryTotalBytes)
        : null;
    public bool HasGcWall => SearchAllocationLimitBytes != long.MaxValue;
    public double AllocationPressureRatio => HasGcWall
        ? Math.Clamp(SearchAllocatedBytes / (double)Math.Max(1, SearchAllocationLimitBytes), 0d, 1d)
        : 0d;
    public double SystemPressureRatio => SystemMemoryLimitBytes != long.MaxValue
        ? Math.Clamp(
            Math.Max(PhysicalMemoryUsedBytes, ProjectedSystemMemoryLoadBytes) / (double)Math.Max(1, SystemMemoryLimitBytes),
            0d,
            1d)
        : 0d;
    public bool SystemPressureDominates => SystemPressureRatio > AllocationPressureRatio;
    public long SystemOccupiedBytes
        => Math.Clamp(
            PhysicalMemoryUsedBytes - ProcessWorkingSetBytes,
            0,
            Math.Max(0, PhysicalMemoryTotalBytes));
    public double CleanupPressureRatio => Math.Max(AllocationPressureRatio, SystemPressureRatio);
    public double SystemSegmentRatio
        => HasPhysicalMemorySample ? SystemOccupiedBytes / (double)PhysicalMemoryTotalBytes : 0d;
    public double ProcessSegmentRatio
        => HasPhysicalMemorySample ? Math.Min(
            1d - SystemSegmentRatio,
            Math.Clamp(
                Math.Min(ProcessWorkingSetBytes, PhysicalMemoryUsedBytes) / (double)PhysicalMemoryTotalBytes,
                0d,
                1d)) : 0d;
}

internal sealed class SolverCombatSession
{
    public CombatState? State { get; set; }
    public SolverResult? LatestResult { get; set; }
    public LiveCombatStamp? LatestStamp { get; set; }
    public SolverResult? ContinuationSource { get; set; }
    public SearchInteractionState? StoppedSearch { get; set; }
    public bool FullAutoEnabled { get; set; }
    public SolverTheftPolicy? TheftPolicy { get; set; }
    public CompleteProjectionBaseline? PendingCompleteProjectionBaseline { get; set; }
    public ManualProjectionBaseline? PendingManualProjectionBaseline { get; set; }
    public ManualProjectionComparison? LastManualProjectionComparison { get; set; }
    public bool ManualRouteImprovementDetected { get; set; }
    public bool AutomaticSearchPaused { get; set; }
    public int? AutomaticSearchPausedTurn { get; set; }
    public bool ManualSearchAfterTurnSetupRequested { get; set; }
    public int? DeployAfterTurnSetupTurn { get; set; }
    public bool MultiplayerSafeExecuteDeploymentRequested { get; set; }
    public bool MultiplayerSafeAutoEnabled { get; set; }
    public CombatState? TurnSetupResumeState { get; set; }
    public Dictionary<ReplanCause, int> ReplanCounts { get; } = [];
    public HashSet<SolverResult> ReviewedWorldlineResults { get; } = [];
    public int SearchesStarted { get; set; }
    public long ReviewedWorldlinesTotal { get; set; }
    public int ContinuationsReused { get; set; }
    public int RoutesRestored { get; set; }
    public IReadOnlyList<string> LastContinuationDifferences { get; set; } = [];
    public int? LastSolverDeployedTurn { get; set; }
    public bool ManualControlObserved { get; set; }
    public bool ShowcaseMode { get; set; }
    public int? LastSafeEndTurnRequestId { get; set; }
    public int? LastSafeEndTurnNumber { get; set; }
    public long? LastSafeEndTurnWorldVersion { get; set; }
    public bool AwaitingMultiplayerContinuation { get; set; }
    public bool PendingMultiplayerPlanRefresh { get; set; }
    public long? LastPlanRefreshDecisionTimestampMilliseconds { get; set; }
    public long LatestRouteInvalidationVersion { get; set; }
    public CombatBugReportIssueLedger BugReportIssues { get; } = new();
    public CombatTransitionMemo TransitionMemo { get; } = new();
}

internal sealed class SolverSearchSession(
    int generation,
    CombatState state,
    LiveCombatStamp stamp,
    bool deployWhenReady)
{
    private static readonly double[] FrameBucketUpperBounds =
        [16.7d, 25d, 33d, 50d, 100d, double.MaxValue];
    private readonly int[] _frameBuckets = new int[FrameBucketUpperBounds.Length];
    private readonly object _workerGate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _workerStartup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _callbackCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _workerCompletion = Task.CompletedTask;
    private bool _workerStarted;
    private bool _startupClosed;
    private int _referenceReleaseState;
    private int _cancellationDisposeState;
    private static int _disposedCancellationCount;

    public int Generation { get; } = generation;
    public int StartTurnNumber { get; init; }
    public ReplanCause ReplanCause { get; init; }
    public long WorldVersion { get; init; }
    public long RouteInvalidationVersion { get; init; }
    public LiveCombatStamp? LocalCoreSearchStamp { get; init; }
    public bool UseRouteScopedCompletion { get; set; }
    public CombatState State { get; } = state;
    public LiveCombatStamp Stamp { get; } = stamp;
    public CancellationToken Token => _cancellation.Token;
    public bool IsWorkerCompleted => _workerStartup.Task.IsCompleted && Volatile.Read(ref _workerCompletion).IsCompleted;
    public static int CancellationDisposeCountForTesting => Volatile.Read(ref _disposedCancellationCount);
    public bool DeployWhenReady { get; set; } = deployWhenReady;
    public int ForegroundPublishedCandidateVersion { get; set; } = -1;
    public int MaxDegreeOfParallelism { get; set; } = 1;
    public SearchMemoryPressureSignal? MemoryPressureSignal { get; set; }
    public SearchInteractionState Interaction { get; } = new();
    public int FrameCount { get; private set; }
    public int FramesOver33Milliseconds { get; private set; }
    public int FramesOver50Milliseconds { get; private set; }
    public int FramesOver100Milliseconds { get; private set; }
    public double MaxFrameGapMilliseconds { get; private set; }

    public long ProcessAllocatedBytesAtStart { get; } = GC.GetTotalAllocatedBytes(precise: false);
    public TimeSpan ProcessGcPauseAtStart { get; } = GC.GetTotalPauseDuration();

    // Search owns two quiescence boundaries: the worker and its main-thread callback.
    // Dispatcher/activity selection stays in the caller's registration adapter.
    public bool TryStartWorker(
        Func<Task<SolverResult>> start, Action<Task<SolverResult>> registerCompletion)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(registerCompletion);
        lock (_workerGate)
        {
            if (_workerStarted)
                throw new InvalidOperationException("The search session already owns a worker.");
            if (_startupClosed || _cancellation.IsCancellationRequested)
            {
                _startupClosed = true;
                _callbackCompletion.TrySetResult();
                _workerStartup.TrySetResult();
                return false;
            }
            _workerStarted = true;
        }
        bool callbackScheduled = false;
        try
        {
            Task<SolverResult> worker = start();
            ArgumentNullException.ThrowIfNull(worker);
            Volatile.Write(ref _workerCompletion, worker);
            registerCompletion(worker);
            callbackScheduled = true;
            return true;
        }
        finally
        {
            if (!callbackScheduled)
                _callbackCompletion.TrySetResult(); // No callback exists after setup failure.
            _workerStartup.TrySetResult();
            DisposeCancellationOnce();
        }
    }

    public void Cancel()
    {
        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) when (Volatile.Read(ref _cancellationDisposeState) != 0) { }
        lock (_workerGate)
        {
            if (_workerStarted)
                return; // Startup/registration or the callback still owns completion.
            _startupClosed = true;
            _callbackCompletion.TrySetResult();
            _workerStartup.TrySetResult();
        }
    }

    public void CompleteCallback()
    {
        _callbackCompletion.TrySetResult();
        DisposeCancellationOnce();
    }

    public async Task ReleaseReferencesAsync()
    {
        try
        {
            await _workerStartup.Task.ConfigureAwait(false);
            await Volatile.Read(ref _workerCompletion).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await _callbackCompletion.Task.ConfigureAwait(false);
            Volatile.Write(ref _workerCompletion, Task.CompletedTask);
        }
        finally { DisposeCancellationOnce(); }
    }

    public bool TryBeginReferenceRelease()
        => Interlocked.Exchange(ref _referenceReleaseState, 1) == 0;

    private void DisposeCancellationOnce()
    {
        if (!_workerStartup.Task.IsCompleted || !Volatile.Read(ref _workerCompletion).IsCompleted
            || !_callbackCompletion.Task.IsCompleted
            || Interlocked.Exchange(ref _cancellationDisposeState, 1) != 0)
            return;
        _cancellation.Dispose();
        Interlocked.Increment(ref _disposedCancellationCount);
    }

    public void ObserveFrame(double milliseconds)
    {
        FrameCount++;
        if (milliseconds > MaxFrameGapMilliseconds)
            MaxFrameGapMilliseconds = milliseconds;
        if (milliseconds >= 33d)
            FramesOver33Milliseconds++;
        if (milliseconds >= 50d)
            FramesOver50Milliseconds++;
        if (milliseconds >= 100d)
            FramesOver100Milliseconds++;
        for (int index = 0; index < FrameBucketUpperBounds.Length; index++)
        {
            if (milliseconds > FrameBucketUpperBounds[index])
                continue;
            _frameBuckets[index]++;
            break;
        }
    }

    public double FramePercentile(double percentile)
    {
        if (FrameCount == 0)
            return 0d;
        int rank = Math.Max(1, (int)Math.Ceiling(FrameCount * percentile));
        int cumulative = 0;
        for (int index = 0; index < _frameBuckets.Length; index++)
        {
            cumulative += _frameBuckets[index];
            if (cumulative >= rank)
            {
                return index == FrameBucketUpperBounds.Length - 1
                    ? MaxFrameGapMilliseconds
                    : FrameBucketUpperBounds[index];
            }
        }
        return MaxFrameGapMilliseconds;
    }
}

// Owns one deferred entry's complete intent and its barrier/queued-callback lifetime.
// Controller still owns exact-current-request, combat epoch/state and live authorization.
internal sealed class SolverDeferredSearchRequest(
    NGame host, CombatState state, SearchReason reason, bool deployWhenReady,
    SolverResult? adoptedRouteReplay, int lifecycleGeneration)
{
    private readonly CancellationTokenSource _cancellation = new();
    private int _cancelled;
    private int _started;
    private int _disposed;

    public NGame Host { get; } = host;
    public CombatState State { get; } = state;
    public SearchReason Reason { get; } = reason;
    public bool DeployWhenReady { get; } = deployWhenReady;
    public SolverResult? AdoptedRouteReplay { get; } = adoptedRouteReplay;
    public int LifecycleGeneration { get; } = lifecycleGeneration;
    public bool IsCancellationRequested => Volatile.Read(ref _cancelled) != 0;

    public void Cancel()
    {
        Interlocked.Exchange(ref _cancelled, 1);
        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0) { }
    }

    public async Task WaitAndDispatchAsync(Task barrier, Action<Action> post, Action resume)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The deferred search request already owns an operation.");
        CancellationToken token = _cancellation.Token;
        try
        {
            await barrier.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            TaskCompletionSource callbackCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            post(() =>
            {
                try { resume(); }
                finally { callbackCompleted.TrySetResult(); }
            });
            // Canceling a queued request cannot release the old combat references
            // before its queued callback has actually run and rejected/resumed it.
            await callbackCompleted.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            Interlocked.Exchange(ref _disposed, 1);
            _cancellation.Dispose();
        }
    }
}

internal sealed class SolverDeploymentSession
{
    private readonly object _operationGate = new();
    private readonly TaskCompletionSource _operationAttachment = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _operation = Task.CompletedTask;
    private bool _operationAttached;
    private bool _rejectedBeforeStart;
    private int _cancellationDisposeState;
    private readonly CancellationTokenSource _cancellation = new();

    // The structural plan is one immutable snapshot for capacity, UI and execution.
    // It grants no live authority: the deployment loop still revalidates every action.
    public SolverDeploymentSession(
        IReadOnlyList<PlanAction> routeActions, int startTurnNumber, bool safeExecute,
        int routeGeneration, long worldVersion)
    {
        StartTurnNumber = startTurnNumber;
        RouteGeneration = routeGeneration;
        WorldVersion = worldVersion;
        PlanAction[] planned = routeActions.Where(action =>
            MultiplayerLocalCrossTurnContracts.IsCurrentTurnAction(action.Turn, startTurnNumber)).ToArray();
        PlannedActions = Array.AsReadOnly(planned);
        if (safeExecute)
        {
            IReadOnlyList<PlanAction> safe = MultiplayerSafeLocalActionClassifier.TakeBoundedDeploymentSlice(
                planned, out SafeLocalActionDecision stop);
            Actions = Array.AsReadOnly(safe.ToArray());
            StructuralStop = stop;
            PlannedEndTurn = safe.Count < planned.Length && planned[safe.Count].Kind == PlanActionKind.EndTurn
                ? planned[safe.Count] : null;
            RejectedBeforeStart = safe.Count == 0 && planned.Length > 0 && PlannedEndTurn == null;
            SafeExecutionSession = new(startTurnNumber, routeGeneration, worldVersion, safe.Count);
        }
        else
        {
            PlanAction[] beforeForecast = planned.TakeWhile(action => !action.IsForecastOnlyObservation).ToArray();
            Actions = Array.AsReadOnly(beforeForecast.Where(action => action.IsExecutable).ToArray());
            PlannedEndTurn = beforeForecast.FirstOrDefault(action => action.Kind == PlanActionKind.EndTurn);
            StructuralStop = SafeLocalActionDecision.Allow;
        }
    }

    public SolverDeploymentSession() : this([], 0, false, 0, 0) { }

    public CombatState? State { get; init; }
    public int StartTurnNumber { get; }
    public long WorldVersion { get; }
    public int RouteGeneration { get; }
    public int CombatLifecycleGeneration { get; init; }
    public MultiplayerSafeExecutionSession? SafeExecutionSession { get; }
    public IReadOnlyList<PlanAction> PlannedActions { get; }
    public IReadOnlyList<PlanAction> Actions { get; }
    public PlanAction? PlannedEndTurn { get; }
    public SafeLocalActionDecision StructuralStop { get; }
    public bool RejectedBeforeStart { get; }
    public CancellationToken Token => _cancellation.Token;
    public int ReferenceReleaseState;

    public Task StartOperation(Func<Task> start)
    {
        ArgumentNullException.ThrowIfNull(start);
        lock (_operationGate)
        {
            if (_operationAttached || _rejectedBeforeStart)
                throw new InvalidOperationException("The deployment already owns an operation or was rejected before start.");
            _operationAttached = true;
        }
        try
        {
            // An async body's synchronous prefix may re-enter cancellation. Reserve
            // ownership first, then publish its returned task before draining can finish.
            Task operation = start();
            ArgumentNullException.ThrowIfNull(operation);
            Volatile.Write(ref _operation, operation);
            return operation;
        }
        finally
        {
            // A synchronous setup failure created no returned operation; its caller
            // still receives the exception, and queued cancellation must not hang.
            _operationAttachment.TrySetResult();
        }
    }

    public void Cancel() => _cancellation.Cancel();

    public async Task ReleaseOperationReferencesAsync()
    {
        await _operationAttachment.Task.ConfigureAwait(false);
        await Volatile.Read(ref _operation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Volatile.Write(ref _operation, Task.CompletedTask);
    }

    public bool DisposeRejectedBeforeStart()
    {
        lock (_operationGate)
        {
            if (!RejectedBeforeStart || _operationAttached)
                throw new InvalidOperationException("Only a structurally rejected deployment with no operation may finish before start.");
            _rejectedBeforeStart = true;
        }
        _operationAttachment.TrySetResult();
        return DisposeCancellationOnce();
    }

    public bool DisposeCancellationOnce()
    {
        if (Interlocked.Exchange(ref _cancellationDisposeState, 1) != 0)
            return false;
        _cancellation.Dispose();
        return true;
    }
}
