using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
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

    internal static bool Observe(CombatState state, string reason)
    {
        if (!SolverSessionCapabilities.IsNetworkMultiplayer
            || !CombatManager.Instance.IsInProgress)
        {
            return false;
        }

        long now = Environment.TickCount64;
        if (_lastSampleAt > 0 && now - _lastSampleAt < MinimumSampleIntervalMilliseconds)
            return false;
        _lastSampleAt = now;

        Player? localPlayer = LocalContext.GetMe(state);
        string display = Describe(state, localPlayer);
        string hardFingerprint = HardFingerprint(state, localPlayer);
        bool changed = MultiplayerWorldTracker.ObserveSnapshot(hardFingerprint, reason);
        if (!changed)
            return false;

        _observationSequence++;
        Entry.Logger.Info(
            $"[CombatSolver/MultiplayerProbe] OBSERVED sequence={_observationSequence} " +
            $"world_version={MultiplayerWorldTracker.WorldVersion} reason={reason} " +
            $"hard_changed=true {display}");
        return true;
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
               $"rng={RngCounters(state)} enemies={enemies} remote_players={RemotePlayers(state, localPlayer)} {local}";
    }

    private static string HardFingerprint(CombatState state, Player? localPlayer)
    {
        string localContinuation = TryCaptureLocalContinuation(state, localPlayer);
        return $"local_continuation={localContinuation};remote_players={RemotePlayers(state, localPlayer)}";
    }

    private static string TryCaptureLocalContinuation(CombatState state, Player? localPlayer)
    {
        if (localPlayer?.PlayerCombatState == null)
            return $"unavailable;fallback={FallbackLocalFingerprint(localPlayer)}";

        try
        {
            // ContinuationStamp already covers the local combat fields whose semantic
            // changes invalidate a search, including upgraded/enchanted card state.
            return ContinuationStamp.CaptureLive(state).StateText;
        }
        catch (Exception)
        {
            // Probing must remain read-only and non-fatal while a native state is
            // between lifecycle phases. Keep a semantic local fallback for that gap.
            return $"unavailable;fallback={FallbackLocalFingerprint(localPlayer)}";
        }
    }

    private static string FallbackLocalFingerprint(Player? localPlayer)
    {
        PlayerCombatState? combat = localPlayer?.PlayerCombatState;
        if (localPlayer == null || combat == null)
            return "local=null";

        return $"net_id={localPlayer.NetId};hp={localPlayer.Creature.CurrentHp}/{localPlayer.Creature.MaxHp};" +
               $"block={localPlayer.Creature.Block};energy={combat.Energy};stars={combat.Stars};" +
               $"turn={combat.TurnNumber};phase={combat.Phase};" +
               $"hand={Cards(combat.Hand.Cards)};draw={Cards(combat.DrawPile.Cards)};" +
               $"discard={Cards(combat.DiscardPile.Cards)};exhaust={Cards(combat.ExhaustPile.Cards)};" +
               $"powers={Powers(localPlayer.Creature.Powers)}";
    }

    private static string RemotePlayers(CombatState state, Player? localPlayer)
        => string.Join(
            ';',
            state.Players
                .Where(player => localPlayer == null || player.NetId != localPlayer.NetId)
                .OrderBy(player => player.NetId)
                .Select(player =>
                {
                    PlayerCombatState? combat = player.PlayerCombatState;
                    return $"{player.NetId}:{player.Character.Id.Entry}:" +
                           $"turn={combat?.TurnNumber.ToString() ?? "-"}/phase={combat?.Phase.ToString() ?? "-"}:" +
                           $"hp={player.Creature.CurrentHp}/{player.Creature.MaxHp}:" +
                           $"block={player.Creature.Block}:powers={Powers(player.Creature.Powers)}";
                }));

    private static string Cards(IEnumerable<CardModel> cards)
        => string.Join(',', cards.Select(card =>
            $"{card.Id.Entry}+{card.CurrentUpgradeLevel}" +
            $"/enchant={EnchantmentStateSupport.Describe(card.Enchantment!)}" +
            $"/affliction={card.Affliction?.Id.Entry ?? "-"}:{card.Affliction?.Amount ?? 0}"));

    private static string Powers(IEnumerable<PowerModel> powers)
        => string.Join(',', powers
            .Where(power => power.Amount != 0)
            .OrderBy(power => power.Id.Entry, StringComparer.Ordinal)
            .Select(power => $"{power.Id.Entry}:{power.Amount}"));

    private static string RngCounters(CombatState state)
    {
        var rng = state.RunState.Rng;
        return $"shuffle={rng.Shuffle.Counter},card_gen={rng.CombatCardGeneration.Counter}," +
               $"potion_gen={rng.CombatPotionGeneration.Counter},card_select={rng.CombatCardSelection.Counter}," +
               $"energy={rng.CombatEnergyCosts.Counter},targets={rng.CombatTargets.Counter}," +
               $"orb={rng.CombatOrbGeneration.Counter},monster_ai={rng.MonsterAi.Counter},niche={rng.Niche.Counter}";
    }
}
