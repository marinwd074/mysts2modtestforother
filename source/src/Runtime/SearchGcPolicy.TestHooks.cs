using System.Runtime;

namespace CombatSolver;

// Test observability and deterministic pause/failure injection for GC policy contracts.
// Production GC lifecycle code remains in SearchGcPolicy.cs; this partial keeps test-only
// surface area out of the main runtime source context without changing behavior.
internal static partial class SearchGcPolicy
{
    private static int _generation2CoveragePauseStageForTesting;
    private static TaskCompletionSource? _generation2CoverageReachedForTesting;
    private static TaskCompletionSource? _generation2CoverageResumeForTesting;
    private static bool _inSearchCheckpointPauseRequestedForTesting;
    private static TaskCompletionSource? _inSearchCheckpointReachedForTesting;
    private static TaskCompletionSource? _inSearchCheckpointResumeForTesting;
    private static bool _inSearchCollectionPauseRequestedForTesting;
    private static bool _inSearchCollectionTimeoutOnResumeForTesting;
    private static TaskCompletionSource? _inSearchCollectionReachedForTesting;
    private static TaskCompletionSource? _inSearchCollectionResumeForTesting;
    private static int _inSearchBackgroundGen2CompletedCountForTesting;
    private static int _inSearchBackgroundGen2TimeoutDrainCountForTesting;
    private static bool _failNextInSearchCheckpointAfterTransitionForTesting;
    private static bool _failNextRegionExitAfterTransitionForTesting;
    private static int _rolloverCountForTesting;
    private static int _budgetChangeRebuildCountForTesting;
    private static int _budgetChangeWaitCountForTesting;
    private static long _lastEstablishedNoGcRegionBudgetBytesForTesting;
    private static int _backgroundReclaimStartedCountForTesting;
    private static int _backgroundGen2CompletedCountForTesting;
    private static int _backgroundReclaimJoinCountForTesting;
    private static int _noGcRegionExitWithoutCollectionCountForTesting;
    private static long _lastBackgroundReclaimManagedLiveBeforeForTesting;
    private static long _lastBackgroundReclaimManagedLiveAfterForTesting;

    internal static int RolloverCountForTesting
    {
        get
        {
            lock (Gate)
                return _rolloverCountForTesting;
        }
    }

    internal static int BudgetChangeRebuildCountForTesting
    {
        get
        {
            lock (Gate)
                return _budgetChangeRebuildCountForTesting;
        }
    }

    internal static int BudgetChangeWaitCountForTesting
    {
        get
        {
            lock (Gate)
                return _budgetChangeWaitCountForTesting;
        }
    }

    internal static long CurrentNoGcRegionBudgetBytesForTesting
    {
        get
        {
            lock (Gate)
                return _noGcRegionBudgetBytes;
        }
    }

    internal static long LastEstablishedNoGcRegionBudgetBytesForTesting
    {
        get
        {
            lock (Gate)
                return _lastEstablishedNoGcRegionBudgetBytesForTesting;
        }
    }

    internal static int BackgroundReclaimStartedCountForTesting
    {
        get
        {
            lock (Gate)
                return _backgroundReclaimStartedCountForTesting;
        }
    }

    internal static int BackgroundGen2CompletedCountForTesting
    {
        get
        {
            lock (Gate)
                return _backgroundGen2CompletedCountForTesting;
        }
    }

    internal static int BackgroundReclaimJoinCountForTesting
    {
        get
        {
            lock (Gate)
                return _backgroundReclaimJoinCountForTesting;
        }
    }

    internal static int NoGcRegionExitWithoutCollectionCountForTesting
    {
        get
        {
            lock (Gate)
                return _noGcRegionExitWithoutCollectionCountForTesting;
        }
    }

    internal static long LastBackgroundReclaimManagedLiveBeforeForTesting
    {
        get
        {
            lock (Gate)
                return _lastBackgroundReclaimManagedLiveBeforeForTesting;
        }
    }

    internal static long LastBackgroundReclaimManagedLiveAfterForTesting
    {
        get
        {
            lock (Gate)
                return _lastBackgroundReclaimManagedLiveAfterForTesting;
        }
    }

    internal static long ReferenceReleaseEpochForTesting
    {
        get
        {
            lock (Gate)
                return _referenceReleaseEpoch;
        }
    }

    internal static (bool ConfirmationPending, int Completed, int TimeoutDrains)
        InSearchBackgroundCollectionForTesting
    {
        get
        {
            lock (Gate)
                return (_activeSearches > 0 && _reclaimActive
                    && _activeGeneration2CollectionStarted,
                    _inSearchBackgroundGen2CompletedCountForTesting,
                    _inSearchBackgroundGen2TimeoutDrainCountForTesting);
        }
    }

