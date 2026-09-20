using System.Diagnostics;
using System.Runtime;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace CombatSolver;

internal enum SearchReason
{
    Manual,
    AutoTurnStart,
    Deploy,
    FullAuto,
    DeploymentDrift,
    PlanExhausted,
}

internal enum ReplanCause
{
    InitialSearch,
    StateMismatch,
    ManualDivergence,
    ContinuationMissing,
    DeploymentDrift,
    PlanExhausted,
    ExplicitRequest,
}

internal static partial class SolverController
{
    private static SolverCombatSession _combat = new();
    private static SolverSearchSession? _search;
    private static SolverDeploymentSession? _deployment;
    private static CombatBugReportClassificationSnapshot? _lastBugReportClassification;
    private static ManualProjectionComparison? _lastManualProjectionComparison;
    private static int _nextSearchGeneration;
    private static int _combatLifecycleGeneration;
    private static bool _solverDisabled;
    private static bool _stopFullAutoOnCombatEnd;
    private static bool _stopFullAutoOnDeathTurn = true;
    private static bool _stopFullAutoOnWorseRecalculation = true;
    private static readonly SearchFramePressureSignal FramePressureSignal = new();
    private static readonly HashSet<string> DeployedCardIdsForTesting = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> DeployedPotionIdsForTesting = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<Task> PendingSearchReferenceReleases = [];
    private static readonly List<Task> PendingDeploymentReferenceReleases = [];
    private static readonly List<Task> PendingDeferredSearchReleases = [];
    private static readonly List<Task> PendingCombatDeferredOperations = [];
    private static int _searchReferenceReleaseScheduledCountForTesting;
    private static int _searchReferenceReleaseCompletedCountForTesting;
    private static int _searchCtsDisposeCountForTesting;
    private static int _deploymentReferenceReleaseScheduledCountForTesting;
    private static int _deploymentReferenceReleaseCompletedCountForTesting;
    private static int _deploymentCtsDisposeCountForTesting;
    private static CancellationTokenSource? _deferredSearchCts;
    private static int _deferredSearchId;
    private static CancellationTokenSource _combatDeferredOperationCancellation = new();
    private static CancellationTokenSource? _multiplayerDebounceCts;
    private static int _multiplayerDebounceId;
    private static bool _multiplayerInertSessionObserved;

    public static bool IsSearching
        => _search != null
           || _deferredSearchCts != null
           || PlayerTurnSetupCoordinator.IsSearching;
    public static bool IsDeploying => _deployment != null;
    public static bool IsStoppingSearch
        => _search?.Interaction.StopRequested == true || PlayerTurnSetupCoordinator.IsStoppingSearch;
    public static bool SolverDisabled => _solverDisabled;
    public static bool CanApplyCurrentTurn
        => CurrentSessionCapabilities.CanDeploySimpleLocalActions
           && (_search is { } search
               && search.Interaction.CurrentTakeoverRequest == null
               && search.Interaction.CanAcceptTakeover
               && Volatile.Read(ref search.Interaction.Progress)?.CurrentTurnPreview != null
           || PlayerTurnSetupCoordinator.CanApplyCurrentTurn);
    public static bool IsApplyingCurrentTurn
        => _search?.Interaction.IsApplyingCurrentTurn == true
           || PlayerTurnSetupCoordinator.IsApplyingCurrentTurn;
    public static bool CanAdoptCurrentRoute
        => CurrentSessionCapabilities.CanDeploySimpleLocalActions
           && (_search is { } search
               && search.Interaction.CurrentTakeoverRequest == null
               && search.Interaction.RenderedRouteAdoptionSeed != null
               && search.Interaction.CanAcceptTakeover
           || HasCurrentStoppedRoute()
           || PlayerTurnSetupCoordinator.CanAdoptCurrentRoute);
    public static bool IsAdoptingCurrentRoute
        => _search?.Interaction.IsAdoptingRoute == true
           || PlayerTurnSetupCoordinator.IsAdoptingCurrentRoute;


    internal static void InvalidateRenderedRouteAdoptionSeed()
    {
        if (_search != null)
            _search.Interaction.RenderedRouteAdoptionSeed = null;
        PlayerTurnSetupCoordinator.InvalidateRenderedRouteAdoptionSeed();
    }

    public static bool CanExecuteCurrentTurn
    {
        get
        {
            CombatState? state = CombatManager.Instance.DebugOnlyGetState();
            if (state == null || !CombatManager.Instance.IsInProgress)
                return false;
            if (!SolverSessionCapabilities.Capture(state).CanDeploySimpleLocalActions)
                return false;
            return _combat.LatestResult != null
                    && _combat.LatestStamp == LiveCombatStamp.Capture(state)
                || PlayerTurnSetupCoordinator.CanTakeOverTurnSetup(state);
        }
    }

    /// <summary>
    /// True whenever the current session is classified as multiplayer, including a
    /// network transition or a state with more than one player. The solver must stay
    /// inert for uploads and single-player-only control surfaces in these sessions.
    /// </summary>
    public static bool IsMultiplayerSession
        => SolverSessionCapabilities.Capture(CombatManager.Instance.DebugOnlyGetState()).IsMultiplayer;

    internal static SolverSessionCapabilitySet CurrentSessionCapabilities
        => SolverSessionCapabilities.Capture(CombatManager.Instance.DebugOnlyGetState());

    public static bool FullAutoEnabled => _combat.FullAutoEnabled;
    public static bool AutomaticSearchPaused => _combat.AutomaticSearchPaused;
    public static bool AutomaticCalculationEnabled => SolverSettings.Current.AutomaticCalculationEnabled;
    public static bool HasCalculatedThisCombat => _combat.SearchesStarted > 0 || _combat.LatestResult != null;
    public static bool StopFullAutoOnCombatEnd => _stopFullAutoOnCombatEnd;
    public static bool StopFullAutoOnDeathTurn => _stopFullAutoOnDeathTurn;
    public static bool StopFullAutoOnWorseRecalculation => _stopFullAutoOnWorseRecalculation;
    public static SolverTheftPolicy? TheftPolicy => _combat.TheftPolicy;
    internal static long ReviewedWorldlinesTotal => _combat.ReviewedWorldlinesTotal;
    internal static SolverResult? CurrentResultForBugReport => _combat.LatestResult ?? _combat.ContinuationSource;
    internal static string ReplanAuditForBugReport => DescribeReplanAudit();
    internal static string BuildBugReportDescription(string playerDescription)
        => CombatBugReportDescription.AppendAutomaticClassification(
            playerDescription,
            CombatManager.Instance.IsInProgress || _lastBugReportClassification == null
                ? CaptureBugReportClassification()
                : _lastBugReportClassification);
    internal static int UnexpectedReplanCount
        => _combat.ReplanCounts.GetValueOrDefault(ReplanCause.StateMismatch)
           + _combat.ReplanCounts.GetValueOrDefault(ReplanCause.DeploymentDrift);
    internal static string ControlModeForBugReport
        => _combat.ManualControlObserved
            ? "manual_plus_solver"
            : "solver_only";
    internal static int? LastSolverDeployedTurnForBugReport => _combat.LastSolverDeployedTurn;
    internal static SolverResult? LastCompletedResultForTesting { get; private set; }
    internal static bool HasActiveSearchSessionForTesting => _search != null;
    internal static SolverResult? LastTurnSetupResultForTesting { get; private set; }
    internal static Exception? LastSearchFailureForTesting { get; private set; }
    internal static bool LastFullAutoStoppedForWorseRecalculationForTesting { get; private set; }
    internal static bool LastFullAutoStoppedAtLiveRiskForTesting { get; private set; }
    internal static int? LastReusedTurnForTesting { get; private set; }
    internal static int? LastReusedProjectedBattleHpLostForTesting { get; private set; }
    internal static int UnexpectedReplanCountForTesting
        => UnexpectedReplanCount;
    internal static int ManualDivergenceCountForTesting
        => _combat.ReplanCounts.GetValueOrDefault(ReplanCause.ManualDivergence);
    internal static bool ManualRouteImprovementDetected
        => _combat.ManualRouteImprovementDetected;
    internal static bool BugReportUploadRecommended
        => UnexpectedReplanCount > 0
           || _combat.ReplanCounts.GetValueOrDefault(ReplanCause.ContinuationMissing) > 0
           || _combat.ReplanCounts.GetValueOrDefault(ReplanCause.PlanExhausted) > 0
           || _combat.BugReportIssues.RequiresPlayerUpload;
    internal static ManualProjectionComparison? LastManualProjectionComparisonForTesting
        => _combat.LastManualProjectionComparison;
    internal static int NoGcRegionRolloverCountForTesting
        => SearchGcPolicy.RolloverCountForTesting;
    internal static SearchMemoryUsageSnapshot CaptureSearchMemoryUsage()
    {
        SearchMemoryPressureSignal? signal = _search?.MemoryPressureSignal
            ?? PlayerTurnSetupCoordinator.CurrentMemoryPressureSignal;
        SearchMemoryPressureUsage pressure = signal?.CaptureUsage()
            ?? SearchMemoryPressureUsage.Disabled;
        SolverSettingsSnapshot settings = SolverSettings.Capture();
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        PhysicalMemoryUsage physicalMemory = PhysicalMemoryUsage.Capture(memory);
        long systemMemoryLimit = pressure.SystemMemoryLimitBytes == long.MaxValue
            ? SearchGcPolicy.ResolveSystemMemoryLimit(memory)
            : pressure.SystemMemoryLimitBytes;
        if (physicalMemory.TotalBytes > 0)
            systemMemoryLimit = Math.Min(systemMemoryLimit, physicalMemory.TotalBytes);
        return new SearchMemoryUsageSnapshot(
            System.Environment.WorkingSet,
            physicalMemory.UsedBytes,
            settings.NoGcRegionBudgetBytes,
            IsSearching,
            pressure.AllocatedBytes,
            pressure.AllocationLimitBytes,
            pressure.ProjectedMemoryLoadBytes,
            systemMemoryLimit,
            pressure.Reclaiming,
            SearchGcPolicy.IsBackgroundReclaiming);
    }
    internal static void LogSearchMemoryDisplayState(
        SearchMemoryUsageSnapshot snapshot,
        string displayState,
        double displayRatio)
    {
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        Entry.Logger.Info(
            $"[CombatSolver/Test] MEMORY_MONITOR_DISPLAY state={displayState} " +
            $"display_ratio={displayRatio:F3} search_active={snapshot.SearchActive.ToString().ToLowerInvariant()} " +
            $"foreground_reclaim={snapshot.Reclaiming.ToString().ToLowerInvariant()} " +
            $"background_reclaim={snapshot.BackgroundReclaiming.ToString().ToLowerInvariant()} " +
            $"search_allocated={snapshot.SearchAllocatedBytes} search_limit={snapshot.SearchAllocationLimitBytes} " +
            $"projected_memory_load={snapshot.ProjectedSystemMemoryLoadBytes} " +
            $"system_memory_limit={snapshot.SystemMemoryLimitBytes} " +
            $"physical_memory_used={snapshot.PhysicalMemoryUsedBytes} " +
            $"system_occupied={snapshot.SystemOccupiedBytes} " +
            $"process_memory_limit={snapshot.ProcessMemoryLimitBytes} " +
            $"system_segment={snapshot.SystemSegmentRatio:F3} " +
            $"process_segment={snapshot.ProcessSegmentRatio:F3} " +
            $"process_pressure={snapshot.ProcessMemoryPressureRatio:F3} " +
            $"allocation_pressure={snapshot.AllocationPressureRatio:F3} " +
            $"system_pressure={snapshot.SystemPressureRatio:F3} " +
            $"system_pressure_dominates={snapshot.SystemPressureDominates.ToString().ToLowerInvariant()} " +
            $"configured_budget={snapshot.ConfiguredMemoryBudgetBytes} " +
            $"working_set={process.WorkingSet64} private_bytes={process.PrivateMemorySize64} " +
            $"managed_live={GC.GetTotalMemory(forceFullCollection: false)} " +
            $"managed_heap={memory.HeapSizeBytes} fragmented={memory.FragmentedBytes} " +
            $"memory_load={memory.MemoryLoadBytes} high_memory_threshold={memory.HighMemoryLoadThresholdBytes} " +
            $"total_available={memory.TotalAvailableMemoryBytes} " +
            $"gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)} " +
            $"latency={GCSettings.LatencyMode} tick_ms={System.Environment.TickCount64}");
    }
    internal static long LastDeployedActionStartedAtMillisecondsForTesting { get; private set; }
    internal static Task LastCombatReferenceReleaseForTesting { get; private set; } = Task.CompletedTask;
    internal static int CombatLifecycleGeneration
        => Volatile.Read(ref _combatLifecycleGeneration);
    internal static int SearchReferenceReleaseScheduledCountForTesting
        => Volatile.Read(ref _searchReferenceReleaseScheduledCountForTesting);
    internal static int SearchReferenceReleaseCompletedCountForTesting
        => Volatile.Read(ref _searchReferenceReleaseCompletedCountForTesting);
    internal static int SearchCtsDisposeCountForTesting
        => Volatile.Read(ref _searchCtsDisposeCountForTesting);
    internal static int PendingSearchReferenceReleaseCountForTesting
        => PendingSearchReferenceReleases.Count(static task => !task.IsCompleted);
    internal static bool WasCardDeployedForTesting(string cardId)
        => DeployedCardIdsForTesting.Contains(cardId);
    internal static bool WasPotionDeployedForTesting(string potionId)
        => DeployedPotionIdsForTesting.Contains(potionId);

