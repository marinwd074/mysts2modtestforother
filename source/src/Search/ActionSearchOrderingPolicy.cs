namespace CombatSolver;

internal readonly record struct ActionSearchOrderHint(
    bool EstimatedLethal,
    bool UrgentDefense,
    double StrategicValuePerResource,
    double StrategicValue,
    int ResourceCost,
    int StableOrdinal);

internal static class ActionSearchOrderingPolicy
{
    internal static int Compare(ActionSearchOrderHint left, ActionSearchOrderHint right)
    {
        int comparison = right.EstimatedLethal.CompareTo(left.EstimatedLethal);
        if (comparison != 0)
            return comparison;

        comparison = right.UrgentDefense.CompareTo(left.UrgentDefense);
        if (comparison != 0)
            return comparison;

        comparison = right.StrategicValuePerResource.CompareTo(left.StrategicValuePerResource);
        if (comparison != 0)
            return comparison;

        comparison = right.StrategicValue.CompareTo(left.StrategicValue);
        if (comparison != 0)
            return comparison;

        comparison = left.ResourceCost.CompareTo(right.ResourceCost);
        return comparison != 0
            ? comparison
            : left.StableOrdinal.CompareTo(right.StableOrdinal);
    }

    internal static bool VerifyForTesting()
    {
        ActionSearchOrderHint[] values =
        [
            new(false, false, 4d, 8d, 2, 0),
            new(false, true, 2d, 5d, 1, 1),
            new(true, false, 1d, 3d, 1, 2),
            new(false, false, 4d, 8d, 2, 3),
        ];

        Array.Sort(values, Compare);
        return values[0].EstimatedLethal
            && values[1].UrgentDefense
            && values[2].StableOrdinal == 0
            && values[3].StableOrdinal == 3;
    }
}
