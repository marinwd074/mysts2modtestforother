namespace CombatSolver;

internal enum SearchTakeoverKind
{
    ApplyCurrentTurn,
    AdoptRoute,
}

internal sealed record SearchTakeoverRequest(
    SearchTakeoverKind Kind,
    SolverRouteAdoptionSeed? RouteAdoptionSeed = null,
    bool StopAfterResult = false,
    SolverRouteAdoptionSeed? CurrentTurnAdoptionSeed = null);

internal sealed class SolverRouteAdoptionSeed(
    int candidateVersion,
    IReadOnlyList<PlanAction> actions,
    Func<SolverResult> materialize)
{
    private readonly Lazy<SolverResult> _materialized = new(
        materialize,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public int CandidateVersion { get; } = candidateVersion;
    public IReadOnlyList<PlanAction> Actions { get; } = actions.ToArray();

    public SolverResult Materialize()
        => _materialized.Value;
}

internal sealed class SearchInteractionState
{
    private readonly object _gate = new();
    private int _acceptingTakeover = 1;
    private SearchTakeoverRequest? _takeoverRequest;
    private SolverProgress? _approvedForegroundProgress;

    public SearchProgressDisplayState ProgressDisplay { get; } = new();
    public SolverProgress? Progress;
    public SolverProgress? RenderedProgress { get; private set; }
    public SolverRouteAdoptionSeed? RenderedCurrentTurnAdoptionSeed { get; set; }
    public SolverRouteAdoptionSeed? RenderedRouteAdoptionSeed { get; set; }
    public SolverResult? StoppedResult { get; private set; }
    public LiveCombatStamp? StoppedStamp { get; private set; }

    public SearchTakeoverRequest? CurrentTakeoverRequest
        => Volatile.Read(ref _takeoverRequest);
    public bool CanAcceptTakeover
        => Volatile.Read(ref _acceptingTakeover) != 0;
    public bool IsApplyingCurrentTurn
        => CurrentTakeoverRequest?.Kind == SearchTakeoverKind.ApplyCurrentTurn;
    public bool IsAdoptingRoute
        => CurrentTakeoverRequest?.Kind == SearchTakeoverKind.AdoptRoute;
    public bool StopRequested
        => CurrentTakeoverRequest?.StopAfterResult == true;
    public SolverProgress? ApprovedForegroundProgress
        => Volatile.Read(ref _approvedForegroundProgress);

    public void PublishProgress(SolverProgress progress)
    {
        Volatile.Write(ref Progress, progress);
        if (!progress.HasApprovedForegroundRoute)
            return;

        int candidateVersion = progress.ApprovedForegroundCandidateVersion;
        while (true)
        {
            SolverProgress? current = Volatile.Read(ref _approvedForegroundProgress);
            int currentVersion = current?.ApprovedForegroundCandidateVersion ?? -1;
            if (currentVersion >= candidateVersion)
                return;
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _approvedForegroundProgress,
                        progress,
                        current),
                    current))
            {
                return;
            }
        }
    }

    public bool TryCreateDisplayProgress(long now, out SolverProgress displayProgress)
    {
        SolverProgress? liveProgress = Volatile.Read(ref Progress);
        if (liveProgress == null)
        {
            displayProgress = null!;
            return false;
        }

        SolverProgress? approved = Volatile.Read(ref _approvedForegroundProgress);
        SolverProgress progress = approved == null
            ? liveProgress
            : liveProgress with
            {
                CurrentBestResult = approved.CurrentBestResult,
                CurrentTurnPreview = approved.CurrentTurnPreview,
                SpeculativeRoutePreview = approved.SpeculativeRoutePreview,
                RouteAdoptionSeed = RouteSeedMatchesApprovedPreview(
                    liveProgress.RouteAdoptionSeed,
                    approved.SpeculativeRoutePreview)
                        ? liveProgress.RouteAdoptionSeed
                        : null,
                CurrentTurnAdoptionSeed = CurrentTurnSeedMatchesApprovedPreview(
                    liveProgress.CurrentTurnAdoptionSeed,
                    approved.CurrentTurnPreview)
                        ? liveProgress.CurrentTurnAdoptionSeed
                        : null,
                OfficialPublishedOrigin = approved.OfficialPublishedOrigin,
                OfficialPublishedEvaluationContextId =
                    approved.OfficialPublishedEvaluationContextId,
            };
        if (ReferenceEquals(progress, RenderedProgress)
            || !ProgressDisplay.TryCreate(progress, now, out displayProgress))
        {
            displayProgress = null!;
            return false;
        }
        RenderedProgress = displayProgress;
        return true;
    }

    private static bool RouteSeedMatchesApprovedPreview(
        SolverRouteAdoptionSeed? seed,
        SolverSpeculativeRoutePreview? preview)
        => seed != null
            && preview != null
            && seed.CandidateVersion == preview.CandidateVersion
            && seed.Actions.SequenceEqual(preview.Turns.SelectMany(static turn => turn.Actions));

    private static bool CurrentTurnSeedMatchesApprovedPreview(
        SolverRouteAdoptionSeed? seed,
        SolverCurrentTurnPreview? preview)
        => seed != null
            && preview != null
            && seed.CandidateVersion == preview.CandidateVersion
            && seed.Actions.SequenceEqual(preview.Actions);

    public void ResetForSearch()
    {
        lock (_gate)
        {
            Volatile.Write(ref _takeoverRequest, null);
            Volatile.Write(ref _acceptingTakeover, 1);
        }
        Progress = null;
        Volatile.Write(ref _approvedForegroundProgress, null);
        RenderedProgress = null;
        RenderedCurrentTurnAdoptionSeed = null;
        RenderedRouteAdoptionSeed = null;
        StoppedResult = null;
        StoppedStamp = null;
        ProgressDisplay.Restart(Environment.TickCount64);
    }

    public bool RequestApplyCurrentTurn(SolverRouteAdoptionSeed? renderedSeed = null)
        => RequestTakeover(new SearchTakeoverRequest(
            SearchTakeoverKind.ApplyCurrentTurn,
            CurrentTurnAdoptionSeed: renderedSeed));

    public bool RequestAdoptRoute(SolverRouteAdoptionSeed seed, bool stopAfterResult = false)
        => RequestTakeover(new SearchTakeoverRequest(
            SearchTakeoverKind.AdoptRoute,
            seed,
            stopAfterResult));

    private bool RequestTakeover(SearchTakeoverRequest request)
    {
        lock (_gate)
        {
            if (!CanAcceptTakeover || _takeoverRequest != null)
                return false;
            Volatile.Write(ref _takeoverRequest, request);
            return true;
        }
    }

    public SolverResult FinalizeWorkerResult(SolverResult result)
    {
        SearchTakeoverRequest? request;
        lock (_gate)
        {
            Volatile.Write(ref _acceptingTakeover, 0);
            request = CurrentTakeoverRequest;
        }

        if (request?.Kind == SearchTakeoverKind.AdoptRoute
            && request.RouteAdoptionSeed != null
            && result.ResultScope != SolverResultScope.RouteAdoption)
        {
            SolverResult exactDisplayed = request.RouteAdoptionSeed.Materialize();
            exactDisplayed.ResultScope = SolverResultScope.RouteAdoption;
            return exactDisplayed;
        }
        if (request?.Kind == SearchTakeoverKind.ApplyCurrentTurn
            && request.CurrentTurnAdoptionSeed != null
            && result.ResultScope != SolverResultScope.CurrentTurnAdoption)
        {
            SolverResult exactDisplayed = request.CurrentTurnAdoptionSeed.Materialize();
            exactDisplayed.ResultScope = SolverResultScope.CurrentTurnAdoption;
            return exactDisplayed;
        }
        return result;
    }

    public void PreserveStoppedResult(SolverResult result, LiveCombatStamp stamp)
    {
        StoppedResult = result;
        StoppedStamp = stamp;
    }

    public SolverResult? TakeStoppedResult(LiveCombatStamp currentStamp)
    {
        SolverResult? result = StoppedStamp == currentStamp ? StoppedResult : null;
        StoppedResult = null;
        StoppedStamp = null;
        return result;
    }

    // Worker completion and UI takeover completion are separate boundaries. Setup keeps its
    // native choice session alive after a result, so it must retire the request explicitly.
    public SearchTakeoverRequest? CompleteTakeover()
    {
        lock (_gate)
        {
            SearchTakeoverRequest? completed = _takeoverRequest;
            Volatile.Write(ref _takeoverRequest, null);
            Volatile.Write(ref _acceptingTakeover, 0);
            RenderedCurrentTurnAdoptionSeed = null;
            RenderedRouteAdoptionSeed = null;
            return completed;
        }
    }
}

