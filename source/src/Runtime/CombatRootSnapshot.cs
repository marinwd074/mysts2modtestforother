using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rooms;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal readonly record struct SearchablePotionSlotSnapshot(
    int Slot,
    string PotionId,
    int StrategicHpCost);

internal sealed class CombatRootSnapshot
{
    private readonly CombatPredictionSimulator _rootSimulator;

    public Player PlayerIdentity { get; }
    public SolverPerspective Perspective { get; }
    public IReadOnlyList<Creature> Enemies { get; }
    public IntentForecast Forecast { get; }
    public LiveCombatStamp LiveStamp { get; }
    public ContinuationStamp ContinuationStamp { get; }
    public int PlayerCount { get; }
    /// <summary>
    /// The root was captured for an explicitly approved multiplayer search capability.
    /// Search actions remain local-player-owned, but every player state already readable
    /// from the local game process may be captured into the detached prediction root.
    /// </summary>
    public bool AllowsLocalPlayerOnlySearch { get; }
    public int StartTurnNumber { get; }
    public int InitialPlayerHp { get; }
    public int InitialPlayerMaxHp { get; }
    public int InitialBrightestFlameMaxHpSpent
        => ((SimulatedCombatState)_rootSimulator.State.CombatState).BrightestFlameMaxHpSpent;
    public int PotionSlotCount { get; }
    public IReadOnlyList<SearchablePotionSlotSnapshot> SearchablePotions { get; }
    public int SearchablePotionCount { get; }
    public int? MinimumSearchablePotionStrategicCost { get; }
    public int ZeroCostSearchablePotionCount { get; }
    public ulong InitialAliveEnemyMask { get; }
    public CombatSide CurrentSide { get; }
    public PlayerTurnPhase PlayerPhase { get; }
    public RoomType? EncounterRoomType { get; }
    public BossHpRelief BossHpRelief { get; }
    public bool IsActEndingBoss => BossHpRelief != BossHpRelief.None;
    public double CaptureElapsedMilliseconds { get; }
    public int CapturedCardCount { get; }
    public IReadOnlySet<string> PlayerCardIds { get; }
    public int CapturedPowerCount { get; }
    public int CapturedHookListenerCount { get; }
    public int CapturedRunModSubscriberCount { get; }
    public int CapturedCombatModSubscriberCount { get; }
    public bool CapturedBaseLibCardModifiers { get; }
    public bool HasUnusedCardReplayAllocator { get; }
    public bool HasRenewablePotionShapedRock { get; }
    public PostCombatRelicHealProfile PostCombatRelicHeal { get; }
    /// <summary>
    /// Exact detached teammate state available to future shadow planning. These snapshots
    /// are observation-only; RootActionPlayers remains local-player-only.
    /// </summary>
    public IReadOnlyList<MultiplayerTeammateForecastState> TeammateForecastStates { get; }
    /// <summary>
    /// Immutable public multiplayer input captured with this root. Single-player and
    /// read-only Probe roots carry a disabled context; background search never reads live
    /// remote players or creatures through this property.
    /// </summary>
    public MultiplayerCarryRankingContext CarryRankingContext { get; }
    internal int CapturedPlayerMaxHp(Player player)
    {
        if (ReferenceEquals(player, PlayerIdentity))
            return InitialPlayerMaxHp;

        string netId = player.NetId.ToString();
        for (int index = 0; index < TeammateForecastStates.Count; index++)
        {
            MultiplayerTeammateForecastState teammate = TeammateForecastStates[index];
            if (string.Equals(teammate.NetId, netId, StringComparison.Ordinal))
                return teammate.MaxHp;
        }

        throw new InvalidOperationException(
            $"Captured multiplayer root has no max-HP baseline for player {netId}.");
    }

    internal HookLayoutCacheStatistics HookLayoutCacheStatistics
        => ((SimulatedCombatState)_rootSimulator.State.CombatState).HookLayoutCacheStatistics;
    internal HookListenerSegmentStatistics HookListenerSegmentStatistics
        => ((SimulatedCombatState)_rootSimulator.State.CombatState).HookListenerSegmentStatistics;

