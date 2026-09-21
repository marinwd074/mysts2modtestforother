using System.Collections.Frozen;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Orbs;

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
    {
        Creature ownerCreature = owner.Creature;
        int finishedPlays = 0;
        int etherealPlays = 0;
        int lightningChannels = 0;
        int unblockedHitsReceived = 0;
        int cardsDrawn = 0;
        int cardsGenerated = 0;
        foreach (CombatPredictionHistoryEntry entry in simulator.History)
        {
            switch (entry)
            {
                case CombatPredictionCardPlayFinishedEntry play:
                    finishedPlays++;
                    if (play.WasEthereal && play.CardPlay.Player == owner)
                        etherealPlays++;
                    break;
                case CombatPredictionOrbChanneledEntry channel:
                    if (channel.Orb is LightningOrb && channel.Orb.Owner == owner)
                        lightningChannels++;
                    break;
                case CombatPredictionDamageReceivedEntry damage:
                    if (damage.Receiver == ownerCreature && damage.Result.UnblockedDamage > 0)
                        unblockedHitsReceived++;
                    break;
                case CombatPredictionCardDrawnEntry drawn:
                    if (drawn.Card.Owner == owner)
                        cardsDrawn++;
                    break;
                case CombatPredictionCardGeneratedEntry generated:
                    if (generated.Creator == owner)
                        cardsGenerated++;
                    break;
            }
        }
        key.Add('h');
        key.Add(finishedPlays);
        key.Add(etherealPlays);
        key.Add(lightningChannels);
        key.Add(unblockedHitsReceived);
        key.Add(cardsDrawn);
        key.Add(cardsGenerated);
    }
}