    internal static void ResetCountersForTesting()
    {
        lock (Gate)
        {
            _rolloverCountForTesting = 0;
            _budgetChangeRebuildCountForTesting = 0;
            _budgetChangeWaitCountForTesting = 0;
            _lastEstablishedNoGcRegionBudgetBytesForTesting = 0;
            _backgroundReclaimStartedCountForTesting = 0;
            _backgroundGen2CompletedCountForTesting = 0;
            _backgroundReclaimJoinCountForTesting = 0;
            _noGcRegionExitWithoutCollectionCountForTesting = 0;
            _lastBackgroundReclaimManagedLiveBeforeForTesting = 0;
            _lastBackgroundReclaimManagedLiveAfterForTesting = 0;
            _failNextInSearchCheckpointAfterTransitionForTesting = false;
            _failNextRegionExitAfterTransitionForTesting = false;
            _inSearchBackgroundGen2CompletedCountForTesting = 0;
            _inSearchBackgroundGen2TimeoutDrainCountForTesting = 0;
        }
    }

    internal static (Task Reclaim, Task CoverageBoundaryReached)
        RequestNoGcExhaustionReclaimForTesting(bool pauseAfterCoverageCapture)
    {
        if (!UnattendedTestRunner.IsActive)
        {
            throw new InvalidOperationException(
                "No-GC exhaustion 回收入口只能在无人测试中使用。");
        }
        lock (Gate)
        {
            if (_activeSearches != 0
                || _reclaimActive
                || _reclaimRequested
                || _deferredReclaimRequested)
                throw new InvalidOperationException("No-GC exhaustion 测试要求 GC policy 已静止。");
            if (_generation2CoveragePauseStageForTesting != 0)
                throw new InvalidOperationException("No-GC exhaustion 覆盖边界测试已经在运行。");
            _generation2CoveragePauseStageForTesting = pauseAfterCoverageCapture ? 2 : 1;
            _generation2CoverageReachedForTesting = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _generation2CoverageResumeForTesting = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            RequireCollectionAfterNextReferenceReleaseLocked();
            _reclaimRequired = true;
            Task reclaim = RequestReclaimLocked("unattended_no_gc_region_exhaustion");
            return (reclaim, _generation2CoverageReachedForTesting.Task);
        }
    }

    internal static void ResumeGeneration2CoverageForTesting()
    {
        TaskCompletionSource? resume;
        lock (Gate)
            resume = _generation2CoverageResumeForTesting;
        resume?.TrySetResult();
    }

    internal static Task PauseNextInSearchCheckpointForTesting()
    {
        if (!UnattendedTestRunner.IsActive)
        {
            throw new InvalidOperationException(
                "搜索内 GC checkpoint 暂停入口只能在无人测试中使用。");
        }
        lock (Gate)
        {
            if (_inSearchCheckpointPauseRequestedForTesting
                || _inSearchCheckpointReachedForTesting != null
                || _inSearchCheckpointResumeForTesting != null)
            {
                throw new InvalidOperationException("搜索内 GC checkpoint 暂停测试已经在运行。");
            }
            _inSearchCheckpointPauseRequestedForTesting = true;
            _inSearchCheckpointReachedForTesting = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _inSearchCheckpointResumeForTesting = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return _inSearchCheckpointReachedForTesting.Task;
        }
    }

    internal static void ResumeInSearchCheckpointForTesting()
    {
        TaskCompletionSource? reached = null;
        TaskCompletionSource? resume;
        lock (Gate)
        {
            resume = _inSearchCheckpointResumeForTesting;
            if (_inSearchCheckpointPauseRequestedForTesting)
            {
                // A setup/cancellation failure may dispose the test before the checkpoint ever
                // consumes the hook. Disarm it so a later unrelated checkpoint cannot inherit it.
                _inSearchCheckpointPauseRequestedForTesting = false;
                reached = _inSearchCheckpointReachedForTesting;
                _inSearchCheckpointReachedForTesting = null;
                _inSearchCheckpointResumeForTesting = null;
            }
        }
        resume?.TrySetResult();
        reached?.TrySetCanceled();
    }

    internal static Task PauseNextInSearchCollectionForTesting(bool timeoutOnResume = false)
    {
        if (!UnattendedTestRunner.IsActive)
            throw new InvalidOperationException("搜索内 Gen2 确认暂停只能在无人测试中使用。");
        lock (Gate)
        {
            if (_inSearchCollectionReachedForTesting != null)
                throw new InvalidOperationException("搜索内 Gen2 确认暂停测试已经在运行。");
            _inSearchCollectionPauseRequestedForTesting = true;
            _inSearchCollectionTimeoutOnResumeForTesting = timeoutOnResume;
            _inSearchCollectionReachedForTesting = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _inSearchCollectionResumeForTesting = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return _inSearchCollectionReachedForTesting.Task;
        }
    }

    internal static void ResumeInSearchCollectionForTesting()
    {
        TaskCompletionSource? reached = null;
        TaskCompletionSource? resume;
        lock (Gate)
        {
            resume = _inSearchCollectionResumeForTesting;
            if (_inSearchCollectionPauseRequestedForTesting)
            {
                _inSearchCollectionPauseRequestedForTesting = false;
                _inSearchCollectionTimeoutOnResumeForTesting = false;
                reached = _inSearchCollectionReachedForTesting;
                _inSearchCollectionReachedForTesting = null;
                _inSearchCollectionResumeForTesting = null;
            }
        }
        resume?.TrySetResult();
        reached?.TrySetCanceled();
    }

