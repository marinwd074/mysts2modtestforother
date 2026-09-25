using System.Diagnostics;

namespace CombatSolver;

/// <summary>一个成员在生产路径上的实测开销；跳过的成员这三项为零。</summary>
internal readonly record struct BeamWidthPortfolioMemberCost(
    long ElapsedMilliseconds,
    long AllocatedBytes,
    long ManagedHeapBytesAfter);

/// <summary>
/// 逐成员明细：既有的 <see cref="BeamWidthPortfolioMember" /> 加上生产路径才有的开销与选中标记。
/// </summary>
internal sealed record BeamWidthPortfolioMemberReport(
    int BeamWidth,
    bool SecondRankBand,
    bool BaseScoreOnly,
    int NodeBudget,
    bool Ran,
    bool Selected,
    bool Compared,
    string? SkippedReason,
    long ExpandedNodes,
    long TransitionCount,
    string? Termination,
    bool? Terminal,
    bool? Won,
    int? BattleHpLost,
    int? PotionCount,
    long ElapsedMilliseconds,
    long AllocatedBytes,
    long ManagedHeapBytesAfter);

/// <summary>
/// E0 candidate identity. Timestamps use <see cref="Stopwatch.GetTimestamp"/> so candidates
/// from different serial portfolio members share one monotonic request timeline.
/// </summary>
internal sealed record CandidateOrigin(
    long CandidateId,
    int SearchMemberId,
    long FirstGeneratedTicks,
    long ExpandedAtGeneration,
    int TurnDepth);

internal sealed record CandidateMilestones(
    CandidateOrigin Origin,
    long? EvaluatedTicks,
    long? SelectedTicks,
    long? PublishedTicks,
    string EvaluationContextId);

internal sealed record SearchEfficiencyMemberReport(
    int MemberId,
    string Kind,
    int BeamWidth,
    bool SecondRankBand,
    bool BaseScoreOnly,
    bool Novelty,
    long StartedTicks,
    long? CompletedTicks,
    long ExpandedNodes,
    long TransitionCount)
{
    /// <summary>
    /// First safe-point slice that committed real parent work. This distinguishes
    /// "session constructed early" from genuine E3 interleaving.
    /// </summary>
    public long? FirstWorkTicks { get; init; }
}

internal sealed record SearchEfficiencyPhaseReport(
    int SearchMemberId,
    string Phase,
    long ExclusiveTicks,
    int CallCount);

/// <summary>
/// 请求级的组合诊断。开关关闭时也照样记录——那时是单成员一行，A/B 才能直接并排比。
/// E0 additionally keeps only compact metadata for candidates that become comparable; it never
/// formats every expanded node or computes a route hash on the hot expansion path.
/// </summary>
/// <remarks>
/// 协调器在一次请求里可能跑不止一轮搜索（无胜利时抬节点上限重搜），每轮的成员按顺序追加，
/// <see cref="FirstRoutePublishedMilliseconds" /> 只记第一次。
/// </remarks>
internal sealed class BeamWidthPortfolioTelemetry
{
    private readonly Lock _gate = new();
    private readonly List<BeamWidthPortfolioMemberReport> _members = [];
    private readonly Dictionary<int, SearchEfficiencyMemberReport> _searchMembers = [];
    private readonly Dictionary<(long CandidateId, string ContextId), CandidateMilestones> _candidateMilestones = [];
    private readonly Dictionary<(int MemberId, string Phase), SearchEfficiencyPhaseReport> _phases = [];
    private readonly long _requestStartedTicks = Stopwatch.GetTimestamp();
    private double? _firstRoutePublishedMilliseconds;
    private long _peakManagedHeapBytes;
    private long _nextCandidateId;
    private int _nextSearchMemberId;

    public long RequestStartedTicks => _requestStartedTicks;

    /// <summary>基线成员完成并按今天的方式发布给覆盖层的时刻，相对本次搜索请求开始。</summary>
    public double? FirstRoutePublishedMilliseconds
    {
        get { lock (_gate) return _firstRoutePublishedMilliseconds; }
    }

    /// <summary>各成员结束后 <c>GC.GetTotalMemory(false)</c> 的最大值。</summary>
    public long PeakManagedHeapBytes
    {
        get { lock (_gate) return _peakManagedHeapBytes; }
    }

    public IReadOnlyList<BeamWidthPortfolioMemberReport> Members
    {
        get { lock (_gate) return _members.ToArray(); }
    }

    public IReadOnlyList<SearchEfficiencyMemberReport> SearchMembers
    {
        get
        {
            lock (_gate)
                return _searchMembers.Values.OrderBy(member => member.MemberId).ToArray();
        }
    }

    public IReadOnlyList<SearchEfficiencyPhaseReport> SearchPhases
    {
        get
        {
            lock (_gate)
                return _phases.Values
                    .OrderBy(phase => phase.SearchMemberId)
                    .ThenBy(phase => phase.Phase, StringComparer.Ordinal)
                    .ToArray();
        }
    }

