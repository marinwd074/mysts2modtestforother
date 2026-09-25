namespace CombatSolver;

internal enum E3AdaptivePriority
{
    None = 0,
    DeathRiskRemoved = 1,
    CompleteVictory = 2,
}

internal readonly record struct E3AdaptiveObservation(
    double MarginalImprovementRate,
    bool ImportantImprovement,
    E3AdaptivePriority Priority);

internal readonly record struct E3AdaptiveMemberSignal(
    int MemberKey,
    int SliceCount,
    bool ExplorationDue,
    bool ImportantCandidate,
    E3AdaptivePriority Priority,
    double MarginalImprovementRate,
    int TieOrder);

/// <summary>
/// E3B adaptive scheduling policy. The fixed round-robin slice remains the fairness floor.
/// Adaptive work only changes which member receives one optional bonus slice after an epoch.
/// </summary>
/// <remarks>
/// The rate follows the E3 plan:
/// m_i = (1-alpha) * m_i + alpha * (delta_q / delta_t).
/// delta_q is measured only when the lexicographic category is unchanged, using the existing
/// scalar Score as the within-category diagnostic. A first complete victory or removal of death
/// risk is represented by <see cref="E3AdaptivePriority"/>, never by a synthetic large score.
/// </remarks>
internal static class E3AdaptiveBudgetPolicy
{
    // Experimental value for the E3B A/B only. It is not treated as an optimal constant.
    internal const double ExperimentAlpha = 0.25d;

    internal static E3AdaptiveObservation Observe(
        SolverInterimResult? previous,
        SolverInterimResult? current,
        double previousRate,
        double exclusiveMilliseconds,
        double alpha = ExperimentAlpha)
    {
        if (!double.IsFinite(previousRate) || previousRate < 0d)
            throw new ArgumentOutOfRangeException(nameof(previousRate));
        if (!double.IsFinite(exclusiveMilliseconds) || exclusiveMilliseconds < 0d)
            throw new ArgumentOutOfRangeException(nameof(exclusiveMilliseconds));
        if (!double.IsFinite(alpha) || alpha <= 0d || alpha > 1d)
            throw new ArgumentOutOfRangeException(nameof(alpha));

        bool important = current != null
            && (previous == null || SolverInterimResultOrdering.IsBetter(current, previous));
        E3AdaptivePriority priority = ResolvePriority(previous, current);

        double deltaQuality = 0d;
        if (previous != null
            && current != null
            && SameScalarCategory(previous, current)
            && double.IsFinite(previous.Score)
            && double.IsFinite(current.Score)
            && current.Score > previous.Score)
        {
            deltaQuality = current.Score - previous.Score;
        }

        double sampleRate = exclusiveMilliseconds > 0d
            ? deltaQuality / exclusiveMilliseconds
            : 0d;
        double marginalRate = (1d - alpha) * previousRate + alpha * sampleRate;
        if (!double.IsFinite(marginalRate) || marginalRate < 0d)
            marginalRate = 0d;

        return new E3AdaptiveObservation(
            marginalRate,
            important,
            priority);
    }

    internal static int? SelectBonusMember(IReadOnlyList<E3AdaptiveMemberSignal> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        E3AdaptiveMemberSignal? selected = null;
        foreach (E3AdaptiveMemberSignal member in members)
        {
            Validate(member);
            if (!member.ExplorationDue
                && !member.ImportantCandidate
                && member.Priority == E3AdaptivePriority.None
                && member.MarginalImprovementRate <= 0d)
            {
                continue;
            }

            if (selected is not { } current || Compare(member, current) < 0)
                selected = member;
        }
        return selected?.MemberKey;
    }

    private static int Compare(
        E3AdaptiveMemberSignal candidate,
        E3AdaptiveMemberSignal current)
    {
        int comparison = current.ExplorationDue.CompareTo(candidate.ExplorationDue);
        if (comparison != 0)
            return comparison;

        comparison = current.Priority.CompareTo(candidate.Priority);
        if (comparison != 0)
            return comparison;

        comparison = current.ImportantCandidate.CompareTo(candidate.ImportantCandidate);
        if (comparison != 0)
            return comparison;

        comparison = current.MarginalImprovementRate.CompareTo(candidate.MarginalImprovementRate);
        if (comparison != 0)
            return comparison;

        comparison = candidate.SliceCount.CompareTo(current.SliceCount);
        if (comparison != 0)
            return comparison;

        comparison = candidate.TieOrder.CompareTo(current.TieOrder);
        if (comparison != 0)
            return comparison;

        return candidate.MemberKey.CompareTo(current.MemberKey);
    }

