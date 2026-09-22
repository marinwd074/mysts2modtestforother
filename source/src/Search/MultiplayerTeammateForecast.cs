using System.Text;
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


internal static class MultiplayerContinuationRemoteFingerprint
{
    internal static StateFingerprint CaptureLive(
        CombatState state,
        Player localPlayer)
    {
        StateFingerprintBuilder fingerprint = new();
        fingerprint.Add(state.Players.Count);
        foreach (Player player in state.Players
                     .Where(candidate => candidate.NetId != localPlayer.NetId)
                     .OrderBy(candidate => candidate.NetId))
        {
            AppendLivePlayer(ref fingerprint, player);
        }
        return fingerprint.Finish();
    }

    internal static StateFingerprint CapturePredicted(
        CombatPredictionSimulator simulator,
        Player localPlayer)
    {
        SimulatedCombatState combat =
            (SimulatedCombatState)simulator.State.CombatState;
        StateFingerprintBuilder fingerprint = new();
        fingerprint.Add(simulator.State.RootCapturedPlayers.Count);
        foreach (Player player in simulator.State.RootCapturedPlayers
                     .Where(candidate => !ReferenceEquals(candidate, localPlayer))
                     .OrderBy(candidate => candidate.NetId))
        {
            AppendPredictedPlayer(ref fingerprint, simulator, combat, player);
        }
        return fingerprint.Finish();
    }

    private static void AppendLivePlayer(
        ref StateFingerprintBuilder fingerprint,
        Player player)
    {
        fingerprint.Add(player.NetId.ToString());
        fingerprint.Add(player.Character.Id.Entry);
        fingerprint.Add(player.Creature.CurrentHp);
        fingerprint.Add(player.Creature.MaxHp);
        fingerprint.Add(player.Creature.Block);
        fingerprint.Add(player.Gold);

        PlayerCombatState? playerState = player.PlayerCombatState;
        fingerprint.Add(playerState != null);
        if (playerState == null)
            return;

        fingerprint.Add(playerState.TurnNumber);
        fingerprint.Add(playerState.Phase.ToString());
        fingerprint.Add(playerState.Energy);
        fingerprint.Add(playerState.Stars);
        AppendCardKeys(ref fingerprint, playerState.Hand.Cards.Select(CardChoiceSupport.ChoiceCardKey));
        AppendCardKeys(ref fingerprint, playerState.DrawPile.Cards.Select(CardChoiceSupport.ChoiceCardKey));
        AppendCardKeys(ref fingerprint, playerState.DiscardPile.Cards.Select(CardChoiceSupport.ChoiceCardKey));
        AppendCardKeys(ref fingerprint, playerState.ExhaustPile.Cards.Select(CardChoiceSupport.ChoiceCardKey));
        AppendCardKeys(ref fingerprint, playerState.PlayPile.Cards.Select(CardChoiceSupport.ChoiceCardKey));

        fingerprint.Add(playerState.OrbQueue.Capacity);
        fingerprint.Add(playerState.OrbQueue.Orbs.Count);
        foreach (var orb in playerState.OrbQueue.Orbs)
        {
            fingerprint.Add(orb.Id.Entry);
            fingerprint.Add(orb.PassiveVal.ToString());
            fingerprint.Add(orb.EvokeVal.ToString());
        }

        fingerprint.Add(player.PotionSlots.Count);
        foreach (var potion in player.PotionSlots)
            fingerprint.Add(potion?.Id.Entry ?? "-");

        fingerprint.Add(player.Relics.Count);
        foreach (var relic in player.Relics)
        {
            fingerprint.Add(relic.Id.Entry);
            fingerprint.Add(relic.IsMelted);
        }
        StringBuilder relicState = new();
        SimulatedCombatState.AppendLiveStatefulRelics(relicState, player);
        RelicPredictionStateSupport.AppendLiveContinuation(relicState, player.Relics);
        fingerprint.Add(relicState.ToString());
    }

    private static void AppendPredictedPlayer(
        ref StateFingerprintBuilder fingerprint,
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player)
    {
        SimCreatureState creature = simulator.State.GetCreature(player.Creature);
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);

        fingerprint.Add(player.NetId.ToString());
        fingerprint.Add(player.Character.Id.Entry);
        fingerprint.Add(creature.CurrentHp);
        fingerprint.Add(creature.MaxHp);
        fingerprint.Add(creature.Block);
        fingerprint.Add(combat.GetPlayerGold(player));
        fingerprint.Add(true);
        fingerprint.Add(combat.GetPlayerTurnNumber(player));
        fingerprint.Add(playerState.Phase.ToString());
        fingerprint.Add(playerState.Energy);
        fingerprint.Add(playerState.Stars);
        AppendCardKeys(ref fingerprint, playerState.Hand.Cards.Select(CardChoiceSupport.ChoiceCardKey));
        AppendCardKeys(ref fingerprint, playerState.DrawPile.Cards.Select(CardChoiceSupport.ChoiceCardKey));
        AppendCardKeys(ref fingerprint, playerState.DiscardPile.Cards.Select(CardChoiceSupport.ChoiceCardKey));
        AppendCardKeys(ref fingerprint, playerState.ExhaustPile.Cards.Select(CardChoiceSupport.ChoiceCardKey));
        AppendCardKeys(ref fingerprint, playerState.PlayPile.Cards.Select(CardChoiceSupport.ChoiceCardKey));

        fingerprint.Add(playerState.OrbQueue.Capacity);
        fingerprint.Add(playerState.OrbQueue.Orbs.Count);
        foreach (var orb in playerState.OrbQueue.Orbs)
        {
            fingerprint.Add(orb.Id.Entry);
            fingerprint.Add(orb.PassiveVal.ToString());
            fingerprint.Add(orb.EvokeVal.ToString());
        }

        int potionSlotCount = combat.CapturedPotionSlotCount(player);
        fingerprint.Add(potionSlotCount);
        for (int slot = 0; slot < potionSlotCount; slot++)
            fingerprint.Add(combat.GetPotionAtSlot(player, slot)?.Id.Entry ?? "-");

        IReadOnlyList<MegaCrit.Sts2.Core.Models.RelicModel> relics = combat.RelicsOf(player);
        fingerprint.Add(relics.Count);
        foreach (var relic in relics)
        {
            fingerprint.Add(relic.Id.Entry);
            fingerprint.Add(relic.IsMelted);
        }
        StringBuilder relicState = new();
        combat.AppendPredictedStatefulRelics(relicState, player);
        RelicPredictionStateSupport.AppendPredictedContinuation(
            relicState,
            simulator,
            relics);
        fingerprint.Add(relicState.ToString());
    }

    private static void AppendCardKeys(
        ref StateFingerprintBuilder fingerprint,
        IEnumerable<string> keys)
    {
        string[] materialized = keys.ToArray();
        fingerprint.Add(materialized.Length);
        for (int index = 0; index < materialized.Length; index++)
            fingerprint.Add(materialized[index]);
    }
}
