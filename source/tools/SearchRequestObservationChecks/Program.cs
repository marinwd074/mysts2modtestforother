using CombatSolver;

int checks = 0;
void Check(bool ok, string reason)
{
    if (!ok) throw new InvalidOperationException(reason);
    checks++;
}
double now = 0;
List<SearchRequestObservationSnapshot> rows = [];
SearchRequestObservation New() => new("fixture", 7, rows.Add, () => now);
void At(SearchRequestObservation observation, double time, SearchRequestStage stage)
{
    now = time; observation.Mark(stage);
}

// Multiple deferred attempts retain one identity and do not finish at the first return.
SearchRequestObservation accepted = New();
using (accepted.BeginAttempt()) accepted.BeginDeferral();
now = 12; accepted.EndDeferral();
using (accepted.BeginAttempt()) accepted.BeginDeferral();
now = 20; accepted.EndDeferral();
Check(rows.Count == 0, "A deferred request finished before its owner resumed it.");
using (accepted.BeginAttempt())
{
    accepted.TransferToAsync(); accepted.Bind(9, 2, 50, 8); accepted.BindRoot("combat-fixture");
    At(accepted, 20, SearchRequestStage.RootCaptureStarted);
    At(accepted, 25, SearchRequestStage.RootCaptureFinished);
    At(accepted, 25, SearchRequestStage.WorkerQueued);
}
At(accepted, 27, SearchRequestStage.WorkerStarted);
now = 30; accepted.ObserveProgress(true, 1);
At(accepted, 35, SearchRequestStage.PreviewSubmitted);
At(accepted, 40, SearchRequestStage.ApprovedRouteSubmitted);
now = 45; accepted.ObserveProgress(true, 3); accepted.ObserveWork(121, 222);
now = 50; accepted.WorkerFinished("RanToCompletion");
At(accepted, 70, SearchRequestStage.CallbackStarted);
now = 72; accepted.Accept("route-a", 3);
At(accepted, 75, SearchRequestStage.CancellationDisposed);
At(accepted, 80, SearchRequestStage.CallbackFinished);
Parallel.For(0, 32, _ => accepted.Finish());
Check(rows.Count == 1, "Completion was emitted more than once.");
var a = rows.Single();
Check(a.Deferrals == 2 && a.DeferredMilliseconds == 20 && a.ElapsedMilliseconds == 80,
    "Deferral restarted the clock or was counted twice.");
Check(a.QueueMilliseconds == 2 && a.WorkerMilliseconds == 23 && a.DispatchMilliseconds == 20,
    "Queue, worker, and dispatcher timings overlap or use different origins.");
Check(a.Stages["PreviewProduced"] == 30 && a.Stages["PreviewSubmitted"] == 35
    && a.Stages["ApprovedRouteSubmitted"] == 40 && a.Stages["ResultAccepted"] == 72,
    "Preview creation was confused with publication or final acceptance.");
Check(a.SearchGeneration == 9 && a.Turn == 2 && a.WorldVersion == 50 && a.RouteInvalidationVersion == 8
    && a.CombatIdentity == "combat-fixture" && a.RouteIdentity == "route-a" && a.RouteDepth == 3
    && a.MaximumCompletedTurnLayers == 3, "Request identity or depth was lost.");
Check(a.WorkObserved && a.ExpandedNodes == 121 && a.LogicalTransitions == 222
    && a.UnacceptedRequestExpandedNodes == 0 && a.CancelToWorkerFinishedMilliseconds == null,
    "Accepted work or absent cancellation was mislabeled.");

