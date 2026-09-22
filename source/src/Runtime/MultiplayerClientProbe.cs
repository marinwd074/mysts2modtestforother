using System.Text.Json;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
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
    string RunSeed,
    int CombatSegmentId,
    string Reason,
    string NetworkType,
    int PlayerCount,
    int RoundNumber,
    string CurrentSide,
    bool? MultiplayerScalingHooks,
    string CardMultiplayerConstraint,
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
/// Main-thread-only action boundary used by MP-2B. Local state, locally readable remote
/// state, and enemy state are kept separate so a local card can account for its own target
/// mutation without silently accepting a concurrent teammate mutation.
/// </summary>
internal sealed record MultiplayerSafeExecutionBoundary(
    long WorldVersion,
    int ObservationSequence,
    string? LocalNetId,
    int RoundNumber,
    string CurrentSide,
    int? LocalTurn,
    string? LocalPhase,
    int? LocalEnergy,
    int? LocalStars,
    StateFingerprint LocalFingerprint,
    StateFingerprint RemotePublicFingerprint,
    string[] Enemies);

/// <summary>
/// Read-only observation for a network multiplayer client. It may read any combat state
/// already materialized in the local game process; it never requests additional remote
/// data, mutates CombatState/RNG, enqueues an action, or sends a packet.
/// </summary>
internal static class MultiplayerClientProbe
{
    private const int MinimumSampleIntervalMilliseconds = 100;
    private const int ProbeEvidenceEstimatedBytes = 16 * 1024;
    private const string EvidenceEnvironmentVariable = "COMBATSOLVER_MULTIPLAYER_PROBE_EVIDENCE";
    private static readonly object EvidenceGate = new();
    private static readonly object CardIdentityGate = new();
    private static readonly Dictionary<CardModel, int> CardIdentityIds =
        new(ReferenceEqualityComparer.Instance);
    private static readonly JsonSerializerOptions EvidenceJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly string EvidenceFileName =
        $"multiplayer-probe-{Environment.ProcessId}-{Guid.NewGuid():N}.jsonl";
    private static long _lastSampleAt;
    private static int _observationSequence;
    private static int _combatSegmentId;
    private static int _nextCardIdentityId;
    private static string? _lastTurnIdentity;
    private static StateFingerprint? _lastReactivePublicFingerprint;
    private static StateFingerprint? _lastLocalBoundaryFingerprint;
    private static AppendOnlyEventLog<MultiplayerProbeSnapshot>? _evidenceLog;
    private static bool _evidenceDisabled;

    private static bool EvidenceEnabled
        => IsTruthy(Environment.GetEnvironmentVariable(EvidenceEnvironmentVariable));

    private static bool IsTruthy(string? value)
        => string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    internal static void Reset()
    {
        _lastSampleAt = 0;
        _observationSequence = 0;
        _lastTurnIdentity = null;
        _lastReactivePublicFingerprint = null;
        _lastLocalBoundaryFingerprint = null;
        lock (CardIdentityGate)
        {
            CardIdentityIds.Clear();
            _nextCardIdentityId = 0;
        }
        MultiplayerWorldTracker.Reset();
    }

    internal static void BeginCombatSegment()
    {
        _combatSegmentId = checked(_combatSegmentId + 1);
        _lastSampleAt = 0;
        _observationSequence = 0;
        _lastTurnIdentity = null;
        _lastReactivePublicFingerprint = null;
        _lastLocalBoundaryFingerprint = null;
        lock (CardIdentityGate)
        {
            CardIdentityIds.Clear();
            _nextCardIdentityId = 0;
        }
        MultiplayerWorldTracker.Reset();
    }

    internal static void Dispose()
    {
        lock (EvidenceGate)
        {
            _evidenceLog?.Dispose();
            _evidenceLog = null;
        }
        lock (CardIdentityGate)
        {
            CardIdentityIds.Clear();
            _nextCardIdentityId = 0;
        }
    }

    internal static bool Observe(CombatState state, string reason)
        => ObserveCore(state, reason, bypassSampleInterval: false);

    /// <summary>
    /// Captures an action-completion observation immediately. The ordinary Probe cadence
    /// remains rate limited; Safe Execute must not let that cadence hide the local action's
    /// world mutation before it decides whether another card is admissible.
    /// </summary>
    internal static bool ObserveActionBoundary(CombatState state, string reason)
        => ObserveCore(state, reason, bypassSampleInterval: true);

