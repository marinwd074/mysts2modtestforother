namespace MegaCrit.Sts2.Core.Entities.Creatures
{
    internal sealed class Creature;
}
namespace MegaCrit.Sts2.Core.Entities.Players
{
    internal sealed class Player
    {
        public MegaCrit.Sts2.Core.Entities.Creatures.Creature Creature { get; } = new();
    }
}
namespace MegaCrit.Sts2.Core.Entities.Cards
{
    internal sealed class CardPlay
    {
        public required MegaCrit.Sts2.Core.Entities.Players.Player Player { get; init; }
    }
}
namespace MegaCrit.Sts2.Core.Models.Orbs
{
    internal class OrbModel
    {
        public MegaCrit.Sts2.Core.Entities.Players.Player? Owner { get; init; }
    }
    internal sealed class LightningOrb : OrbModel;
}
namespace CombatSolver.Engine.InCombat.Simulation
{
    using MegaCrit.Sts2.Core.Entities.Cards;
    using MegaCrit.Sts2.Core.Entities.Creatures;
    using MegaCrit.Sts2.Core.Entities.Players;
    using MegaCrit.Sts2.Core.Models.Orbs;

    internal abstract class CombatPredictionHistoryEntry;
    internal sealed class CombatPredictionCardPlayFinishedEntry : CombatPredictionHistoryEntry
    {
        public required CardSnapshot Card { get; init; }
        public required CardPlay CardPlay { get; init; }
        public bool WasEthereal { get; init; }
    }
    internal sealed class CombatPredictionOrbChanneledEntry : CombatPredictionHistoryEntry
    {
        public required OrbModel Orb { get; init; }
    }
    internal sealed class DamageResult { public int UnblockedDamage { get; init; } }
    internal sealed class CombatPredictionDamageReceivedEntry : CombatPredictionHistoryEntry
    {
        public required Creature Receiver { get; init; }
        public required DamageResult Result { get; init; }
    }
    internal sealed class CardSnapshot { public Player? Owner { get; init; } }
    internal sealed class CombatPredictionCardDrawnEntry : CombatPredictionHistoryEntry
    {
        public required CardSnapshot Card { get; init; }
    }
    internal sealed class CombatPredictionCardGeneratedEntry : CombatPredictionHistoryEntry
    {
        public Player? Creator { get; init; }
    }
    internal sealed class CombatPredictionHistory : List<CombatPredictionHistoryEntry>
    {
        public CombatHistoryCounters Counters { get; set; }
        public CombatHistoryCounters GetCounters(Player owner) => Counters;
    }
    internal sealed class CombatPredictionSimulator
    {
        public CombatPredictionHistory History { get; } = [];
    }
}
namespace CombatSolver
{
    internal struct StateFingerprintBuilder
    {
        private List<string>? _parts;
        public void Add(char value) => (_parts ??= []).Add(value.ToString());
        public void Add(int value) => (_parts ??= []).Add(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        public string Snapshot() => string.Join("|", _parts ?? []);
    }
}
