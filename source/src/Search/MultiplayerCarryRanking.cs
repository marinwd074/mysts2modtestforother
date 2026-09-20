namespace CombatSolver;

/// <summary>
/// The only target classification that Carry Ranking may use. The runtime capture path
/// deliberately records <see cref="Unknown"/> unless the public game state proves a
/// remote-player target; it never infers a target from a move name.
/// </summary>
internal enum MultiplayerCarryThreatTarget
{
    Unknown,
    LocalPlayer,
    RemotePlayer,
    AllPlayers,
}

internal static class MultiplayerCarryThreatTargetContracts
{
    /// <summary>
    /// STS2 0.107.1 base-game monster attacks are created through
    /// AttackCommand.FromMonster(), whose public targeting contract is all opponents.
    /// Third-party monsters are deliberately excluded because their execution semantics
    /// are not proven by the base-game assembly contract.
    /// </summary>
    internal static MultiplayerCarryThreatTarget ClassifyBaseGameMove(
        bool isBaseGameMonster,
        bool hasAttackIntent)
        => isBaseGameMonster && hasAttackIntent
            ? MultiplayerCarryThreatTarget.AllPlayers
            : MultiplayerCarryThreatTarget.Unknown;
}

internal sealed class MultiplayerCarryPowerPublicState
{
    public MultiplayerCarryPowerPublicState(string powerId, int amount)
    {
        PowerId = powerId ?? string.Empty;
        Amount = amount;
    }

    public string PowerId { get; }
    public int Amount { get; }
}

internal sealed class MultiplayerCarryRemotePlayerPublicState
{
    public MultiplayerCarryRemotePlayerPublicState(
        string netId,
        string characterId,
        int currentHp,
        int maxHp,
        int block,
        int turnNumber,
        string phase,
        IEnumerable<MultiplayerCarryPowerPublicState>? powers = null)
    {
        NetId = netId ?? string.Empty;
        CharacterId = characterId ?? string.Empty;
        CurrentHp = currentHp;
        MaxHp = maxHp;
        Block = block;
        TurnNumber = turnNumber;
        Phase = phase ?? string.Empty;
        Powers = Array.AsReadOnly((powers ?? []).ToArray());
    }

    public string NetId { get; }
    public string CharacterId { get; }
    public int CurrentHp { get; }
    public int MaxHp { get; }
    public int Block { get; }
    public int TurnNumber { get; }
    public string Phase { get; }
    public IReadOnlyList<MultiplayerCarryPowerPublicState> Powers { get; }
}

internal sealed class MultiplayerCarryEnemyPublicState
{
    public MultiplayerCarryEnemyPublicState(
        uint combatId,
        string monsterId,
        int currentHp,
        int maxHp,
        int block,
        string nextMoveId,
        IEnumerable<MultiplayerCarryPowerPublicState>? powers = null,
        MultiplayerCarryThreatTarget threatTarget = MultiplayerCarryThreatTarget.Unknown)
    {
        CombatId = combatId;
        MonsterId = monsterId ?? string.Empty;
        CurrentHp = currentHp;
        MaxHp = maxHp;
        Block = block;
        NextMoveId = nextMoveId ?? string.Empty;
        Powers = Array.AsReadOnly((powers ?? []).ToArray());
        ThreatTarget = threatTarget;
    }

    public uint CombatId { get; }
    public string MonsterId { get; }
    public int CurrentHp { get; }
    public int MaxHp { get; }
    public int Block { get; }
    public string NextMoveId { get; }
    public IReadOnlyList<MultiplayerCarryPowerPublicState> Powers { get; }
    public MultiplayerCarryThreatTarget ThreatTarget { get; }
}

/// <summary>
/// Main-thread-owned public multiplayer input copied into a search root. It contains no
/// Player, Creature, PlayerCombatState, card, potion, relic, or live simulator reference.
/// </summary>
internal sealed class MultiplayerCarryRankingContext
{
    private MultiplayerCarryRankingContext(
        bool enabled,
        long worldVersion,
        string publicFingerprint,
        StateFingerprint remotePublicFingerprint,
        IEnumerable<MultiplayerCarryRemotePlayerPublicState> remotePlayers,
        IEnumerable<MultiplayerCarryEnemyPublicState> enemies,
        bool? multiplayerScalingHooks,
        string cardMultiplayerConstraint)
    {
        Enabled = enabled;
        WorldVersion = worldVersion;
        PublicFingerprint = publicFingerprint ?? string.Empty;
        RemotePublicFingerprint = remotePublicFingerprint;
        RemotePlayers = Array.AsReadOnly(remotePlayers.ToArray());
        Enemies = Array.AsReadOnly(enemies.ToArray());
        MultiplayerScalingHooks = multiplayerScalingHooks;
        CardMultiplayerConstraint = cardMultiplayerConstraint ?? string.Empty;
    }