    private CombatRootSnapshot(
        Player playerIdentity,
        SolverPerspective perspective,
        IReadOnlyList<Creature> enemies,
        IntentForecast forecast,
        LiveCombatStamp liveStamp,
        ContinuationStamp continuationStamp,
        CombatPredictionSimulator rootSimulator,
        int playerCount,
        bool allowsLocalPlayerOnlySearch,
        int startTurnNumber,
        int initialPlayerHp,
        int initialPlayerMaxHp,
        int potionSlotCount,
        IReadOnlyList<SearchablePotionSlotSnapshot> searchablePotions,
        ulong initialAliveEnemyMask,
        CombatSide currentSide,
        PlayerTurnPhase playerPhase,
        RoomType? encounterRoomType,
        BossHpRelief bossHpRelief,
        double captureElapsedMilliseconds,
        int capturedCardCount,
        IReadOnlySet<string> playerCardIds,
        int capturedPowerCount,
        int capturedHookListenerCount,
        int capturedRunModSubscriberCount,
        int capturedCombatModSubscriberCount,
        bool capturedBaseLibCardModifiers,
        bool hasUnusedCardReplayAllocator,
        bool hasRenewablePotionShapedRock,
        PostCombatRelicHealProfile postCombatRelicHeal,
        IReadOnlyList<MultiplayerTeammateForecastState> teammateForecastStates,
        MultiplayerCarryRankingContext carryRankingContext)
    {
        PlayerIdentity = playerIdentity;
        Perspective = perspective;
        Enemies = enemies;
        Forecast = forecast;
        LiveStamp = liveStamp;
        ContinuationStamp = continuationStamp;
        _rootSimulator = rootSimulator;
        PlayerCount = playerCount;
        AllowsLocalPlayerOnlySearch = allowsLocalPlayerOnlySearch;
        StartTurnNumber = startTurnNumber;
        InitialPlayerHp = initialPlayerHp;
        InitialPlayerMaxHp = initialPlayerMaxHp;
        PotionSlotCount = potionSlotCount;
        SearchablePotions = searchablePotions;
        SearchablePotionCount = searchablePotions.Count;
        MinimumSearchablePotionStrategicCost = searchablePotions.Count == 0
            ? null
            : searchablePotions.Min(potion => potion.StrategicHpCost);
        ZeroCostSearchablePotionCount = searchablePotions.Count(potion =>
            potion.StrategicHpCost == 0);
        InitialAliveEnemyMask = initialAliveEnemyMask;
        CurrentSide = currentSide;
        PlayerPhase = playerPhase;
        EncounterRoomType = encounterRoomType;
        BossHpRelief = bossHpRelief;
        CaptureElapsedMilliseconds = captureElapsedMilliseconds;
        CapturedCardCount = capturedCardCount;
        PlayerCardIds = playerCardIds;
        CapturedPowerCount = capturedPowerCount;
        CapturedHookListenerCount = capturedHookListenerCount;
        CapturedRunModSubscriberCount = capturedRunModSubscriberCount;
        CapturedCombatModSubscriberCount = capturedCombatModSubscriberCount;
        CapturedBaseLibCardModifiers = capturedBaseLibCardModifiers;
        HasUnusedCardReplayAllocator = hasUnusedCardReplayAllocator;
        HasRenewablePotionShapedRock = hasRenewablePotionShapedRock;
        PostCombatRelicHeal = postCombatRelicHeal;
        TeammateForecastStates = teammateForecastStates;
        CarryRankingContext = carryRankingContext;
    }

    public static CombatRootSnapshot Capture(CombatState state)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("Combat root snapshot must be captured on the main thread.");
        Engine.InCombat.Mirrors.Hooks.TurnEnd.AfterSideTurnEndLateMirrors.Seal();
        Stopwatch stopwatch = Stopwatch.StartNew();

        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("找不到本地玩家。");
        PlayerCombatState playerState = player.PlayerCombatState
            ?? throw new InvalidOperationException("玩家没有战斗状态。");
        SolverSessionCapabilitySet capabilities = SolverSessionCapabilities.Capture(state);
        bool multiplayerSearch = capabilities.IsMultiplayer && capabilities.CanSearch;
        bool useMultiplayerPrediction = multiplayerSearch
            && SolverSettings.Current.UseMultiplayerPrediction;
        // Default multiplayer now deliberately uses the single-player search core. Keep the
        // native multiplayer enemy/root state, but capture only the local player's private
        // search state so every fork is as close as possible to a single-player fork.
        IReadOnlyList<Player>? rootCapturedPlayers = multiplayerSearch
            ? useMultiplayerPrediction
                ? state.Players.ToArray()
                : [player]
            : null;
        MultiplayerCarryRankingContext carryRankingContext = useMultiplayerPrediction
            ? MultiplayerCarryRankingContextCapture.Capture(
                state,
                MultiplayerWorldTracker.WorldVersion)
            : MultiplayerCarryRankingContext.Disabled;
        SolverPerspective perspective = SolverPerspective.Capture(
            player,
            state.Players.Count,
            capabilities.IsMultiplayer);

