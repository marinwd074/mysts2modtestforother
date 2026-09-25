namespace CombatSolver;

internal readonly record struct E3AdaptiveMemberSignal(
    int MemberKey,
    int SliceCount,
    int ImprovementCredits);

/// <summary>
/// E3B 保守自适应预算。公平轮转始终先执行；这里只从当前 epoch 的完整胜利
/// incumbent 改进信号里选至多一个 bonus slice。
/// </summary>
internal static class E3AdaptiveBudgetPolicy
{
    internal static int? SelectBonusMember(IReadOnlyList<E3AdaptiveMemberSignal> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        E3AdaptiveMemberSignal? selected = null;
        foreach (E3AdaptiveMemberSignal member in members)
        {
            if (member.MemberKey < 0 || member.SliceCount < 0 || member.ImprovementCredits < 0)
                throw new ArgumentOutOfRangeException(nameof(members));
            if (member.ImprovementCredits == 0)
                continue;
            if (selected is not { } current
                || member.ImprovementCredits > current.ImprovementCredits
                || member.ImprovementCredits == current.ImprovementCredits && member.SliceCount < current.SliceCount
                || member.ImprovementCredits == current.ImprovementCredits
                    && member.SliceCount == current.SliceCount
                    && member.MemberKey < current.MemberKey)
            {
                selected = member;
            }
        }
        return selected?.MemberKey;
    }

    internal static void VerifyForTesting()
    {
        if (SelectBonusMember([]) != null)
            throw new InvalidOperationException("E3B empty set must not get bonus work.");
        if (SelectBonusMember([new(1, 2, 0), new(2, 2, 0)]) != null)
            throw new InvalidOperationException("E3B must stay fixed without improvement.");
        if (SelectBonusMember([new(1, 2, 1), new(2, 2, 3), new(3, 2, 2)]) != 2)
            throw new InvalidOperationException("E3B strongest improvement was not selected.");
        if (SelectBonusMember([new(1, 4, 2), new(2, 2, 2)]) != 2)
            throw new InvalidOperationException("E3B equal improvement must prefer less-served work.");
        if (SelectBonusMember([new(3, 2, 2), new(2, 2, 2)]) != 2)
            throw new InvalidOperationException("E3B exact tie must keep stable member order.");
    }
}
