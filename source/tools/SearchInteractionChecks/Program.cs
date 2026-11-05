using CombatSolver;

int passed = 0;
Check("approved display alone has no executable seed", () =>
{
    SearchInteractionState state = new();
    state.PublishProgress(Progress(7, approved: true));
    SolverProgress display = Display(state);
    Require(display.HasApprovedForegroundRoute && display.RouteAdoptionSeed == null);
    Require(state.RenderedRouteAdoptionSeed == null);
    Require(state.MarkRenderedProgress(display));
    Require(state.RenderedRouteAdoptionSeed == null && state.CurrentTakeoverRequest == null);
});
Check("clearing live progress preserves approval, rendered selection and takeover", () =>
{
    SearchInteractionState state = new();
    SolverRouteAdoptionSeed seed = Seed(7);
    SolverProgress progress = Progress(7, approved: true, routeSeed: seed);
    state.PublishProgress(progress);
    state.MarkRenderedProgress(Display(state));
    Require(state.RequestAdoptRoute(seed));
    SearchTakeoverRequest request = state.CurrentTakeoverRequest!;
    state.ClearProgress();
    Require(state.Progress == null && ReferenceEquals(state.ApprovedForegroundProgress, progress)
        && ReferenceEquals(state.RenderedRouteAdoptionSeed, seed)
        && ReferenceEquals(state.CurrentTakeoverRequest, request) && state.CanAcceptTakeover);
    Require(!state.TryCreateDisplayProgress(long.MaxValue / 2, out _));
    SolverProgress next = Progress(8);
    state.PublishProgress(next);
    Require(ReferenceEquals(state.Progress, next) && ReferenceEquals(state.ApprovedForegroundProgress, progress));
});
Check("seeded cancellation resolves either selected scope once outside the state lock", () =>
{
    foreach (bool route in new[] { false, true })
    {
        SearchInteractionState state = new();
        SolverResult result = new();
        int calls = 0;
        SolverRouteAdoptionSeed seed = new(7, [Action()], () =>
        {
            calls++;
            Require(Task.Run(state.InvalidateRenderedCandidate).Wait(2_000), "Materialization held the state lock");
            return result;
        });
        Require(route ? state.RequestAdoptRoute(seed) : state.RequestApplyCurrentTurn(seed));
        Require(ReferenceEquals(state.ResolveTakeoverResult(), result)
            && ReferenceEquals(state.ResolveTakeoverResult(), result) && calls == 1);
        Require(result.ResultScope == (route ? SolverResultScope.RouteAdoption : SolverResultScope.CurrentTurnAdoption));
    }
});
Check("seedless cancellation leaves complete-result quality fallback with Search", () =>
{
    SearchInteractionState state = new();
    Require(state.ResolveTakeoverResult() == null && state.RequestApplyCurrentTurn());
    Require(state.ResolveTakeoverResult() == null && state.IsApplyingCurrentTurn);
});
Check("publishing a seed does not acknowledge a rendered selection", () =>
{
    SearchInteractionState state = new();
    SolverProgress progress = Progress(7, routeSeed: Seed(7));
    state.PublishProgress(progress);
    Require(state.RenderedRouteAdoptionSeed == null);
    SolverProgress display = Display(state);
    Require(state.RenderedRouteAdoptionSeed == null);
    Require(!state.MarkRenderedProgress(progress with { Phase = "unrendered" }));
    Require(state.MarkRenderedProgress(display));
    Require(ReferenceEquals(state.RenderedRouteAdoptionSeed, progress.RouteAdoptionSeed));
});
Check("candidate version mismatch rejects identical actions", () => RejectMismatch(Seed(8)));
Check("candidate action mismatch rejects identical version", () => RejectMismatch(Seed(7, turn: 2)));
Check("unapproved display also rejects an unrelated seed", () =>
{
    SearchInteractionState state = new();
    state.PublishProgress(Progress(7, routeSeed: Seed(99)));
    SolverProgress display = Display(state);
    state.MarkRenderedProgress(display);
    Require(display.RouteAdoptionSeed == null && state.RenderedRouteAdoptionSeed == null);
});
Check("background work cannot replace an approved preview", () =>
{
    SearchInteractionState state = new();
    state.PublishProgress(Progress(7, approved: true));
    state.PublishProgress(Progress(8, routeSeed: Seed(8)));
    SolverProgress display = Display(state);
    Require(display.SpeculativeRoutePreview?.CandidateVersion == 7 && display.RouteAdoptionSeed == null);
    state.PublishProgress(Progress(7, routeSeed: Seed(7)));
    display = Display(state);
    Require(display.SpeculativeRoutePreview?.CandidateVersion == 7 && display.RouteAdoptionSeed != null);
});
Check("newer approved candidate replaces both previews and clears old seeds", () =>
{
    SearchInteractionState state = new();
    state.PublishProgress(Progress(7, approved: true, routeSeed: Seed(7), currentSeed: Seed(7)));
    state.MarkRenderedProgress(Display(state));
    state.PublishProgress(Progress(8, approved: true));
    SolverProgress display = Display(state);
    state.MarkRenderedProgress(display);
    Require(display.SpeculativeRoutePreview?.CandidateVersion == 8 && display.CurrentTurnPreview?.CandidateVersion == 8);
    Require(state.RenderedRouteAdoptionSeed == null && state.RenderedCurrentTurnAdoptionSeed == null);
});
Check("older approved publication cannot regress the latched version", () =>
{
    SearchInteractionState state = new();
    state.PublishProgress(Progress(8, approved: true));
    state.PublishProgress(Progress(7, approved: true, routeSeed: Seed(7)));
    SolverProgress display = Display(state);
    Require(display.SpeculativeRoutePreview?.CandidateVersion == 8 && display.RouteAdoptionSeed == null);
});
Check("publication display adoption background update finalization keep the selected route", () =>
{
    SearchInteractionState state = new();
    SolverResult chosen = new();
    SolverRouteAdoptionSeed seed = Seed(7, result: chosen);
    state.PublishProgress(Progress(7, approved: true));
    state.PublishProgress(Progress(7, routeSeed: seed));
    state.MarkRenderedProgress(Display(state));
    Require(state.RequestAdoptRoute(state.RenderedRouteAdoptionSeed!, stopAfterResult: true));
    state.PublishProgress(Progress(8, approved: true, routeSeed: Seed(8)));
    state.MarkRenderedProgress(Display(state));
    Require(ReferenceEquals(state.CurrentTakeoverRequest?.RouteAdoptionSeed, seed));
    Require(state.StopRequested && !state.RequestAdoptRoute(Seed(9)));
    SolverResult result = state.FinalizeWorkerResult(new());
    Require(ReferenceEquals(result, chosen) && result.ResultScope == SolverResultScope.RouteAdoption);
    Require(!state.CanAcceptTakeover && !state.RequestApplyCurrentTurn());
    Require(state.CompleteTakeover()?.StopAfterResult == true);
    Require(state.RenderedRouteAdoptionSeed == null && state.CurrentTakeoverRequest == null);
});
Check("current-turn takeover keeps the exact rendered seed and scope", () =>
{
    SearchInteractionState state = new();
    SolverResult chosen = new();
    SolverRouteAdoptionSeed seed = Seed(7, result: chosen);
    state.PublishProgress(Progress(7, currentSeed: seed));
    state.MarkRenderedProgress(Display(state));
    Require(state.RequestApplyCurrentTurn(state.RenderedCurrentTurnAdoptionSeed));
    SolverResult result = state.FinalizeWorkerResult(new());
    Require(ReferenceEquals(result, chosen) && result.ResultScope == SolverResultScope.CurrentTurnAdoption);
});
Check("current-turn identity rejects wrong version and wrong action independently", () =>
{
    foreach (SolverRouteAdoptionSeed seed in new[] { Seed(8), Seed(7, turn: 2) })
    {
        SearchInteractionState state = new();
        state.PublishProgress(Progress(7, currentSeed: seed));
        SolverProgress display = Display(state);
        state.MarkRenderedProgress(display);
        Require(display.CurrentTurnAdoptionSeed == null && state.RenderedCurrentTurnAdoptionSeed == null);
    }
});
Check("turn-setup adapter keeps route-only rendering and seedless current-turn behavior", () =>
{
    SearchInteractionState state = new();
    state.PublishProgress(Progress(7, routeSeed: Seed(7), currentSeed: Seed(7)));
    state.MarkRenderedProgress(Display(state), includeCurrentTurn: false);
    Require(state.RenderedRouteAdoptionSeed != null && state.RenderedCurrentTurnAdoptionSeed == null);
    Require(state.RequestApplyCurrentTurn());
    SolverResult ordinary = new();
    Require(state.ResolveTakeoverResult(ordinary) == null);
    Require(ReferenceEquals(state.FinalizeWorkerResult(ordinary), ordinary));
    Require(ordinary.ResultScope == SolverResultScope.SearchCompletion);
});
Check("ordinary completion never acquires adoption scope", () =>
{
    SearchInteractionState state = new();
    SolverResult result = new();
    Require(state.ResolveTakeoverResult(result) == null);
    Require(ReferenceEquals(state.FinalizeWorkerResult(result), result));
    Require(result.ResultScope == SolverResultScope.SearchCompletion);
});
Check("coordinator resolution and worker finalization share one selected materialization", () =>
{
    SearchInteractionState state = new();
    SolverResult chosen = new();
    int calls = 0;
    SolverRouteAdoptionSeed seed = new(7, [Action()], () => { calls++; return chosen; });
    Require(state.RequestAdoptRoute(seed));
    SolverResult selected = state.ResolveTakeoverResult(new())!;
    Require(ReferenceEquals(state.FinalizeWorkerResult(selected), selected));
    Require(calls == 1 && selected.ResultScope == SolverResultScope.RouteAdoption);
});
Check("intermediate existing adoption scope is preserved until the final requested scope", () =>
{
    SearchInteractionState state = new();
    SolverResult chosen = new();
    Require(state.RequestAdoptRoute(Seed(7, result: chosen)));
    SolverResult intermediate = new() { ResultScope = SolverResultScope.CurrentTurnAdoption };
    Require(ReferenceEquals(state.ResolveTakeoverResult(intermediate), intermediate));
    Require(ReferenceEquals(state.FinalizeWorkerResult(intermediate), chosen));
    Require(chosen.ResultScope == SolverResultScope.RouteAdoption);
});
Check("lazy materialization runs outside the interaction lock", () =>
{
    SearchInteractionState state = new();
    SolverRouteAdoptionSeed seed = new(7, [Action()], () =>
    {
        Task otherThread = Task.Run(state.InvalidateRenderedCandidate);
        Require(otherThread.Wait(TimeSpan.FromSeconds(3)), "Materialization held the interaction lock.");
        return new();
    });
    Require(state.RequestAdoptRoute(seed));
    Require(state.FinalizeWorkerResult(new()).ResultScope == SolverResultScope.RouteAdoption);
});
Check("failed materialization propagates and leaves takeover acceptance closed", () =>
{
    SearchInteractionState state = new();
    int calls = 0;
    SolverRouteAdoptionSeed seed = new(7, [Action()], () =>
    {
        calls++;
        throw new InvalidOperationException("expected materialization failure");
    });
    Require(state.RequestAdoptRoute(seed));
    for (int i = 0; i < 2; i++)
    {
        bool threw = false;
        try { state.FinalizeWorkerResult(new()); }
        catch (InvalidOperationException error) when (error.Message == "expected materialization failure") { threw = true; }
        Require(threw && !state.CanAcceptTakeover && !state.RequestApplyCurrentTurn());
    }
    Require(calls == 1);
});
Check("seed owns a read-only action-list snapshot and materializes once", () =>
{
    List<PlanAction> actions = [Action()];
    int calls = 0;
    SolverRouteAdoptionSeed seed = new(7, actions, () => { Interlocked.Increment(ref calls); return new(); });
    actions[0] = Action(2);
    Require(seed.Actions[0].Turn == 1 && seed.Actions is not PlanAction[]);
    bool rejected = false;
    try { ((IList<PlanAction>)seed.Actions)[0] = Action(3); }
    catch (NotSupportedException) { rejected = true; }
    Require(rejected);
    Parallel.For(0, 32, _ => seed.Materialize());
    Require(calls == 1);
});
Check("invalidation and reset clear displayed selection without leaking an old request", () =>
{
    SearchInteractionState state = new();
    state.PublishProgress(Progress(7, routeSeed: Seed(7)));
    state.MarkRenderedProgress(Display(state));
    state.InvalidateRenderedCandidate();
    Require(state.RenderedRouteAdoptionSeed == null);
    state.RequestApplyCurrentTurn();
    state.FinalizeWorkerResult(new());
    state.ResetForSearch();
    Require(state.CanAcceptTakeover && state.CurrentTakeoverRequest == null && state.Progress == null);
    Require(state.ApprovedForegroundProgress == null && state.RenderedProgress == null);
    Require(state.RenderedRouteAdoptionSeed == null && state.RenderedCurrentTurnAdoptionSeed == null);
});
Check("materialized result presentation pairs independent current-turn and full-route versions", () =>
{
    SolverResult result = new() { BestNode = new([Action(), Action(2)]) };
    SolverCandidatePresentation presentation = SolverCandidatePresentation.FromResult(result, 11, 23);
    SolverProgress progress = presentation.ApplyTo(Progress(0));
    Require(progress.CurrentTurnPreview?.CandidateVersion == 11
        && progress.SpeculativeRoutePreview?.CandidateVersion == 23);
    Require(progress.CurrentTurnAdoptionSeed?.Actions.Count == 1 && progress.RouteAdoptionSeed?.Actions.Count == 2);
    Require(ReferenceEquals(progress.CurrentTurnAdoptionSeed!.Materialize(), result)
        && result.ResultScope == SolverResultScope.CurrentTurnAdoption);
    Require(ReferenceEquals(progress.RouteAdoptionSeed!.Materialize(), result));
});
Check("a stale render acknowledgement cannot replace the newer displayed selection", () =>
{
    SearchInteractionState state = new();
    state.PublishProgress(Progress(7, routeSeed: Seed(7)));
    SolverProgress older = Display(state);
    state.MarkRenderedProgress(older);
    SolverRouteAdoptionSeed newerSeed = Seed(8);
    state.PublishProgress(Progress(8, routeSeed: newerSeed));
    state.MarkRenderedProgress(Display(state));
    Require(!state.MarkRenderedProgress(older)
        && ReferenceEquals(state.RenderedRouteAdoptionSeed, newerSeed));
});
Check("stopped candidate requires the same live stamp and is consumed once", () =>
{
    SearchInteractionState state = new();
    SolverResult result = new();
    state.PreserveStoppedResult(result, new(1));
    Require(state.TakeStoppedResult(new(2)) == null && state.TakeStoppedResult(new(1)) == null);
    state.PreserveStoppedResult(result, new(3));
    Require(ReferenceEquals(state.TakeStoppedResult(new(3)), result) && state.TakeStoppedResult(new(3)) == null);
});
Console.WriteLine($"SearchInteractionChecks PASS ({passed} production-module contracts; foreign DTO stubs, no game runtime)");

