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
    public IReadOnlyList<PlanAction> Actions { get; } = Array.AsReadOnly(actions.ToArray());

    public SolverResult Materialize()
        => _materialized.Value;
}

// A candidate's two previews and their executable seeds travel together. Capture
// rejects an independently stale/mismatched seed; display approval remains separate.
internal sealed class SolverCandidatePresentation
{
    public static SolverCandidatePresentation Empty { get; } = new(null, null, null, null);

    private SolverCandidatePresentation(
        SolverCurrentTurnPreview? currentTurnPreview,
        SolverSpeculativeRoutePreview? speculativeRoutePreview,
        SolverRouteAdoptionSeed? currentTurnAdoptionSeed,
        SolverRouteAdoptionSeed? routeAdoptionSeed)
    {
        CurrentTurnPreview = currentTurnPreview;
        SpeculativeRoutePreview = speculativeRoutePreview;
        CurrentTurnAdoptionSeed = Matches(currentTurnAdoptionSeed, currentTurnPreview)
            ? currentTurnAdoptionSeed : null;
        RouteAdoptionSeed = Matches(routeAdoptionSeed, speculativeRoutePreview)
            ? routeAdoptionSeed : null;
    }

    public SolverCurrentTurnPreview? CurrentTurnPreview { get; }
    public SolverSpeculativeRoutePreview? SpeculativeRoutePreview { get; }
    public SolverRouteAdoptionSeed? CurrentTurnAdoptionSeed { get; }
    public SolverRouteAdoptionSeed? RouteAdoptionSeed { get; }

    public static SolverCandidatePresentation Capture(SolverProgress progress)
        => new(progress.CurrentTurnPreview, progress.SpeculativeRoutePreview,
            progress.CurrentTurnAdoptionSeed, progress.RouteAdoptionSeed);

    public static SolverCandidatePresentation FromResult(
        SolverResult result, int currentTurnVersion, int routeVersion)
    {
        SolverCurrentTurnPreview currentTurn = SolverCurrentTurnPreview.FromResult(result, currentTurnVersion);
        SolverSpeculativeRoutePreview route = SolverSpeculativeRoutePreview.FromResult(result, routeVersion);
        return new(currentTurn, route,
            new SolverRouteAdoptionSeed(currentTurnVersion, currentTurn.Actions, () =>
            {
                result.ResultScope = SolverResultScope.CurrentTurnAdoption;
                return result;
            }),
            new SolverRouteAdoptionSeed(routeVersion, result.BestNode.Actions, () => result));
    }

    public SolverCandidatePresentation WithSeedsFrom(SolverProgress progress)
        => new(CurrentTurnPreview, SpeculativeRoutePreview,
            progress.CurrentTurnAdoptionSeed, progress.RouteAdoptionSeed);

    public SolverCandidatePresentation WithoutCurrentTurnSeed()
        => new(CurrentTurnPreview, SpeculativeRoutePreview, null, RouteAdoptionSeed);

    public SolverProgress ApplyTo(SolverProgress progress)
        => ReferenceEquals(progress.CurrentTurnPreview, CurrentTurnPreview)
            && ReferenceEquals(progress.SpeculativeRoutePreview, SpeculativeRoutePreview)
            && ReferenceEquals(progress.CurrentTurnAdoptionSeed, CurrentTurnAdoptionSeed)
            && ReferenceEquals(progress.RouteAdoptionSeed, RouteAdoptionSeed)
                ? progress
                : progress with
                {
                    CurrentTurnPreview = CurrentTurnPreview,
                    SpeculativeRoutePreview = SpeculativeRoutePreview,
                    CurrentTurnAdoptionSeed = CurrentTurnAdoptionSeed,
                    RouteAdoptionSeed = RouteAdoptionSeed,
                };

    private static bool Matches(SolverRouteAdoptionSeed? seed, SolverCurrentTurnPreview? preview)
        => seed != null && preview != null
            && seed.CandidateVersion == preview.CandidateVersion
            && seed.Actions.SequenceEqual(preview.Actions);

    private static bool Matches(SolverRouteAdoptionSeed? seed, SolverSpeculativeRoutePreview? preview)
        => seed != null && preview != null
            && seed.CandidateVersion == preview.CandidateVersion
            && seed.Actions.SequenceEqual(preview.Turns.SelectMany(static turn => turn.Actions));
}

internal sealed class SearchInteractionState
{
    private readonly object _gate = new();
    private int _acceptingTakeover = 1;
    private SearchTakeoverRequest? _takeoverRequest;
    private SolverProgress? _approvedForegroundProgress;
    private SolverCandidatePresentation? _renderedCandidate;
    private SolverProgress? _progress;
    private readonly SearchProgressDisplayState _progressDisplay = new();

    public SolverProgress? Progress => Volatile.Read(ref _progress);
    public SolverProgress? RenderedProgress { get; private set; }
    public SolverRouteAdoptionSeed? RenderedCurrentTurnAdoptionSeed
        => _renderedCandidate?.CurrentTurnAdoptionSeed;
    public SolverRouteAdoptionSeed? RenderedRouteAdoptionSeed
        => _renderedCandidate?.RouteAdoptionSeed;
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
        Volatile.Write(ref _progress, progress);
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

    // Completion clears only the live publication slot. The displayed selection,
    // approval and pending takeover keep their existing, independent lifetimes.
    public void ClearProgress()
        => Volatile.Write(ref _progress, null);