    public bool Enabled { get; }
    public long WorldVersion { get; }
    public string PublicFingerprint { get; }
    /// <summary>
    /// Public teammate/scaling input that is expected to remain stable across a
    /// local-only continuation. Enemy state is intentionally not included here;
    /// it is validated by <see cref="ContinuationStamp"/> at the predicted turn.
    /// </summary>
    public StateFingerprint RemotePublicFingerprint { get; }
    public IReadOnlyList<MultiplayerCarryRemotePlayerPublicState> RemotePlayers { get; }
    public IReadOnlyList<MultiplayerCarryEnemyPublicState> Enemies { get; }
    public bool? MultiplayerScalingHooks { get; }
    public string CardMultiplayerConstraint { get; }

    public static MultiplayerCarryRankingContext Disabled { get; } =
        Create(
            enabled: false,
            worldVersion: 0,
            publicFingerprint: string.Empty,
            remotePublicFingerprint: default,
            remotePlayers: [],
            enemies: [],
            multiplayerScalingHooks: null,
            cardMultiplayerConstraint: string.Empty);

    public static MultiplayerCarryRankingContext Create(
        bool enabled,
        long worldVersion,
        string publicFingerprint,
        IEnumerable<MultiplayerCarryRemotePlayerPublicState> remotePlayers,
        IEnumerable<MultiplayerCarryEnemyPublicState> enemies,
        bool? multiplayerScalingHooks,
        string cardMultiplayerConstraint)
        => Create(
            enabled,
            worldVersion,
            publicFingerprint,
            default,
            remotePlayers,
            enemies,
            multiplayerScalingHooks,
            cardMultiplayerConstraint);

    public static MultiplayerCarryRankingContext Create(
        bool enabled,
        long worldVersion,
        string publicFingerprint,
        StateFingerprint remotePublicFingerprint,
        IEnumerable<MultiplayerCarryRemotePlayerPublicState> remotePlayers,
        IEnumerable<MultiplayerCarryEnemyPublicState> enemies,
        bool? multiplayerScalingHooks,
        string cardMultiplayerConstraint)
        => new(
            enabled,
            worldVersion,
            publicFingerprint,
            remotePublicFingerprint,
            remotePlayers,
            enemies,
            multiplayerScalingHooks,
            cardMultiplayerConstraint);

    public bool IsFreshFor(long worldVersion, string publicFingerprint)
        => Enabled
            && WorldVersion == worldVersion
            && string.Equals(PublicFingerprint, publicFingerprint, StringComparison.Ordinal);
}

internal sealed class MultiplayerCarryEnemyOutcome
{
    public MultiplayerCarryEnemyOutcome(uint combatId, int effectiveDurability)
    {
        CombatId = combatId;
        EffectiveDurability = effectiveDurability;
    }

    public uint CombatId { get; }
    public int EffectiveDurability { get; }
}

/// <summary>
/// Public outcome information made available to the pure evaluator. An empty outcome list
/// is intentionally not interpreted as "all enemies are gone"; only the explicit terminal
/// flag can make that claim.
/// </summary>
internal sealed class MultiplayerCarryCandidateObservation
{
    private MultiplayerCarryCandidateObservation(
        bool allEnemiesDead,
        IEnumerable<MultiplayerCarryEnemyOutcome> enemiesAfter)
    {
        AllEnemiesDead = allEnemiesDead;
        EnemiesAfter = Array.AsReadOnly(enemiesAfter.ToArray());
    }

    public bool AllEnemiesDead { get; }
    public IReadOnlyList<MultiplayerCarryEnemyOutcome> EnemiesAfter { get; }

    public static MultiplayerCarryCandidateObservation Create(
        bool allEnemiesDead,
        IEnumerable<MultiplayerCarryEnemyOutcome> enemiesAfter)
        => new(allEnemiesDead, enemiesAfter);
}

internal readonly record struct MultiplayerCarryEvaluation(
    bool Enabled,
    int RemoteRiskBefore,
    int RemoteRiskAfter,
    int ThreatsRemoved,
    int UnknownRiskCount,
    int CarryPreference,
    string Reason)
{
    public static MultiplayerCarryEvaluation Disabled(string reason = "disabled")
        => new(false, 0, 0, 0, 0, 0, reason);
}

