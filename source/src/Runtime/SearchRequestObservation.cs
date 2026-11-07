using System.Collections.ObjectModel;
using System.Diagnostics;

namespace CombatSolver;

internal enum SearchRequestStage
{
    RootCaptureStarted, RootCaptureFinished, SearchingOverlayStarted, SearchingOverlayFinished,
    WorkerQueued, WorkerStarted, WorkerFinished,
    PreviewProduced, PreviewSubmitted, ApprovedRouteSubmitted, ResultAccepted,
    CancellationRequested, CallbackStarted, CallbackFinished, CancellationDisposed,
}

// Pure values only: this observer cannot keep a live combat, result, or simulator alive.
// "Submitted" is a call to the overlay, not proof that a frame reached the display.
internal sealed record SearchRequestObservationSnapshot(
    long RequestId, string Reason, int LifecycleGeneration, int? SearchGeneration, int? Turn,
    long? WorldVersion, long? RouteInvalidationVersion, string? CombatIdentity,
    string Disposition, string WorkerOutcome, string? CancellationReason,
    string? RouteIdentity, int? RouteDepth, int? MaximumCompletedTurnLayers, int Deferrals, double DeferredMilliseconds,
    double ElapsedMilliseconds, IReadOnlyDictionary<string, double> Stages,
    double? QueueMilliseconds, double? WorkerMilliseconds, double? DispatchMilliseconds,
    double? CancelToWorkerFinishedMilliseconds, double? CancelToCallbackFinishedMilliseconds,
    bool WorkObserved, long ExpandedNodes, long LogicalTransitions,
    long UnacceptedRequestExpandedNodes, bool HadSubmittedPreview);

