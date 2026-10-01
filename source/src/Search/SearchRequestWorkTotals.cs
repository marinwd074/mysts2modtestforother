namespace CombatSolver;

internal readonly record struct SearchRequestWorkSnapshot(
    long ExpandedNodes, long TransitionCount, long ChoiceBranchesEvaluated,
    TimeSpan Elapsed, long WorkerAllocatedBytes,
    long Gen0Collections, long Gen1Collections, long Gen2Collections,
    TimeSpan GcPauseDuration, TimeSpan MaxObservedGcPause, int RecordedSolverCount);

internal readonly record struct SearchSolverWorkContribution(
    int ExpandedNodes, int TransitionCount, int ChoiceBranchesEvaluated,
    TimeSpan Elapsed, long WorkerAllocatedBytes,
    int Gen0Collections, int Gen1Collections, int Gen2Collections,
    TimeSpan GcPauseDuration, TimeSpan MaxObservedGcPause);

internal enum SearchRequestMemberKind
{
    Unclassified, Baseline, BeamRefinement, SmartPotion, ContinuationSeed, RouteReplay, Novelty,
}

internal sealed record SearchSolverPhaseContribution(
    string Kind, int BeamWidth, int MinimumPotionUses, int? MaximumPotionUses,
    string Outcome, SearchSolverWorkContribution Work, IReadOnlyDictionary<string, SearchPhaseMetric> Phases);

internal sealed record SearchRequestPhaseSnapshot(
    int RecordedSolverCount, long ExpandedNodes, long LogicalTransitions,
    TimeSpan CoordinatorOverhead, IReadOnlyList<SearchSolverPhaseContribution> Members,
    IReadOnlyDictionary<string, SearchPhaseMetric> Phases);

/// <summary>Each solver contributes once, including cancellation and failed policy searches.</summary>
internal sealed class SearchRequestWorkTotals(bool measurePhases = false)
{
    private readonly Lock _gate = new();
    private SearchRequestWorkSnapshot _totals;
    private readonly List<SearchSolverPhaseContribution>? _phases = measurePhases ? [] : null;
    private TimeSpan _coordinatorOverhead;
    internal bool MeasuresPhases => measurePhases;
    internal int RecordedSolverCountForTesting { get { lock (_gate) return _totals.RecordedSolverCount; } }

    public void Record(SearchSolverWorkContribution work, SearchSolverPhaseContribution? phases = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(work.ExpandedNodes);
        ArgumentOutOfRangeException.ThrowIfNegative(work.TransitionCount);
        ArgumentOutOfRangeException.ThrowIfNegative(work.ChoiceBranchesEvaluated);
        ValidateWork(work.Elapsed, work.WorkerAllocatedBytes, work.Gen0Collections, work.Gen1Collections,
            work.Gen2Collections, work.GcPauseDuration, work.MaxObservedGcPause);
        lock (_gate)
        {
            if (_phases != null && phases != null)
                _phases.Add(phases);
            Accumulate(work.Elapsed, work.WorkerAllocatedBytes, work.Gen0Collections, work.Gen1Collections,
                work.Gen2Collections, work.GcPauseDuration, work.MaxObservedGcPause);
            _totals = _totals with
            {
                ExpandedNodes = _totals.ExpandedNodes + work.ExpandedNodes,
                TransitionCount = _totals.TransitionCount + work.TransitionCount,
                ChoiceBranchesEvaluated = _totals.ChoiceBranchesEvaluated + work.ChoiceBranchesEvaluated,
                RecordedSolverCount = _totals.RecordedSolverCount + 1,
            };
        }
    }

    public void RecordCoordinatorOverhead(TimeSpan elapsed, long allocatedBytes,
        int gen0Collections, int gen1Collections, int gen2Collections,
        TimeSpan gcPauseDuration, TimeSpan maxObservedGcPause)
    {
        ValidateWork(elapsed, allocatedBytes, gen0Collections, gen1Collections, gen2Collections, gcPauseDuration, maxObservedGcPause);
        lock (_gate)
        {
            _coordinatorOverhead += elapsed;
            Accumulate(elapsed, allocatedBytes, gen0Collections, gen1Collections, gen2Collections, gcPauseDuration, maxObservedGcPause);
        }
    }

    private static void ValidateWork(TimeSpan elapsed, long allocatedBytes, int gen0Collections,
        int gen1Collections, int gen2Collections, TimeSpan gcPauseDuration, TimeSpan maxObservedGcPause)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(allocatedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(gen0Collections);
        ArgumentOutOfRangeException.ThrowIfNegative(gen1Collections);
        ArgumentOutOfRangeException.ThrowIfNegative(gen2Collections);
        if (elapsed < TimeSpan.Zero || gcPauseDuration < TimeSpan.Zero || maxObservedGcPause < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(elapsed));
    }

    private void Accumulate(TimeSpan elapsed, long allocatedBytes, int gen0Collections,
        int gen1Collections, int gen2Collections, TimeSpan gcPauseDuration, TimeSpan maxObservedGcPause)
    {
        _totals = _totals with
        {
            Elapsed = _totals.Elapsed + elapsed,
            WorkerAllocatedBytes = _totals.WorkerAllocatedBytes + allocatedBytes,
            Gen0Collections = _totals.Gen0Collections + gen0Collections,
            Gen1Collections = _totals.Gen1Collections + gen1Collections,
            Gen2Collections = _totals.Gen2Collections + gen2Collections,
            GcPauseDuration = _totals.GcPauseDuration + gcPauseDuration,
            MaxObservedGcPause = maxObservedGcPause > _totals.MaxObservedGcPause ? maxObservedGcPause : _totals.MaxObservedGcPause,
        };
    }

    public SearchRequestWorkSnapshot Snapshot() { lock (_gate) return _totals; }

    internal SearchRequestPhaseSnapshot? CapturePhases()
    {
        if (_phases == null)
            return null;
        lock (_gate)
        {
            Dictionary<string, SearchPhaseMetric> totals = [];
            foreach (SearchMetricPhase phase in Enum.GetValues<SearchMetricPhase>())
            {
                TimeSpan elapsed = TimeSpan.Zero;
                long allocated = 0, samples = 0;
                foreach (SearchSolverPhaseContribution member in _phases)
                {
                    SearchPhaseMetric metric = member.Phases[phase.ToString()];
                    elapsed += metric.Elapsed;
                    allocated += metric.AllocatedBytes;
                    samples += metric.Samples;
                }
                totals.Add(phase.ToString(), new(elapsed, allocated, samples));
            }
            return new(_totals.RecordedSolverCount, _totals.ExpandedNodes, _totals.TransitionCount,
                _coordinatorOverhead, _phases.ToArray(), totals);
        }
    }
}