    internal static MultiplayerSafeExecutionBoundary CaptureSafeExecutionBoundary(CombatState state)
    {
        Player? localPlayer = LocalContext.GetMe(state);
        PlayerCombatState? localCombat = localPlayer?.PlayerCombatState;
        return new(
            WorldVersion: MultiplayerWorldTracker.WorldVersion,
            ObservationSequence: _observationSequence,
            LocalNetId: localPlayer?.NetId.ToString(),
            RoundNumber: state.RoundNumber,
            CurrentSide: state.CurrentSide.ToString(),
            LocalTurn: localCombat?.TurnNumber,
            LocalPhase: localCombat?.Phase.ToString(),
            LocalEnergy: localCombat?.Energy,
            LocalStars: localCombat?.Stars,
            LocalFingerprint: LocalBoundaryFingerprint(localPlayer),
            RemotePublicFingerprint: RemotePublicBoundaryFingerprint(state, localPlayer),
            Enemies: EnemyTokens(state));
    }

    /// <summary>
    /// Captures only the public teammate/scaling portion used to validate a local
    /// cross-turn continuation. Round/side and enemy state are checked separately:
    /// they may advance as part of the modeled local route without implying a
    /// teammate action.
    /// </summary>
    internal static StateFingerprint CaptureContinuationRemotePublicFingerprint(
        CombatState state)
    {
        Player? localPlayer = LocalContext.GetMe(state);
        return ContinuationRemotePublicFingerprint(state, localPlayer);
    }

    internal static MultiplayerContinuationValidation CaptureContinuationValidation(
        CombatState state,
        long minimumWorldVersion)
    {
        Player? localPlayer = LocalContext.GetMe(state);
        return new MultiplayerContinuationValidation(
            ContinuationStamp.CaptureLiveCombatIdentity(state),
            localPlayer?.NetId.ToString() ?? string.Empty,
            ContinuationRemotePublicFingerprint(state, localPlayer),
            state.MultiplayerScalingModel?.ShouldReceiveCombatHooks,
            state.RunState.CardMultiplayerConstraint.ToString(),
            MultiplayerWorldTracker.WorldVersion,
            minimumWorldVersion);
    }

    private static bool ObserveCore(CombatState state, string reason, bool bypassSampleInterval)
    {
        if (!SolverSessionCapabilities.Capture(state).IsMultiplayer
            || !CombatManager.Instance.IsInProgress)
        {
            return false;
        }

        long now = Environment.TickCount64;
        if (!bypassSampleInterval
            && _lastSampleAt > 0
            && now - _lastSampleAt < MinimumSampleIntervalMilliseconds)
        return false;
        _lastSampleAt = now;

        Player? localPlayer = LocalContext.GetMe(state);
        StateFingerprint compactFingerprint = CompactFingerprint(state, localPlayer);
        StateFingerprint reactivePublicFingerprint = ReactivePublicFingerprint(state, localPlayer);
        StateFingerprint localBoundaryFingerprint = LocalBoundaryFingerprint(localPlayer);
        bool changed = MultiplayerWorldTracker.ObserveSnapshot(compactFingerprint, reason);
        if (!changed)
            return false;

        _observationSequence++;
        StateFingerprint? previousReactivePublicFingerprint = _lastReactivePublicFingerprint;
        StateFingerprint? previousLocalBoundaryFingerprint = _lastLocalBoundaryFingerprint;
        _lastReactivePublicFingerprint = reactivePublicFingerprint;
        _lastLocalBoundaryFingerprint = localBoundaryFingerprint;
        if (previousReactivePublicFingerprint is { } previousReactive
            && previousLocalBoundaryFingerprint is { } previousLocal)
        {
            Entry.Logger.Info(
                $"[CombatSolver/MultiplayerProbe] MP_REACTIVE_WORLD_DELTA " +
                $"world_version={MultiplayerWorldTracker.WorldVersion} reason={reason} " +
                $"remote_public_changed={(previousReactive != reactivePublicFingerprint).ToString().ToLowerInvariant()} " +
                $"remote_readable_changed={(previousReactive != reactivePublicFingerprint).ToString().ToLowerInvariant()} " +
                $"local_private_changed={(previousLocal != localBoundaryFingerprint).ToString().ToLowerInvariant()} " +
                "fresh_probe=true");
        }
        string currentTurnIdentity = TurnIdentityToken(state, localPlayer);
        string? previousTurnIdentity = _lastTurnIdentity;
        _lastTurnIdentity = currentTurnIdentity;
        if (previousTurnIdentity != null
            && !string.Equals(previousTurnIdentity, currentTurnIdentity, StringComparison.Ordinal))
        {
            Entry.Logger.Info(
                $"[CombatSolver/MultiplayerProbe] MP_REACTIVE_TURN_BOUNDARY " +
                $"previous={previousTurnIdentity} current={currentTurnIdentity} " +
                $"world_version={MultiplayerWorldTracker.WorldVersion} " +
                $"observation_sequence={_observationSequence} " +
                "fresh_probe=true fresh_capture=true");
        }
        string? display = null;
        if (EvidenceEnabled)
        {
            string hardFingerprint = HardFingerprint(state, localPlayer);
            WriteEvidence(CaptureSnapshot(
                state,
                localPlayer,
                reason,
                hardFingerprint,
                _observationSequence));
            display = Describe(state, localPlayer);
        }
        Entry.Logger.Info(
            $"[CombatSolver/MultiplayerProbe] OBSERVED sequence={_observationSequence} " +
            $"world_version={MultiplayerWorldTracker.WorldVersion} reason={reason} " +
            $"compact_changed=true fingerprint={compactFingerprint.First:X16}:{compactFingerprint.Second:X16}" +
            (display is null ? string.Empty : $" {display}"));
        return true;
    }