        PowerDynamicVarWarmup.EnsureMaterialized(state);
        CardDynamicVarWarmup.EnsureMaterialized(state, rootCapturedPlayers);

        // Listener enumeration and third-party owner discovery are part of root capture.
        // Take the baseline first so any semantic mutation in those callbacks is rejected by
        // the existing after-capture stamp without paying for another full serialization.
        ContinuationStamp continuationBefore = ContinuationStamp.CaptureLive(state, rootCapturedPlayers);
        LiveCombatStamp liveBefore = LiveCombatStamp.FromContinuation(continuationBefore);

        AbstractModel[] liveCombatHookListeners = state.IterateHookListeners().ToArray();
        if (liveCombatHookListeners.Any(PredictionModModelSupport.IsBaseLibCardModifier))
        {
            PredictionModModelSupport.RegisterBaseLibCardModifierOwners(
                (rootCapturedPlayers ?? state.Players)
                    .Where(candidate => candidate.PlayerCombatState != null)
                    .SelectMany(candidate => candidate.PlayerCombatState!.AllCards));
        }
        IntentForecast forecast = IntentForecaster.Build(state, SolverWeights.SetupValueHorizonTurns);

        SimulatedCombatState simulatedCombat = new(
            state,
            liveCombatHookListeners,
            localPlayerOnly: multiplayerSearch && !useMultiplayerPrediction
                ? player
                : null,
            localActionPlayer: multiplayerSearch
                ? player
                : null);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        IReadOnlyList<MultiplayerTeammateForecastState> teammateForecastStates = [];
        if (useMultiplayerPrediction)
        {
            teammateForecastStates =
                MultiplayerTeammateForecastCapture.Capture(simulator, player);
        }
        ContinuationStamp projected = ContinuationStamp.CapturePredicted(
            player,
            simulator,
            playerState.TurnNumber,
            forecast,
            playerState.TurnNumber);
        bool hasUnusedCardReplayAllocator = simulatedCombat.RelicsOf(player)
            .OfType<ThrowingAxe>()
            .Any(relic => !relic.IsMelted && !relic._usedThisCombat);
        bool hasRenewablePotionShapedRock = simulatedCombat.RelicsOf(player)
            .OfType<PetrifiedToad>()
            .Any(relic => !relic.IsMelted);
        PostCombatRelicHealProfile postCombatRelicHeal = CapturePostCombatRelicHeal(
            simulatedCombat.RelicsOf(player));
        SearchablePotionSlotSnapshot[] searchablePotions = player.PotionSlots
            .Select((potion, slot) => (Potion: potion, Slot: slot))
            .Where(item => item.Potion != null && PotionOnUseSupport.CanSearch(item.Potion))
            .Select(item => new SearchablePotionSlotSnapshot(
                item.Slot,
                item.Potion!.Id.Entry,
                PotionUsePolicy.StrategicHpCost(
                    item.Potion,
                    hasRenewablePotionShapedRock)))
            .ToArray();
        if (!string.Equals(
                continuationBefore.StateText,
                projected.StateText,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Combat root projection differs from the captured live state: " +
                continuationBefore.DescribeFirstDifference(projected));
        }

