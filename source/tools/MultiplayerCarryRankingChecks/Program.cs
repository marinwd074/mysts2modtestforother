using CombatSolver;

int checks = 0;

void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
    Console.WriteLine($"PASS {++checks}: {message}");
}

MultiplayerCarryRemotePlayerPublicState LowHpTeammate()
    => new(
        netId: "1001",
        characterId: "IRONCLAD",
        currentHp: 12,
        maxHp: 60,
        block: 0,
        turnNumber: 3,
        phase: "Action");

MultiplayerCarryEnemyPublicState RemoteThreat(
    MultiplayerCarryThreatTarget target = MultiplayerCarryThreatTarget.RemotePlayer)
    => new(
        combatId: 7,
        monsterId: "THREAT",
        currentHp: 10,
        maxHp: 10,
        block: 0,
        nextMoveId: "ATTACK",
        threatTarget: target);

MultiplayerCarryRankingContext Context(
    MultiplayerCarryThreatTarget target = MultiplayerCarryThreatTarget.RemotePlayer,
    string fingerprint = "public-v1")
    => MultiplayerCarryRankingContext.Create(
        enabled: true,
        worldVersion: 10,
        publicFingerprint: fingerprint,
        remotePlayers: [LowHpTeammate()],
        enemies: [RemoteThreat(target)],
        multiplayerScalingHooks: true,
        cardMultiplayerConstraint: "Shared");

MultiplayerCarryCandidateObservation Candidate(int durability, bool allEnemiesDead = false)
    => MultiplayerCarryCandidateObservation.Create(
        allEnemiesDead,
        [new MultiplayerCarryEnemyOutcome(7, durability)]);

MultiplayerCarryEvaluation disabled = MultiplayerCarryRankingEvaluator.Evaluate(
    MultiplayerCarryRankingContext.Disabled,
    Candidate(10));
Check(!disabled.Enabled && disabled.CarryPreference == 0, "No remote teammate keeps Carry Ranking disabled and neutral.");

MultiplayerCarryRankingContext equivalentContext = Context();
MultiplayerCarryEvaluation unchangedA = MultiplayerCarryRankingEvaluator.Evaluate(
    equivalentContext,
    Candidate(10));
MultiplayerCarryEvaluation unchangedB = MultiplayerCarryRankingEvaluator.Evaluate(
    equivalentContext,
    Candidate(10));
Check(
    unchangedA.CarryPreference == unchangedB.CarryPreference
        && unchangedA.RemoteRiskBefore == unchangedB.RemoteRiskBefore
        && unchangedA.RemoteRiskAfter == unchangedB.RemoteRiskAfter,
    "Equivalent public team risk produces the same neutral carry ordering key.");

MultiplayerCarryEvaluation clearThreatRemoved = MultiplayerCarryRankingEvaluator.Evaluate(
    equivalentContext,
    Candidate(0));
Check(
    clearThreatRemoved.CarryPreference > 0
        && clearThreatRemoved.ThreatsRemoved == 1
        && clearThreatRemoved.RemoteRiskAfter < clearThreatRemoved.RemoteRiskBefore,
    "A quality-compatible route that removes a proven remote threat receives carry preference.");

MultiplayerCarryEvaluation terminalClear = MultiplayerCarryRankingEvaluator.Evaluate(
    equivalentContext,
    Candidate(10, allEnemiesDead: true));
Check(
    terminalClear.ThreatsRemoved == 1 && terminalClear.CarryPreference > 0,
    "An explicit all-enemies-dead outcome is the only global threat-clear claim.");

MultiplayerCarryEvaluation unknownTarget = MultiplayerCarryRankingEvaluator.Evaluate(
    Context(MultiplayerCarryThreatTarget.Unknown),
    Candidate(0));
Check(
    unknownTarget.CarryPreference == 0
        && unknownTarget.ThreatsRemoved == 0
        && unknownTarget.UnknownRiskCount == 1
        && unknownTarget.Reason == "unknown_enemy_targeting_neutral",
    "Unknown enemy targeting stays neutral and never becomes guessed damage.");

MultiplayerCarryRankingContext localOnly = MultiplayerCarryRankingContext.Create(
    enabled: true,
    worldVersion: 10,
    publicFingerprint: "local-only",
    remotePlayers: [LowHpTeammate()],
    enemies: [new MultiplayerCarryEnemyPublicState(
        8, "LOCAL_THREAT", 10, 10, 0, "ATTACK",
        threatTarget: MultiplayerCarryThreatTarget.LocalPlayer)],
    multiplayerScalingHooks: false,
    cardMultiplayerConstraint: "Shared");
MultiplayerCarryEvaluation localThreat = MultiplayerCarryRankingEvaluator.Evaluate(
    localOnly,
    Candidate(0));
Check(
    localThreat.CarryPreference == 0 && localThreat.ThreatsRemoved == 0,
    "A threat proven to target only the local player cannot inflate team carry score.");

List<MultiplayerCarryPowerPublicState> mutablePowers =
    [new MultiplayerCarryPowerPublicState("POWER", 2)];
MultiplayerCarryRemotePlayerPublicState copiedPublic = new(
    "1002", "DEFECT", 20, 50, 3, 4, "Action", mutablePowers);
mutablePowers.Add(new MultiplayerCarryPowerPublicState("PRIVATE_SHOULD_NOT_LEAK", 99));
Check(
    copiedPublic.Powers.Count == 1
        && !typeof(MultiplayerCarryRemotePlayerPublicState)
            .GetProperties()
            .Any(property => property.Name.Contains("Hand", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Potion", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Energy", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Relic", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Private", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("CombatState", StringComparison.OrdinalIgnoreCase)),
    "Remote public context copies its inputs and exposes no private hand/potion/energy/relic state.");

MultiplayerCarryRankingContext oldContext = Context(fingerprint: "public-v1");
MultiplayerCarryRankingContext freshContext = Context(fingerprint: "public-v2");
Check(
    oldContext.IsFreshFor(10, "public-v1")
        && !oldContext.IsFreshFor(11, "public-v1")
        && !oldContext.IsFreshFor(10, "public-v2")
        && freshContext.IsFreshFor(10, "public-v2"),
    "A world-version or public-fingerprint change invalidates the old ranking context.");

Check(
    MultiplayerCarryThreatTargetContracts.ClassifyBaseGameMove(
        isBaseGameMonster: true,
        hasAttackIntent: true) == MultiplayerCarryThreatTarget.AllPlayers
        && MultiplayerCarryThreatTargetContracts.ClassifyBaseGameMove(
            isBaseGameMonster: true,
            hasAttackIntent: false) == MultiplayerCarryThreatTarget.Unknown
        && MultiplayerCarryThreatTargetContracts.ClassifyBaseGameMove(
            isBaseGameMonster: false,
            hasAttackIntent: true) == MultiplayerCarryThreatTarget.Unknown,
    "Only a proven base-game attack intent becomes an all-player public threat; non-attacks and third-party monsters stay Unknown.");

Console.WriteLine($"PASS: {checks} Multiplayer Carry Ranking checks");
