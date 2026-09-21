using System.Collections.Frozen;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal static class CombatHistoryCounterKey
{
    private static readonly FrozenSet<string> CardIds = new[]
    {
        "GOLD_AXE",
        "VOLTAIC",
        "TEAR_ASUNDER",
        "PULL_FROM_BELOW",
        "MURDER",
        "SUPERMASSIVE",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool AppliesTo(IReadOnlySet<string> playerCardIds)
    {
        foreach (string cardId in CardIds)
            if (playerCardIds.Contains(cardId))
                return true;
        return false;
    }

    public static void Append(ref StateFingerprintBuilder key, CombatPredictionSimulator simulator, Player owner)
        => AppendCounters(ref key, simulator.History.GetCounters(owner));

    internal static void AppendCounters(ref StateFingerprintBuilder key, CombatHistoryCounters counters)
    {
        key.Add('h');
        key.Add(counters.FinishedPlays);
        key.Add(counters.EtherealPlays);
        key.Add(counters.LightningChannels);
        key.Add(counters.UnblockedHitsReceived);
        key.Add(counters.CardsDrawn);
        key.Add(counters.CardsGenerated);
    }
}
