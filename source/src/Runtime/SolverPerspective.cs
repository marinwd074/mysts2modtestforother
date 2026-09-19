using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal readonly record struct SolverPlayerSlot(int Value)
{
    internal static SolverPlayerSlot Local => new(0);
}

[Flags]
internal enum PlayerKnowledgeFlags
{
    None = 0,
    PublicCombatState = 1 << 0,
    Hand = 1 << 1,
    DrawPile = 1 << 2,
    DrawPileOrder = 1 << 3,
    DiscardPile = 1 << 4,
    ExhaustPile = 1 << 5,
    Potions = 1 << 6,
    Relics = 1 << 7,
    RunDeck = 1 << 8,
}

/// <summary>
/// A root-level observation perspective. Knowledge flags describe what may be
/// observed or simulated; they do not grant authority to act for another player.
/// Missing remote flags are intentionally Unknown rather than an empty collection.
/// </summary>
internal readonly record struct SolverPerspective(
    SolverPlayerSlot LocalPlayer,
    string LocalPlayerNetId,
    int PlayerCount,
    bool IsMultiplayer,
    PlayerKnowledgeFlags LocalKnowledge,
    PlayerKnowledgeFlags RemoteKnowledge)
{
    internal static SolverPerspective Capture(Player localPlayer, int playerCount, bool isMultiplayer)
        => new(
            SolverPlayerSlot.Local,
            localPlayer.NetId.ToString(),
            playerCount,
            isMultiplayer,
            PlayerKnowledgeFlags.PublicCombatState
                | PlayerKnowledgeFlags.Hand
                | PlayerKnowledgeFlags.DrawPile
                | PlayerKnowledgeFlags.DrawPileOrder
                | PlayerKnowledgeFlags.DiscardPile
                | PlayerKnowledgeFlags.ExhaustPile
                | PlayerKnowledgeFlags.Potions
                | PlayerKnowledgeFlags.Relics
                | PlayerKnowledgeFlags.RunDeck,
            isMultiplayer ? PlayerKnowledgeFlags.PublicCombatState : PlayerKnowledgeFlags.None);

    internal bool KnowsLocal(PlayerKnowledgeFlags field)
        => (LocalKnowledge & field) == field;

    internal bool KnowsRemote(PlayerKnowledgeFlags field)
        => (RemoteKnowledge & field) == field;

    internal bool IsRemoteUnknown(PlayerKnowledgeFlags field)
        => !KnowsRemote(field);
}