        ContinuationStamp continuationAfter = ContinuationStamp.CaptureLive(state, rootCapturedPlayers);
        LiveCombatStamp liveAfter = LiveCombatStamp.FromContinuation(continuationAfter);
        if (!string.Equals(liveBefore.StateText, liveAfter.StateText, StringComparison.Ordinal)
            || !string.Equals(
                continuationBefore.StateText,
                continuationAfter.StateText,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Combat state changed while the root snapshot was being captured.");
        }

        ulong aliveEnemyMask = 0;
        for (int index = 0; index < state.Enemies.Count; index++)
        {
            if (state.Enemies[index].IsAlive)
                aliveEnemyMask |= 1UL << index;
        }
        int cardCount = (rootCapturedPlayers ?? state.Players)
            .Where(candidate => candidate.PlayerCombatState != null)
            .Sum(candidate => candidate.PlayerCombatState!.AllCards.Count());
        IReadOnlySet<string> playerCardIds = playerState.Hand.Cards
            .Concat(playerState.DrawPile.Cards)
            .Concat(playerState.DiscardPile.Cards)
            .Select(card => card.Id.Entry)
            .ToHashSet(StringComparer.Ordinal);
        int powerCount = state.Creatures.Sum(creature => creature.Powers.Count);
        stopwatch.Stop();

        if (multiplayerSearch)
        {
            Entry.Logger.Info(
                $"[CombatSolver/Multiplayer] MP_SEARCH_ROOT_CAPTURE " +
                $"mode={(useMultiplayerPrediction ? "full_prediction" : "local_single_player_core")} " +
                $"captured_players={rootCapturedPlayers?.Count ?? state.Players.Count} " +
                $"cards={cardCount} hooks={simulatedCombat.RootHookListenerCount} " +
                $"capture_ms={stopwatch.Elapsed.TotalMilliseconds:F1}");
        }

        if (carryRankingContext.Enabled)
        {
            int allPlayerThreats = carryRankingContext.Enemies.Count(enemy =>
                enemy.ThreatTarget == MultiplayerCarryThreatTarget.AllPlayers);
            int unknownThreats = carryRankingContext.Enemies.Count(enemy =>
                enemy.ThreatTarget == MultiplayerCarryThreatTarget.Unknown);
            Entry.Logger.Info(
                $"[CombatSolver/MultiplayerCarry] MP_CARRY_CONTEXT_CAPTURE " +
                $"world_version={carryRankingContext.WorldVersion} " +
                $"remote_players={carryRankingContext.RemotePlayers.Count} " +
                $"enemies={carryRankingContext.Enemies.Count} " +
                $"all_player_threats={allPlayerThreats} unknown_threats={unknownThreats} " +
                "remote_private_read=true context_reused=false");
        }

        return new CombatRootSnapshot(
            player,
            perspective,
            Array.AsReadOnly(state.Enemies.ToArray()),
            forecast,
            liveBefore,
            continuationBefore,
            simulator,
            state.Players.Count,
            capabilities.IsMultiplayer && capabilities.CanSearch,
            playerState.TurnNumber,
            player.Creature.CurrentHp,
            player.Creature.MaxHp,
            player.PotionSlots.Count,
            Array.AsReadOnly(searchablePotions),
            aliveEnemyMask,
            state.CurrentSide,
            playerState.Phase,
            state.Encounter?.RoomType,
            ActEndingBossPolicy.ResolveHpRelief(state),
            stopwatch.Elapsed.TotalMilliseconds,
            cardCount,
            playerCardIds,
            powerCount,
            simulatedCombat.RootHookListenerCount,
            simulatedCombat.RootRunModSubscriberCount,
            simulatedCombat.RootCombatModSubscriberCount,
            simulatedCombat.RootHasBaseLibCardModifiers,
            hasUnusedCardReplayAllocator,
            hasRenewablePotionShapedRock,
            postCombatRelicHeal,
            teammateForecastStates,
            carryRankingContext);
    }

    /// <summary>
    /// Reads how much HP the player's relics will restore once this fight is won.
    /// </summary>
    /// <remarks>
    /// Melted relics are dropped from the hook listener list by the game, so they heal nothing. The heal
    /// amounts come from the live models rather than hard-coded constants so a data-layer rebalance of these
    /// relics is followed without a code change here.
    /// </remarks>
    private static PostCombatRelicHealProfile CapturePostCombatRelicHeal(
        IEnumerable<RelicModel> relics)
    {
        int unconditionalHeal = 0;
        int woundedHeal = 0;
        int woundedHpPercent = 0;
        foreach (RelicModel relic in relics)
        {
            if (relic.IsMelted)
                continue;
            switch (relic)
            {
                case BurningBlood or BlackBlood:
                    unconditionalHeal += relic.DynamicVars.Heal.IntValue;
                    break;
                case MeatOnTheBone:
                    woundedHeal += relic.DynamicVars.Heal.IntValue;
                    woundedHpPercent = Math.Max(
                        woundedHpPercent,
                        relic.DynamicVars[MeatOnTheBone._hpThresholdKey].IntValue);
                    break;
            }
        }
        return new PostCombatRelicHealProfile(unconditionalHeal, woundedHeal, woundedHpPercent);
    }

    public CombatPredictionSimulator ForkSimulator() => _rootSimulator.Fork();
}
