namespace CombatSolver;

internal enum ShadowTeammateScenarioKind
{
    Unspecified = 0,
    Aggressive = 1,
    Defensive = 2,
    Conserve = 3,
    NoAction = 4,
}

internal readonly record struct ShadowTeammateScenarioObservation(
    int ActionCount,
    bool CompleteVictory,
    bool AllPlayersAlive,
    int EnemyDurability,
    int TeamEffectiveHp,
    double WorstPlayerEffectiveHpRatio,
    int TeamEnergy,
    int TeamStars,
    double BehaviorLogMass,
    string ActionOrderKey);

internal readonly record struct ShadowTeammateScenarioChoice(
    int Index,
    ShadowTeammateScenarioKind Kind);

/// <summary>
/// Small, deliberately non-probabilistic stress-scenario portfolio for P3. The behavior prior
/// only breaks otherwise equal routes; it does not turn these lanes into calibrated probabilities.
/// </summary>
internal static class ShadowTeammateScenarioPolicy
{
    internal static IReadOnlyList<ShadowTeammateScenarioChoice> SelectProtected(
        IReadOnlyList<ShadowTeammateScenarioObservation> observations,
        int limit)
    {
        if (limit <= 0 || observations.Count == 0)
            return Array.Empty<ShadowTeammateScenarioChoice>();

        List<ShadowTeammateScenarioChoice> selected = new(Math.Min(limit, 4));
        HashSet<int> selectedIndices = [];
        HashSet<string> selectedActionOrders = new(StringComparer.Ordinal);

        AddBest(
            ShadowTeammateScenarioKind.Aggressive,
            observation => observation.ActionCount > 0,
            CompareAggressive);
        AddBest(
            ShadowTeammateScenarioKind.Defensive,
            observation => observation.ActionCount > 0,
            CompareDefensive);
        AddBest(
            ShadowTeammateScenarioKind.Conserve,
            observation => observation.ActionCount > 0,
            CompareConserve);
        AddBest(
            ShadowTeammateScenarioKind.NoAction,
            observation => observation.ActionCount == 0,
            CompareNoAction);

        if (selected.Count < limit)
        {
            HashSet<string> usedOrders = selected
                .Select(choice => observations[choice.Index].ActionOrderKey)
                .Where(key => !string.IsNullOrEmpty(key))
                .ToHashSet(StringComparer.Ordinal);
            foreach (int index in Enumerable.Range(0, observations.Count)
                         .Where(index => !selectedIndices.Contains(index))
                         .OrderByDescending(index => observations[index].BehaviorLogMass)
                         .ThenBy(index => observations[index].ActionCount)
                         .ThenBy(index => index))
            {
                string orderKey = observations[index].ActionOrderKey;
                if (!string.IsNullOrEmpty(orderKey) && usedOrders.Add(orderKey))
                {
                    selectedIndices.Add(index);
                    selected.Add(new ShadowTeammateScenarioChoice(
                        index,
                        ShadowTeammateScenarioKind.Unspecified));
                    if (selected.Count == limit)
                        return selected;
                }
            }
        }

        if (selected.Count < limit)
        {
            foreach (int index in Enumerable.Range(0, observations.Count)
                         .Where(index => !selectedIndices.Contains(index))
                         .OrderByDescending(index => observations[index].BehaviorLogMass)
                         .ThenBy(index => observations[index].ActionCount)
                         .ThenBy(index => index))
            {
                selected.Add(new ShadowTeammateScenarioChoice(
                    index,
                    ShadowTeammateScenarioKind.Unspecified));
                if (selected.Count == limit)
                    break;
            }
        }
        return selected;

        void AddBest(
            ShadowTeammateScenarioKind kind,
            Func<ShadowTeammateScenarioObservation, bool> eligible,
            Func<ShadowTeammateScenarioObservation, ShadowTeammateScenarioObservation, int> compare)
        {
            if (selected.Count >= limit)
                return;

            int best = -1;
            bool preferFreshActionOrder = false;
            if (kind != ShadowTeammateScenarioKind.NoAction)
            {
                for (int index = 0; index < observations.Count; index++)
                {
                    ShadowTeammateScenarioObservation observation = observations[index];
                    if (!selectedIndices.Contains(index)
                        && eligible(observation)
                        && !string.IsNullOrEmpty(observation.ActionOrderKey)
                        && !selectedActionOrders.Contains(observation.ActionOrderKey))
                    {
                        preferFreshActionOrder = true;
                        break;
                    }
                }
            }

            for (int index = 0; index < observations.Count; index++)
            {
                ShadowTeammateScenarioObservation observation = observations[index];
                if (selectedIndices.Contains(index) || !eligible(observation))
                    continue;
                if (preferFreshActionOrder
                    && (string.IsNullOrEmpty(observation.ActionOrderKey)
                        || selectedActionOrders.Contains(observation.ActionOrderKey)))
                {
                    continue;
                }

                if (best < 0 || compare(observation, observations[best]) < 0)
                    best = index;
            }
            if (best < 0)
                return;

            selectedIndices.Add(best);
            if (!string.IsNullOrEmpty(observations[best].ActionOrderKey))
                selectedActionOrders.Add(observations[best].ActionOrderKey);
            selected.Add(new ShadowTeammateScenarioChoice(best, kind));
        }
    }

    private static int CompareAggressive(
        ShadowTeammateScenarioObservation left,
        ShadowTeammateScenarioObservation right)
    {
        int comparison = right.CompleteVictory.CompareTo(left.CompleteVictory);
        if (comparison != 0)
            return comparison;
        comparison = left.EnemyDurability.CompareTo(right.EnemyDurability);
        if (comparison != 0)
            return comparison;
        comparison = right.BehaviorLogMass.CompareTo(left.BehaviorLogMass);
        if (comparison != 0)
            return comparison;
        return left.ActionCount.CompareTo(right.ActionCount);
    }

    private static int CompareDefensive(
        ShadowTeammateScenarioObservation left,
        ShadowTeammateScenarioObservation right)
    {
        int comparison = right.AllPlayersAlive.CompareTo(left.AllPlayersAlive);
        if (comparison != 0)
            return comparison;
        comparison = right.WorstPlayerEffectiveHpRatio.CompareTo(
            left.WorstPlayerEffectiveHpRatio);
        if (comparison != 0)
            return comparison;
        comparison = right.TeamEffectiveHp.CompareTo(left.TeamEffectiveHp);
        if (comparison != 0)
            return comparison;
        comparison = left.EnemyDurability.CompareTo(right.EnemyDurability);
        if (comparison != 0)
            return comparison;
        return right.BehaviorLogMass.CompareTo(left.BehaviorLogMass);
    }

    private static int CompareConserve(
        ShadowTeammateScenarioObservation left,
        ShadowTeammateScenarioObservation right)
    {
        int comparison = right.TeamEnergy.CompareTo(left.TeamEnergy);
        if (comparison != 0)
            return comparison;
        comparison = right.TeamStars.CompareTo(left.TeamStars);
        if (comparison != 0)
            return comparison;
        comparison = left.ActionCount.CompareTo(right.ActionCount);
        if (comparison != 0)
            return comparison;
        comparison = right.AllPlayersAlive.CompareTo(left.AllPlayersAlive);
        if (comparison != 0)
            return comparison;
        return right.BehaviorLogMass.CompareTo(left.BehaviorLogMass);
    }

    private static int CompareNoAction(
        ShadowTeammateScenarioObservation left,
        ShadowTeammateScenarioObservation right)
        => right.BehaviorLogMass.CompareTo(left.BehaviorLogMass);
}
