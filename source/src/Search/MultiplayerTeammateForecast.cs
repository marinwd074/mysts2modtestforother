using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal readonly record struct TeammateForecastCardSnapshot(
    string CardId,
    int UpgradeLevel,
    string SemanticKey);

internal sealed class MultiplayerTeammateForecastState
{
    internal MultiplayerTeammateForecastState(
        string netId,
        string characterId,
        int turnNumber,
        PlayerTurnPhase phase,
        int currentHp,
        int maxHp,
        int block,
        int energy,
        int stars,
        IReadOnlyList<TeammateForecastCardSnapshot> hand,
        IReadOnlyList<TeammateForecastCardSnapshot> drawPile,
        IReadOnlyList<TeammateForecastCardSnapshot> discardPile,
        IReadOnlyList<TeammateForecastCardSnapshot> exhaustPile,
        IReadOnlyList<TeammateForecastCardSnapshot> playPile,
        int orbCapacity,
        IReadOnlyList<string> orbs)
    {
        NetId = netId;
        CharacterId = characterId;
        TurnNumber = turnNumber;
        Phase = phase;
        CurrentHp = currentHp;
        MaxHp = maxHp;
        Block = block;
        Energy = energy;
        Stars = stars;
        Hand = hand;
        DrawPile = drawPile;
        DiscardPile = discardPile;
        ExhaustPile = exhaustPile;
        PlayPile = playPile;
        OrbCapacity = orbCapacity;
        Orbs = orbs;
    }

    public string NetId { get; }
    public string CharacterId { get; }
    public int TurnNumber { get; }
    public PlayerTurnPhase Phase { get; }
    public int CurrentHp { get; }
    public int MaxHp { get; }
    public int Block { get; }
    public int Energy { get; }
    public int Stars { get; }
    public IReadOnlyList<TeammateForecastCardSnapshot> Hand { get; }
    public IReadOnlyList<TeammateForecastCardSnapshot> DrawPile { get; }
    public IReadOnlyList<TeammateForecastCardSnapshot> DiscardPile { get; }
    public IReadOnlyList<TeammateForecastCardSnapshot> ExhaustPile { get; }
    public IReadOnlyList<TeammateForecastCardSnapshot> PlayPile { get; }
    public int OrbCapacity { get; }
    public IReadOnlyList<string> Orbs { get; }
}

internal static class MultiplayerTeammateForecastCapture
{
    internal static IReadOnlyList<MultiplayerTeammateForecastState> Capture(
        CombatPredictionSimulator simulator,
        Player localPlayer)
    {
        if (simulator.State.CombatState is not SimulatedCombatState simulatedCombat)
            return [];

        return simulator.State.RootCapturedPlayers
            .Where(player => !ReferenceEquals(player, localPlayer))
            .OrderBy(player => player.NetId)
            .Select(player => CapturePlayer(simulator, simulatedCombat, player))
            .ToArray();
    }

    private static MultiplayerTeammateForecastState CapturePlayer(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player)
    {
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
        SimCreatureState creature = simulator.State.GetCreature(player.Creature);
        SimOrbQueue orbQueue = playerState.OrbQueue;
        return new MultiplayerTeammateForecastState(
            player.NetId.ToString(),
            player.Character.Id.Entry,
            combat.GetPlayerTurnNumber(player),
            playerState.Phase,
            creature.CurrentHp,
            creature.MaxHp,
            creature.Block,
            playerState.Energy,
            playerState.Stars,
            CaptureCards(playerState.Hand.Cards),
            CaptureCards(playerState.DrawPile.Cards),
            CaptureCards(playerState.DiscardPile.Cards),
            CaptureCards(playerState.ExhaustPile.Cards),
            CaptureCards(playerState.PlayPile.Cards),
            orbQueue.Capacity,
            Array.AsReadOnly(orbQueue.Orbs.Select(orb => orb.Id.Entry).ToArray()));
    }

    private static IReadOnlyList<TeammateForecastCardSnapshot> CaptureCards(
        IEnumerable<PredictedCard> cards)
        => Array.AsReadOnly(cards
            .Select(card => new TeammateForecastCardSnapshot(
                card.Preview.Id.Entry,
                card.Preview.CurrentUpgradeLevel,
                CardChoiceSupport.ChoiceCardKey(card)))
            .ToArray());
}
