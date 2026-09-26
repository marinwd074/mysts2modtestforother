using System.Diagnostics;

namespace CombatSolver;

/// <summary>
/// Request-scoped wall-clock and expanded-node budget shared by resumable P3 members.
/// Normal search sessions keep their existing per-member budgets when this is absent.
/// </summary>
internal sealed class SearchRequestWallClockBudget
{
    private readonly long _startedTicks = Stopwatch.GetTimestamp();
    private int _expandedNodes;

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