internal readonly record struct MultiplayerCarryCompatibilityKey(
    bool CompleteVictory,
    int DeadFallbackRank,
    int DeathSaveUseCount,
    int PreservedStolenResource,
    int StrategicHpDeficit,
    int StrategyGoalHpCredit,
    int StrategyGoalCount,
    int CombatEndedTurn,
    int PolicyHpDeficit,
    int HealthResourceCost,
    int LongTermResourceValue,
    int AngerCopiesGenerated,
    int BoundaryRank,
    int OptionalPotionCount,
    int StrategicSold,
    int EnemyHp);

internal static class MultiplayerCarryRankingContracts
{
    internal static bool IsDecisiveTieBreak(
        MultiplayerCarryCompatibilityKey selectedKey,
        int selectedCarryPreference,
        MultiplayerCarryCompatibilityKey carryFreeWinnerKey,
        int carryFreeWinnerPreference,
        bool selectedWouldAlreadyWinWithoutCarry)
        => selectedKey == carryFreeWinnerKey
            && selectedCarryPreference > carryFreeWinnerPreference
            && !selectedWouldAlreadyWinWithoutCarry;
}

/// <summary>
/// Pure, deterministic Carry Ranking evaluator. It scores no teammate action and assigns
/// no guessed damage/block value. Unknown enemy targeting stays outside the preference score.
/// </summary>
internal static class MultiplayerCarryRankingEvaluator
{
    public static MultiplayerCarryEvaluation Evaluate(
        MultiplayerCarryRankingContext context,
        MultiplayerCarryCandidateObservation candidate)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidate);

        if (!context.Enabled || context.RemotePlayers.Count == 0)
            return MultiplayerCarryEvaluation.Disabled(
                context.Enabled ? "no_remote_teammate" : "disabled");

        int remoteHealthRisk = context.RemotePlayers.Sum(RemoteHealthRisk);
        int knownThreatsBefore = 0;
        int knownThreatsAfter = 0;
        int threatsRemoved = 0;
        int unknownRiskCount = 0;

        foreach (MultiplayerCarryEnemyPublicState enemy in context.Enemies)
        {
            if (!IsActivePublicThreat(enemy))
                continue;

            if (enemy.ThreatTarget is MultiplayerCarryThreatTarget.RemotePlayer
                or MultiplayerCarryThreatTarget.AllPlayers)
            {
                knownThreatsBefore++;
                if (candidate.AllEnemiesDead || DurabilityAfter(candidate, enemy.CombatId) <= 0)
                    threatsRemoved++;
                else
                    knownThreatsAfter++;
                continue;
            }

            if (enemy.ThreatTarget == MultiplayerCarryThreatTarget.Unknown)
                unknownRiskCount++;
        }

        int riskBefore = checked(remoteHealthRisk + knownThreatsBefore);
        int riskAfter = checked(remoteHealthRisk + knownThreatsAfter);
        int carryPreference = Math.Max(0, riskBefore - riskAfter);
        string reason = carryPreference > 0
            ? remoteHealthRisk > 0
                ? "low_remote_effective_hp+public_remote_threat_removed"
                : "public_remote_threat_removed"
            : unknownRiskCount > 0
                ? "unknown_enemy_targeting_neutral"
                : "no_meaningful_team_risk_difference";

        return new(
            Enabled: true,
            RemoteRiskBefore: riskBefore,
            RemoteRiskAfter: riskAfter,
            ThreatsRemoved: threatsRemoved,
            UnknownRiskCount: unknownRiskCount,
            CarryPreference: carryPreference,
            Reason: reason);
    }

    private static int RemoteHealthRisk(MultiplayerCarryRemotePlayerPublicState player)
        => Math.Max(0, player.MaxHp - player.CurrentHp - Math.Max(0, player.Block));

    private static bool IsActivePublicThreat(MultiplayerCarryEnemyPublicState enemy)
        => Math.Max(0, enemy.CurrentHp) + Math.Max(0, enemy.Block) > 0
            && !string.IsNullOrWhiteSpace(enemy.NextMoveId)
            && !string.Equals(enemy.NextMoveId, "-", StringComparison.Ordinal);

    private static int DurabilityAfter(
        MultiplayerCarryCandidateObservation candidate,
        uint combatId)
    {
        foreach (MultiplayerCarryEnemyOutcome outcome in candidate.EnemiesAfter)
        {
            if (outcome.CombatId == combatId)
                return outcome.EffectiveDurability;
        }

        return int.MaxValue;
    }
}