// Work from an invalidated request can have had a useful preview. Do not claim all of it was wasted.
rows.Clear(); now = 0;
SearchRequestObservation stale = New();
stale.TransferToAsync();
At(stale, 1, SearchRequestStage.WorkerQueued); At(stale, 2, SearchRequestStage.WorkerStarted);
At(stale, 10, SearchRequestStage.PreviewSubmitted);
now = 15; stale.Cancel("multiplayer_state_changed");
now = 17; stale.Cancel("later_reason_must_not_replace_first");
stale.ObserveWork(180, 570);
now = 20; stale.WorkerFinished("Canceled");
At(stale, 25, SearchRequestStage.CallbackStarted);
stale.SetDisposition("Superseded");
At(stale, 30, SearchRequestStage.CancellationDisposed);
At(stale, 31, SearchRequestStage.CallbackFinished); stale.Finish();
var s = rows.Single();
Check(s.CancelToWorkerFinishedMilliseconds == 5 && s.CancelToCallbackFinishedMilliseconds == 16,
    "Cancellation drain omitted worker cleanup or dispatcher delay.");
Check(s.CancellationReason == "multiplayer_state_changed" && s.UnacceptedRequestExpandedNodes == 180
    && s.HadSubmittedPreview && s.Disposition == "Superseded", "Invalidated work lost its context.");
stale.Accept("late-result", 3); stale.ObserveWork(999, 999); stale.Finish();
Check(rows.Count == 1 && s.RouteIdentity == null && s.ExpandedNodes == 180,
    "A late callback mutated or re-emitted the sealed observation.");

rows.Clear(); now = 0;
SearchRequestObservation neverStarted = New();
At(neverStarted, 1, SearchRequestStage.WorkerQueued);
now = 2; neverStarted.Cancel("before_task_start");
now = 3; neverStarted.WorkerFinished("Canceled");
At(neverStarted, 4, SearchRequestStage.CallbackStarted);
neverStarted.SetDisposition("Canceled");
At(neverStarted, 5, SearchRequestStage.CallbackFinished); neverStarted.Finish();
Check(rows.Count == 0, "The callback sealed a request before the concurrent CTS owner finished.");
At(neverStarted, 6, SearchRequestStage.CancellationDisposed);
var n = rows.Single();
Check(n.WorkerMilliseconds == null && n.QueueMilliseconds == null && !n.WorkObserved
    && !n.Stages.ContainsKey("WorkerStarted") && n.CancelToWorkerFinishedMilliseconds == 1,
    "A pre-start cancellation invented worker execution or a work ledger.");
Check(n.Stages["CancellationDisposed"] == 6 && n.ElapsedMilliseconds == 6,
    "Late CTS disposal was omitted from the final observation.");

rows.Clear(); now = 0;
SearchRequestObservation deferred = New();
using (deferred.BeginAttempt()) deferred.BeginDeferral();
now = 5; deferred.Cancel("deferred_request_canceled");
now = 8; deferred.SetDisposition("DeferredCanceled"); deferred.Finish();
Check(rows.Single().DeferredMilliseconds == 8 && rows.Single().WorkerOutcome == "NotScheduled"
    && rows.Single().SearchGeneration == null, "A canceled root barrier was treated as a worker.");

rows.Clear(); now = 0;
SearchRequestObservation reused = New();
using (reused.BeginAttempt())
{
    now = 5; reused.Accept("reused", 2, "ContinuationReused");
    At(reused, 6, SearchRequestStage.ApprovedRouteSubmitted);
}
Check(rows.Single().Disposition == "ContinuationReused" && !rows.Single().WorkObserved
    && rows.Single().WorkerMilliseconds == null, "Synchronous reuse inherited old search work.");

// A real canceled Task can reach the completion continuation without invoking its delegate.
rows.Clear(); now = 0;
SearchRequestObservation canceledTask = New();
using CancellationTokenSource cts = new(); cts.Cancel();
bool ran = false;
Task task = Task.Run(() => ran = true, cts.Token);
await task.ContinueWith(done => canceledTask.WorkerFinished(done.Status.ToString()),
    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
canceledTask.Finish();
Check(!ran && rows.Single().WorkerOutcome == "Canceled" && rows.Single().WorkerMilliseconds == null,
    "The Task completion hook failed for cancellation before start.");
Console.WriteLine($"SEARCH_REQUEST_OBSERVATION_PASS checks={checks}; pure production observer and Task lifecycle; no Controller/UI");