    private static StateFingerprint LocalBoundaryFingerprint(Player? localPlayer)
    {
        StateFingerprintBuilder fingerprint = new();
        AppendCompactPlayer(ref fingerprint, localPlayer, includePrivateState: true);
        return fingerprint.Finish();
    }

    private static StateFingerprint RemotePublicBoundaryFingerprint(
        CombatState state,
        Player? localPlayer)
    {
        // Legacy method name retained for serialized/runtime contract compatibility.
        // The fingerprint now includes every teammate field already readable locally.
        StateFingerprintBuilder fingerprint = new();
        fingerprint.Add(state.Players.Count);
        fingerprint.Add(state.RoundNumber);
        fingerprint.Add(state.CurrentSide.ToString());
        fingerprint.Add(state.MultiplayerScalingModel is null
            ? -1
            : state.MultiplayerScalingModel.ShouldReceiveCombatHooks ? 1 : 0);
        fingerprint.Add(state.RunState.CardMultiplayerConstraint.ToString());
        foreach (Player player in state.Players
                     .Where(candidate => localPlayer == null || candidate.NetId != localPlayer.NetId)
                     .OrderBy(candidate => candidate.NetId))
        {
            AppendCompactPlayer(ref fingerprint, player, includePrivateState: true);
        }
        return fingerprint.Finish();
    }

    private static StateFingerprint ContinuationRemotePublicFingerprint(
        CombatState state,
        Player? localPlayer)
    {
        // Legacy name: continuation now invalidates on any locally readable teammate
        // combat-state change, including cards/resources/potions when the client has them.
        StateFingerprintBuilder fingerprint = new();
        fingerprint.Add(state.Players.Count);
        fingerprint.Add(state.MultiplayerScalingModel is null
            ? -1
            : state.MultiplayerScalingModel.ShouldReceiveCombatHooks ? 1 : 0);
        fingerprint.Add(state.RunState.CardMultiplayerConstraint.ToString());
        foreach (Player player in state.Players
                     .Where(candidate => localPlayer == null || candidate.NetId != localPlayer.NetId)
                     .OrderBy(candidate => candidate.NetId))
        {
            AppendCompactPlayer(ref fingerprint, player, includePrivateState: true);
        }
        return fingerprint.Finish();
    }

