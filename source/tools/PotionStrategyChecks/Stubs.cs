namespace CombatSolver;

internal enum SolverPotionPolicy { Disabled, Smart, RequireAtLeastOne }
internal enum PlanActionKind { UsePotion, PlayCard, EndTurn }

internal sealed record PlanAction(
    PlanActionKind Kind,
    int PotionSlot = -1,
    string? PotionId = null);

internal sealed class PotionStrategicCostLookup
{
    public int Get(string potionId, bool renewablePotionShapedRock) => potionId == "AMBERGRIS" ? 4 : 3;
}

internal static class PotionUsePolicy
{
    public static int StrategicHpCost(string potionId, bool renewablePotionShapedRock)
        => potionId == "AMBERGRIS" ? 4 : 3;
}
