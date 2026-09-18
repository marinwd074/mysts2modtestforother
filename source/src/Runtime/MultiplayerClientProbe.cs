using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

/// <summary>
/// Read-only Phase 0 observation for a network multiplayer client. It records only the
/// local player's visible combat state and public enemy state; it never starts a search,
/// mutates CombatState/RNG, enqueues an action, or sends a packet.
/// </summary>
internal static class MultiplayerClientProbe
{
    private const int MinimumSampleIntervalMilliseconds = 100;
    private static long _lastSampleAt;
    private static int _observationSequence;

    internal static void Reset()
    {
        _lastSampleAt = 0;
        _observationSequence = 0;
        MultiplayerWorldTracker.Reset();
    }

    internal static void Observe(CombatState state, string reason)
    {
        if (!SolverSessionCapabilities.IsNetworkMultiplayer
            || !CombatManager.Instance.IsInProgress)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (_lastSampleAt > 0 && now - _lastSampleAt < MinimumSampleIntervalMilliseconds)
            return;
        _lastSampleAt = now;

        Player? localPlayer = LocalContext.GetMe(state);
        string fingerprint = Describe(state, localPlayer);
        if (!MultiplayerWorldTracker.ObserveSnapshot(fingerprint, reason))
            return;

        _observationSequence++;
        Entry.Logger.Info(
            $"[CombatSolver/MultiplayerProbe] OBSERVED sequence={_observationSequence} " +
            $"world_version={MultiplayerWorldTracker.WorldVersion} reason={reason} " +
            fingerprint);
    }

    private static string Describe(CombatState state, Player? localPlayer)
    {
        PlayerCombatState? combat = localPlayer?.PlayerCombatState;
        string local = localPlayer == null || combat == null
            ? "local=null"
            : $"local_net_id={localPlayer.NetId} character={localPlayer.Character.Id.Entry} " +
              $"hp={localPlayer.Creature.CurrentHp}/{localPlayer.Creature.MaxHp} " +
              $"block={localPlayer.Creature.Block} energy={combat.Energy} stars={combat.Stars} " +
              $"turn={combat.TurnNumber} phase={combat.Phase} " +
              $"hand={Cards(combat.Hand.Cards)} " +
              $"draw={Cards(combat.DrawPile.Cards)} " +
              $"discard={Cards(combat.DiscardPile.Cards)} " +
              $"exhaust={Cards(combat.ExhaustPile.Cards)} " +
              $"potions={string.Join(',', localPlayer.PotionSlots.Select(potion => potion?.Id.Entry ?? "-"))}";

        string enemies = string.Join(
            ';',
            state.Enemies.Select(enemy =>
                $"{enemy.CombatId?.ToString() ?? "-"}:{enemy.Monster?.Id.Entry ?? "-"}:" +
                $"{enemy.CurrentHp}/{enemy.MaxHp}/{enemy.Block}:{enemy.Monster?.NextMove?.Id ?? "-"}"));

        return $"net_type={RunManager.Instance.NetService.Type} players={state.Players.Count} " +
               $"round={state.RoundNumber} side={state.CurrentSide} " +
               $"seed={state.RunState.Rng.StringSeed} " +
               $"rng={RngCounters(state)} enemies={enemies} {local}";
    }

    private static string Cards(IEnumerable<CardModel> cards)
        => string.Join(',', cards.Select(card => card.Id.Entry));

    private static string RngCounters(CombatState state)
    {
        var rng = state.RunState.Rng;
        return $"shuffle={rng.Shuffle.Counter},card_gen={rng.CombatCardGeneration.Counter}," +
               $"potion_gen={rng.CombatPotionGeneration.Counter},card_select={rng.CombatCardSelection.Counter}," +
               $"energy={rng.CombatEnergyCosts.Counter},targets={rng.CombatTargets.Counter}," +
               $"orb={rng.CombatOrbGeneration.Counter},monster_ai={rng.MonsterAi.Counter},niche={rng.Niche.Counter}";
    }
}