    private static StateFingerprint ReactivePublicFingerprint(
        CombatState state,
        Player? localPlayer)
    {
        StateFingerprintBuilder fingerprint = new();
        StateFingerprint remote = RemotePublicBoundaryFingerprint(state, localPlayer);
        fingerprint.Add(remote.First);
        fingerprint.Add(remote.Second);
        foreach (string token in EnemyTokens(state))
            fingerprint.Add(token);
        return fingerprint.Finish();
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
            SchemaVersion: 2,
            CapturedAtUnixMilliseconds: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Sequence: sequence,
            WorldVersion: MultiplayerWorldTracker.WorldVersion,
            RunSeed: state.RunState.Rng.StringSeed,
            CombatSegmentId: _combatSegmentId,
            Reason: reason,
            NetworkType: RunManager.Instance.NetService.Type.ToString(),
            PlayerCount: state.Players.Count,
            RoundNumber: state.RoundNumber,
            CurrentSide: state.CurrentSide.ToString(),
            MultiplayerScalingHooks: state.MultiplayerScalingModel?.ShouldReceiveCombatHooks,
            CardMultiplayerConstraint: state.RunState.CardMultiplayerConstraint.ToString(),
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
        if (!EvidenceEnabled)
            return;

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
                    outputPath: Path.Combine(CombatBugReportPaths.ModLogsDirectory, EvidenceFileName),
                    flushPolicy: EventLogFlushPolicy.FlushEachAppend);
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

    private static StateFingerprint CompactFingerprint(CombatState state, Player? localPlayer)
    {
        StateFingerprintBuilder fingerprint = new();
        fingerprint.Add(RunManager.Instance.NetService.Type.ToString());
        fingerprint.Add(state.Players.Count);
        fingerprint.Add(state.RoundNumber);
        fingerprint.Add(state.CurrentSide.ToString());
        fingerprint.Add(state.RunState.Rng.StringSeed);
        fingerprint.Add(state.MultiplayerScalingModel is null
            ? -1
            : state.MultiplayerScalingModel.ShouldReceiveCombatHooks ? 1 : 0);
        fingerprint.Add(state.RunState.CardMultiplayerConstraint.ToString());
        AppendCompactRng(ref fingerprint, state);

        AppendCompactPlayer(ref fingerprint, localPlayer, includePrivateState: true);
        foreach (Player player in state.Players)
        {
            if (localPlayer != null && player.NetId == localPlayer.NetId)
                continue;
            AppendCompactPlayer(ref fingerprint, player, includePrivateState: true);
        }

        fingerprint.Add(state.Enemies.Count);
        foreach (Creature enemy in state.Enemies)
        {
            fingerprint.Add(enemy.CombatId?.ToString());
            fingerprint.Add(enemy.Monster?.Id.Entry);
            fingerprint.Add(enemy.CurrentHp);
            fingerprint.Add(enemy.MaxHp);
            fingerprint.Add(enemy.Block);
            fingerprint.Add(enemy.Monster?.NextMove?.Id.ToString());
            AppendCompactPowers(ref fingerprint, enemy.Powers);
        }
        return fingerprint.Finish();
    }

    private static void AppendCompactPlayer(
        ref StateFingerprintBuilder fingerprint,
        Player? player,
        bool includePrivateState)
    {
        if (player == null)
        {
            fingerprint.Add(false);
            return;
        }

        fingerprint.Add(true);
        fingerprint.Add(player.NetId.ToString());
        fingerprint.Add(player.Character.Id.Entry);
        fingerprint.Add(player.Creature.CurrentHp);
        fingerprint.Add(player.Creature.MaxHp);
        fingerprint.Add(player.Creature.Block);
        PlayerCombatState? combat = player.PlayerCombatState;
        fingerprint.Add(combat != null);
        if (combat == null)
            return;

        fingerprint.Add(combat.TurnNumber);
        fingerprint.Add(combat.Phase.ToString());
        if (includePrivateState)
        {
            fingerprint.Add(combat.Energy);
            fingerprint.Add(combat.Stars);
            AppendCompactCards(ref fingerprint, combat.Hand.Cards);
            AppendCompactCards(ref fingerprint, combat.DrawPile.Cards);
            AppendCompactCards(ref fingerprint, combat.DiscardPile.Cards);
            AppendCompactCards(ref fingerprint, combat.ExhaustPile.Cards);
            fingerprint.Add(player.PotionSlots.Count);
            foreach (PotionModel? potion in player.PotionSlots)
                fingerprint.Add(potion?.Id.Entry);
        }
        AppendCompactPowers(ref fingerprint, player.Creature.Powers);
    }