    private static E3AdaptivePriority ResolvePriority(
        SolverInterimResult? previous,
        SolverInterimResult? current)
    {
        if (current == null)
            return E3AdaptivePriority.None;
        if (current.Won && previous?.Won != true)
            return E3AdaptivePriority.CompleteVictory;
        if (current.Survives && previous is { Survives: false })
            return E3AdaptivePriority.DeathRiskRemoved;
        if (previous != null && current.DeathSaveUseCount < previous.DeathSaveUseCount)
            return E3AdaptivePriority.DeathRiskRemoved;
        return E3AdaptivePriority.None;
    }

    private static bool SameScalarCategory(
        SolverInterimResult previous,
        SolverInterimResult current)
        => previous.Won == current.Won
            && previous.Survives == current.Survives
            && previous.DeathSaveUseCount == current.DeathSaveUseCount
            && previous.TheftPolicy == current.TheftPolicy
            && previous.OutstandingStolenResource == current.OutstandingStolenResource
            && previous.StrategicHpDeficit == current.StrategicHpDeficit
            && previous.PotionStrategicCost == current.PotionStrategicCost
            && previous.ProjectedBattleHpLost == current.ProjectedBattleHpLost
            && previous.GrowthHpCredit == current.GrowthHpCredit
            && previous.GrowthRewardCount == current.GrowthRewardCount
            && previous.CombatEndedTurn == current.CombatEndedTurn
            && previous.ProjectedBattlePotionCount == current.ProjectedBattlePotionCount
            && previous.EnemyHp == current.EnemyHp;

    private static void Validate(E3AdaptiveMemberSignal member)
    {
        if (member.MemberKey < 0
            || member.SliceCount < 0
            || member.TieOrder < 0
            || !double.IsFinite(member.MarginalImprovementRate)
            || member.MarginalImprovementRate < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(member));
        }
    }

    internal static void VerifyForTesting()
    {
        SolverInterimResult Base(double score) => new(
            Won: true,
            OutstandingStolenResource: 0,
            ProjectedBattleHpLost: 3,
            StrategicHpDeficit: 3,
            PotionStrategicCost: 0,
            ProjectedBattlePotionCount: 1,
            EnemyHp: 0,
            Score: score,
            CombatEndedTurn: 3)
        {
            Survives = true,
            DeathSaveUseCount = 0,
        };

        E3AdaptiveObservation rate = Observe(
            Base(10d),
            Base(14d),
            previousRate: 1d,
            exclusiveMilliseconds: 2d,
            alpha: 0.5d);
        if (Math.Abs(rate.MarginalImprovementRate - 1.5d) > 1e-9
            || !rate.ImportantImprovement
            || rate.Priority != E3AdaptivePriority.None)
        {
            throw new InvalidOperationException("E3B EMA improvement-rate contract changed.");
        }

        SolverInterimResult unsafeResult = Base(10d) with
        {
            Won = false,
            Survives = false,
            DeathSaveUseCount = 1,
            EnemyHp = 10,
            CombatEndedTurn = null,
        };
        E3AdaptiveObservation victory = Observe(
            unsafeResult,
            Base(10d),
            previousRate: 0d,
            exclusiveMilliseconds: 5d);
        if (victory.Priority != E3AdaptivePriority.CompleteVictory
            || victory.MarginalImprovementRate != 0d)
        {
            throw new InvalidOperationException("E3B hard-priority improvement leaked into scalar rate.");
        }

        if (SelectBonusMember([]) != null)
            throw new InvalidOperationException("E3B empty set must not get bonus work.");
        if (SelectBonusMember([
                new(1, 2, false, false, E3AdaptivePriority.None, 0d, 0),
                new(2, 2, false, false, E3AdaptivePriority.None, 0d, 1)]) != null)
        {
            throw new InvalidOperationException("E3B must collapse to fixed rotation without gain.");
        }
        if (SelectBonusMember([
                new(1, 2, true, false, E3AdaptivePriority.None, 0d, 0),
                new(2, 2, false, true, E3AdaptivePriority.CompleteVictory, 10d, 1)]) != 1)
        {
            throw new InvalidOperationException("E3B exploration floor lost lexicographic priority.");
        }
        if (SelectBonusMember([
                new(1, 2, false, true, E3AdaptivePriority.DeathRiskRemoved, 100d, 0),
                new(2, 2, false, true, E3AdaptivePriority.CompleteVictory, 0d, 1)]) != 2)
        {
            throw new InvalidOperationException("E3B hard improvement priority changed.");
        }
        if (SelectBonusMember([
                new(1, 2, false, false, E3AdaptivePriority.None, 2d, 0),
                new(2, 2, false, false, E3AdaptivePriority.None, 3d, 1)]) != 2)
        {
            throw new InvalidOperationException("E3B did not prefer the higher same-category EMA rate.");
        }
        if (SelectBonusMember([
                new(2, 2, false, false, E3AdaptivePriority.None, 3d, 1),
                new(1, 2, false, false, E3AdaptivePriority.None, 3d, 0)]) != 1)
        {
            throw new InvalidOperationException("E3B exact tie lost deterministic round-robin order.");
        }
    }
}