internal sealed class SearchRequestObservation(
    string reason, int lifecycleGeneration, Action<SearchRequestObservationSnapshot> publish,
    Func<double>? elapsedMilliseconds = null)
{
    private static long _nextId;
    private readonly long _id = Interlocked.Increment(ref _nextId);
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly Lock _gate = new();
    private readonly Dictionary<SearchRequestStage, double> _stages = [];
    private bool _asyncOwned, _finished, _workObserved, _finishRequested;
    private int? _generation, _turn, _depth, _completedLayers;
    private long? _world, _routeVersion;
    private long _expanded, _transitions;
    private int _deferrals;
    private double? _deferredStart;
    private double _deferredMilliseconds;
    private string _disposition = "NotAdmitted", _workerOutcome = "NotScheduled";
    private string? _cancelReason, _combatIdentity, _routeIdentity;

    private double Now => elapsedMilliseconds?.Invoke() ?? Stopwatch.GetElapsedTime(_started).TotalMilliseconds;

    internal IDisposable BeginAttempt()
    {
        lock (_gate) _asyncOwned = false;
        return new Attempt(this);
    }

    private sealed class Attempt(SearchRequestObservation owner) : IDisposable
    {
        public void Dispose()
        {
            bool finish;
            lock (owner._gate) finish = !owner._asyncOwned;
            if (finish) owner.Finish();
        }
    }

    internal void TransferToAsync()
    {
        lock (_gate) _asyncOwned = true;
    }

    internal void BeginDeferral()
    {
        lock (_gate)
        {
            _asyncOwned = true;
            _deferredStart = Now;
            _deferrals++;
        }
    }

    internal void EndDeferral()
    {
        lock (_gate)
        {
            if (_deferredStart is { } start) _deferredMilliseconds += Math.Max(0, Now - start);
            _deferredStart = null;
        }
    }

    internal void Bind(int generation, int turn, long world, long routeVersion)
    {
        lock (_gate) (_generation, _turn, _world, _routeVersion) = (generation, turn, world, routeVersion);
    }

    internal void BindRoot(string identity)
    {
        lock (_gate) _combatIdentity = identity;
    }

    internal void Mark(SearchRequestStage stage)
    {
        bool retryFinish;
        lock (_gate)
        {
            if (!_finished && !_stages.ContainsKey(stage)) _stages.Add(stage, Now);
            retryFinish = _finishRequested && !_finished;
        }
        if (retryFinish) Finish();
    }

    internal void WorkerFinished(string outcome)
    {
        lock (_gate)
        {
            if (_finished) return;
            _workerOutcome = outcome;
            Mark(SearchRequestStage.WorkerFinished);
        }
    }

    internal void Cancel(string cause)
    {
        lock (_gate)
        {
            if (_finished) return;
            _cancelReason ??= cause;
            Mark(SearchRequestStage.CancellationRequested);
        }
    }

    internal void SetDisposition(string disposition)
    {
        lock (_gate) if (!_finished) _disposition = disposition;
    }

    internal void ObserveWork(long expanded, long transitions)
    {
        lock (_gate)
        {
            if (_finished) return;
            _workObserved = true;
            _expanded = expanded;
            _transitions = transitions;
        }
    }

    internal void ObserveProgress(bool hasPreview, int completedLayers)
    {
        lock (_gate)
        {
            if (_finished) return;
            _completedLayers = Math.Max(_completedLayers ?? 0, completedLayers);
            if (hasPreview) Mark(SearchRequestStage.PreviewProduced);
        }
    }

    internal void Accept(string routeIdentity, int depth, string disposition = "Accepted")
    {
        lock (_gate)
        {
            if (_finished) return;
            (_routeIdentity, _depth, _disposition) = (routeIdentity, depth, disposition);
            Mark(SearchRequestStage.ResultAccepted);
        }
    }

    internal void Finish()
    {
        SearchRequestObservationSnapshot snapshot;
        lock (_gate)
        {
            if (_finished) return;
            _finishRequested = true;
            // Cancellation release can win the CTS-dispose interlock while the main
            // callback is still returning. Seal only after both owners report completion.
            if (_stages.ContainsKey(SearchRequestStage.WorkerQueued) && _disposition != "SetupFailed"
                && (!_stages.ContainsKey(SearchRequestStage.WorkerFinished)
                    || !_stages.ContainsKey(SearchRequestStage.CallbackFinished)
                    || !_stages.ContainsKey(SearchRequestStage.CancellationDisposed))) return;
            _finished = true;
            EndDeferral();
            double? Delta(SearchRequestStage from, SearchRequestStage to)
                => _stages.TryGetValue(from, out double a) && _stages.TryGetValue(to, out double b)
                    ? Math.Max(0, b - a) : null;
            bool submitted = _stages.ContainsKey(SearchRequestStage.PreviewSubmitted)
                || _stages.ContainsKey(SearchRequestStage.ApprovedRouteSubmitted);
            // This is the work of an unaccepted request, not a claim that no preview was useful.
            bool accepted = _stages.ContainsKey(SearchRequestStage.ResultAccepted)
                || _stages.ContainsKey(SearchRequestStage.ApprovedRouteSubmitted);
            snapshot = new(_id, reason, lifecycleGeneration, _generation, _turn, _world, _routeVersion,
                _combatIdentity, _disposition, _workerOutcome, _cancelReason, _routeIdentity, _depth, _completedLayers,
                _deferrals, _deferredMilliseconds, Now,
                new ReadOnlyDictionary<string, double>(_stages.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value)),
                Delta(SearchRequestStage.WorkerQueued, SearchRequestStage.WorkerStarted),
                Delta(SearchRequestStage.WorkerStarted, SearchRequestStage.WorkerFinished),
                Delta(SearchRequestStage.WorkerFinished, SearchRequestStage.CallbackStarted),
                Delta(SearchRequestStage.CancellationRequested, SearchRequestStage.WorkerFinished),
                Delta(SearchRequestStage.CancellationRequested, SearchRequestStage.CallbackFinished),
                _workObserved, _expanded, _transitions, accepted ? 0 : _expanded, submitted);
        }
        publish(snapshot);
    }
}