    private static async Task<bool> PauseInSearchCollectionForTestingAsync()
    {
        Task resume;
        bool timeoutOnResume;
        lock (Gate)
        {
            if (!_inSearchCollectionPauseRequestedForTesting)
                return false;
            _inSearchCollectionPauseRequestedForTesting = false;
            timeoutOnResume = _inSearchCollectionTimeoutOnResumeForTesting;
            _inSearchCollectionTimeoutOnResumeForTesting = false;
            resume = (_inSearchCollectionResumeForTesting
                ?? throw new InvalidOperationException("搜索内 Gen2 测试缺少恢复信号。")).Task;
            (_inSearchCollectionReachedForTesting
                ?? throw new InvalidOperationException("搜索内 Gen2 测试缺少到达信号。"))
                .TrySetResult();
        }
        try
        {
            await resume.ConfigureAwait(false);
            return timeoutOnResume;
        }
        finally
        {
            lock (Gate)
            {
                _inSearchCollectionReachedForTesting = null;
                _inSearchCollectionResumeForTesting = null;
            }
        }
    }

    internal static void FailNextRegionExitAfterTransitionForTesting()
    {
        if (!UnattendedTestRunner.IsActive)
        {
            throw new InvalidOperationException(
                "NoGC region-exit 失败注入只能在无人测试中使用。");
        }
        lock (Gate)
        {
            if (_failNextRegionExitAfterTransitionForTesting)
                throw new InvalidOperationException("NoGC region-exit 失败注入已经登记。");
            _failNextRegionExitAfterTransitionForTesting = true;
        }
    }

    internal static void FailNextInSearchCheckpointAfterTransitionForTesting()
    {
        if (!UnattendedTestRunner.IsActive)
        {
            throw new InvalidOperationException(
                "搜索内 GC checkpoint 失败注入只能在无人测试中使用。");
        }
        lock (Gate)
        {
            if (_failNextInSearchCheckpointAfterTransitionForTesting)
                throw new InvalidOperationException("搜索内 GC checkpoint 失败注入已经登记。");
            _failNextInSearchCheckpointAfterTransitionForTesting = true;
        }
    }

    private static void ThrowInjectedInSearchCheckpointFailureForTesting()
    {
        lock (Gate)
        {
            if (!_failNextInSearchCheckpointAfterTransitionForTesting)
                return;
            _failNextInSearchCheckpointAfterTransitionForTesting = false;
        }
        throw new InvalidOperationException("无人测试注入的搜索内 GC checkpoint 失败。");
    }

    private static void ThrowInjectedRegionExitFailureForTesting()
    {
        lock (Gate)
        {
            if (!_failNextRegionExitAfterTransitionForTesting)
                return;
            _failNextRegionExitAfterTransitionForTesting = false;
        }
        throw new InvalidOperationException("无人测试注入的 NoGC region-exit 完成失败。");
    }

    private static void PauseInSearchCheckpointForTesting()
    {
        TaskCompletionSource? reached;
        Task? resume;
        lock (Gate)
        {
            if (!_inSearchCheckpointPauseRequestedForTesting)
                return;
            _inSearchCheckpointPauseRequestedForTesting = false;
            reached = _inSearchCheckpointReachedForTesting
                ?? throw new InvalidOperationException("搜索内 GC checkpoint 暂停测试缺少到达信号。");
            resume = (_inSearchCheckpointResumeForTesting
                ?? throw new InvalidOperationException("搜索内 GC checkpoint 暂停测试缺少恢复信号。"))
                .Task;
            reached.TrySetResult();
        }
        try
        {
            resume.GetAwaiter().GetResult();
        }
        finally
        {
            lock (Gate)
            {
                _inSearchCheckpointReachedForTesting = null;
                _inSearchCheckpointResumeForTesting = null;
            }
        }
    }

    private static Task PauseGeneration2CoverageForTestingAsync(
        bool afterCoverageCapture)
    {
        lock (Gate)
        {
            int expectedStage = afterCoverageCapture ? 2 : 1;
            if (_generation2CoveragePauseStageForTesting != expectedStage)
                return Task.CompletedTask;
            TaskCompletionSource reached = _generation2CoverageReachedForTesting
                ?? throw new InvalidOperationException("Gen2 覆盖边界测试缺少到达信号。");
            TaskCompletionSource resume = _generation2CoverageResumeForTesting
                ?? throw new InvalidOperationException("Gen2 覆盖边界测试缺少恢复信号。");
            reached.TrySetResult();
            return resume.Task;
        }
    }

    internal static async Task<string> CollectAutomaticReclaimForTesting()
        => (await CollectGeneration2ForAutomaticReclaimAsync()).Kind;

    private static BackgroundGen2Completion CollectGeneration2ForManualMemoryRelease()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        CollectGeneration2(blocking: true, compacting: true);
        return new BackgroundGen2Completion(
            "full_blocking_compacting",
            GC.GetGCMemoryInfo(GCKind.FullBlocking).Index,
            Requests: 1);
    }
}
