// Minimal native type shapes required by the linked multiplayer boundary contracts.
// This keeps the L1 contract project runnable on CI without a local Slay the Spire 2 install.
// Runtime/native integration remains covered by Multiplayer Lab evidence.

namespace MegaCrit.Sts2.Core.Models
{
    class AbstractModel;
    class RelicModel : AbstractModel;
}

namespace MegaCrit.Sts2.Core.Models.Relics
{
    sealed class BurningBlood : RelicModel;
    sealed class IceCream : RelicModel;
}

namespace MegaCrit.Sts2.Core.Entities.Players
{
    sealed class Player
    {
        public int NetId { get; set; }
    }
}
