using System.Diagnostics;

namespace CombatSolver;

/// <summary>
/// Request-scoped wall-clock and expanded-node budget shared by recovery or P3 members.
/// Normal search sessions keep their existing per-member budgets when this is absent.
/// </summary>
internal sealed class SearchRequestWallClockBudget
{
    private readonly long _startedTicks = Stopwatch.GetTimestamp();
    private int _expandedNodes;
    private long _cancellationObservedMilliseconds = -1;

    public SearchRequestWallClockBudget(int budgetMilliseconds, int maxExpandedNodes)
    {
        if (budgetMilliseconds < 1)
            throw new ArgumentOutOfRangeException(nameof(budgetMilliseconds));
        if (maxExpandedNodes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxExpandedNodes));
        BudgetMilliseconds = budgetMilliseconds;
        MaxExpandedNodes = maxExpandedNodes;
    }

    public int BudgetMilliseconds { get; }
    public int MaxExpandedNodes { get; }

    public long ElapsedMilliseconds
        => (long)Stopwatch.GetElapsedTime(_startedTicks).TotalMilliseconds;

    public bool IsExpired => ElapsedMilliseconds >= BudgetMilliseconds;
    public int ExpandedNodes => Volatile.Read(ref _expandedNodes);
    public int RemainingExpandedNodes => Math.Max(0, MaxExpandedNodes - ExpandedNodes);
    public bool HasExpandedNodeBudgetRemaining => RemainingExpandedNodes > 0;

    public SolverSearchProfile? RemainingProfile(SolverSearchProfile profile)
    {
        long remainingMs = BudgetMilliseconds - ElapsedMilliseconds;
        int remainingNodes = RemainingExpandedNodes;
        if (remainingMs <= 0 || remainingNodes <= 0)
            return null;
        return profile with
        {
            SoftTimeBudgetMilliseconds = (int)Math.Min(profile.SoftTimeBudgetMilliseconds, remainingMs),
            MaxExpandedNodes = Math.Min(profile.MaxExpandedNodes, remainingNodes),
        };
    }

    internal void ObserveCancellation()
        => Interlocked.CompareExchange(ref _cancellationObservedMilliseconds, ElapsedMilliseconds, -1);

    internal SearchRecoveryBudgetSnapshot Capture(string outcome)
    {
        long elapsed = ElapsedMilliseconds;
        long canceled = Volatile.Read(ref _cancellationObservedMilliseconds);
        return new(outcome, BudgetMilliseconds, MaxExpandedNodes, elapsed, ExpandedNodes,
            Math.Max(0, elapsed - BudgetMilliseconds), canceled < 0 ? null : canceled,
            canceled < 0 ? null : Math.Max(0, elapsed - canceled));
    }

    public bool TryConsumeExpandedNode()
    {
        while (true)
        {
            int current = Volatile.Read(ref _expandedNodes);
            if (current >= MaxExpandedNodes)
                return false;
            if (Interlocked.CompareExchange(ref _expandedNodes, current + 1, current) == current)
                return true;
        }
    }
}

// Coordinator wall clock only. Root capture, dispatcher acceptance, and the outer
// worker/GC cleanup have separate owners and are not claimed as measured here.
internal sealed record SearchRecoveryBudgetSnapshot(
    string Outcome, int BudgetMilliseconds, int MaxExpandedNodes,
    long ElapsedMilliseconds, int ExpandedNodes, long OverrunMilliseconds,
    long? CancellationObservedMilliseconds, long? CoordinatorCancellationDrainMilliseconds);
