using System;
using System.Threading;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Settings;

namespace CombatSolver;

// Automation and user-control policy are kept in a partial file so the façade's
// state ownership and hot deployment paths remain unchanged.
internal static partial class SolverController
{
    public static void SetFullAuto(NGame host, CombatState state, bool enabled)
    {
        AssertMainThread();
        SolverDispatcher.Ensure(host);
        if (!enabled)
        {
            _combat.FullAutoEnabled = false;
            Entry.Logger.Info("[CombatSolver/Test] FULL_AUTO enabled=false reason=user");
            SolverOverlay.RefreshControls();
            return;
        }

        SolverSessionCapabilitySet capabilities = SolverSessionCapabilities.Capture(state);
        if (!capabilities.CanFullAuto)
        {
            Entry.Logger.Info($"[CombatSolver/MultiplayerProbe] FULL_AUTO_REJECT reason={capabilities.DeploymentRejection}");
            SolverOverlay.RefreshControls();
            return;
        }

        if (_combat.AutomaticSearchPaused)
        {
            _combat.AutomaticSearchPaused = false;
            _combat.AutomaticSearchPausedTurn = null;
            Entry.Logger.Info("[CombatSolver/Test] AUTOMATIC_SEARCH_RESUMED reason=explicit_full_auto");
        }

        if (PlayerTurnSetupCoordinator.CanTakeOverTurnSetup(state))
        {
            _combat.FullAutoEnabled = true;
            Entry.Logger.Info(
                $"[CombatSolver/Test] FULL_AUTO enabled=true reason=turn_setup_takeover " +
                $"stop_on_combat_end={_stopFullAutoOnCombatEnd} " +
                $"stop_on_death_turn={_stopFullAutoOnDeathTurn} " +
                $"stop_on_worse_recalculation={_stopFullAutoOnWorseRecalculation}");
            SolverOverlay.RefreshControls();
            if (!PlayerTurnSetupCoordinator.TryContinuePlannedChoice(
                    host,
                    state,
                    deployAfterSetup: false))
            {
                InvalidOperationException failure = new("回合开始选牌页在全自动接管时失去活动状态。");
                _ = RecordTurnSetupFailure(
                    state,
                    Volatile.Read(ref _combatLifecycleGeneration),
                    failure);
                throw failure;
            }
            return;
        }

        if (!CanSolve(state, out string rejection))
        {
            SolverOverlay.Show(host, $"[b]战斗路线求解器[/b]\n{rejection}");
            Entry.Logger.Info($"[CombatSolver/Test] FULL_AUTO_REJECT reason={rejection}");
            return;
        }

        int currentTurn = LocalContext.GetMe(state)?.PlayerCombatState?.TurnNumber ?? 0;
        if (currentTurn > 1 && _combat.LastSolverDeployedTurn != currentTurn - 1)
            MarkManualControlObserved("full_auto_after_manual_turn");

        _combat.FullAutoEnabled = true;
        Entry.Logger.Info(
            $"[CombatSolver/Test] FULL_AUTO enabled=true stop_on_combat_end={_stopFullAutoOnCombatEnd} " +
            $"stop_on_death_turn={_stopFullAutoOnDeathTurn} " +
            $"stop_on_worse_recalculation={_stopFullAutoOnWorseRecalculation}");
        SolverOverlay.RefreshControls();

        LiveCombatStamp current = LiveCombatStamp.Capture(state);
        if (IsLatestResultDeploymentCompatible(
                state,
                SolverSessionCapabilities.Capture(state),
                current))
        {
            if (_combat.LatestStamp != current)
                RefreshLatestDeploymentStamp(state, current);
            StartFullAutoDeployment(host, state, _combat.LatestResult!);
            return;
        }
        if (_search == null && _deployment == null)
            RequestSearch(host, state, SearchReason.FullAuto);
    }

