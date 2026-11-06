namespace CombatSolver;

// Origins are relative to this frozen search root, never live game objects.
internal enum PotionOrigin { RootInventory, Procured, EntropicBrew }

internal readonly record struct PredictedPotionUse(
    int Slot,
    string PotionId,
    int StrategicHpCost,
    bool Automatic,
    PotionOrigin Origin = PotionOrigin.RootInventory,
    int Turn = 0)
{
    internal bool IsPaidAmbergris => !Automatic && StrategicHpCost > 0 && PotionId == "AMBERGRIS";
}
