using System.Runtime.CompilerServices;

namespace CombatSolver;

// Ranking and ordinary-beam diversification stay physically separate from the
// retention policy implementation; the ordering contract is intentionally unchanged.
internal sealed partial class CombatBeamSolver
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CompareBeamRankOrder(
        double leftBeamRankScore,
        int leftOffensiveProgressValue,
        int leftActionCount,
        double rightBeamRankScore,
        int rightOffensiveProgressValue,
        int rightActionCount)
    {
        int comparison = rightBeamRankScore.CompareTo(leftBeamRankScore);
        if (comparison != 0)
            return comparison;
        comparison = leftActionCount.CompareTo(rightActionCount);
        return comparison != 0
            ? comparison
            : rightOffensiveProgressValue.CompareTo(leftOffensiveProgressValue);
    }

    internal readonly record struct OrdinaryBeamTacticalValues(
        int Turn,
        int PotionCount,
        int PotionStrategicCost,
        int FutureSoldHp,
        int CumulativePlayerHpLost,
        int ActionCount,
        double Score,
        int ZeroCostPlayableCount,
        int ReachableHandValue,
        int HandCount,
        bool HasRetainedRoutingChoice = false);

    internal static void DiversifyOrdinaryBeamBoundary<T>(
        IReadOnlyList<T> rankedPool,
        List<T> selected,
        IReadOnlyList<T> required,
        Func<T, (double Score, int Actions, int OffensiveProgress, int Potions, bool Victory)> describe,
        bool finalQualityFirst,
        Func<T, OrdinaryBeamTacticalValues>? describeTactical = null)
        where T : class
    {
        if (finalQualityFirst || selected.Count >= rankedPool.Count)
            return;

        HashSet<T> requiredSet = new(required, ReferenceEqualityComparer.Instance);
        Dictionary<T, int> selectedPositions = new(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < selected.Count; index++)
            selectedPositions.Add(selected[index], index);

        // Required replacement leaves selected unsorted. Locate the last ordinary survivor
        // in the original ranking, not the last selected slot or the configured beam width.
        int boundary = rankedPool.Count - 1;
        while (boundary >= 0
            && (requiredSet.Contains(rankedPool[boundary])
                || !selectedPositions.ContainsKey(rankedPool[boundary])))
        {
            boundary--;
        }
        if (boundary < 0)
            return;
        var boundaryValue = describe(rankedPool[boundary]);
        bool SamePrimary(T candidate)
        {
            var value = describe(candidate);
            return value.Score.Equals(boundaryValue.Score)
                && value.Actions == boundaryValue.Actions;
        }

        int end = boundary + 1;
        while (end < rankedPool.Count && SamePrimary(rankedPool[end]))
            end++;
        if (end == boundary + 1)
            return;
        int start = boundary;
        while (start > 0 && SamePrimary(rankedPool[start - 1]))
            start--;

        Dictionary<int, List<T>> byPotionCount = [];
        for (int index = start; index < end; index++)
        {
            T candidate = rankedPool[index];
            var value = describe(candidate);
            // Completed outcomes remain exclusively under the existing final policy.
            if (value.Victory)
                return;
            if (requiredSet.Contains(candidate))
                continue;
            if (!byPotionCount.TryGetValue(value.Potions, out List<T>? candidates))
            {
                candidates = [];
                byPotionCount.Add(value.Potions, candidates);
            }
            candidates.Add(candidate);
        }

        foreach (List<T> candidates in byPotionCount.Values)
        {
            List<int> slots = [];
            foreach (T candidate in candidates)
            {
                if (selectedPositions.TryGetValue(candidate, out int slot))
                    slots.Add(slot);
            }
            if (slots.Count <= 1 || slots.Count == candidates.Count)
                continue;

            List<List<T>> progressGroups = candidates
                .GroupBy(candidate => describe(candidate).OffensiveProgress)
                .OrderByDescending(group => group.Key)
                .Select(group => group.ToList())
                .ToList();
            if (progressGroups.Count <= 1)
                continue;

            if (describeTactical != null)
            {
                foreach (List<T> group in progressGroups)
                    OrderOrdinaryBeamTacticalCohorts(group, describeTactical);
            }

            // The supplemental progress key still supplies the first representative, but
            // one value cannot monopolize a partially retained primary tie. Tactical order
            // changes only route-less, equal-policy cohort slots within a progress group;
            // preserve routing positions, progress rotation and each potion count's seats.
            List<T> replacements = new(slots.Count);
            for (int round = 0; replacements.Count < slots.Count; round++)
            {
                foreach (List<T> group in progressGroups)
                {
                    if (round < group.Count)
                        replacements.Add(group[round]);
                    if (replacements.Count == slots.Count)
                        break;
                }
            }
            for (int index = 0; index < slots.Count; index++)
                selected[slots[index]] = replacements[index];
        }
    }

    private static void OrderOrdinaryBeamTacticalCohorts<T>(
        List<T> group,
        Func<T, OrdinaryBeamTacticalValues> describeTactical)
        where T : class
    {
        Dictionary<(int Turn, TranspositionLabel Policy),
            List<(int Position, T Candidate, OrdinaryBeamTacticalValues Values)>> cohorts = [];
        for (int position = 0; position < group.Count; position++)
        {
            T candidate = group[position];
            OrdinaryBeamTacticalValues values = describeTactical(candidate);
            // Existing routing representatives already have their own diversity policy.
            // Leave their positions fixed without blocking other positions in this group.
            if (values.HasRetainedRoutingChoice)
                continue;
            var key = (values.Turn, new TranspositionLabel(
                values.PotionCount,
                values.PotionStrategicCost,
                values.FutureSoldHp,
                values.CumulativePlayerHpLost,
                values.ActionCount,
                values.Score));
            if (!cohorts.TryGetValue(key, out var cohort))
            {
                cohort = [];
                cohorts.Add(key, cohort);
            }
            cohort.Add((position, candidate, values));
        }

        foreach (var cohort in cohorts.Values)
        {
            if (cohort.Count <= 1)
                continue;
            // Stable LINQ ordering preserves raw rank for fully equal tactical values.
            // Rewrite the cohort's original positions rather than flattening cohorts:
            // interleaved, unequal policy labels must retain their existing seats.
            T[] ordered = cohort
                .OrderByDescending(item => item.Values.ZeroCostPlayableCount)
                .ThenByDescending(item => item.Values.ReachableHandValue)
                .ThenByDescending(item => item.Values.HandCount)
                .Select(item => item.Candidate)
                .ToArray();
            for (int index = 0; index < cohort.Count; index++)
                group[cohort[index].Position] = ordered[index];
        }
    }
}