internal sealed record SolverInterimResult(
    bool Won,
    int OutstandingStolenResource,
    int ProjectedBattleHpLost,
    int StrategicHpDeficit,
    int PotionStrategicCost,
    int ProjectedBattlePotionCount,
    int EnemyHp,
    double Score,
    int? CombatEndedTurn = null)
{
    public SolverTheftPolicy? TheftPolicy { get; init; }
    public bool Survives { get; init; }
    public int DeathSaveUseCount { get; init; }
    public int GrowthHpCredit { get; init; }
    public int GrowthRewardCount { get; init; }
    public bool RollingHorizonLossFirst { get; init; }
}

internal sealed record SolverFrontierTurn(
    int Turn,
    IReadOnlyList<PlanAction> Actions,
    int HpLost,
    int HpRecovered,
    int EnemyHpLost,
    int EnergyLeft,
    bool CombatEnded)
{
    public IReadOnlyList<PlanCardChoice> TurnStartChoices { get; init; } = [];
    public IReadOnlyDictionary<int, IReadOnlyList<string>> KillsAfterAction { get; init; }
        = new Dictionary<int, IReadOnlyList<string>>();

    public static IReadOnlyList<SolverFrontierTurn> FromResult(SolverResult result)
        => result.BestNode.Actions
            .Select((action, index) => (Action: action, Index: index))
            .GroupBy(item => item.Action.Turn)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var indexed = group.ToArray();
                Dictionary<int, IReadOnlyList<string>> kills = [];
                for (int localIndex = 0; localIndex < indexed.Length; localIndex++)
                {
                    if (result.KillsAfterAction.TryGetValue(
                            indexed[localIndex].Index,
                            out IReadOnlyList<string>? actionKills)
                        && actionKills.Count > 0)
                    {
                        kills[localIndex] = actionKills;
                    }
                }
                return new SolverFrontierTurn(
                    group.Key,
                    indexed.Select(item => item.Action).ToArray(),
                    result.HpLostByTurn.GetValueOrDefault(group.Key),
                    result.HpRecoveredByTurn.GetValueOrDefault(group.Key),
                    result.EnemyHpLostByTurn.GetValueOrDefault(group.Key),
                    result.EnergyLeftByTurn.GetValueOrDefault(group.Key),
                    result.CombatEndedTurn == group.Key)
                {
                    TurnStartChoices = TurnStartChoicePreviewPolicy.ChoicesForTurn(
                        group.Key,
                        result.StartTurnNumber,
                        result.WasReused ? [] : result.TurnSetupChoices,
                        result.BestNode.Actions),
                    KillsAfterAction = kills,
                };
            })
            .ToArray();
}