    public bool TryCreateDisplayProgress(long now, out SolverProgress displayProgress)
    {
        SolverProgress? liveProgress = Progress;
        if (liveProgress == null)
        {
            displayProgress = null!;
            return false;
        }

        SolverProgress? approved = Volatile.Read(ref _approvedForegroundProgress);
        SolverCandidatePresentation presentation = approved == null
            ? SolverCandidatePresentation.Capture(liveProgress)
            : SolverCandidatePresentation.Capture(approved).WithSeedsFrom(liveProgress);
        SolverProgress progress = presentation.ApplyTo(approved == null
            ? liveProgress
            : liveProgress with
            {
                CurrentBestResult = approved.CurrentBestResult,
                OfficialPublishedOrigin = approved.OfficialPublishedOrigin,
                OfficialPublishedEvaluationContextId =
                    approved.OfficialPublishedEvaluationContextId,
            });
        if (ReferenceEquals(progress, RenderedProgress)
            || !_progressDisplay.TryCreate(progress, now, out displayProgress))
        {
            displayProgress = null!;
            return false;
        }
        RenderedProgress = displayProgress;
        return true;
    }

    // The main-thread display adapter acknowledges the exact progress it projected.
    // Publishing background progress alone must never change the user's selected seed.
    public bool MarkRenderedProgress(SolverProgress progress, bool includeCurrentTurn = true)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(progress, RenderedProgress) || !CanAcceptTakeover)
                return false;
            SolverCandidatePresentation candidate = SolverCandidatePresentation.Capture(progress);
            _renderedCandidate = includeCurrentTurn ? candidate : candidate.WithoutCurrentTurnSeed();
            return true;
        }
    }

    public void InvalidateRenderedCandidate()
    {
        lock (_gate)
            _renderedCandidate = null;
    }

    public void ResetForSearch()
    {
        lock (_gate)
        {
            Volatile.Write(ref _takeoverRequest, null);
            Volatile.Write(ref _acceptingTakeover, 1);
        }
        ClearProgress();
        Volatile.Write(ref _approvedForegroundProgress, null);
        RenderedProgress = null;
        InvalidateRenderedCandidate();
        StoppedResult = null;
        StoppedStamp = null;
        _progressDisplay.Restart(Environment.TickCount64);
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

        // Lazy flattening/replay can be expensive or re-enter interaction code. Never
        // run it under _gate, including when a selected route is already materialized.
        return ResolveExactCandidate(result, request, preserveExistingAdoption: false) ?? result;
    }

    // A cancelled search may have no ordinary result. A selected seed still uses
    // this same scope/materialization seam; seedless quality fallback stays in Search.
    public SolverResult? ResolveTakeoverResult(SolverResult? result = null)
        => ResolveExactCandidate(result, CurrentTakeoverRequest, preserveExistingAdoption: true);

    private static SolverResult? ResolveExactCandidate(
        SolverResult? result, SearchTakeoverRequest? request, bool preserveExistingAdoption)
    {
        if (request == null)
            return null;
        if (preserveExistingAdoption && result?.ResultScope is
            SolverResultScope.CurrentTurnAdoption or SolverResultScope.RouteAdoption)
            return result;

        SolverResultScope scope = request.Kind == SearchTakeoverKind.AdoptRoute
            ? SolverResultScope.RouteAdoption
            : SolverResultScope.CurrentTurnAdoption;
        SolverRouteAdoptionSeed? seed = request.Kind == SearchTakeoverKind.AdoptRoute
            ? request.RouteAdoptionSeed
            : request.CurrentTurnAdoptionSeed;
        if (seed == null)
            return null; // TurnSetup's existing seedless current-turn request.
        if (result?.ResultScope == scope)
            return result;
        SolverResult exactDisplayed = seed.Materialize();
        exactDisplayed.ResultScope = scope;
        return exactDisplayed;
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
            _renderedCandidate = null;
            return completed;
        }
    }
}

internal sealed class SearchProgressDisplayState(long startedAtTick)
{
    public SearchProgressDisplayState() : this(Environment.TickCount64)
    {
    }

    public long StartedAtTick { get; private set; } = startedAtTick;
    public long LastRenderAtTick { get; private set; } = startedAtTick;
    public SolverProgress? RenderedProgress { get; private set; }

    public void Restart(long nowTick)
    {
        StartedAtTick = nowTick;
        LastRenderAtTick = nowTick;
        RenderedProgress = null;
    }

    public bool TryCreate(
        SolverProgress? progress,
        long nowTick,
        out SolverProgress displayProgress)
    {
        if (progress == null
            || nowTick - LastRenderAtTick < SolverWeights.ProgressUiIntervalMilliseconds)
        {
            displayProgress = null!;
            return false;
        }

        long elapsedMilliseconds = Math.Max(
            progress.ElapsedMilliseconds,
            Math.Max(
                RenderedProgress?.ElapsedMilliseconds ?? 0L,
                Math.Max(0L, nowTick - StartedAtTick)));
        displayProgress = elapsedMilliseconds == progress.ElapsedMilliseconds
            ? progress
            : progress with { ElapsedMilliseconds = elapsedMilliseconds };
        LastRenderAtTick = nowTick;
        RenderedProgress = displayProgress;
        return true;
    }
}
