namespace CombatSolver;

internal sealed partial class PotionStrategySnapshot
{
    // Value snapshots survive simulator release; never reach back into a live or
    // released branch merely to establish which root bottle fulfilled a directive.
    public ForcedPotionUseEvaluation EvaluateForcedUses(SearchNode node)
        => EvaluateForcedUses(EnumerateExplicitUses(node));

    private static IEnumerable<PredictedPotionUse> EnumerateExplicitUses(SearchNode node)
    {
        for (SearchNode? cursor = node; cursor?.Parent != null; cursor = cursor.Parent)
        {
            if (cursor.Action is not { Kind: PlanActionKind.UsePotion } action) continue;
            yield return new(action.PotionSlot, action.PotionId,
                cursor.Snapshot.ExplicitPotionStrategicCost - cursor.Parent.Snapshot.ExplicitPotionStrategicCost,
                false, cursor.Snapshot.LastExplicitPotionOrigin, action.Turn);
        }
    }

    internal static IReadOnlyList<PredictedPotionUse> CaptureUses(SearchNode node, IReadOnlyList<PredictedPotionUse> uses)
    {
        PredictedPotionUse[] result = uses.ToArray();
        for (SearchNode? cursor = node; cursor != null; cursor = cursor.Parent)
        {
            int start = cursor.Parent?.PotionCount ?? 0;
            for (int index = start; index < cursor.PotionCount; index++)
                result[index] = result[index] with { Turn = cursor.Action?.Turn ?? cursor.Turn };
        }
        return Array.AsReadOnly(result);
    }
}
