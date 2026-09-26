using System.Diagnostics;

namespace CombatSolver;

/// <summary>
/// Request-scoped wall-clock deadline shared by resumable P3 search members.
/// Normal search sessions keep their existing exclusive-time stopwatch when this is absent.
/// </summary>
internal sealed class SearchRequestWallClockBudget
{
    private readonly long _startedTicks = Stopwatch.GetTimestamp();

    public SearchRequestWallClockBudget(int budgetMilliseconds)
    {
        if (budgetMilliseconds < 1)
            throw new ArgumentOutOfRangeException(nameof(budgetMilliseconds));
        BudgetMilliseconds = budgetMilliseconds;
    }

    public int BudgetMilliseconds { get; }

    public long ElapsedMilliseconds
        => (long)Stopwatch.GetElapsedTime(_startedTicks).TotalMilliseconds;

    public bool IsExpired => ElapsedMilliseconds >= BudgetMilliseconds;
}