    public static void SetMultiplayerSafeAuto(NGame host, CombatState state, bool enabled)
    {
        AssertMainThread();
        SolverDispatcher.Ensure(host);
        SolverSessionCapabilitySet capabilities = SolverSessionCapabilities.Capture(state);

        if (!enabled)
        {
            _combat.MultiplayerSafeAutoEnabled = false;
            _combat.MultiplayerSafeExecuteDeploymentRequested = false;
            Entry.Logger.Info("[CombatSolver/MultiplayerSafeExecute] MP_SAFE_AUTO enabled=false reason=user");
            SolverOverlay.RefreshControls();
            return;
        }

        if (capabilities.Kind != SolverSessionKind.MultiplayerSafeExecute)
        {
            _combat.MultiplayerSafeAutoEnabled = false;
            _combat.MultiplayerSafeExecuteDeploymentRequested = false;
            Entry.Logger.Info(
                $"[CombatSolver/MultiplayerSafeExecute] MP_SAFE_AUTO_REJECT kind={capabilities.Kind}");
            SolverOverlay.RefreshControls();
            return;
        }

        _combat.FullAutoEnabled = false;
        _combat.MultiplayerSafeAutoEnabled = true;
        if (_combat.AutomaticSearchPaused)
        {
            _combat.AutomaticSearchPaused = false;
            _combat.AutomaticSearchPausedTurn = null;
        }
        Entry.Logger.Info(
            $"[CombatSolver/MultiplayerSafeExecute] MP_SAFE_AUTO enabled=true " +
            $"world_version={MultiplayerWorldTracker.WorldVersion}");
        SolverOverlay.RefreshControls();

        if (_deployment != null)
            return;

        if (CanSolve(state, out _))
        {
            LiveCombatStamp current = LiveCombatStamp.Capture(state);
            if (IsLatestResultDeploymentCompatible(state, capabilities, current))
            {
                if (_combat.LatestStamp != current)
                    RefreshLatestDeploymentStamp(state, current);
                Entry.Logger.Info(
                    $"[CombatSolver/MultiplayerSafeExecute] MP_SAFE_AUTO_ARMED " +
                    $"source=existing_result turn={LocalContext.GetMe(state)?.PlayerCombatState?.TurnNumber ?? 0} " +
                    $"world_version={MultiplayerWorldTracker.WorldVersion}");
                StartDeployment(host, state, _combat.LatestResult!);
                return;
            }
        }

        if (_search == null)
            TryScheduleMultiplayerSearch(host, state);
    }

    public static bool PrepareAutomaticSearchForTurn(NGame host, CombatState state)
    {
        AssertMainThread();
        if (CombatShowcaseRuntime.ImportInProgress)
            return false;
        if (_combat.ShowcaseMode)
            return !_combat.AutomaticSearchPaused;
        int turn = LocalContext.GetMe(state)?.PlayerCombatState?.TurnNumber ?? -1;
        if (_combat.AutomaticSearchPaused
            && _combat.AutomaticSearchPausedTurn is { } stoppedTurn
            && stoppedTurn != turn)
        {
            _combat.AutomaticSearchPaused = false;
            _combat.AutomaticSearchPausedTurn = null;
            Entry.Logger.Info(
                $"[CombatSolver/Test] AUTOMATIC_SEARCH_STOP_CLEARED stopped_turn={stoppedTurn} current_turn={turn}");
        }
        if (!AutomaticCalculationEnabled && !FullAutoEnabled)
        {
            SolverOverlay.ShowManualCalculationReady(host, HasCalculatedThisCombat);
            return false;
        }
        if (_combat.AutomaticSearchPaused)
        {
            SolverOverlay.ShowSearchStopped(host);
            return false;
        }
        return true;
    }

    public static void SetAutomaticCalculationEnabled(bool enabled, bool persist = true)
    {
        AssertMainThread();
        if (persist)
        {
            SolverSettings.Update(SolverSettings.Current with
            {
                AutomaticCalculationEnabled = enabled,
            });
        }
        if (!enabled)
            _combat.FullAutoEnabled = false;
        Entry.Logger.Info(
            $"[CombatSolver/Test] AUTOMATIC_CALCULATION enabled={enabled.ToString().ToLowerInvariant()}");
        SolverOverlay.RefreshControls();

        NGame? host = NGame.Instance;
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (host == null || state == null || !CombatManager.Instance.IsInProgress)
            return;
        if (!enabled)
        {
            if (!IsSearching)
                SolverOverlay.ShowManualCalculationReady(host, HasCalculatedThisCombat);
            return;
        }
        if (!_combat.AutomaticSearchPaused
            && !IsSearching
            && !IsDeploying
            && UnattendedTestRunner.AutomaticTurnSearchEnabled
            && CanSolve(state, out _))
        {
            if (SolverSessionCapabilities.Capture(state).IsMultiplayer)
                TryScheduleMultiplayerSearch(host, state);
            else
                RequestSearch(host, state, SearchReason.AutoTurnStart);
        }
    }

