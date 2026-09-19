using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using CombatSolver.Engine.Common;
using CombatSolver.Replay;

namespace CombatSolver;

internal sealed record MultiplayerProbeSnapshot(
    int SchemaVersion,
    long CapturedAtUnixMilliseconds,
    int Sequence,
    long WorldVersion,
    string Reason,
    string NetworkType,
    int PlayerCount,
    int RoundNumber,
    string CurrentSide,
    string? LocalNetId,
    string? LocalCharacter,
    string? LocalHp,
    string? LocalBlock,
    string? LocalEnergy,
    string? LocalStars,
    string? LocalTurn,
    string? LocalPhase,
    string[] LocalHand,
    string[] LocalDrawPile,
    string[] LocalDiscard,
    string[] LocalExhaust,
    string[] LocalPotions,
    string[] LocalPowers,
    string[] RemotePlayers,
    string[] Enemies,
    string[] RngStates,
    string HardFingerprint,
    bool ReadOnly,
    bool SearchStarted,
    bool ActionsEnqueued,
    bool CustomNetworkPacketSent);

/// <summary>
/// Read-only Phase 0 observation for a network multiplayer client. It records only the
/// local player's visible combat state and public enemy state; it never starts a search,
/// mutates CombatState/RNG, enqueues an action, or sends a packet.
/// </summary>
internal static class MultiplayerClientProbe
{
    private const int MinimumSampleIntervalMilliseconds = 100;
    private const int ProbeEvidenceEstimatedBytes = 16 * 1024;
    private static readonly object EvidenceGate = new();
    private static readonly JsonSerializerOptions EvidenceJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly string EvidenceFileName =
        $"multiplayer-probe-{Environment.ProcessId}-{Guid.NewGuid():N}.jsonl";
    private static long _lastSampleAt;
    private static int _observationSequence;
    private static AppendOnlyEventLog<MultiplayerProbeSnapshot>? _evidenceLog;
    private static bool _evidenceDisabled;

    internal static void Reset()
    {
        _lastSampleAt = 0;
        _observationSequence = 0;
        MultiplayerWorldTracker.Reset();
    }

    internal static void Dispose()
    {
        lock (EvidenceGate)
        {
            _evidenceLog?.Dispose();
            _evidenceLog = null;
        }
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
        WriteEvidence(CaptureSnapshot(
            state,
            localPlayer,
            reason,
            hardFingerprint,
            _observationSequence));
        Entry.Logger.Info(
            $"[CombatSolver/MultiplayerProbe] OBSERVED sequence={_observationSequence} " +
            $"world_version={MultiplayerWorldTracker.WorldVersion} reason={reason} " +
            $"hard_changed=true {display}");
        return true;
    }

    private static MultiplayerProbeSnapshot CaptureSnapshot(
        CombatState state,
        Player? localPlayer,
        string reason,
        string hardFingerprint,
        int sequence)
    {
        PlayerCombatState? combat = localPlayer?.PlayerCombatState;
        return new(
            SchemaVersion: 1,
            CapturedAtUnixMilliseconds: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Sequence: sequence,
            WorldVersion: MultiplayerWorldTracker.WorldVersion,
            Reason: reason,
            NetworkType: RunManager.Instance.NetService.Type.ToString(),
            PlayerCount: state.Players.Count,
            RoundNumber: state.RoundNumber,
            CurrentSide: state.CurrentSide.ToString(),
            LocalNetId: localPlayer?.NetId.ToString(),
            LocalCharacter: localPlayer?.Character.Id.Entry,
            LocalHp: combat == null || localPlayer == null
                ? null
                : $"{localPlayer.Creature.CurrentHp}/{localPlayer.Creature.MaxHp}",
            LocalBlock: combat == null || localPlayer == null
                ? null
                : localPlayer.Creature.Block.ToString(),
            LocalEnergy: combat?.Energy.ToString(),
            LocalStars: combat?.Stars.ToString(),
            LocalTurn: combat?.TurnNumber.ToString(),
            LocalPhase: combat?.Phase.ToString(),
            LocalHand: combat == null ? [] : CardTokens(combat.Hand.Cards),
            LocalDrawPile: combat == null ? [] : CardTokens(combat.DrawPile.Cards),
            LocalDiscard: combat == null ? [] : CardTokens(combat.DiscardPile.Cards),
            LocalExhaust: combat == null ? [] : CardTokens(combat.ExhaustPile.Cards),
            LocalPotions: localPlayer == null
                ? []
                : localPlayer.PotionSlots.Select(potion => potion?.Id.Entry ?? "-").ToArray(),
            LocalPowers: localPlayer == null ? [] : PowerTokens(localPlayer.Creature.Powers),
            RemotePlayers: RemotePlayerTokens(state, localPlayer),
            Enemies: EnemyTokens(state),
            RngStates: RngStateTokens(state),
            HardFingerprint: hardFingerprint,
            ReadOnly: true,
            SearchStarted: false,
            ActionsEnqueued: false,
            CustomNetworkPacketSent: false);
    }

