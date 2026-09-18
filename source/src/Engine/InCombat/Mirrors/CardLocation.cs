using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver.Engine.InCombat.Mirrors;

// StS2 0.107.1 does not expose the later CardLocation value type. Keep the
// owner-aware result location local to the prediction engine for this port.
internal struct CardLocation : IEquatable<CardLocation>
{
    public Player player;
    public PileType pileType;
    public CardPilePosition position;

    public CardLocation(Player player, PileType pileType, CardPilePosition position)
    {
        this.player = player;
        this.pileType = pileType;
        this.position = position;
    }

    public bool Equals(CardLocation other) =>
        ReferenceEquals(player, other.player) &&
        pileType == other.pileType &&
        position == other.position;

    public override bool Equals(object? obj) => obj is CardLocation other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(player, pileType, position);

    public static bool operator ==(CardLocation left, CardLocation right) => left.Equals(right);

    public static bool operator !=(CardLocation left, CardLocation right) => !left.Equals(right);
}