    public static void SetSolverDisabled(bool disabled, bool persist = true)
    {
        AssertMainThread();
        _solverDisabled = disabled;
        if (persist)
            SolverSettings.Update(SolverSettings.Current with { SolverDisabled = disabled });

        Entry.Logger.Info($"[CombatSolver/Test] SOLVER_DISABLED disabled={disabled}");
        if (disabled)
        {
            bool controllerSearchCanceled = _search != null || _deferredSearchCts != null;
            PlayerTurnSetupCoordinator.CancelForSolverDisabled();
            CancelDeferredSearch();
            CancelSearch();
            if (controllerSearchCanceled)
                SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Canceled);
            CancelDeployment();
            _combat.FullAutoEnabled = false;
            _combat.MultiplayerSafeAutoEnabled = false;
            _combat.MultiplayerSafeExecuteDeploymentRequested = false;
            _combat.State = null;
            _combat.LatestResult = null;
            _combat.LatestStamp = null;
            _combat.ContinuationSource = null;
            _combat.PendingCompleteProjectionBaseline = null;
            if (NGame.Instance is { } host)
                SolverOverlay.ShowDisabled(host);
            else
                SolverOverlay.RefreshControls();
            return;
        }

        SolverOverlay.RefreshControls();
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        NGame? game = NGame.Instance;
        if (game != null
            && state != null
            && UnattendedTestRunner.AutomaticTurnSearchEnabled
            && AutomaticCalculationEnabled
            && CanSolve(state, out _))
        {
            if (SolverSessionCapabilities.Capture(state).IsMultiplayer)
                TryScheduleMultiplayerSearch(game, state);
            else
                RequestSearch(game, state, SearchReason.AutoTurnStart);
        }
    }

    public static void StopSearchByUser(NGame host)
    {
        AssertMainThread();
        if (!IsSearching)
            return;

        bool controllerSearchCanceled = _search != null || _deferredSearchCts != null;
        int? generation = _search?.Generation;
        _combat.FullAutoEnabled = false;
        _combat.MultiplayerSafeAutoEnabled = false;
        _combat.MultiplayerSafeExecuteDeploymentRequested = false;
        _combat.AutomaticSearchPaused = true;
        _combat.AutomaticSearchPausedTurn = LocalContext.GetMe(
            CombatManager.Instance.DebugOnlyGetState())?.PlayerCombatState?.TurnNumber;
        CancelDeferredSearch();
        bool stoppingControllerAtCandidate = _search is { } search
            && search.Interaction.RenderedRouteAdoptionSeed is { } seed
            && search.Interaction.RequestAdoptRoute(seed, stopAfterResult: true);
        bool stoppingSetupAtCandidate = _search == null
            && PlayerTurnSetupCoordinator.TryStopSearchAtCurrentRoute();
        bool stoppingAtCandidate = stoppingControllerAtCandidate || stoppingSetupAtCandidate;
        if (!stoppingAtCandidate)
            CancelSearch();
        bool stoppedTurnSetupSearch = stoppingSetupAtCandidate
            || !stoppingAtCandidate && PlayerTurnSetupCoordinator.StopSearchByUser();
        if (!stoppingAtCandidate && (controllerSearchCanceled || stoppedTurnSetupSearch))
            SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Canceled);
        _combat.PendingCompleteProjectionBaseline = null;
        _combat.PendingManualProjectionBaseline = null;
        SolverOverlay.ShowSearchStopped(host);
        Entry.Logger.Info(
            $"[CombatSolver/Test] SEARCH_STOPPED_BY_USER generation={generation?.ToString() ?? "-"} " +
            $"turn_setup={stoppedTurnSetupSearch.ToString().ToLowerInvariant()} " +
            $"candidate_preserved={stoppingAtCandidate.ToString().ToLowerInvariant()} automatic_search_paused=true");
    }

    public static void ApplyCurrentTurn()
    {
        AssertMainThread();
        if (!CurrentSessionCapabilities.CanDeploySimpleLocalActions)
        {
            Entry.Logger.Info(
                $"[CombatSolver/MultiplayerProbe] APPLY_CURRENT_TURN_REJECT " +
                $"reason={CurrentSessionCapabilities.DeploymentRejection}");
            SolverOverlay.RefreshControls();
            return;
        }
        _combat.AutomaticSearchPaused = false;
        _combat.AutomaticSearchPausedTurn = null;
        _combat.FullAutoEnabled = false;
        _combat.MultiplayerSafeAutoEnabled = false;
        _combat.MultiplayerSafeExecuteDeploymentRequested = false;
        if (_search is not { } search)
        {
            PlayerTurnSetupCoordinator.ApplyCurrentTurn();
            return;
        }
        if (search.Interaction.CurrentTakeoverRequest != null
            || Volatile.Read(ref search.Interaction.Progress)?.CurrentTurnPreview == null)
        {
            return;
        }

        search.DeployWhenReady = true;
        if (!search.Interaction.RequestApplyCurrentTurn())
            return;
        SolverOverlay.RefreshControls();
        Entry.Logger.Info("[CombatSolver/Test] UI_ACTION action=apply_current_turn");
    }

    public static void AdoptCurrentRoute()
    {
        AssertMainThread();
        if (!CurrentSessionCapabilities.CanDeploySimpleLocalActions)
        {
            Entry.Logger.Info(
                $"[CombatSolver/MultiplayerProbe] ADOPT_ROUTE_REJECT " +
                $"reason={CurrentSessionCapabilities.DeploymentRejection}");
            SolverOverlay.RefreshControls();
            return;
        }
        _combat.AutomaticSearchPaused = false;
        _combat.AutomaticSearchPausedTurn = null;
        _combat.MultiplayerSafeAutoEnabled = false;
        _combat.MultiplayerSafeExecuteDeploymentRequested = false;
        if (_search is not { } search)
        {
            if (TryAdoptStoppedRoute())
                return;
            PlayerTurnSetupCoordinator.AdoptCurrentRoute();
            return;
        }
        SolverRouteAdoptionSeed? seed = search.Interaction.RenderedRouteAdoptionSeed;
        if (seed == null || !search.Interaction.RequestAdoptRoute(seed))
            return;
        SolverOverlay.RefreshControls();
        Entry.Logger.Info(
            $"[CombatSolver/Test] UI_ACTION action=adopt_current_route " +
            $"candidate_version={seed.CandidateVersion}");
    }

    public static void SetStopFullAutoOnCombatEnd(bool enabled, bool persist = true)
    {
        AssertMainThread();
        _stopFullAutoOnCombatEnd = enabled;
        if (persist)
            SolverSettings.Update(SolverSettings.Current with { StopFullAutoOnCombatEnd = enabled });
        Entry.Logger.Info($"[CombatSolver/Test] FULL_AUTO_COMBAT_END_STOP enabled={enabled}");
        SolverOverlay.RefreshControls();
    }

    public static void SetStopFullAutoOnDeathTurn(bool enabled, bool persist = true)
    {
        AssertMainThread();
        _stopFullAutoOnDeathTurn = enabled;
        if (persist)
            SolverSettings.Update(SolverSettings.Current with { StopFullAutoOnDeathTurn = enabled });
        Entry.Logger.Info($"[CombatSolver/Test] FULL_AUTO_DEATH_TURN_STOP enabled={enabled}");
        SolverOverlay.RefreshControls();
    }

    public static void SetStopFullAutoOnWorseRecalculation(bool enabled, bool persist = true)
    {
        AssertMainThread();
        _stopFullAutoOnWorseRecalculation = enabled;
        if (persist)
        {
            SolverSettings.Update(SolverSettings.Current with
            {
                StopFullAutoOnWorseRecalculation = enabled,
            });
        }
        Entry.Logger.Info($"[CombatSolver/Test] FULL_AUTO_WORSE_RECALCULATION_STOP enabled={enabled}");
        SolverOverlay.RefreshControls();
    }

}