    private static void AppendCompactCards(
        ref StateFingerprintBuilder fingerprint,
        IEnumerable<CardModel> cards)
    {
        if (cards is IReadOnlyCollection<CardModel> collection)
        {
            fingerprint.Add(collection.Count);
        }
        else
        {
            int count = 0;
            foreach (CardModel _ in cards)
                count++;
            fingerprint.Add(count);
        }
        foreach (CardModel card in cards)
        {
            fingerprint.Add(card.Id.Entry);
            fingerprint.Add(card.CurrentUpgradeLevel);
            fingerprint.Add(card.Enchantment?.Id.Entry);
            fingerprint.Add(card.Affliction?.Id.Entry);
            fingerprint.Add(card.Affliction?.Amount ?? 0);
            fingerprint.Add(RuntimeHelpers.GetHashCode(card));
        }
    }

    private static void AppendCompactPowers(
        ref StateFingerprintBuilder fingerprint,
        IEnumerable<PowerModel> powers)
    {
        if (powers is IReadOnlyCollection<PowerModel> collection)
        {
            fingerprint.Add(collection.Count);
        }
        else
        {
            int count = 0;
            foreach (PowerModel _ in powers)
                count++;
            fingerprint.Add(count);
        }
        foreach (PowerModel power in powers)
        {
            fingerprint.Add(power.Id.Entry);
            fingerprint.Add(power.Amount);
        }
    }

    private static void AppendCompactRng(ref StateFingerprintBuilder fingerprint, CombatState state)
    {
        var rng = state.RunState.Rng;
        fingerprint.Add(rng.Shuffle.Counter);
        fingerprint.Add(rng.CombatCardGeneration.Counter);
        fingerprint.Add(rng.CombatPotionGeneration.Counter);
        fingerprint.Add(rng.CombatCardSelection.Counter);
        fingerprint.Add(rng.CombatEnergyCosts.Counter);
        fingerprint.Add(rng.CombatTargets.Counter);
        fingerprint.Add(rng.CombatOrbGeneration.Counter);
        fingerprint.Add(rng.MonsterAi.Counter);
        fingerprint.Add(rng.Niche.Counter);
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
               $"multiplayer_scaling_hooks={state.MultiplayerScalingModel?.ShouldReceiveCombatHooks.ToString() ?? "-"} " +
               $"card_multiplayer_constraint={state.RunState.CardMultiplayerConstraint} " +
               $"rng={RngCounters(state)} enemies={enemies} remote_players={RemotePlayers(state, localPlayer)} {local}";
    }

    private static string TurnIdentityToken(CombatState state, Player? localPlayer)
    {
        PlayerCombatState? combat = localPlayer?.PlayerCombatState;
        return $"round={state.RoundNumber};side={state.CurrentSide};" +
               $"local_net_id={localPlayer?.NetId.ToString() ?? "-"};" +
               $"turn={combat?.TurnNumber.ToString() ?? "-"};" +
               $"phase={combat?.Phase.ToString() ?? "-"}";
    }

    private static string HardFingerprint(CombatState state, Player? localPlayer)
        => $"net_type={RunManager.Instance.NetService.Type};players={state.Players.Count};" +
           $"round={state.RoundNumber};side={state.CurrentSide};seed={state.RunState.Rng.StringSeed};" +
           $"multiplayer_scaling_hooks={state.MultiplayerScalingModel?.ShouldReceiveCombatHooks.ToString() ?? "-"};" +
           $"card_multiplayer_constraint={state.RunState.CardMultiplayerConstraint};" +
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
            $"/affliction={card.Affliction?.Id.Entry ?? "-"}:{card.Affliction?.Amount ?? 0}" +
            $"/instance={CardIdentity(card)}").ToArray();

    private static int CardIdentity(CardModel card)
    {
        // Card names are not unique in a real combat. A reference-based ID lets the
        // read-only probe distinguish duplicate cards without mutating the game model.
        lock (CardIdentityGate)
        {
            if (CardIdentityIds.TryGetValue(card, out int identity))
                return identity;

            identity = ++_nextCardIdentityId;
            CardIdentityIds.Add(card, identity);
            return identity;
        }
    }

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