    internal static void CancelSearchForTesting()
    {
        AssertMainThread();
        if (!UnattendedTestRunner.IsActive)
            throw new InvalidOperationException("搜索会话取消入口只能在无人测试中使用。");
        CancelSearch();
    }


    internal static Task StartCombatDeferredOperation(
        Func<CancellationToken, Task> operationFactory)
    {
        AssertMainThread();
        ArgumentNullException.ThrowIfNull(operationFactory);
        CancellationToken token = _combatDeferredOperationCancellation.Token;
        Task operation = RunCombatDeferredOperationAsync(operationFactory, token);
        PendingCombatDeferredOperations.RemoveAll(static task => task.IsCompleted);
        if (!operation.IsCompleted)
            PendingCombatDeferredOperations.Add(operation);
        return operation;
    }

    private static async Task RunCombatDeferredOperationAsync(
        Func<CancellationToken, Task> operationFactory,
        CancellationToken token)
    {
        try
        {
            await operationFactory(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Reset owns cancellation. The operation remains in the reference-release barrier
            // until its state machine has dropped every captured combat/result reference.
        }
    }

    internal static async Task<(
        bool StaleCompletionPreservedCurrentSession,
        bool BarrierWaitedForBothOperations,
        int ReleasesScheduled,
        int ReleasesCompleted,
        int CancellationsDisposed)> ExerciseDeploymentSessionLifecycleForTestingAsync()
    {
        AssertMainThread();
        if (!UnattendedTestRunner.IsActive)
            throw new InvalidOperationException("部署会话生命周期测试只能在无人测试中使用。");
        PendingDeploymentReferenceReleases.RemoveAll(static task => task.IsCompleted);
        if (_deployment != null || PendingDeploymentReferenceReleases.Count != 0)
            throw new InvalidOperationException("部署会话生命周期测试要求控制器初始空闲。");

        int releasesScheduledBefore = Volatile.Read(
            ref _deploymentReferenceReleaseScheduledCountForTesting);
        int releasesCompletedBefore = Volatile.Read(
            ref _deploymentReferenceReleaseCompletedCountForTesting);
        int cancellationsDisposedBefore = Volatile.Read(
            ref _deploymentCtsDisposeCountForTesting);
        TaskCompletionSource firstCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        SolverDeploymentSession first = new() { Operation = firstCompletion.Task };
        _deployment = first;
        CancelDeployment();
        SolverDeploymentSession second = new() { Operation = secondCompletion.Task };
        _deployment = second;
        CompleteDeployment(first);
        bool staleCompletionPreservedCurrentSession = ReferenceEquals(_deployment, second);
        CancelDeployment();
        Task referenceRelease = DrainDeploymentReferenceReleases();
        bool barrierWaitedForBothOperations = !referenceRelease.IsCompleted;
        firstCompletion.TrySetResult();
        barrierWaitedForBothOperations &= !referenceRelease.IsCompleted;
        secondCompletion.TrySetResult();
        await referenceRelease.ConfigureAwait(false);

        return (
            staleCompletionPreservedCurrentSession,
            barrierWaitedForBothOperations,
            Volatile.Read(ref _deploymentReferenceReleaseScheduledCountForTesting)
                - releasesScheduledBefore,
            Volatile.Read(ref _deploymentReferenceReleaseCompletedCountForTesting)
                - releasesCompletedBefore,
            Volatile.Read(ref _deploymentCtsDisposeCountForTesting)
                - cancellationsDisposedBefore);
    }

    internal static bool IsCurrentCombatLifecycle(
        CombatState combat,
        int lifecycleGeneration)
        => lifecycleGeneration == Volatile.Read(ref _combatLifecycleGeneration)
           && CombatManager.Instance.IsInProgress
           && !CombatManager.Instance.IsOverOrEnding
           && ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), combat);

    internal static bool RecordTurnSetupFailure(
        CombatState combat,
        int lifecycleGeneration,
        Exception exception,
        bool parallelSearchWasEnabled = false)
    {
        AssertMainThread();
        if (!IsCurrentCombatLifecycle(combat, lifecycleGeneration))
        {
            Entry.Logger.Info(
                $"[CombatSolver/Test] TURN_SETUP_FAILURE_IGNORED reason=stale_lifecycle " +
                $"operation_epoch={lifecycleGeneration} " +
                $"current_epoch={Volatile.Read(ref _combatLifecycleGeneration)}");
            return false;
        }
        _combat.BugReportIssues.RecordFailure(
            CombatBugReportIssueKind.TurnSetupFailure,
            exception);
        CombatBugReportExporter.RecordRuntimeException("turn_setup", exception);
        Entry.Logger.Error(
            $"[CombatSolver/Test] TURN_SETUP_FAILURE exception={exception.GetBaseException()}");
        if (NGame.Instance is { } host)
            SolverOverlay.Show(host, FormatTurnSetupFailure(exception, parallelSearchWasEnabled));
        return true;
    }

    internal static bool RecordTurnSetupStateMismatch(
        CombatState combat,
        int lifecycleGeneration,
        string difference)
    {
        AssertMainThread();
        if (!IsCurrentCombatLifecycle(combat, lifecycleGeneration))
        {
            Entry.Logger.Info(
                $"[CombatSolver/Test] TURN_SETUP_MISMATCH_IGNORED reason=stale_lifecycle " +
                $"operation_epoch={lifecycleGeneration} " +
                $"current_epoch={Volatile.Read(ref _combatLifecycleGeneration)}");
            return false;
        }
        _combat.BugReportIssues.Record(
            CombatBugReportIssueKind.TurnSetupStateMismatch,
            difference);
        CombatBugReportExporter.RecordRuntimeDivergence("turn_setup", difference);
        SolverOverlay.RefreshControls();
        return true;
    }

    public static void ApplyPersistentSettings(SolverSettingsSnapshot settings)
    {
        _solverDisabled = settings.SolverDisabled;
        _stopFullAutoOnCombatEnd = settings.StopFullAutoOnCombatEnd;
        _stopFullAutoOnDeathTurn = settings.StopFullAutoOnDeathTurn;
        _stopFullAutoOnWorseRecalculation = settings.StopFullAutoOnWorseRecalculation;
    }

    /// <summary>
    /// 显示服务器名字的取值口。游戏内一律是默认值（直接问 Godot），
    /// 只有 tools/OfflineSearchHarness 这种不启动 Godot 的进程会把它换成固定的 "headless"。
    /// </summary>
    internal static Func<string> DisplayServerNameProvider { get; set; } = static () => DisplayServer.GetName();

    internal static SearchPolicySnapshot CaptureSearchPolicy(
        SolverSettingsSnapshot settings,
        CombatState state,
        bool includeTurnSetup,
        SolverTheftPolicy? theftPolicy,
        SearchInteractionState? interaction = null)
    {
        SolverSessionCapabilitySet capabilities = SolverSessionCapabilities.Capture(state);
        SolverPotionPolicy effectivePotionPolicy = capabilities.CanUsePotionsAutomatically
            ? settings.PotionPolicy
            : SolverPotionPolicy.Disabled;
        PotionStrategySnapshot effectivePotionStrategy = capabilities.CanUsePotionsAutomatically
            ? CapturePotionStrategy(state, effectivePotionPolicy)
            : new PotionStrategySnapshot(SolverPotionPolicy.Disabled, []);
        FramePressureSignal.ResetPressure(
            recoveryEnabled: !string.Equals(
                DisplayServerNameProvider(),
                "headless",
                StringComparison.OrdinalIgnoreCase));
        int maxDegreeOfParallelism = UnattendedTestRunner.SearchMaxDegreeOfParallelismOverride
            ?? settings.SearchMaxDegreeOfParallelism;
        if (maxDegreeOfParallelism < 1
            || maxDegreeOfParallelism > SolverWeights.MaximumSearchMaxDegreeOfParallelism)
        {
            throw new InvalidOperationException(
                $"搜索并行度必须在 1..{SolverWeights.MaximumSearchMaxDegreeOfParallelism} 之间，" +
                $"实际为 {maxDegreeOfParallelism}。");
        }
        SearchRoutePolicy routePolicy = capabilities.Kind switch
        {
            SolverSessionKind.Singleplayer => SearchRoutePolicy.SinglePlayerFullRoute,
            SolverSessionKind.MultiplayerProbe => SearchRoutePolicy.MultiplayerCurrentTurnOnly,
            _ when capabilities.CanPlanLocalCrossTurn
                => SearchRoutePolicy.MultiplayerLocalCrossTurn,
            _ => SearchRoutePolicy.MultiplayerCurrentTurnOnly,
        };
        SearchPolicySnapshot policy = new(
            settings.Profile,
            effectivePotionPolicy,
            effectivePotionStrategy,
            settings.EnableDetailedDiagnosticLogs,
            UnattendedTestRunner.VerifyIncrementalSearch,
            UnattendedTestRunner.FixedSearchBudget,
            UnattendedTestRunner.MeasureSearchPhases,
            maxDegreeOfParallelism,
            UnattendedTestRunner.SearchBudgetOverrideMilliseconds,
            includeTurnSetup && capabilities.CanInterceptTurnSetup,
            theftPolicy,
            settings.ActTransitionBossHpStrategy,
            settings.FinalBossHpStrategy,
            settings.AcceptableBattleHpLoss,
            new SearchDiagnosticsSink(
                Entry.Logger.Journal.Bind("info"),
                Entry.Logger.Journal.Bind("debug")),
            FramePressureSignal,
            new SearchMemoryPressureSignal())
        {
            Interaction = interaction,
            RoutePolicy = routePolicy,
            CurrentTurnOnly = MultiplayerLocalCrossTurnContracts.IsCurrentTurnOnly(routePolicy),
            UseNoveltyPortfolio = (settings.UseNoveltyPortfolio
                || UnattendedTestRunner.UseNoveltyPortfolioOverride)
                && capabilities.CanCrossTurnSearch,
            UseBeamWidthPortfolio = settings.UseBeamWidthPortfolio
                || UnattendedTestRunner.UseBeamWidthPortfolioOverride,
            BeamWidthPortfolioWidths = UnattendedTestRunner.BeamWidthPortfolioWidthsOverride,
            Act3BossStrategy = UnattendedTestRunner.Act3BossStrategyOverride != false
                && SearchPolicySnapshot.IsAct3BossEncounter(state.RunState.CurrentActIndex, state.Encounter?.Id.Entry),
            // 这里记的是玩家填的原始值；「不考虑局外收益」的折算交给快照上的 Effective* 一处做，
            // 免得两边各判一次而走岔。问题包里两样都在，方便看出当时是填了额度还是开了开关。
            GrowthBudgets = capabilities.CanCrossTurnSearch ? settings.GrowthBudgets : default,
            RelicTargets = capabilities.CanCrossTurnSearch
                ? RelicCounterCatalog.Capture(state, settings.RelicStrategyEnabled, settings.RelicCounterRules)
                : [],
            StopAtAcceptableBattleHpLoss = settings.StopAtAcceptableBattleHpLoss,
            BrightestFlameMaxHpLossLimit = settings.BrightestFlameMaxHpLossLimit,
            GrowthOpportunityTargets = capabilities.CanCrossTurnSearch
                ? GrowthOpportunityPolicy.Capture(state)
                : GrowthOpportunityTargets.Empty,
            IgnoreLongTermRewards = settings.IgnoreLongTermRewards || !capabilities.CanCrossTurnSearch,
        };
        CombatBugReportExporter.RecordSearchPolicy(state, policy);
        return policy;
    }

    public static void BeginCombat(ICombatState? state)
    {
        AssertMainThread();
        ResetCore("combat_starting");
        if (state is CombatState activeMultiplayerCombat
            && SolverSessionCapabilities.Capture(activeMultiplayerCombat).IsMultiplayer)
        {
            MultiplayerClientProbe.BeginCombatSegment();
        }
        _combat.FullAutoEnabled = !_solverDisabled
            && SolverSessionCapabilities.Capture(state as CombatState).CanFullAuto
            && state is CombatState { Players.Count: 1 }
            && SolverSettings.Current.AutoEnableFullAuto;
        DeployedCardIdsForTesting.Clear();
        DeployedPotionIdsForTesting.Clear();
        LastDeployedActionStartedAtMillisecondsForTesting = 0;
        NativeChoiceRuntime.ResetTraceForTesting();
        LastTurnSetupResultForTesting = null;
        LastReusedTurnForTesting = null;
        LastReusedProjectedBattleHpLostForTesting = null;
        SearchGcPolicy.ResetCountersForTesting();
        BattleDamageTracker.Begin(state);
        CombatShowcaseCollector.BeginCombat();
        CombatBugReportExporter.BeginCombat(state);
        _combat.TheftPolicy = state is CombatState combat && TheftEncounterStrategy.IsApplicable(combat)
            ? SolverTheftPolicy.PreserveResources
            : null;
        if (state is CombatState activeCombat)
            ReconcilePersistedPotionDirectives(activeCombat);
        Entry.Logger.Info(
            $"[CombatSolver/Test] THEFT_POLICY_INIT policy={_combat.TheftPolicy?.ToString() ?? "-"}");
    }

    private static void RecordReviewedWorldlines(SolverResult result)
    {
        if (result.WasRestoredFromCache)
            return;
        if (!_combat.ReviewedWorldlineResults.Add(result))
            return;
        long reviewed = result.TotalExpandedNodes;
        _combat.ReviewedWorldlinesTotal = checked(_combat.ReviewedWorldlinesTotal + reviewed);
    }

    internal static void ShowTurnSetupResultPreview(NGame host, SolverResult result)
    {
        AssertMainThread();
        RecordReviewedWorldlines(result);
        SolverOverlaySnapshot snapshot = result.ResultScope == SolverResultScope.CurrentTurnAdoption
            ? SolverOverlaySnapshot.CaptureCurrentTurn(SolverCurrentTurnPreview.FromResult(result))
            : SolverOverlaySnapshot.CaptureWithReviewedWorldlines(
                result,
                UnexpectedReplanCount > 0,
                _combat.ReviewedWorldlinesTotal);
        if (result.ResultScope == SolverResultScope.RouteAdoption)
            snapshot = MarkRouteAdopted(snapshot);
        SolverOverlay.ShowResult(host, snapshot);
        SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Succeeded);
        Entry.Logger.Info(
            $"[CombatSolver/Test] TURN_SETUP_RESULT_PREVIEW turn={result.StartTurnNumber} " +
            $"scope={result.ResultScope} native_choice_pending=true");
        Entry.Logger.Info(SolverDiagnostics.DescribeResult(result));
    }

    internal static void StartDeploymentAfterTurnSetup(
        NGame host,
        CombatState state,
        SolverResult result)
    {
        if (!SolverSessionCapabilities.Capture(state).CanDeploySimpleLocalActions)
        {
            Entry.Logger.Info(
                $"[CombatSolver/MultiplayerProbe] DEPLOY_AFTER_SETUP_REJECT " +
                $"reason={SolverSessionCapabilities.Capture(state).DeploymentRejection}");
            return;
        }
        Task deploymentTask = StartCombatDeferredOperation(
            token => StartDeploymentAfterTurnSetupAsync(host, state, result, token));
        if (UnattendedAsyncActivityTracker.IsRequestActive)
            deploymentTask = UnattendedAsyncActivityTracker.Track(deploymentTask);
        TaskHelper.RunSafely(deploymentTask);
    }

    internal static async Task ResumeAfterTurnSetupAsync(
        NGame host,
        CombatState state,
        int lifecycleGeneration,
        int turn,
        bool deployWhenReady = false,
        bool waitForDeploymentDelay = false,
        CancellationToken token = default)
    {
        SolverCombatSession session = _combat;
        session.TurnSetupResumeState = state;
        bool searchRequested = false;
        try
        {
            token.ThrowIfCancellationRequested();
            long deadline = System.Environment.TickCount64 + 30_000;
            while (CombatManager.Instance.IsInProgress
                   && !CombatManager.Instance.IsOverOrEnding
                   && ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state)
                   && (LocalContext.GetMe(state)?.PlayerCombatState?.Phase != PlayerTurnPhase.Play
                       || CombatManager.Instance.PlayerActionsDisabled
                       || _deployment != null))
            {
                if (System.Environment.TickCount64 >= deadline)
                    throw new TimeoutException("回合准备页面完成后 30 秒内没有进入可复用状态。");
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                token.ThrowIfCancellationRequested();
            }

            if (!IsCurrentCombatLifecycle(state, lifecycleGeneration)
                || _solverDisabled
                || !CombatManager.Instance.IsInProgress
                || CombatManager.Instance.IsOverOrEnding
                || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state)
                || LocalContext.GetMe(state)?.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } playerState
                || playerState.TurnNumber != turn)
            {
                return;
            }

            if (waitForDeploymentDelay)
                await WaitForTurnStartDeploymentDelayAsync(host, turn, token);

            if (!IsCurrentCombatLifecycle(state, lifecycleGeneration)
                || _solverDisabled
                || !CombatManager.Instance.IsInProgress
                || CombatManager.Instance.IsOverOrEnding
                || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state)
                || LocalContext.GetMe(state)?.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } resumedState
                || resumedState.TurnNumber != turn)
            {
                return;
            }

            SearchReason resumedReason = session.ManualSearchAfterTurnSetupRequested
                ? SearchReason.Manual
                : SearchReason.AutoTurnStart;
            session.ManualSearchAfterTurnSetupRequested = false;
            session.TurnSetupResumeState = null;
            searchRequested = true;
            RequestSearch(host, state, resumedReason, deployWhenReady);
        }
        finally
        {
            if (ReferenceEquals(session.TurnSetupResumeState, state))
                session.TurnSetupResumeState = null;
            if (!searchRequested && ReferenceEquals(_combat, session))
                session.ManualSearchAfterTurnSetupRequested = false;
        }
    }

    internal static bool ManualSearchAfterTurnSetupRequested
        => _combat.ManualSearchAfterTurnSetupRequested;

    internal static void QueueManualSearchAfterTurnSetup()
    {
        AssertMainThread();
        _combat.ManualSearchAfterTurnSetupRequested = true;
    }

    private static async Task StartFullAutoAfterTurnSetupAsync(
        NGame host,
        CombatState state,
        SolverResult result,
        CancellationToken token)
    {
        if (!SolverSessionCapabilities.Capture(state).CanFullAuto)
            return;
        long deadline = System.Environment.TickCount64 + 30_000;
        while (CombatManager.Instance.IsInProgress
               && !CombatManager.Instance.IsOverOrEnding
               && (_deployment != null || CombatManager.Instance.PlayerActionsDisabled))
        {
            if (System.Environment.TickCount64 >= deadline)
                throw new TimeoutException("回合准备完成后 30 秒内没有进入可部署状态。");
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            token.ThrowIfCancellationRequested();
        }
        await WaitForTurnStartDeploymentDelayAsync(host, result.StartTurnNumber, token);
        token.ThrowIfCancellationRequested();
        if (_combat.FullAutoEnabled
            && ReferenceEquals(_combat.LatestResult, result)
            && IsSamePlayableTurn(state, result.StartTurnNumber))
        {
            StartFullAutoDeployment(host, state, result);
        }
    }

    private static async Task StartDeploymentAfterTurnSetupAsync(
        NGame host,
        CombatState state,
        SolverResult result,
        CancellationToken token)
    {
        if (!SolverSessionCapabilities.Capture(state).CanDeploySimpleLocalActions)
            return;
        long deadline = System.Environment.TickCount64 + 30_000;
        while (CombatManager.Instance.IsInProgress
               && !CombatManager.Instance.IsOverOrEnding
               && (_deployment != null || CombatManager.Instance.PlayerActionsDisabled))
        {
            if (System.Environment.TickCount64 >= deadline)
                throw new TimeoutException("回合准备完成后 30 秒内没有进入可部署状态。");
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            token.ThrowIfCancellationRequested();
        }
        await WaitForTurnStartDeploymentDelayAsync(host, result.StartTurnNumber, token);
        token.ThrowIfCancellationRequested();
        SolverSessionCapabilitySet capabilities = SolverSessionCapabilities.Capture(state);
        bool safeExecuteDeploymentRequested = capabilities.Kind != SolverSessionKind.MultiplayerSafeExecute
            || _combat.MultiplayerSafeExecuteDeploymentRequested;
        if (!_combat.FullAutoEnabled
            && safeExecuteDeploymentRequested
            && ReferenceEquals(_combat.LatestResult, result)
            && IsSamePlayableTurn(state, result.StartTurnNumber))
        {
            _combat.MultiplayerSafeExecuteDeploymentRequested = false;
            StartDeployment(host, state, result);
        }
    }

    internal static string FormatSearchSetupFailure(Exception exception)
    {
        string title = $"[color={SolverUiTokens.Palette.DangerHex}][b]{SolverText.Get("搜索初始化失败")}[/b][/color]";
        if (exception.GetBaseException() is not IncompatibleGameplayModException incompatible)
        {
            return $"{title}\n[color={SolverUiTokens.Palette.DangerHex}]{EscapeRichText(exception.Message)}[/color]" +
                   $"\n{SolverUiTokens.BugReportUploadInstructionRichText}";
        }

        return FormatIncompatibleModFailure(incompatible);
    }

    private static string FormatIncompatibleModFailure(IncompatibleGameplayModException incompatible)
        => $"[color={SolverUiTokens.Palette.DangerHex}]" +
           SolverText.Format($"检测到不兼容的第三方 Mod：{EscapeRichText(incompatible.PlayerFacingModName)}。建议卸载该 Mod 并重启游戏后再使用求解器。") + "[/color]";

    internal static string FormatSearchFailureForTesting(
        Exception exception,
        bool parallelSearchWasEnabled)
        => FormatSearchFailure(exception, parallelSearchWasEnabled);

    private static string EscapeRichText(string value)
        => value.Replace('[', '［').Replace(']', '］');

    public static void RequestDeploy(NGame host, CombatState state)
    {
        AssertMainThread();
        SolverSessionCapabilitySet capabilities = SolverSessionCapabilities.Capture(state);
        if (!capabilities.CanDeploySimpleLocalActions)
        {
            Entry.Logger.Info($"[CombatSolver/MultiplayerProbe] DEPLOY_REJECT reason={capabilities.DeploymentRejection}");
            return;
        }
        SolverDispatcher.Ensure(host);
        if (_deployment != null)
        {
            Entry.Logger.Info("[CombatSolver/Test] DEPLOY_REJECT reason=already_deploying");
            return;
        }
        bool safeExecuteRequest = capabilities.Kind == SolverSessionKind.MultiplayerSafeExecute;
        _combat.AutomaticSearchPaused = false;
        _combat.AutomaticSearchPausedTurn = null;
        if (PlayerTurnSetupCoordinator.TryContinuePlannedChoice(
                host,
                state,
                deployAfterSetup: true))
        {
            if (safeExecuteRequest)
                _combat.MultiplayerSafeExecuteDeploymentRequested = true;
            Entry.Logger.Info("[CombatSolver/Test] DEPLOY_WAIT reason=turn_setup_choice");
            return;
        }
        Player? turnStartPlayer = LocalContext.GetMe(state);
        if (state.CurrentSide == CombatSide.Player
            && turnStartPlayer?.PlayerCombatState?.Phase == PlayerTurnPhase.Start)
        {
            if (safeExecuteRequest)
                _combat.MultiplayerSafeExecuteDeploymentRequested = true;
            _combat.DeployAfterTurnSetupTurn = turnStartPlayer.PlayerCombatState.TurnNumber;
            Entry.Logger.Info("[CombatSolver/Test] DEPLOY_WAIT reason=turn_setup_pending");
            return;
        }
        if (!CanSolve(state, out string rejection))
        {
            if (safeExecuteRequest)
                _combat.MultiplayerSafeExecuteDeploymentRequested = false;
            SolverOverlay.Show(host, $"[b]战斗路线求解器[/b]\n{rejection}");
            Entry.Logger.Info($"[CombatSolver/Test] DEPLOY_REJECT reason={rejection}");
            return;
        }

        if (safeExecuteRequest)
            _combat.MultiplayerSafeExecuteDeploymentRequested = true;
        LiveCombatStamp current = LiveCombatStamp.Capture(state);
        if (_combat.LatestResult != null && _combat.LatestStamp == current)
        {
            _combat.MultiplayerSafeExecuteDeploymentRequested = false;
            StartDeployment(host, state, _combat.LatestResult);
            return;
        }

        if (_search is { } search
            && ReferenceEquals(search.State, state)
            && search.Stamp == current)
        {
            search.DeployWhenReady = true;
            Player player = LocalContext.GetMe(state)!;
            SolverOverlay.ShowSearching(
                host,
                player.PlayerCombatState!.TurnNumber,
                deployWhenReady: true,
                _combat.ReviewedWorldlinesTotal);
            Entry.Logger.Info($"[CombatSolver/Test] DEPLOY_WAIT generation={search.Generation}");
            return;
        }

        if ((_combat.LatestResult != null || _search != null) && _combat.LatestStamp != current)
            MarkManualControlObserved("deploy_after_live_state_change");

        RequestSearch(host, state, SearchReason.Deploy, deployWhenReady: true);
    }

    public static void SetTheftPolicy(NGame host, CombatState state, SolverTheftPolicy policy)
    {
        AssertMainThread();
        if (!TheftEncounterStrategy.IsApplicable(state))
            throw new InvalidOperationException("当前战斗不支持偷窃路线策略。");
        if (_deployment != null)
        {
            Entry.Logger.Info("[CombatSolver/Test] THEFT_POLICY_REJECT reason=deploying");
            return;
        }
        if (_combat.TheftPolicy == policy)
            return;

        SolverTheftPolicy? previous = _combat.TheftPolicy;
        _combat.TheftPolicy = policy;
        _combat.ContinuationSource = null;
        _combat.PendingCompleteProjectionBaseline = null;
        Entry.Logger.Info(
            $"[CombatSolver/Test] THEFT_POLICY_CHANGED previous={previous?.ToString() ?? "-"} current={policy}");
        SolverOverlay.RefreshControls();
        if (_combat.AutomaticSearchPaused || !AutomaticCalculationEnabled)
        {
            Entry.Logger.Info("[CombatSolver/Test] THEFT_POLICY_RECALCULATION_SKIPPED reason=automatic_calculation_inactive");
            return;
        }
        RequestSearch(host, state, SearchReason.Manual);
    }

    internal static PotionStrategySnapshot CapturePotionStrategy(
        CombatState state,
        SolverPotionPolicy defaultPolicy)
    {
        AssertMainThread();
        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("当前战斗找不到本地玩家。");
        List<PotionSlotDirective> directives = [];
        foreach (PersistedPotionDirective persisted in SolverSettings.Current.PotionDirectives)
        {
            PotionModel? potion = GetPotionAtExistingSlot(player, persisted.Slot);
            if (potion != null
                && string.Equals(potion.Id.Entry, persisted.PotionId, StringComparison.Ordinal))
            {
                directives.Add(new PotionSlotDirective(
                    persisted.Slot,
                    persisted.PotionId,
                    persisted.Directive));
            }
        }
        return new PotionStrategySnapshot(defaultPolicy, directives);
    }

    internal static SolverPotionDirective ResolvePotionDirective(
        CombatState state,
        int slot,
        string potionId)
        => SolverSettings.ResolvePotionDirective(slot, potionId);

    internal static void SetBrightestFlameLimit(NGame host, CombatState state, int? limit)
    {
        AssertMainThread();
        if (_deployment != null || SolverSettings.Current.BrightestFlameMaxHpLossLimit == limit)
            return;
        SolverSettings.Update(SolverSettings.Current with { BrightestFlameMaxHpLossLimit = limit });
        _combat.ContinuationSource = null;
        _combat.PendingCompleteProjectionBaseline = null;
        _combat.LatestResult = null;
        _combat.LatestStamp = null;
        RequestSearch(host, state, SearchReason.Manual);
        SolverOverlay.RefreshControls();
    }

    internal static void SetGrowthPolicy(NGame host, CombatState state, GrowthValues budgets)
    {
        AssertMainThread();
        if (_deployment != null)
            return;
        SolverSettingsData current = SolverSettings.Current;
        if (current.GrowthBudgets == budgets)
            return;
        SolverSettings.Update(current with { GrowthBudgets = budgets });
        _combat.ContinuationSource = null;
        _combat.PendingCompleteProjectionBaseline = null;
        SolverOverlay.RefreshControls();
        if (!_combat.AutomaticSearchPaused && AutomaticCalculationEnabled)
            RequestSearch(host, state, SearchReason.Manual);
    }

    internal static void SetRelicCounterPolicy(NGame host, CombatState state, bool enabled, RelicCounterRule[] rules)
    {
        AssertMainThread();
        if (_deployment != null) return;
        var copied = RelicCounterPolicy.ValidateAndCopy(rules);
        var current = SolverSettings.Current;
        if (current.RelicStrategyEnabled == enabled && current.RelicCounterRules.SequenceEqual(copied)) return;
        SolverSettings.Update(current with { RelicStrategyEnabled = enabled, RelicCounterRules = copied });
        _combat.ContinuationSource = null;
        _combat.PendingCompleteProjectionBaseline = null;
        SolverOverlay.RefreshControls();
        if (!_combat.AutomaticSearchPaused && AutomaticCalculationEnabled)
            RequestSearch(host, state, SearchReason.Manual);
    }

    internal static void SetIgnoreLongTermRewards(NGame host, CombatState state, bool ignore)
    {
        AssertMainThread();
        if (_deployment != null)
            return;
        SolverSettingsData current = SolverSettings.Current;
        if (current.IgnoreLongTermRewards == ignore)
            return;
        SolverSettings.Update(current with { IgnoreLongTermRewards = ignore });
        _combat.ContinuationSource = null;
        _combat.PendingCompleteProjectionBaseline = null;
        SolverOverlay.RefreshControls();
        if (!_combat.AutomaticSearchPaused && AutomaticCalculationEnabled)
            RequestSearch(host, state, SearchReason.Manual);
    }

    internal static void SetPotionDirective(
        NGame host,
        CombatState state,
        int slot,
        string potionId,
        SolverPotionDirective directive)
    {
        AssertMainThread();
        if (!Enum.IsDefined(directive))
            throw new ArgumentOutOfRangeException(nameof(directive));
        if (_deployment != null)
        {
            Entry.Logger.Info("[CombatSolver/Test] POTION_DIRECTIVE_REJECT reason=deploying");
            return;
        }
        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("当前战斗找不到本地玩家。");
        PotionModel potion = player.GetPotionAtSlotIndex(slot)
            ?? throw new InvalidOperationException($"药水槽位 {slot} 为空。");
        if (!string.Equals(potion.Id.Entry, potionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"药水槽位 {slot} 为 {potion.Id.Entry}，预期 {potionId}。");
        }

        if (SolverSettings.ResolvePotionDirective(slot, potionId) == directive)
            return;
        SolverSettings.Update(SolverSettings.ApplyPotionDirective(
            SolverSettings.Current,
            slot,
            potionId,
            directive));
        _combat.ContinuationSource = null;
        _combat.PendingCompleteProjectionBaseline = null;
        Entry.Logger.Info(
            $"[CombatSolver/Test] POTION_DIRECTIVE_CHANGED slot={slot} potion={potionId} directive={directive}");
        SolverOverlay.RefreshControls();
        if (_combat.AutomaticSearchPaused || !AutomaticCalculationEnabled)
        {
            Entry.Logger.Info("[CombatSolver/Test] POTION_DIRECTIVE_RECALCULATION_SKIPPED reason=automatic_calculation_inactive");
            return;
        }
        RequestSearch(host, state, SearchReason.Manual);
    }

    internal static void SetPotionPreset(
        NGame host,
        CombatState state,
        PotionStrategyPreset preset)
    {
        AssertMainThread();
        if (!Enum.IsDefined(preset))
            throw new ArgumentOutOfRangeException(nameof(preset));
        if (_deployment != null)
        {
            Entry.Logger.Info("[CombatSolver/Test] POTION_PRESET_REJECT reason=deploying");
            return;
        }

        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("当前战斗找不到本地玩家。");
        SolverSettingsData current = SolverSettings.Current;
        PotionStrategySnapshot currentStrategy = CapturePotionStrategy(state, current.PotionPolicy);
        List<PotionSlotDirective> searchable = [];
        for (int slot = 0; slot < player.PotionSlots.Count; slot++)
        {
            PotionModel? potion = player.GetPotionAtSlotIndex(slot);
            if (potion == null || !PotionOnUseSupport.CanSearch(potion))
                continue;
            searchable.Add(new PotionSlotDirective(
                slot,
                potion.Id.Entry,
                currentStrategy.Resolve(slot, potion.Id.Entry)));
        }
        SolverSettingsData updated = SolverSettings.ApplyPotionPreset(current, searchable, preset);
        if (updated == current)
            return;

        SolverSettings.Update(updated);
        _combat.ContinuationSource = null;
        _combat.PendingCompleteProjectionBaseline = null;
        Entry.Logger.Info($"[CombatSolver/Test] POTION_PRESET_CHANGED preset={preset} potions={searchable.Count}");
        SolverOverlay.RefreshControls();
        if (_combat.AutomaticSearchPaused || !AutomaticCalculationEnabled)
        {
            Entry.Logger.Info("[CombatSolver/Test] POTION_PRESET_RECALCULATION_SKIPPED reason=automatic_calculation_inactive");
            return;
        }
        RequestSearch(host, state, SearchReason.Manual);
    }

    internal static void SetPotionDirectiveForTesting(
        CombatState state,
        int slot,
        string potionId,
        SolverPotionDirective directive)
    {
        AssertMainThread();
        if (!UnattendedTestRunner.IsActive)
            throw new InvalidOperationException("药水策略测试覆盖只能在无人测试中使用。");
        PotionModel potion = LocalContext.GetMe(state)?.GetPotionAtSlotIndex(slot)
            ?? throw new InvalidOperationException($"测试药水槽位 {slot} 为空。");
        if (!string.Equals(potion.Id.Entry, potionId, StringComparison.Ordinal))
            throw new InvalidOperationException($"测试药水槽位 {slot} 与 {potionId} 不一致。");
        SolverSettings.ApplyForTesting(SolverSettings.ApplyPotionDirective(
            SolverSettings.Current,
            slot,
            potionId,
            directive));
    }

    private static void ReconcilePersistedPotionDirectives(CombatState state)
    {
        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("当前战斗找不到本地玩家。");
        SolverSettingsData settings = SolverSettings.Current;
        PersistedPotionDirective[] retained = settings.PotionDirectives
            .Where(directive =>
            {
                PotionModel? potion = GetPotionAtExistingSlot(player, directive.Slot);
                return potion != null
                    && string.Equals(potion.Id.Entry, directive.PotionId, StringComparison.Ordinal);
            })
            .ToArray();
        if (retained.Length == settings.PotionDirectives.Length)
            return;
        SolverSettings.Update(settings with { PotionDirectives = retained });
        Entry.Logger.Info(
            $"[CombatSolver/Test] POTION_DIRECTIVES_RECONCILED " +
            $"previous={settings.PotionDirectives.Length} current={retained.Length}");
    }

    private static PotionModel? GetPotionAtExistingSlot(Player player, int slot)
        => slot >= 0 && slot < player.PotionSlots.Count
            ? player.PotionSlots[slot]
            : null;

    internal static SolverTheftPolicy? ResolveTheftPolicy(CombatState state)
        => TheftEncounterStrategy.IsApplicable(state)
            ? _combat.TheftPolicy ?? SolverTheftPolicy.PreserveResources
            : null;

    internal static void SetTheftPolicyForTesting(CombatState state, SolverTheftPolicy policy)
    {
        AssertMainThread();
        if (!UnattendedTestRunner.IsActive)
            throw new InvalidOperationException("偷窃策略测试覆盖只能在无人测试中使用。");
        if (!TheftEncounterStrategy.IsApplicable(state))
            throw new InvalidOperationException("测试战斗不是偷窃资源战斗。");
        _combat.TheftPolicy = policy;
        Entry.Logger.Info($"[CombatSolver/Test] THEFT_POLICY_TEST_OVERRIDE policy={policy}");
        SolverOverlay.RefreshControls();
    }

    public static void Reset(string reason = "unspecified")
    {
        AssertMainThread();
        ResetCore(reason);
    }

    private static void ResetCore(string reason)
    {
        int lifecycleGeneration = Interlocked.Increment(ref _combatLifecycleGeneration);
        CancelMultiplayerDebouncedSearch();
        bool deferredSearchCanceled = _deferredSearchCts != null;
        CancelDeferredSearch();
        Task deferredSearchRelease = DrainDeferredSearchReleases();
        Task combatDeferredRelease = CancelAndDrainCombatDeferredOperations();
        bool searchCanceled = deferredSearchCanceled
            || _search != null
            || PlayerTurnSetupCoordinator.IsSearching;
        Task turnSetupRelease = PlayerTurnSetupCoordinator.Reset(reason);
        long forensicCaptureAllocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
        Task forensicCaptureRelease = CombatBugReportExporter.CompleteCombat(
            reason,
            CurrentResultForBugReport,
            DescribeReplanAudit());
        bool automaticGcLifecycleUsed = SearchGcPolicy.AutomaticGcLifecycleUsed;
        SearchGcPolicy.ReportCombatLifecycleAllocation(
            Math.Max(
                0,
                GC.GetTotalAllocatedBytes(precise: false) - forensicCaptureAllocatedAtStart),
            "combat_forensic_capture",
            automaticGcLifecycleUsed);
        SearchGcPolicy.CombatLifecyclePressure lifecyclePressure =
            SearchGcPolicy.DetachCombatLifecyclePressure(reason);
        bool hadState = _search != null
            || _deployment != null
            || _combat.State != null
            || _combat.LatestResult != null
            || SolverOverlay.IsVisible;
        if (_combat.SearchesStarted > 0 || _combat.ContinuationsReused > 0)
            Entry.Logger.Info($"[CombatSolver/Test] REPLAN_SUMMARY reason={reason} {DescribeReplanCounts()}");
        _lastBugReportClassification = CaptureBugReportClassification();
        _lastManualProjectionComparison = _combat.LastManualProjectionComparison;
        CancelSearch();
        Task searchReferenceRelease = DrainSearchReferenceReleases();
        if (searchCanceled)
            SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Canceled);
        CancelDeployment();
        Task deploymentReferenceRelease = DrainDeploymentReferenceReleases();
        _combat = new SolverCombatSession();
        MultiplayerClientProbe.Reset();
        _multiplayerInertSessionObserved = false;
        LastFullAutoStoppedForWorseRecalculationForTesting = false;
        LastFullAutoStoppedAtLiveRiskForTesting = false;
        LastSearchFailureForTesting = null;
        BattleDamageTracker.Reset();
        SolverOverlay.Hide();
        bool unattendedRequestActive = UnattendedAsyncActivityTracker.IsRequestActive;
        Task regionExit = unattendedRequestActive
            ? Task.CompletedTask
            : SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync(reason);
        // The unattended protocol performs one reclaim only after the game and every tracked
        // callback are quiescent. Starting a fire-and-forget reclaim here would make the 0.18
        // serialized reclaim chain request a second collection when the protocol joins it.
        Task referenceRelease = Task.WhenAll(
            searchReferenceRelease,
            deploymentReferenceRelease,
            deferredSearchRelease,
            combatDeferredRelease,
            turnSetupRelease,
            forensicCaptureRelease,
            regionExit);
        LastCombatReferenceReleaseForTesting = referenceRelease;
        if (unattendedRequestActive)
        {
            TaskHelper.RunSafely(UnattendedAsyncActivityTracker.Track(referenceRelease));
        }
        else if (automaticGcLifecycleUsed
                 && (hadState
                     || searchCanceled
                     || !referenceRelease.IsCompletedSuccessfully
                     || lifecyclePressure.AllocatedBytes > 0))
        {
            Stopwatch referenceReleaseStopwatch = Stopwatch.StartNew();
            TaskHelper.RunSafely(SearchGcPolicy.ReclaimAfterReferenceReleaseAsync(
                reason,
                lifecyclePressure.RequiresCollection,
                includeCombatLifecyclePressure: false,
                referenceRelease,
                () => LogCombatReferenceBarrier(
                    reason,
                    lifecycleGeneration,
                    lifecyclePressure,
                    referenceReleaseStopwatch)));
        }
        if (hadState)
            Entry.Logger.Info($"[CombatSolver/Test] RESET reason={reason}");
    }

    private static void LogCombatReferenceBarrier(
        string reason,
        int lifecycleGeneration,
        SearchGcPolicy.CombatLifecyclePressure lifecyclePressure,
        Stopwatch stopwatch)
    {
        int currentLifecycleGeneration = Volatile.Read(ref _combatLifecycleGeneration);
        Entry.Logger.Info(
            $"[CombatSolver/Test] HEAP_REFERENCE_BARRIER reason={reason} " +
            $"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1} " +
            $"lifecycle_epoch={lifecycleGeneration} " +
            $"current_lifecycle_epoch={currentLifecycleGeneration} " +
            $"crossed_combat={(currentLifecycleGeneration != lifecycleGeneration).ToString().ToLowerInvariant()} " +
            $"lifecycle_allocated_bytes={lifecyclePressure.AllocatedBytes} " +
            $"lifecycle_requires_gen2={lifecyclePressure.RequiresCollection.ToString().ToLowerInvariant()} " +
            $"managed_live_bytes={GC.GetTotalMemory(forceFullCollection: false)}");
    }

    internal static void SimulateDeploymentCompletionForTesting(NGame host, CombatState state)
    {
        AssertMainThread();
        if (!UnattendedTestRunner.IsActive
            || !ReferenceEquals(_combat.State, state)
            || _combat.LatestResult is not { } result
            || _combat.LatestStamp != LiveCombatStamp.Capture(state))
        {
            throw new InvalidOperationException("无人测试无法模拟消费当前就绪路线。");
        }
        int actionCount = result.BestNode.Actions.Count(action =>
            action.Turn == result.StartTurnNumber && action.IsExecutable);
        _combat.LatestResult = null;
        _combat.LatestStamp = null;
        SolverOverlay.ShowDeploymentComplete(
            host,
            result.StartTurnNumber,
            actionCount,
            endedTurn: true);
    }

    internal static void ReleaseUnattendedResultReferencesForTesting()
    {
        AssertMainThread();
        if (!UnattendedTestRunner.IsActive)
        {
            throw new InvalidOperationException(
                "无人测试结果引用只能在活动请求的最终清理阶段释放。");
        }
        // Executor assertions intentionally inspect the final result after combat teardown.
        // Release it once those assertions are finished and before the protocol's reuse Gen2;
        // IntentForecast can otherwise retain live creatures and move states from the old fight.
        LastCompletedResultForTesting = null;
        LastTurnSetupResultForTesting = null;
        LastSearchFailureForTesting = null;
    }

    public static void MonitorCombatPresence()
    {
        AssertMainThread();
        CombatState? current = CombatManager.Instance.DebugOnlyGetState();
        if (!CombatManager.Instance.IsInProgress || current == null)
        {
            if (_combat.State != null || SolverOverlay.IsVisible)
                Reset("combat_inactive");
            return;
        }

        SolverSessionCapabilitySet capabilities = SolverSessionCapabilities.Capture(current);
        bool multiplayerWorldChanged = capabilities.IsMultiplayer
            && MultiplayerClientProbe.Observe(current, "main_thread_monitor");
        bool enteredMultiplayerSession = capabilities.IsMultiplayer && !_multiplayerInertSessionObserved;
        if (capabilities.IsMultiplayer)
        {
            _multiplayerInertSessionObserved = true;
            if (enteredMultiplayerSession || multiplayerWorldChanged)
            {
                if (multiplayerWorldChanged)
                {
                    if (capabilities.Kind == SolverSessionKind.MultiplayerAdvisor)
                    {
                        Entry.Logger.Info(
                            $"[CombatSolver/MultiplayerAdvisor] MP_ADVISOR_WORLD_CHANGED " +
                            $"world_version={MultiplayerWorldTracker.WorldVersion} " +
                            $"reason={MultiplayerWorldTracker.LastReason}");
                    }
                    else if (capabilities.Kind == SolverSessionKind.MultiplayerSafeExecute)
                    {
                        Entry.Logger.Info(
                            $"[CombatSolver/MultiplayerSafeExecute] MP2B_WORLD_CHANGED " +
                            $"world_version={MultiplayerWorldTracker.WorldVersion} " +
                            $"reason={MultiplayerWorldTracker.LastReason} " +
                            $"session_state={_deployment?.SafeExecutionSession?.State.ToString() ?? "-"}");
                    }
                }
                if (enteredMultiplayerSession)
                {
                    if (capabilities.Kind == SolverSessionKind.MultiplayerSafeExecute)
                    {
                        bool labCapability = SolverSessionCapabilities.IsMultiplayerSafeExecuteLabOptedIn;
                        string capabilityMarker = labCapability ? "LAB_CAPABILITY" : "FORMAL_CAPABILITY";
                        string capabilityScope = labCapability ? "owned_client_instance" : "explicit_opt_in";
                        Entry.Logger.Info(
                            $"[CombatSolver/MultiplayerSafeExecute] {capabilityMarker} " +
                            $"enabled=true scope={capabilityScope} " +
                            $"max_actions={MultiplayerSafeExecutePolicy.MaxActionsPerDeployment} " +
                            $"automatic_end_turn={capabilities.CanEndTurnAutomatically.ToString().ToLowerInvariant()} " +
                            "custom_network_api=false");
                        Entry.Logger.Info(
                            $"[CombatSolver/MultiplayerSafeExecute] MP2B_CAPABILITY " +
                            $"enabled=true max_actions={MultiplayerSafeExecutePolicy.MaxActionsPerDeployment} " +
                            $"attribution=revalidation automatic_end_turn={capabilities.CanEndTurnAutomatically.ToString().ToLowerInvariant()} " +
                            "custom_network_api=false");
                    }
                    Entry.Logger.Info(
                        "[CombatSolver/MultiplayerProbe] CAPABILITY_BOUNDARY " +
                        "entered=true search_cancel=true deployment_cancel=true turn_setup_reset=true");
                }
                InvalidateMultiplayerSearch(current);
            }
        }
        else
        {
            _multiplayerInertSessionObserved = false;
        }

        if (!capabilities.CanSearch)
            return;

        if (_combat.State != null && !ReferenceEquals(current, _combat.State))
        {
            BeginCombat(current);
        }
        BattleDamageTracker.Observe(current);
        // SL may replace the combat after TurnStarted. Reattach at the playable boundary.
        if (!SolverOverlay.IsVisible && !IsSearching && !IsDeploying
            && !PendingCombatDeferredOperations.Any(task => !task.IsCompleted)
            && !PlayerTurnSetupCoordinator.IsManaging(current)
            && (current.Players.Count == 1
                || SolverSessionCapabilities.Capture(current).IsMultiplayer)
            && LocalContext.GetMe(current)?.PlayerCombatState?.Phase == PlayerTurnPhase.Play
            && NGame.Instance is { } host)
        {
            _combat.State = current;
            if (_solverDisabled)
                SolverOverlay.ShowDisabled(host);
            else if (_combat.AutomaticSearchPaused)
                SolverOverlay.ShowSearchStopped(host);
            else if (!AutomaticCalculationEnabled || !UnattendedTestRunner.AutomaticTurnSearchEnabled)
                SolverOverlay.ShowManualCalculationReady(host, HasCalculatedThisCombat);
            else if (!capabilities.IsMultiplayer && CanSolve(current, out _))
                RequestSearch(host, current, SearchReason.AutoTurnStart);
        }

        if (capabilities.IsMultiplayer)
            TryScheduleMultiplayerSearch(host: NGame.Instance, current);
    }

    private static void InvalidateMultiplayerSearch(CombatState state)
    {
        CancelMultiplayerDebouncedSearch();
        CancelDeferredSearch();
        CancelSearch();
        SolverSessionCapabilitySet capabilities = SolverSessionCapabilities.Capture(state);
        MultiplayerSafeExecutionState? safeExecutionState = _deployment?.SafeExecutionSession?.State;
        SolverDeploymentSession? remoteAbortDeployment = null;
        int remoteAbortCompletedActions = 0;
        bool preserveExpectedSafeDeployment =
            SolverSessionCapabilities.Capture(state).Kind == SolverSessionKind.MultiplayerSafeExecute
            && safeExecutionState is MultiplayerSafeExecutionState.Executing
                or MultiplayerSafeExecutionState.AwaitingWorldUpdate
                or MultiplayerSafeExecutionState.Revalidating
                or MultiplayerSafeExecutionState.EndTurnExecuting;
        bool preservePendingContinuation = MultiplayerLocalCrossTurnContracts.CanPreserveFutureRoute(
            capabilities.CanCrossTurnReuse,
            awaitingContinuation: preserveExpectedSafeDeployment
                || _combat.AwaitingMultiplayerContinuation,
            _combat.ContinuationSource?.Continuations.Count ?? 0,
            _combat.ContinuationSource?.MultiplayerScope ?? MultiplayerSearchResultScope.CurrentTurnOnly);
        if (!preserveExpectedSafeDeployment)
        {
            if (safeExecutionState == MultiplayerSafeExecutionState.Authorized
                && _deployment?.SafeExecutionSession is { } safeSession)
            {
                remoteAbortDeployment = _deployment;
                remoteAbortCompletedActions = safeSession.CompletedActions;
                safeSession.Abort("remote_or_unknown_change");
                Entry.Logger.Info(
                    $"[CombatSolver/MultiplayerSafeExecute] MP2B_REMOTE_DELTA_ABORT " +
                    $"request_id={safeSession.RequestId} " +
                    $"turn={_deployment.StartTurnNumber} " +
                    $"completed_actions={safeSession.CompletedActions} " +
                    "reason=remote_or_unknown_change " +
                    $"last_accepted_world_version={safeSession.LastAcceptedWorldVersion}");
            }
            CancelDeployment();
            _combat.MultiplayerSafeExecuteDeploymentRequested = false;
        }
        if (remoteAbortDeployment is { } abortedDeployment
            && NGame.Instance is { } host)
        {
            SolverOverlay.ShowDeploymentComplete(
                host,
                abortedDeployment.StartTurnNumber,
                remoteAbortCompletedActions,
                endedTurn: false,
                completionMessage: "检测到多人状态变化，已停止后续执行并重新计算。");
        }
        Task turnSetupRelease = PlayerTurnSetupCoordinator.Reset("multiplayer_capability");
        PendingCombatDeferredOperations.RemoveAll(static task => task.IsCompleted);
        if (!turnSetupRelease.IsCompleted)
            PendingCombatDeferredOperations.Add(turnSetupRelease);
        _combat.FullAutoEnabled = false;
        _combat.LatestResult = null;
        _combat.LatestStamp = null;
        if (!preservePendingContinuation)
            _combat.ContinuationSource = null;
        _combat.PendingCompleteProjectionBaseline = null;
        _combat.PendingManualProjectionBaseline = null;
        InvalidateRenderedRouteAdoptionSeed();
        SolverOverlay.RefreshControls();
        string invalidationPrefix = SolverSessionCapabilities.Capture(state).Kind
            == SolverSessionKind.MultiplayerSafeExecute
            ? "[CombatSolver/MultiplayerSafeExecute] MP2B_WORLD_INVALIDATED"
            : "[CombatSolver/MultiplayerAdvisor] WORLD_INVALIDATED";
        Entry.Logger.Info(
            $"{invalidationPrefix} " +
            $"world_version={MultiplayerWorldTracker.WorldVersion} " +
            $"reason={MultiplayerWorldTracker.LastReason} " +
            $"turn={LocalContext.GetMe(state)?.PlayerCombatState?.TurnNumber ?? 0} " +
            $"continuation_preserved={preservePendingContinuation.ToString().ToLowerInvariant()} " +
            $"route_identity={_combat.ContinuationSource?.RouteIdentity ?? "-"}");
    }

    private static void TryScheduleMultiplayerSearch(NGame? host, CombatState state)
    {
        int currentTurn = LocalContext.GetMe(state)?.PlayerCombatState?.TurnNumber ?? 0;
        if (_combat.AwaitingMultiplayerContinuation
            && _combat.ContinuationSource is { } pendingRoute
            && !pendingRoute.Continuations.Any(item => item.StartTurnNumber == currentTurn))
        {
            // Remote turns may advance WorldVersion while the local player is still
            // waiting. Hold the immutable future route until the next local turn;
            // continuation validation then decides reuse versus fresh search.
            return;
        }
        if (host == null
            || _search != null
            || _deployment != null
            || _deferredSearchCts != null
            || PendingCombatDeferredOperations.Any(task => !task.IsCompleted)
            || !AutomaticCalculationEnabled
            || !UnattendedTestRunner.AutomaticTurnSearchEnabled
            || _combat.AutomaticSearchPaused
            || !CanSolve(state, out _)
            || !MultiplayerWorldTracker.TryTakeStable(out long worldVersion))
        {
            return;
        }

        CancelMultiplayerDebouncedSearch();
        CancellationTokenSource debounceCancellation = new();
        _multiplayerDebounceCts = debounceCancellation;
        int requestId = ++_multiplayerDebounceId;
        Entry.Logger.Info(
            $"[CombatSolver/MultiplayerAdvisor] SEARCH_DEBOUNCED " +
            $"world_version={worldVersion} request_id={requestId} " +
            $"delay_ms={MultiplayerWorldTracker.DefaultDebounceMilliseconds}");
        Task operation = StartCombatDeferredOperation(combatToken =>
            RunMultiplayerDebouncedSearchAsync(
                host,
                state,
                worldVersion,
                requestId,
                debounceCancellation,
                combatToken));
        if (UnattendedAsyncActivityTracker.IsRequestActive)
            operation = UnattendedAsyncActivityTracker.Track(operation);
        TaskHelper.RunSafely(operation);
    }

    private static async Task RunMultiplayerDebouncedSearchAsync(
        NGame host,
        CombatState state,
        long worldVersion,
        int requestId,
        CancellationTokenSource debounceCancellation,
        CancellationToken combatToken)
    {
        CancellationToken token = debounceCancellation.Token;
        try
        {
            ActionExecutor actionExecutor = RunManager.Instance.ActionExecutor;
            Task nativeActionBarrier = actionExecutor.CurrentlyRunningAction is { } runningAction
                ? Task.WhenAll(actionExecutor.FinishedExecutingActions(), runningAction.CompletionTask)
                : actionExecutor.FinishedExecutingActions();
            await nativeActionBarrier.WaitAsync(token);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            token.ThrowIfCancellationRequested();
            combatToken.ThrowIfCancellationRequested();

            if (requestId != _multiplayerDebounceId
                || !ReferenceEquals(_multiplayerDebounceCts, debounceCancellation)
                || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state)
                || MultiplayerWorldTracker.WorldVersion != worldVersion
                || !CanSolve(state, out _)
                || !AutomaticCalculationEnabled
                || !UnattendedTestRunner.AutomaticTurnSearchEnabled
                || _combat.AutomaticSearchPaused)
            {
                return;
            }

            Entry.Logger.Info(
                $"[CombatSolver/MultiplayerAdvisor] SEARCH_DEBOUNCED_START " +
                $"world_version={worldVersion} request_id={requestId}");
            RequestSearch(host, state, SearchReason.AutoTurnStart);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || combatToken.IsCancellationRequested)
        {
            Entry.Logger.Info(
                $"[CombatSolver/MultiplayerAdvisor] SEARCH_DEBOUNCED_CANCEL " +
                $"world_version={worldVersion} request_id={requestId}");
        }
        finally
        {
            if (ReferenceEquals(_multiplayerDebounceCts, debounceCancellation))
                _multiplayerDebounceCts = null;
            debounceCancellation.Dispose();
        }
    }

    private static void CancelMultiplayerDebouncedSearch()
    {
        CancellationTokenSource? cancellation = _multiplayerDebounceCts;
        _multiplayerDebounceCts = null;
        if (cancellation == null)
            return;
        _multiplayerDebounceId++;
        cancellation.Cancel();
    }

    public static void RefreshSearchProgress()
    {
        AssertMainThread();
        if (_search is not { } search || !SolverOverlay.IsVisible
            || !search.Interaction.TryCreateDisplayProgress(
                System.Environment.TickCount64,
                out SolverProgress progress))
        {
            return;
        }
        SolverOverlaySnapshot? preview = progress.SpeculativeRoutePreview is { } speculative
            ? SolverOverlaySnapshot.CaptureSpeculativeRoute(speculative)
            : progress.CurrentTurnPreview is { } currentTurn
                ? SolverOverlaySnapshot.CaptureCurrentTurn(currentTurn)
                : null;
        search.Interaction.RenderedRouteAdoptionSeed = progress.SpeculativeRoutePreview == null
            ? null
            : progress.RouteAdoptionSeed;
        SolverOverlay.ShowProgress(
            progress,
            search.DeployWhenReady,
            _combat.ReviewedWorldlinesTotal,
            preview);
        SolverOverlay.RefreshControls();
    }

    public static void ObserveMainThreadFrameGap(TimeSpan gap)
    {
        AssertMainThread();
        double milliseconds = gap.TotalMilliseconds;
        SolverSearchSession? search = _search;
        bool frameRecoveryAllowed = !string.Equals(
                DisplayServer.GetName(),
                "headless",
                StringComparison.OrdinalIgnoreCase)
            && DisplayServer.WindowIsFocused();
        FramePressureSignal.ObserveFrame(
            milliseconds,
            searchActive: IsSearching,
            frameRecoveryAllowed);
        if (search == null)
            return;
        search.ObserveFrame(milliseconds);
        if (milliseconds >= 100d)
        {
            SolverProgress? progress = Volatile.Read(ref search.Interaction.Progress);
            Entry.Logger.Info(
                $"[CombatSolver/Test] MAIN_THREAD_LONG_FRAME gap_ms={milliseconds:F1} " +
                $"frame={search.FrameCount} expanded={progress?.ExpandedNodes ?? -1} " +
                $"process_allocated_delta={GC.GetTotalAllocatedBytes(precise: false) - search.ProcessAllocatedBytesAtStart} " +
                $"gc_pause_delta_ms={(GC.GetTotalPauseDuration() - search.ProcessGcPauseAtStart).TotalMilliseconds:F1}");
        }
    }
    internal static int SearchesStartedForShowcase => _combat.SearchesStarted;

    internal static void AcceptShowcaseRoute(NGame host, CombatState state, SolverResult result)
    {
        AssertMainThread();
        CancelSearch();
        CancelDeployment();
        _combat.State = state;
        _combat.ShowcaseMode = true;
        _combat.LatestResult = result;
        _combat.LatestStamp = LiveCombatStamp.Capture(state);
        _combat.ContinuationSource = result;
        _combat.AutomaticSearchPaused = false;
        _combat.FullAutoEnabled = false;
        BattleDamageTracker.RegisterPlan(state, result);
        SolverOverlaySnapshot snapshot = SolverOverlaySnapshot.CaptureWithReviewedWorldlines(result, false, 0) with
        {
            StatusText = "预计算录像路线 · 未启动本地搜索",
            StatusTone = SolverOverlayTone.Success,
        };
        SolverOverlay.ShowResult(host, snapshot);
        Entry.Logger.Info($"[CombatSolver/Showcase] ROUTE_ACCEPTED turn={result.StartTurnNumber} end_turn={result.CombatEndedTurn} local_searches=0");
    }

    private static void StopShowcaseRoute(NGame host, string message)
    {
        _combat.FullAutoEnabled = false;
        _combat.AutomaticSearchPaused = true;
        _combat.LatestResult = null;
        _combat.LatestStamp = null;
        _combat.ContinuationSource = null;
        SolverOverlay.Show(host, $"[b]录像路线已停止[/b]\n{message}\n此临时对局不会自动重新计算。");
        Entry.Logger.Warn("[CombatSolver/Showcase] ROUTE_MISMATCH stopped=true replan=false");
    }

    private static Task CancelAndDrainCombatDeferredOperations()
    {
        CancellationTokenSource cancellation = _combatDeferredOperationCancellation;
        _combatDeferredOperationCancellation = new CancellationTokenSource();
        cancellation.Cancel();
        if (PendingCombatDeferredOperations.Count == 0)
        {
            cancellation.Dispose();
            return Task.CompletedTask;
        }
        Task[] operations = PendingCombatDeferredOperations.ToArray();
        PendingCombatDeferredOperations.Clear();
        return ReleaseCombatDeferredOperationsAsync(operations, cancellation);
    }

    private static async Task ReleaseCombatDeferredOperationsAsync(
        Task[] operations,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.WhenAll(operations)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static string FormatTurnSetupFailure(
        Exception exception,
        bool parallelSearchWasEnabled)
        => exception.GetBaseException() is IncompatibleGameplayModException incompatible
           ? FormatIncompatibleModFailure(incompatible)
           : $"[color={SolverUiTokens.Palette.DangerHex}][b]{SolverText.Get("回合准备选牌失败")}[/b]\n" +
           $"{EscapeRichText(exception.GetBaseException().Message)}[/color]\n" +
           SolverUiTokens.SearchFailureInstructionRichText(parallelSearchWasEnabled);

    private static CombatBugReportClassificationSnapshot CaptureBugReportClassification()
        => new(
            _combat.ReplanCounts.GetValueOrDefault(ReplanCause.StateMismatch),
            _combat.ReplanCounts.GetValueOrDefault(ReplanCause.DeploymentDrift),
            _combat.ReplanCounts.GetValueOrDefault(ReplanCause.ContinuationMissing),
            _combat.ReplanCounts.GetValueOrDefault(ReplanCause.PlanExhausted),
            _combat.ReplanCounts.GetValueOrDefault(ReplanCause.ManualDivergence),
            _combat.BugReportIssues.Snapshot());

    internal static CombatBugReportClassificationSnapshot CaptureBugReportClassificationForExport()
        => CombatManager.Instance.IsInProgress ? CaptureBugReportClassification()
            : _lastBugReportClassification ?? CaptureBugReportClassification();
    internal static CombatBugReportClassificationSnapshot CaptureBugReportClassificationForRuntimeEvidence()
        => CaptureBugReportClassification();
    internal static ManualProjectionComparison? ManualProjectionComparisonForExport
        => CombatManager.Instance.IsInProgress ? _combat.LastManualProjectionComparison : _lastManualProjectionComparison;

    private static bool CanSolve(CombatState state, out string rejection)
    {
        Player? player = LocalContext.GetMe(state);
        SolverSessionCapabilitySet capabilities = SolverSessionCapabilities.Capture(state);
        if (_solverDisabled)
            rejection = "求解器已在设置中禁用。";
        else if (!CombatManager.Instance.IsInProgress)
            rejection = "当前没有进行中的战斗。";
        else if (state.Players.Count != 1 && !capabilities.IsMultiplayer)
            rejection = "第一版只支持单人战斗。";
        else if (!capabilities.CanSearch)
            rejection = capabilities.SearchRejection;
        else if (state.CurrentSide != CombatSide.Player || player?.PlayerCombatState?.Phase != PlayerTurnPhase.Play)
            rejection = "当前不是玩家出牌阶段。";
        else if (CombatManager.Instance.PlayerActionsDisabled)
            rejection = "玩家操作当前被游戏禁用。";
        else
        {
            rejection = string.Empty;
            return true;
        }
        return false;
    }

    private static bool IsSamePlayableTurn(CombatState state, int turn)
    {
        Player? player = LocalContext.GetMe(state);
        return ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state)
            && state.CurrentSide == CombatSide.Player
            && player?.PlayerCombatState?.TurnNumber == turn
            && player.PlayerCombatState.Phase == PlayerTurnPhase.Play;
    }

    private static void AssertMainThread()
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("CombatSolver 的主线程控制器被后台线程调用。");
    }
}