    private static void WriteEvidence(MultiplayerProbeSnapshot snapshot)
    {
        lock (EvidenceGate)
        {
            if (_evidenceDisabled)
                return;

            try
            {
                _evidenceLog ??= new AppendOnlyEventLog<MultiplayerProbeSnapshot>(
                    value => JsonSerializer.SerializeToUtf8Bytes(value, EvidenceJson),
                    maximumPendingBytes: 2 * 1024 * 1024,
                    maximumFileBytes: 16 * 1024 * 1024,
                    outputPath: Path.Combine(CombatBugReportPaths.ModLogsDirectory, EvidenceFileName));
                _evidenceLog.TryAppend(snapshot, ProbeEvidenceEstimatedBytes);
            }
            catch (DirectoryNotFoundException)
            {
                _evidenceDisabled = true;
            }
            catch (IOException)
            {
                _evidenceDisabled = true;
            }
            catch (UnauthorizedAccessException)
            {
                _evidenceDisabled = true;
            }
        }
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
                $"{enemy.CurrentHp}/{enemy.MaxHp}/{enemy.Block}:{enemy.Monster?.NextMove?.Id ?? "-"}:" +
                $"powers={Powers(enemy.Powers)}"));

        return $"net_type={RunManager.Instance.NetService.Type} players={state.Players.Count} " +
               $"round={state.RoundNumber} side={state.CurrentSide} " +
               $"seed={state.RunState.Rng.StringSeed} " +
               $"rng={RngCounters(state)} enemies={enemies} remote_players={RemotePlayers(state, localPlayer)} {local}";
    }

    private static string HardFingerprint(CombatState state, Player? localPlayer)
        => $"net_type={RunManager.Instance.NetService.Type};players={state.Players.Count};" +
           $"round={state.RoundNumber};side={state.CurrentSide};seed={state.RunState.Rng.StringSeed};" +
           $"rng={RngStates(state)};enemies={Enemies(state)};" +
           $"remote_players={RemotePlayers(state, localPlayer)};local={LocalPlayer(localPlayer)}";

    private static string LocalPlayer(Player? localPlayer)
    {
        PlayerCombatState? combat = localPlayer?.PlayerCombatState;
        if (localPlayer == null || combat == null)
            return "local=null";

        return $"net_id={localPlayer.NetId};hp={localPlayer.Creature.CurrentHp}/{localPlayer.Creature.MaxHp};" +
               $"block={localPlayer.Creature.Block};energy={combat.Energy};stars={combat.Stars};" +
               $"turn={combat.TurnNumber};phase={combat.Phase};" +
               $"hand={Cards(combat.Hand.Cards)};draw={Cards(combat.DrawPile.Cards)};" +
               $"discard={Cards(combat.DiscardPile.Cards)};exhaust={Cards(combat.ExhaustPile.Cards)};" +
               $"potions={string.Join(',', localPlayer.PotionSlots.Select(potion => potion?.Id.Entry ?? "-"))};" +
               $"powers={Powers(localPlayer.Creature.Powers)}";
    }

    private static string Enemies(CombatState state)
        => string.Join(';', EnemyTokens(state));

    private static string[] EnemyTokens(CombatState state)
        => state.Enemies.Select(enemy =>
                $"{enemy.CombatId?.ToString() ?? "-"}:{enemy.Monster?.Id.Entry ?? "-"}:" +
                $"{enemy.CurrentHp}/{enemy.MaxHp}/{enemy.Block}:{enemy.Monster?.NextMove?.Id ?? "-"}:" +
                $"powers={Powers(enemy.Powers)}").ToArray();

    private static string RemotePlayers(CombatState state, Player? localPlayer)
        => string.Join(';', RemotePlayerTokens(state, localPlayer));

    private static string[] RemotePlayerTokens(CombatState state, Player? localPlayer)
        => state.Players
                .Where(player => localPlayer == null || player.NetId != localPlayer.NetId)
                .OrderBy(player => player.NetId)
                .Select(player =>
                {
                    PlayerCombatState? combat = player.PlayerCombatState;
                    return $"{player.NetId}:{player.Character.Id.Entry}:" +
                           $"turn={combat?.TurnNumber.ToString() ?? "-"}/phase={combat?.Phase.ToString() ?? "-"}:" +
                           $"hp={player.Creature.CurrentHp}/{player.Creature.MaxHp}:" +
                           $"block={player.Creature.Block}:powers={Powers(player.Creature.Powers)}";
                })
                .ToArray();

    private static string Cards(IEnumerable<CardModel> cards)
        => string.Join(',', CardTokens(cards));

    private static string[] CardTokens(IEnumerable<CardModel> cards)
        => cards.Select(card =>
            $"{card.Id.Entry}+{card.CurrentUpgradeLevel}" +
            $"/enchant={(card.Enchantment == null ? "-" : EnchantmentStateSupport.Describe(card.Enchantment))}" +
            $"/affliction={card.Affliction?.Id.Entry ?? "-"}:{card.Affliction?.Amount ?? 0}").ToArray();

    private static string Powers(IEnumerable<PowerModel> powers)
        => string.Join(',', PowerTokens(powers));

    private static string[] PowerTokens(IEnumerable<PowerModel> powers)
        => powers
            .Where(power => power.Amount != 0)
            .OrderBy(power => power.Id.Entry, StringComparer.Ordinal)
            .Select(power => $"{power.Id.Entry}:{power.Amount}")
            .ToArray();

    private static string RngCounters(CombatState state)
    {
        var rng = state.RunState.Rng;
        return $"shuffle={rng.Shuffle.Counter},card_gen={rng.CombatCardGeneration.Counter}," +
               $"potion_gen={rng.CombatPotionGeneration.Counter},card_select={rng.CombatCardSelection.Counter}," +
               $"energy={rng.CombatEnergyCosts.Counter},targets={rng.CombatTargets.Counter}," +
               $"orb={rng.CombatOrbGeneration.Counter},monster_ai={rng.MonsterAi.Counter},niche={rng.Niche.Counter}";
    }

    private static string RngStates(CombatState state)
        => string.Join(',', RngStateTokens(state));

    private static string[] RngStateTokens(CombatState state)
    {
        var rng = state.RunState.Rng;
        return
        [
            $"shuffle={rng.Shuffle.CaptureState()}",
            $"card_gen={rng.CombatCardGeneration.CaptureState()}",
            $"potion_gen={rng.CombatPotionGeneration.CaptureState()}",
            $"card_select={rng.CombatCardSelection.CaptureState()}",
            $"energy={rng.CombatEnergyCosts.CaptureState()}",
            $"targets={rng.CombatTargets.CaptureState()}",
            $"orb={rng.CombatOrbGeneration.CaptureState()}",
            $"monster_ai={rng.MonsterAi.CaptureState()}",
            $"niche={rng.Niche.CaptureState()}",
        ];
    }
}