    public void RecordFirstRoutePublished(double elapsedMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMilliseconds);
        lock (_gate)
            _firstRoutePublishedMilliseconds ??= elapsedMilliseconds;
    }

    public void RecordMember(BeamWidthPortfolioMemberReport member)
    {
        ArgumentNullException.ThrowIfNull(member);
        lock (_gate)
        {
            _members.Add(member);
            if (member.Ran && member.ManagedHeapBytesAfter > _peakManagedHeapBytes)
                _peakManagedHeapBytes = member.ManagedHeapBytesAfter;
        }
    }

    public int BeginSearchMember(
        string kind,
        int beamWidth,
        bool secondRankBand,
        bool baseScoreOnly,
        bool novelty)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        int memberId = Interlocked.Increment(ref _nextSearchMemberId);
        SearchEfficiencyMemberReport report = new(
            memberId,
            kind,
            beamWidth,
            secondRankBand,
            baseScoreOnly,
            novelty,
            Stopwatch.GetTimestamp(),
            CompletedTicks: null,
            ExpandedNodes: 0,
            TransitionCount: 0);
        lock (_gate)
            _searchMembers.Add(memberId, report);
        return memberId;
    }

    public void RecordSearchMemberFirstWork(int memberId)
    {
        if (memberId <= 0)
            return;
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (!_searchMembers.TryGetValue(memberId, out SearchEfficiencyMemberReport? report)
                || report.FirstWorkTicks.HasValue)
            {
                return;
            }
            _searchMembers[memberId] = report with { FirstWorkTicks = now };
        }
    }

    public void CompleteSearchMember(int memberId, long expandedNodes, long transitionCount)
    {
        if (memberId <= 0)
            return;
        lock (_gate)
        {
            if (!_searchMembers.TryGetValue(memberId, out SearchEfficiencyMemberReport? report))
                return;
            _searchMembers[memberId] = report with
            {
                CompletedTicks = Stopwatch.GetTimestamp(),
                ExpandedNodes = expandedNodes,
                TransitionCount = transitionCount,
            };
        }
    }

    public CandidateOrigin CreateCandidateOrigin(
        int searchMemberId,
        long expandedAtGeneration,
        int turnDepth)
    {
        if (searchMemberId <= 0)
            throw new ArgumentOutOfRangeException(nameof(searchMemberId));
        return new CandidateOrigin(
            Interlocked.Increment(ref _nextCandidateId),
            searchMemberId,
            Stopwatch.GetTimestamp(),
            expandedAtGeneration,
            turnDepth);
    }

    public void RecordCandidateEvaluated(CandidateOrigin? origin, string evaluationContextId)
        => RecordCandidateMilestone(origin, evaluationContextId, CandidateMilestoneStage.Evaluated);

    public void RecordCandidateSelected(CandidateOrigin? origin, string evaluationContextId)
        => RecordCandidateMilestone(origin, evaluationContextId, CandidateMilestoneStage.Selected);

    public void RecordCandidatePublished(CandidateOrigin? origin, string evaluationContextId)
        => RecordCandidateMilestone(origin, evaluationContextId, CandidateMilestoneStage.Published);

    public CandidateMilestones? FindCandidateMilestones(
        CandidateOrigin? origin,
        string? evaluationContextId)
    {
        if (origin == null || string.IsNullOrWhiteSpace(evaluationContextId))
            return null;
        lock (_gate)
            return _candidateMilestones.GetValueOrDefault((origin.CandidateId, evaluationContextId));
    }

    public SearchEfficiencyMemberReport? FindSearchMember(int memberId)
    {
        lock (_gate)
            return _searchMembers.GetValueOrDefault(memberId);
    }

    public void RecordExclusivePhase(int searchMemberId, string phase, long exclusiveTicks)
    {
        if (searchMemberId <= 0 || exclusiveTicks < 0 || string.IsNullOrWhiteSpace(phase))
            return;
        lock (_gate)
        {
            (int MemberId, string Phase) key = (searchMemberId, phase);
            SearchEfficiencyPhaseReport previous = _phases.GetValueOrDefault(key)
                ?? new SearchEfficiencyPhaseReport(searchMemberId, phase, 0, 0);
            _phases[key] = previous with
            {
                ExclusiveTicks = checked(previous.ExclusiveTicks + exclusiveTicks),
                CallCount = checked(previous.CallCount + 1),
            };
        }
    }

    public long GetExclusivePhaseTicks(int searchMemberId, string phase)
    {
        lock (_gate)
            return _phases.GetValueOrDefault((searchMemberId, phase))?.ExclusiveTicks ?? 0;
    }

    public double ToRequestMilliseconds(long timestamp)
        => timestamp <= _requestStartedTicks
            ? 0d
            : Stopwatch.GetElapsedTime(_requestStartedTicks, timestamp).TotalMilliseconds;

    public static double DurationMilliseconds(long ticks)
        => ticks <= 0
            ? 0d
            : TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency).TotalMilliseconds;

    private void RecordCandidateMilestone(
        CandidateOrigin? origin,
        string evaluationContextId,
        CandidateMilestoneStage stage)
    {
        if (origin == null || string.IsNullOrWhiteSpace(evaluationContextId))
            return;
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            (long CandidateId, string ContextId) key = (origin.CandidateId, evaluationContextId);
            CandidateMilestones previous = _candidateMilestones.GetValueOrDefault(key)
                ?? new CandidateMilestones(origin, null, null, null, evaluationContextId);
            _candidateMilestones[key] = stage switch
            {
                CandidateMilestoneStage.Evaluated => previous with
                {
                    EvaluatedTicks = previous.EvaluatedTicks ?? now,
                },
                CandidateMilestoneStage.Selected => previous with
                {
                    SelectedTicks = previous.SelectedTicks ?? now,
                },
                CandidateMilestoneStage.Published => previous with
                {
                    PublishedTicks = previous.PublishedTicks ?? now,
                },
                _ => previous,
            };
        }
    }

    private enum CandidateMilestoneStage
    {
        Evaluated,
        Selected,
        Published,
    }
}