internal sealed record SolverCurrentTurnPreview(
    int CandidateVersion,
    int Turn,
    IReadOnlyList<PlanAction> Actions,
    int HpLost,
    int HpRecovered,
    int EnemyHpLost,
    int EnergyLeft,
    bool CombatEnded,
    IReadOnlyList<SolverFrontierTurn>? FrontierTurns = null)
{
    public IReadOnlyList<PlanCardChoice> TurnStartChoices { get; init; } = [];

    public static SolverCurrentTurnPreview FromResult(
        SolverResult result,
        int candidateVersion = 0)
        => new(
            candidateVersion,
            result.StartTurnNumber,
            result.BestNode.Actions
                .Where(action => action.Turn == result.StartTurnNumber)
                .ToArray(),
            result.HpLostByTurn.GetValueOrDefault(result.StartTurnNumber),
            result.HpRecoveredByTurn.GetValueOrDefault(result.StartTurnNumber),
            result.EnemyHpLostByTurn.GetValueOrDefault(result.StartTurnNumber),
            result.EnergyLeftByTurn.GetValueOrDefault(result.StartTurnNumber),
            result.CombatEndedTurn == result.StartTurnNumber,
            SolverFrontierTurn.FromResult(result))
        {
            TurnStartChoices = result.WasReused ? [] : result.TurnSetupChoices,
        };
}

internal sealed record SolverSpeculativeRoutePreview(
    int CandidateVersion,
    int StartTurnNumber,
    int ProjectedBattlePotionCount,
    int ProjectedBattleHpLost,
    bool CombatEnded,
    bool OnlyDeathRoutesFound,
    bool HasRisk,
    IReadOnlyList<SolverFrontierTurn> Turns)
{
    public static SolverSpeculativeRoutePreview FromResult(
        SolverResult result,
        int candidateVersion = 0)
        => new(
            candidateVersion,
            result.StartTurnNumber,
            result.ProjectedBattlePotionCount,
            result.ProjectedBattleHpLost,
            result.CombatEndedTurn.HasValue,
            result.OnlyDeathRoutesFound,
            result.Snapshot.HasRisk,
            SolverFrontierTurn.FromResult(result));
}

internal sealed record SolverProgress(
    int StartTurnNumber,
    int CurrentTurnNumber,
    int CompletedTurnLayers,
    int PlayDepth,
    int ExpandedNodes,
    long ReviewedWorldlines,
    int MaxNodes,
    int FrontierNodes,
    int EndedNodes,
    long ElapsedMilliseconds,
    string Phase,
    SolverInterimResult? CurrentBestResult = null,
    SolverCurrentTurnPreview? CurrentTurnPreview = null,
    SolverSpeculativeRoutePreview? SpeculativeRoutePreview = null,
    SolverRouteAdoptionSeed? RouteAdoptionSeed = null,
    int RequestBudgetMilliseconds = 0)
{
    public SolverRouteAdoptionSeed? CurrentTurnAdoptionSeed { get; init; }

    // Set only when a member has finished the full production final ordering (including
    // scenario rerank and deterministic block-potion insertion) but is still flattening
    // replay/annotation data. The coordinator records Published only if this route actually
    // wins the global displayed-result ordering.
    public CandidateOrigin? OfficialPublishedOrigin { get; init; }
    public string? OfficialPublishedEvaluationContextId { get; init; }

    public bool HasApprovedForegroundRoute
        => OfficialPublishedOrigin != null
            && !string.IsNullOrWhiteSpace(OfficialPublishedEvaluationContextId)
            && SpeculativeRoutePreview != null;

    public int ApprovedForegroundCandidateVersion
        => SpeculativeRoutePreview?.CandidateVersion ?? -1;
}
