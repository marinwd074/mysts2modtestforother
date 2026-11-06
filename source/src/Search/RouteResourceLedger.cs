namespace CombatSolver;

// Root-relative, realized amounts only. Projected death saves and terminal relic
// alignment are not cumulative events and must never be subtracted as history.
internal readonly record struct RouteResourceTotals(
    int HpLost, int HpRecovered, int RelicReviveHp, int PotionReviveHp, int DeathSaves,
    int RealizedLongTermValue, int AngerCopies, int GrowthHpCredit, GrowthValues Growth)
{
    internal static RouteResourceTotals Capture(SimulationSnapshot snapshot)
        => new(snapshot.CumulativePlayerHpLost, snapshot.RecoveredPlayerHp,
            snapshot.DeathSaveRelicHpRestored, snapshot.DeathSavePotionHpRestored, snapshot.DeathSaveUseCount,
            snapshot.RealizedLongTermResourceValue, snapshot.AngerCopiesGenerated, snapshot.GrowthHpCredit, snapshot.GrowthRewards);

    internal static RouteResourceTotals Capture(SolverSnapshot snapshot)
        => new(snapshot.CumulativePlayerHpLost, snapshot.RecoveredPlayerHp,
            snapshot.DeathSaveRelicHpRestored, snapshot.DeathSavePotionHpRestored, snapshot.DeathSaveUseCount,
            snapshot.RealizedLongTermResourceValue, snapshot.AngerCopiesGenerated, snapshot.GrowthHpCredit, snapshot.GrowthRewards);

    internal bool TrySubtract(RouteResourceTotals earlier, out RouteResourceTotals remaining)
    {
        remaining = default;
        if (HpLost < earlier.HpLost || HpRecovered < earlier.HpRecovered
            || RelicReviveHp < earlier.RelicReviveHp || PotionReviveHp < earlier.PotionReviveHp
            || DeathSaves < earlier.DeathSaves || RealizedLongTermValue < earlier.RealizedLongTermValue
            || AngerCopies < earlier.AngerCopies || GrowthHpCredit < earlier.GrowthHpCredit
            || !TrySubtractGrowth(Growth, earlier.Growth, out GrowthValues growth))
            return false;
        remaining = new(HpLost - earlier.HpLost, HpRecovered - earlier.HpRecovered,
            RelicReviveHp - earlier.RelicReviveHp, PotionReviveHp - earlier.PotionReviveHp, DeathSaves - earlier.DeathSaves,
            RealizedLongTermValue - earlier.RealizedLongTermValue, AngerCopies - earlier.AngerCopies,
            GrowthHpCredit - earlier.GrowthHpCredit, growth);
        return true;
    }

    internal static bool TrySubtractGrowth(GrowthValues total, GrowthValues earlier, out GrowthValues remaining)
    {
        remaining = total;
        if (!total.Satisfies(earlier)) return false;
        foreach (GrowthSource source in Enum.GetValues<GrowthSource>())
            remaining = remaining.With(source, total.Get(source) - earlier.Get(source));
        foreach (var entry in earlier.Extras.Entries)
            remaining = remaining with { Extras = remaining.Extras.With(entry.Key, total.Extras.Get(entry.Key) - entry.Value) };
        return true;
    }
}

internal readonly record struct RouteResourceCheckpoint(int Turn, RouteResourceTotals Realized);

internal sealed class RouteResourceLedger
{
    internal int StartTurn { get; }
    internal IReadOnlyList<RouteResourceCheckpoint> Checkpoints { get; }

    internal RouteResourceLedger(int startTurn, IReadOnlyList<RouteResourceCheckpoint> checkpoints)
    {
        StartTurn = startTurn;
        Checkpoints = Array.AsReadOnly(checkpoints.ToArray());
    }

    internal static RouteResourceLedger Capture(SearchNode best, int startTurn)
    {
        List<RouteResourceCheckpoint> checkpoints = [];
        for (SearchNode? node = best; node?.Parent != null; node = node.Parent)
        {
            if (node.Action is { } action && action.Turn < node.Turn)
                checkpoints.Add(new(node.Turn, RouteResourceTotals.Capture(node.Snapshot)));
        }
        checkpoints.Reverse();
        return new(startTurn, checkpoints);
    }

    internal bool TryAdvance(int turn, out RouteResourceTotals past, out RouteResourceLedger remaining)
    {
        past = default;
        remaining = this;
        if (turn == StartTurn) return true;
        if (turn < StartTurn) return false;
        int index = -1;
        for (int i = 0; i < Checkpoints.Count; i++)
            if (Checkpoints[i].Turn == turn) { index = i; break; }
        if (index < 0) return false;
        past = Checkpoints[index].Realized;
        List<RouteResourceCheckpoint> future = [];
        for (int i = index + 1; i < Checkpoints.Count; i++)
        {
            if (!Checkpoints[i].Realized.TrySubtract(past, out var values)) return false;
            future.Add(new(Checkpoints[i].Turn, values));
        }
        remaining = new(turn, future);
        return true;
    }

    internal bool TryRebase(SolverSnapshot source, int turn, out SolverSnapshot snapshot, out RouteResourceLedger ledger)
    {
        snapshot = source;
        if (!TryAdvance(turn, out var past, out ledger)
            || !RouteResourceTotals.Capture(source).TrySubtract(past, out var future)
            || source.ProjectedDeathSaveUseCount < past.DeathSaves)
            return false;
        snapshot = source with
        {
            CumulativePlayerHpLost = future.HpLost, RecoveredPlayerHp = future.HpRecovered,
            DeathSaveRelicHpRestored = future.RelicReviveHp, DeathSavePotionHpRestored = future.PotionReviveHp,
            DeathSaveUseCount = future.DeathSaves, ProjectedDeathSaveUseCount = source.ProjectedDeathSaveUseCount - past.DeathSaves,
            RealizedLongTermResourceValue = future.RealizedLongTermValue,
            LongTermResourceValue = source.LongTermResourceValue - past.RealizedLongTermValue,
            AngerCopiesGenerated = future.AngerCopies, GrowthHpCredit = future.GrowthHpCredit, GrowthRewards = future.Growth,
        };
        return true;
    }
}
