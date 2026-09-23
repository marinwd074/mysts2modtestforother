namespace CombatSolver;

internal enum MultiplayerRetentionLane
{
    LowTeamLoss,
    TeamSafety,
    FastLethal,
    Growth,
}

internal readonly record struct MultiplayerRetentionObservation(
    bool CompleteVictory,
    bool AllPlayersAlive,
    double TeamLossRatio,
    double WorstPlayerLossRatio,
    double EnemyDurabilityRatio,
    int CombatEndedTurn,
    int LongTermResourceValue,
    int PersistentBuffValue,
    int LatentSetupValue,
    int FutureResourceValue,
    int RetainedAttackGrowth,
    int ReplayPotentialValue);

internal readonly record struct MultiplayerRetentionChoice(
    int Index,
    MultiplayerRetentionLane Lane);

/// <summary>
/// P2 fixed-budget diversity protection. These lanes reserve representatives; they do not
/// increase Beam width and they are not a claim of state dominance or exact equivalence.
/// </summary>
internal static class MultiplayerRetentionDiversityPolicy
{
    internal static IReadOnlyList<MultiplayerRetentionChoice> SelectProtected(
        IReadOnlyList<MultiplayerRetentionObservation> observations,
        int limit)
    {
        if (limit <= 0 || observations.Count == 0)
            return Array.Empty<MultiplayerRetentionChoice>();

        bool hasAlive = observations.Any(observation => observation.AllPlayersAlive);
        int[] eligible = Enumerable.Range(0, observations.Count)
            .Where(index => !hasAlive || observations[index].AllPlayersAlive)
            .ToArray();

        List<MultiplayerRetentionChoice> selected =
            new(Math.Min(Math.Min(limit, 4), eligible.Length));
        HashSet<int> selectedIndices = [];

        AddBest(MultiplayerRetentionLane.LowTeamLoss, CompareLowLoss);
        AddBest(MultiplayerRetentionLane.TeamSafety, CompareTeamSafety);
        AddBest(MultiplayerRetentionLane.FastLethal, CompareFastLethal);
        AddBest(MultiplayerRetentionLane.Growth, CompareGrowth);
        return selected;

        void AddBest(
            MultiplayerRetentionLane lane,
            Func<MultiplayerRetentionObservation, MultiplayerRetentionObservation, int> compare)
        {
            if (selected.Count >= limit)
                return;

            int best = eligible[0];
            for (int offset = 1; offset < eligible.Length; offset++)
            {
                int candidate = eligible[offset];
                int comparison = compare(observations[candidate], observations[best]);
                if (comparison < 0 || comparison == 0 && candidate < best)
                    best = candidate;
            }

            if (selectedIndices.Add(best))
                selected.Add(new MultiplayerRetentionChoice(best, lane));
        }
    }

    private static int CompareLowLoss(
        MultiplayerRetentionObservation left,
        MultiplayerRetentionObservation right)
    {
        int comparison = left.TeamLossRatio.CompareTo(right.TeamLossRatio);
        if (comparison != 0)
            return comparison;
        comparison = left.WorstPlayerLossRatio.CompareTo(right.WorstPlayerLossRatio);
        if (comparison != 0)
            return comparison;
        comparison = left.EnemyDurabilityRatio.CompareTo(right.EnemyDurabilityRatio);
        if (comparison != 0)
            return comparison;
        comparison = right.CompleteVictory.CompareTo(left.CompleteVictory);
        if (comparison != 0)
            return comparison;
        return left.CombatEndedTurn.CompareTo(right.CombatEndedTurn);
    }

    private static int CompareTeamSafety(
        MultiplayerRetentionObservation left,
        MultiplayerRetentionObservation right)
    {
        int comparison = left.WorstPlayerLossRatio.CompareTo(right.WorstPlayerLossRatio);
        if (comparison != 0)
            return comparison;
        comparison = left.TeamLossRatio.CompareTo(right.TeamLossRatio);
        if (comparison != 0)
            return comparison;
        comparison = left.EnemyDurabilityRatio.CompareTo(right.EnemyDurabilityRatio);
        if (comparison != 0)
            return comparison;
        comparison = right.CompleteVictory.CompareTo(left.CompleteVictory);
        if (comparison != 0)
            return comparison;
        return left.CombatEndedTurn.CompareTo(right.CombatEndedTurn);
    }

    private static int CompareFastLethal(
        MultiplayerRetentionObservation left,
        MultiplayerRetentionObservation right)
    {
        int comparison = right.CompleteVictory.CompareTo(left.CompleteVictory);
        if (comparison != 0)
            return comparison;
        if (left.CompleteVictory && right.CompleteVictory)
        {
            comparison = left.CombatEndedTurn.CompareTo(right.CombatEndedTurn);
            if (comparison != 0)
                return comparison;
        }
        comparison = left.EnemyDurabilityRatio.CompareTo(right.EnemyDurabilityRatio);
        if (comparison != 0)
            return comparison;
        comparison = left.TeamLossRatio.CompareTo(right.TeamLossRatio);
        if (comparison != 0)
            return comparison;
        return left.WorstPlayerLossRatio.CompareTo(right.WorstPlayerLossRatio);
    }

    private static int CompareGrowth(
        MultiplayerRetentionObservation left,
        MultiplayerRetentionObservation right)
    {
        int comparison = right.LongTermResourceValue.CompareTo(left.LongTermResourceValue);
        if (comparison != 0)
            return comparison;
        comparison = right.PersistentBuffValue.CompareTo(left.PersistentBuffValue);
        if (comparison != 0)
            return comparison;
        comparison = right.LatentSetupValue.CompareTo(left.LatentSetupValue);
        if (comparison != 0)
            return comparison;
        comparison = right.FutureResourceValue.CompareTo(left.FutureResourceValue);
        if (comparison != 0)
            return comparison;
        comparison = right.RetainedAttackGrowth.CompareTo(left.RetainedAttackGrowth);
        if (comparison != 0)
            return comparison;
        comparison = right.ReplayPotentialValue.CompareTo(left.ReplayPotentialValue);
        if (comparison != 0)
            return comparison;
        comparison = left.TeamLossRatio.CompareTo(right.TeamLossRatio);
        if (comparison != 0)
            return comparison;
        return left.EnemyDurabilityRatio.CompareTo(right.EnemyDurabilityRatio);
    }
}