void Check(string name, Action body)
{
    body();
    passed++;
    Console.WriteLine($"PASS {name}");
}
void RejectMismatch(SolverRouteAdoptionSeed seed)
{
    SearchInteractionState state = new();
    state.PublishProgress(Progress(7, approved: true));
    state.PublishProgress(Progress(7, routeSeed: seed));
    SolverProgress display = Display(state);
    state.MarkRenderedProgress(display);
    Require(display.RouteAdoptionSeed == null && state.RenderedRouteAdoptionSeed == null);
}
static PlanAction Action(int turn = 1) => new(PlanActionKind.EndTurn, turn);
static SolverRouteAdoptionSeed Seed(int version, int turn = 1, SolverResult? result = null)
    => new(version, [Action(turn)], () => result ?? new());
static SolverProgress Progress(int version, bool approved = false,
    SolverRouteAdoptionSeed? routeSeed = null, SolverRouteAdoptionSeed? currentSeed = null)
    => new(1, 1, 1, 1, 10, 10, 100, 1, 0, 100, "test",
        CurrentTurnPreview: new(version, 1, [Action()], 0, 0, 0, 0, false),
        SpeculativeRoutePreview: new(version, 1, 0, 0, false, false, false,
            [new(1, [Action()], 0, 0, 0, 0, false)]), RouteAdoptionSeed: routeSeed)
    {
        CurrentTurnAdoptionSeed = currentSeed,
        OfficialPublishedOrigin = approved ? new(1, 1, 1, version, 1) : null,
        OfficialPublishedEvaluationContextId = approved ? "contract" : null,
    };
static SolverProgress Display(SearchInteractionState state)
{
    long now = Math.Max(Environment.TickCount64, DisplayClock.LastTick)
        + SolverWeights.ProgressUiIntervalMilliseconds + 1;
    DisplayClock.LastTick = now;
    Require(state.TryCreateDisplayProgress(now, out SolverProgress progress));
    return progress;
}
static void Require(bool condition, string message = "Contract failed")
{
    if (!condition) throw new InvalidOperationException(message);
}

static class DisplayClock
{
    public static long LastTick;
}
