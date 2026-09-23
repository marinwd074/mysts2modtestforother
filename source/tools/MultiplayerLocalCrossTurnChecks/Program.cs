using CombatSolver;

int checks = 0;

void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
    Console.WriteLine($"PASS {++checks}: {message}");
}

StateFingerprint Fingerprint(ulong first, ulong second = 0)
    => new(first, second);

MultiplayerContinuationMatchInput Match(
    StateFingerprint? remote = null,
    string combatIdentity = "combat-a",
    long actualWorldVersion = 12)
    => new(
        ExpectedCombatIdentity: "combat-a",
        ActualCombatIdentity: combatIdentity,
        ExpectedLocalNetId: "local-1",
        ActualLocalNetId: "local-1",
        ExpectedRemotePublicFingerprint: Fingerprint(1),
        ActualRemotePublicFingerprint: remote ?? Fingerprint(1),
        ExpectedMultiplayerScalingHooks: true,
        ActualMultiplayerScalingHooks: true,
        ExpectedCardMultiplayerConstraint: "Shared",
        ActualCardMultiplayerConstraint: "Shared",
        ExpectedSourceWorldVersion: 10,
        MinimumWorldVersion: 10,
        ActualWorldVersion: actualWorldVersion);

Check(
    !MultiplayerLocalCrossTurnContracts.IsCurrentTurnOnly(SearchRoutePolicy.SinglePlayerFullRoute)
        && MultiplayerLocalCrossTurnContracts.IsCurrentTurnOnly(SearchRoutePolicy.MultiplayerCurrentTurnOnly)
        && !MultiplayerLocalCrossTurnContracts.IsCurrentTurnOnly(SearchRoutePolicy.MultiplayerLocalCrossTurn)
        && MultiplayerLocalCrossTurnContracts.CanUseFullSearchHeuristics(SearchRoutePolicy.SinglePlayerFullRoute)
        && !MultiplayerLocalCrossTurnContracts.CanUseFullSearchHeuristics(SearchRoutePolicy.MultiplayerCurrentTurnOnly)
        && MultiplayerLocalCrossTurnContracts.CanUseFullSearchHeuristics(SearchRoutePolicy.MultiplayerLocalCrossTurn)
        && MultiplayerLocalCrossTurnContracts.CanUsePersistentRouteCache(SearchRoutePolicy.SinglePlayerFullRoute)
        && !MultiplayerLocalCrossTurnContracts.CanUsePersistentRouteCache(SearchRoutePolicy.MultiplayerLocalCrossTurn),
    "Singleplayer and multiplayer local-cross-turn share full search heuristics while current-turn-only remains reduced; persistent route cache stays singleplayer-only.");

Check(
    !MultiplayerLocalCrossTurnContracts.ShouldExcludeMultiplayerOnlyCard(
        SearchRoutePolicy.SinglePlayerFullRoute,
        isMultiplayerOnly: true)
        && MultiplayerLocalCrossTurnContracts.ShouldExcludeMultiplayerOnlyCard(
            SearchRoutePolicy.MultiplayerCurrentTurnOnly,
            isMultiplayerOnly: true)
        && MultiplayerLocalCrossTurnContracts.ShouldExcludeMultiplayerOnlyCard(
            SearchRoutePolicy.MultiplayerLocalCrossTurn,
            isMultiplayerOnly: true)
        && !MultiplayerLocalCrossTurnContracts.ShouldExcludeMultiplayerOnlyCard(
            SearchRoutePolicy.MultiplayerLocalCrossTurn,
            isMultiplayerOnly: false),
    "Multiplayer-only cards remain in card state but never become multiplayer search actions; singleplayer policy is unchanged.");

Check(
    !MultiplayerLocalCrossTurnContracts.DelayAngerCopyPreferenceUntilAfterEnemyHp(
        SearchRoutePolicy.SinglePlayerFullRoute,
        completeVictory: false)
        && !MultiplayerLocalCrossTurnContracts.DelayAngerCopyPreferenceUntilAfterEnemyHp(
            SearchRoutePolicy.MultiplayerLocalCrossTurn,
            completeVictory: true)
        && MultiplayerLocalCrossTurnContracts.DelayAngerCopyPreferenceUntilAfterEnemyHp(
            SearchRoutePolicy.MultiplayerLocalCrossTurn,
            completeVictory: false),
    "Incomplete multiplayer local-cross-turn routes rank deterministic enemy HP before Anger copy count, while singleplayer and complete victories keep the original long-term ordering.");

Check(
    MultiplayerLocalCrossTurnContracts.ShouldStopBeforeSharedRngShuffle(
        SearchRoutePolicy.MultiplayerLocalCrossTurn,
        rootSetup: false,
        sharedShuffleForecastTrusted: false,
        willShuffle: true)
        && !MultiplayerLocalCrossTurnContracts.ShouldStopBeforeSharedRngShuffle(
            SearchRoutePolicy.MultiplayerLocalCrossTurn,
            rootSetup: false,
            sharedShuffleForecastTrusted: true,
            willShuffle: true)
        && !MultiplayerLocalCrossTurnContracts.ShouldStopBeforeSharedRngShuffle(
            SearchRoutePolicy.MultiplayerLocalCrossTurn,
            rootSetup: true,
            sharedShuffleForecastTrusted: false,
            willShuffle: true)
        && !MultiplayerLocalCrossTurnContracts.ShouldStopBeforeSharedRngShuffle(
            SearchRoutePolicy.MultiplayerLocalCrossTurn,
            rootSetup: false,
            sharedShuffleForecastTrusted: false,
            willShuffle: false)
        && !MultiplayerLocalCrossTurnContracts.ShouldStopBeforeSharedRngShuffle(
            SearchRoutePolicy.SinglePlayerFullRoute,
            rootSetup: false,
            sharedShuffleForecastTrusted: false,
            willShuffle: true)
        && !MultiplayerLocalCrossTurnContracts.ShouldStopBeforeSharedRngShuffle(
            SearchRoutePolicy.MultiplayerCurrentTurnOnly,
            rootSetup: false,
            sharedShuffleForecastTrusted: false,
            willShuffle: true),
    "Shuffle is a trust boundary, not a count boundary: a Joint/Shadow worldline may cross repeated shuffles while an untrusted multiplayer fallback stops at the first future shuffle.");

Check(
    MultiplayerLocalCrossTurnContracts.ValidateLocalOnlyProjection(
        [
            new(1, IsLocalAction: true, IsEndTurn: false),
            new(1, IsLocalAction: true, IsEndTurn: true),
            new(2, IsLocalAction: true, IsEndTurn: false),
        ],
        startTurnNumber: 1,
        out string t2Failure)
        && t2Failure.Length == 0,
    "A local T1 to T2 projection crosses one EndTurn without adding a remote action.");

Check(
    MultiplayerLocalCrossTurnContracts.ValidateLocalOnlyProjection(
        [
            new(1, IsLocalAction: true, IsEndTurn: false),
            new(1, IsLocalAction: true, IsEndTurn: true),
            new(2, IsLocalAction: true, IsEndTurn: false),
            new(2, IsLocalAction: true, IsEndTurn: true),
            new(3, IsLocalAction: true, IsEndTurn: false),
        ],
        startTurnNumber: 1,
        out string t3Failure)
        && t3Failure.Length == 0,
    "A local T1 to T2 to T3 projection remains valid across two local turn boundaries.");

Check(
    !MultiplayerLocalCrossTurnContracts.ValidateLocalOnlyProjection(
        [new(1, IsLocalAction: false, IsEndTurn: true)],
        startTurnNumber: 1,
        out string remoteActionFailure)
        && remoteActionFailure == "remote_action_present",
    "A projected teammate action is rejected instead of being fabricated into the local route.");

Check(
    MultiplayerLocalCrossTurnContracts.IsExactContinuation(Match())
        && MultiplayerLocalCrossTurnContracts.DescribeContinuationMismatch(Match()) is null,
    "An exact local continuation requires a strictly advanced WorldVersion and matching public inputs.");

Check(
    !MultiplayerLocalCrossTurnContracts.IsExactContinuation(Match(remote: Fingerprint(2)))
        && MultiplayerLocalCrossTurnContracts.DescribeContinuationMismatch(Match(remote: Fingerprint(2)))
            == "remote_public_mismatch"
        && !MultiplayerLocalCrossTurnContracts.IsExactContinuation(Match(actualWorldVersion: 10))
        && MultiplayerLocalCrossTurnContracts.DescribeContinuationMismatch(Match(actualWorldVersion: 10))
            == "world_version_not_advanced",
    "A remote public delta or a non-advanced WorldVersion rejects future-route reuse with a precise reason.");

Check(
    !MultiplayerLocalCrossTurnContracts.CanSoftReuseRemotePublicDelta(
        Match(remote: Fingerprint(2)))
        && !MultiplayerLocalCrossTurnContracts.CanSoftReuseRemotePublicDelta(
            Match(remote: Fingerprint(1)))
        && !MultiplayerLocalCrossTurnContracts.CanSoftReuseRemotePublicDelta(
            Match(remote: Fingerprint(2), actualWorldVersion: 10))
        && !MultiplayerLocalCrossTurnContracts.CanSoftReuseRemotePublicDelta(
            Match(remote: Fingerprint(2), combatIdentity: "combat-b")),
    "A locally readable teammate-state delta always rejects soft reuse and requires a fresh search.");

Check(
    !MultiplayerLocalCrossTurnContracts.IsExactContinuation(Match(combatIdentity: "combat-without-target"))
        && MultiplayerLocalCrossTurnContracts.DescribeContinuationMismatch(
            Match(combatIdentity: "combat-without-target")) == "combat_identity_mismatch",
    "A combat identity change such as teammate removal of the planned target rejects the old route.");

Check(
    MultiplayerLocalCrossTurnContracts.IsCurrentTurnAction(1, 1)
        && !MultiplayerLocalCrossTurnContracts.IsCurrentTurnAction(2, 1)
        && !MultiplayerLocalCrossTurnContracts.HasCurrentTurnPlayableAction(
            [new(1, IsLocalAction: true, IsEndTurn: true)],
            currentTurn: 1)
        && MultiplayerLocalCrossTurnContracts.HasCurrentTurnPlayableAction(
            [new(1, IsLocalAction: true, IsEndTurn: false), new(1, IsLocalAction: true, IsEndTurn: true)],
            currentTurn: 1)
        && MultiplayerLocalCrossTurnContracts.CanPreserveFutureRoute(
            canReuse: true,
            awaitingContinuation: true,
            continuationCount: 2,
            scope: MultiplayerSearchResultScope.PartialLocalCrossTurnProjection)
        && !MultiplayerLocalCrossTurnContracts.CanPreserveFutureRoute(
            canReuse: true,
            awaitingContinuation: true,
            continuationCount: 2,
            scope: MultiplayerSearchResultScope.CurrentTurnOnly),
    "Deployment admits only the current local turn, while a non-current-turn route is preserved only as future data.");

MultiplayerContinuationScheduleDecision heldContinuation =
    MultiplayerLocalCrossTurnContracts.DecidePendingContinuationScheduling(
        awaitingContinuation: true,
        hasContinuationSource: true,
        hasCurrentTurnContinuation: false,
        localTurnPlayable: false);
MultiplayerContinuationScheduleDecision missingPlayableContinuation =
    MultiplayerLocalCrossTurnContracts.DecidePendingContinuationScheduling(
        awaitingContinuation: true,
        hasContinuationSource: true,
        hasCurrentTurnContinuation: false,
        localTurnPlayable: true);
MultiplayerContinuationScheduleDecision currentTurnContinuation =
    MultiplayerLocalCrossTurnContracts.DecidePendingContinuationScheduling(
        awaitingContinuation: true,
        hasContinuationSource: true,
        hasCurrentTurnContinuation: true,
        localTurnPlayable: true);
MultiplayerContinuationScheduleDecision noContinuationSource =
    MultiplayerLocalCrossTurnContracts.DecidePendingContinuationScheduling(
        awaitingContinuation: true,
        hasContinuationSource: false,
        hasCurrentTurnContinuation: false,
        localTurnPlayable: true);
Check(
    heldContinuation.HoldPendingRoute
        && !heldContinuation.FreshSearchMissingCurrentTurn
        && !heldContinuation.ClearAwaitingContinuation
        && !heldContinuation.ClearContinuationSource
        && !missingPlayableContinuation.HoldPendingRoute
        && missingPlayableContinuation.FreshSearchMissingCurrentTurn
        && missingPlayableContinuation.ClearAwaitingContinuation
        && missingPlayableContinuation.ClearContinuationSource
        && currentTurnContinuation == default
        && noContinuationSource == default,
    "The scheduler holds a missing future turn only while local play is unavailable; once playable it fresh-searches and clears both pending-route ownership fields.");

Check(
    MultiplayerLocalCrossTurnContracts.PreferCurrentTurnPlayableRoute(
        SearchRoutePolicy.MultiplayerLocalCrossTurn,
        candidateHasCurrentTurnCard: true,
        currentHasCurrentTurnCard: false)
        && !MultiplayerLocalCrossTurnContracts.PreferCurrentTurnPlayableRoute(
            SearchRoutePolicy.MultiplayerLocalCrossTurn,
            candidateHasCurrentTurnCard: false,
            currentHasCurrentTurnCard: true)
        && !MultiplayerLocalCrossTurnContracts.PreferCurrentTurnPlayableRoute(
            SearchRoutePolicy.SinglePlayerFullRoute,
            candidateHasCurrentTurnCard: true,
            currentHasCurrentTurnCard: false)
        && !MultiplayerLocalCrossTurnContracts.PreferCurrentTurnPlayableRoute(
            SearchRoutePolicy.MultiplayerCurrentTurnOnly,
            candidateHasCurrentTurnCard: true,
            currentHasCurrentTurnCard: false),
    "Local cross-turn tie-breaking prefers a current-turn card over an EndTurn-only route without changing single-player or current-turn-only policies.");


MultiplayerRetentionObservation[] diversityObservations =
[
    // Lowest team loss, but not the safest individual.
    new(false, true, 0.01d, 0.20d, 0.70d, int.MaxValue, 0, 0, 0, 0, 0, 0),
    // Safest worst-player route.
    new(false, true, 0.03d, 0.02d, 0.65d, int.MaxValue, 0, 0, 0, 0, 0, 0),
    // Fast completed lethal route.
    new(true, true, 0.06d, 0.08d, 0d, 2, 0, 0, 0, 0, 0, 0),
    // Growth route.
    new(false, true, 0.04d, 0.10d, 0.60d, int.MaxValue, 9, 7, 5, 4, 3, 2),
    // Attractive but dead route: must not consume a protected lane while any alive route exists.
    new(true, false, 0.00d, 0.00d, 0d, 1, 99, 99, 99, 99, 99, 99),
];
IReadOnlyList<MultiplayerRetentionChoice> diversityChoices =
    MultiplayerRetentionDiversityPolicy.SelectProtected(
        diversityObservations,
        limit: 4);
Check(
    diversityChoices.Count == 4
        && diversityChoices.Any(choice =>
            choice.Lane == MultiplayerRetentionLane.LowTeamLoss
            && choice.Index == 0)
        && diversityChoices.Any(choice =>
            choice.Lane == MultiplayerRetentionLane.TeamSafety
            && choice.Index == 1)
        && diversityChoices.Any(choice =>
            choice.Lane == MultiplayerRetentionLane.FastLethal
            && choice.Index == 2)
        && diversityChoices.Any(choice =>
            choice.Lane == MultiplayerRetentionLane.Growth
            && choice.Index == 3)
        && diversityChoices.All(choice => choice.Index != 4),
    "P2 fixed-budget retention protects distinct low-loss, team-safety, fast-lethal and growth representatives without spending a slot on a dead route.");

IReadOnlyList<MultiplayerRetentionChoice> tightDiversityChoices =
    MultiplayerRetentionDiversityPolicy.SelectProtected(
        diversityObservations,
        limit: 2);
Check(
    tightDiversityChoices.Count == 2
        && tightDiversityChoices[0].Lane == MultiplayerRetentionLane.LowTeamLoss
        && tightDiversityChoices[1].Lane == MultiplayerRetentionLane.TeamSafety,
    "P2 diversity protection respects the existing beam limit instead of expanding the budget.");

MultiplayerRetentionObservation[] overlappingLaneObservations =
[
    new(false, true, 0.01d, 0.01d, 0.80d, int.MaxValue, 0, 0, 0, 0, 0, 0),
    new(false, true, 0.02d, 0.02d, 0.70d, int.MaxValue, 0, 0, 0, 0, 0, 0),
    new(false, true, 0.03d, 0.03d, 0.10d, int.MaxValue, 0, 0, 0, 0, 0, 0),
    new(false, true, 0.04d, 0.04d, 0.60d, int.MaxValue, 8, 6, 4, 3, 2, 1),
];
IReadOnlyList<MultiplayerRetentionChoice> overlappingLaneChoices =
    MultiplayerRetentionDiversityPolicy.SelectProtected(
        overlappingLaneObservations,
        limit: 4);
Check(
    overlappingLaneChoices.Count == 4
        && overlappingLaneChoices.Select(choice => choice.Index).Distinct().Count() == 4,
    "When one route leads multiple objectives, P2 reuses that route once and spends remaining protected slots on distinct representatives.");

MultiplayerChanceCoverageCandidate[] chanceCoverageCandidates =
[
    // Decision 0 already retained its modal scenario; its next scenario belongs to round 1.
    new(0, DecisionRank: 0, ScenarioRank: 0, AlreadyRetained: true),
    new(1, DecisionRank: 0, ScenarioRank: 1, AlreadyRetained: false),
    new(2, DecisionRank: 0, ScenarioRank: 2, AlreadyRetained: false),
    // Decisions 1 and 2 still need their modal scenarios.
    new(3, DecisionRank: 1, ScenarioRank: 0, AlreadyRetained: false),
    new(4, DecisionRank: 1, ScenarioRank: 1, AlreadyRetained: false),
    new(5, DecisionRank: 2, ScenarioRank: 0, AlreadyRetained: false),
];
IReadOnlyList<int> chanceCoverage =
    MultiplayerChanceCoveragePolicy.SelectAdditionalCandidateIndices(
        chanceCoverageCandidates,
        extraLimit: 4);
Check(
    chanceCoverage.SequenceEqual([3, 5, 1, 4]),
    "P3 chance coverage counts an already-retained modal scenario in its natural round and fills missing scenarios round-robin across represented decisions.");

IReadOnlyList<int> cappedChanceCoverage =
    MultiplayerChanceCoveragePolicy.SelectAdditionalCandidateIndices(
        chanceCoverageCandidates,
        extraLimit: 2);
Check(
    cappedChanceCoverage.SequenceEqual([3, 5]),
    "P3 chance coverage obeys its hard extra-candidate cap instead of widening the final portfolio without bound.");


ShadowTeammateScenarioObservation[] teammateScenarioObservations =
[
    new(2, false, true, 20, 50, 0.50d, 1, 0, -1.0d, "A:Vulnerable>B:Attack"),
    new(2, false, true, 35, 75, 0.80d, 1, 0, -1.2d, "B:Defend>A:Attack"),
    new(1, false, true, 45, 55, 0.55d, 3, 1, -0.9d, "A:Power"),
    new(0, false, true, 50, 50, 0.50d, 4, 1, -0.7d, ""),
    new(2, false, true, 24, 52, 0.52d, 1, 0, -0.8d, "B:Attack>A:Vulnerable"),
];
IReadOnlyList<ShadowTeammateScenarioChoice> teammateScenarioChoices =
    ShadowTeammateScenarioPolicy.SelectProtected(
        teammateScenarioObservations,
        limit: 4);
string teammateScenarioSelection = string.Join(
    ",",
    teammateScenarioChoices.Select(choice => $"{choice.Kind}:{choice.Index}"));
Check(
    teammateScenarioChoices.Count == 4
        && teammateScenarioChoices.Any(choice =>
            choice.Kind == ShadowTeammateScenarioKind.Aggressive
            && choice.Index == 0)
        && teammateScenarioChoices.Any(choice =>
            choice.Kind == ShadowTeammateScenarioKind.Defensive
            && choice.Index == 1)
        && teammateScenarioChoices.Any(choice =>
            choice.Kind == ShadowTeammateScenarioKind.Conserve
            && choice.Index == 2)
        && teammateScenarioChoices.Any(choice =>
            choice.Kind == ShadowTeammateScenarioKind.NoAction
            && choice.Index == 3),
    $"P3 Shadow Top-K protects aggressive, defensive, conserve-resource and no-action teammate stress scenarios. actual={teammateScenarioSelection}");

IReadOnlyList<ShadowTeammateScenarioChoice> orderDiversityChoices =
    ShadowTeammateScenarioPolicy.SelectProtected(
        teammateScenarioObservations,
        limit: 5);
Check(
    orderDiversityChoices.Count == 5
        && orderDiversityChoices.Select(choice => choice.Index).Distinct().Count() == 5
        && orderDiversityChoices.Any(choice => choice.Index == 4),
    "P3 uses remaining Shadow capacity for a distinct ordered action sequence, so vulnerable-before-attack and attack-before-vulnerable can remain separate when their modeled states differ.");

MultiplayerScenarioDecisionRank optimisticSingleRoute =
    MultiplayerScenarioReevaluationPolicy.Aggregate(
    [
        new(
            ShadowTeammateScenarioKind.Aggressive,
            CompleteVictory: true,
            AllPlayersAlive: true,
            LossEquivalent: 0.01d,
            WorstPlayerLossRatio: 0.01d,
            TeamLossRatio: 0.01d,
            EnemyDurabilityRatio: 0d),
        new(
            ShadowTeammateScenarioKind.NoAction,
            CompleteVictory: false,
            AllPlayersAlive: true,
            LossEquivalent: 0.45d,
            WorstPlayerLossRatio: 0.35d,
            TeamLossRatio: 0.40d,
            EnemyDurabilityRatio: 0.90d),
    ]);
MultiplayerScenarioDecisionRank robustCurrentAction =
    MultiplayerScenarioReevaluationPolicy.Aggregate(
    [
        new(
            ShadowTeammateScenarioKind.Aggressive,
            CompleteVictory: false,
            AllPlayersAlive: true,
            LossEquivalent: 0.12d,
            WorstPlayerLossRatio: 0.10d,
            TeamLossRatio: 0.10d,
            EnemyDurabilityRatio: 0.30d),
        new(
            ShadowTeammateScenarioKind.NoAction,
            CompleteVictory: false,
            AllPlayersAlive: true,
            LossEquivalent: 0.14d,
            WorstPlayerLossRatio: 0.12d,
            TeamLossRatio: 0.12d,
            EnemyDurabilityRatio: 0.40d),
    ]);
Check(
    MultiplayerScenarioReevaluationPolicy.Compare(
        robustCurrentAction,
        optimisticSingleRoute) < 0,
    "P3 robust reranking prefers the current action with a better worst teammate scenario over a route that only wins under one optimistic teammate behavior.");

Console.WriteLine($"PASS: {checks} multiplayer local-cross-turn contract checks");


ShadowBehaviorActionObservation[] behaviorActions =
[
    new(
        CompleteVictory: false,
        EnemyDurabilityReduction: 18,
        TeamEffectiveHpGain: 0,
        EnergyCost: 1,
        StarCost: 0,
        IsPowerCard: false),
    new(
        CompleteVictory: false,
        EnemyDurabilityReduction: 0,
        TeamEffectiveHpGain: 0,
        EnergyCost: 1,
        StarCost: 0,
        IsPowerCard: false),
    new(
        CompleteVictory: true,
        EnemyDurabilityReduction: 5,
        TeamEffectiveHpGain: 0,
        EnergyCost: 1,
        StarCost: 0,
        IsPowerCard: false),
];
double[] behaviorLogProbabilities =
    ShadowTeammateBehaviorModel.DecisionLogProbabilities(behaviorActions);
double behaviorProbabilitySum = behaviorLogProbabilities.Sum(Math.Exp);
Check(
    Math.Abs(behaviorProbabilitySum - 1d) < 1e-9
        && behaviorLogProbabilities.Length == behaviorActions.Length + 1,
    "Shadow behavior decisions normalize legal actions plus stop into one probability distribution.");

Check(
    behaviorLogProbabilities[2] > behaviorLogProbabilities[0]
        && behaviorLogProbabilities[0] > behaviorLogProbabilities[1],
    "Shadow behavior prior prefers lethal over ordinary progress and ordinary progress over visible no-op play.");

Check(
    ShadowTeammateBehaviorModel.MeanLogProbability(-2d, 2) == -1d
        && ShadowTeammateBehaviorModel.MeanLogProbability(-2d, 0) == 0d,
    "Shadow route plausibility keeps cumulative and per-decision likelihood as separate values.");


double urgencyAboveOldThreshold =
    MultiplayerCombatObjectiveMath.ComputeLethalUrgency(0.350001d);
double urgencyAtOldThreshold =
    MultiplayerCombatObjectiveMath.ComputeLethalUrgency(0.35d);
double urgencyBelowOldThreshold =
    MultiplayerCombatObjectiveMath.ComputeLethalUrgency(0.349999d);
Check(
    urgencyBelowOldThreshold > urgencyAtOldThreshold
        && urgencyAtOldThreshold > urgencyAboveOldThreshold
        && Math.Abs(urgencyBelowOldThreshold - urgencyAboveOldThreshold) < 0.00001d,
    "Adaptive lethal urgency is continuous through the old 35% durability boundary.");

Check(
    MultiplayerCombatObjectiveMath.ComputeLethalUrgency(1d) == 0d
        && MultiplayerCombatObjectiveMath.ComputeLethalUrgency(0d) == 1d
        && MultiplayerCombatObjectiveMath.ComputeLethalUrgency(0.25d)
            > MultiplayerCombatObjectiveMath.ComputeLethalUrgency(0.75d),
    "Adaptive lethal urgency rises smoothly as enemy effective durability falls.");

double earlyBaselineEquivalent =
    MultiplayerCombatObjectiveMath.InterimLossEquivalent(
        teamLossRatio: 0.005d,
        worstPlayerLossRatio: 0.10d,
        enemyDurabilityRatio: 0.80d);
double earlyRiskierProgressEquivalent =
    MultiplayerCombatObjectiveMath.InterimLossEquivalent(
        teamLossRatio: 0.020d,
        worstPlayerLossRatio: 0.10d,
        enemyDurabilityRatio: 0.70d);
double lateProgressEquivalent =
    MultiplayerCombatObjectiveMath.InterimLossEquivalent(
        teamLossRatio: 0.020d,
        worstPlayerLossRatio: 0.10d,
        enemyDurabilityRatio: 0.20d);
Check(
    earlyRiskierProgressEquivalent > earlyBaselineEquivalent
        && lateProgressEquivalent < earlyBaselineEquivalent,
    "Interim objective keeps extra-loss tolerance tiny at high durability but can trade modest loss for strong near-lethal progress.");

MultiplayerCombatObjectiveRank adaptiveFastRank =
    MultiplayerCombatObjectiveMath.BuildRank(
        MultiplayerCombatObjectiveStrategy.AdaptiveLethalTempo,
        completeVictory: true,
        allPlayersAlive: true,
        teamLossRatio: 0.12d,
        worstPlayerLossRatio: 0.10d,
        enemyDurabilityRatio: 0d,
        terminalTempoReferenceEnemyDurabilityRatio: 0.08d,
        combatEndedTurn: 3,
        startTurnNumber: 1);
MultiplayerCombatObjectiveRank adaptiveSlowRank =
    MultiplayerCombatObjectiveMath.BuildRank(
        MultiplayerCombatObjectiveStrategy.AdaptiveLethalTempo,
        completeVictory: true,
        allPlayersAlive: true,
        teamLossRatio: 0.08d,
        worstPlayerLossRatio: 0.10d,
        enemyDurabilityRatio: 0d,
        terminalTempoReferenceEnemyDurabilityRatio: 0.08d,
        combatEndedTurn: 5,
        startTurnNumber: 1);
Check(
    MultiplayerCombatObjectiveMath.Compare(adaptiveFastRank, adaptiveSlowRank) < 0,
    "The shared P1 terminal rank preserves adaptive loss-versus-finish-turn behavior.");

MultiplayerCombatObjectiveRank safeIncompleteRank =
    MultiplayerCombatObjectiveMath.BuildRank(
        MultiplayerCombatObjectiveStrategy.AdaptiveLethalTempo,
        completeVictory: false,
        allPlayersAlive: true,
        teamLossRatio: 0.04d,
        worstPlayerLossRatio: 0.05d,
        enemyDurabilityRatio: 0.30d,
        terminalTempoReferenceEnemyDurabilityRatio: 0.30d,
        combatEndedTurn: null,
        startTurnNumber: 1);
MultiplayerCombatObjectiveRank deadIncompleteRank =
    MultiplayerCombatObjectiveMath.BuildRank(
        MultiplayerCombatObjectiveStrategy.AdaptiveLethalTempo,
        completeVictory: false,
        allPlayersAlive: false,
        teamLossRatio: 0.01d,
        worstPlayerLossRatio: 0.01d,
        enemyDurabilityRatio: 0.05d,
        terminalTempoReferenceEnemyDurabilityRatio: 0.30d,
        combatEndedTurn: null,
        startTurnNumber: 1);
Check(
    MultiplayerCombatObjectiveMath.Compare(safeIncompleteRank, deadIncompleteRank) < 0,
    "All-player survival remains a hard objective boundary before loss or enemy progress.");

MultiplayerCombatObjectiveRank minimizeHighDurability =
    MultiplayerCombatObjectiveMath.BuildRank(
        MultiplayerCombatObjectiveStrategy.MinimizeTeamLoss,
        completeVictory: false,
        allPlayersAlive: true,
        teamLossRatio: 0.03d,
        worstPlayerLossRatio: 0.03d,
        enemyDurabilityRatio: 0.80d,
        terminalTempoReferenceEnemyDurabilityRatio: 0.80d,
        combatEndedTurn: null,
        startTurnNumber: 1);
MultiplayerCombatObjectiveRank minimizeLowDurability =
    MultiplayerCombatObjectiveMath.BuildRank(
        MultiplayerCombatObjectiveStrategy.MinimizeTeamLoss,
        completeVictory: false,
        allPlayersAlive: true,
        teamLossRatio: 0.03d,
        worstPlayerLossRatio: 0.03d,
        enemyDurabilityRatio: 0.20d,
        terminalTempoReferenceEnemyDurabilityRatio: 0.80d,
        combatEndedTurn: null,
        startTurnNumber: 1);
Check(
    minimizeHighDurability.LossEquivalent == minimizeLowDurability.LossEquivalent
        && MultiplayerCombatObjectiveMath.Compare(
            minimizeLowDurability,
            minimizeHighDurability) < 0,
    "MinimizeTeamLoss keeps loss primary while enemy durability remains a deterministic tie-break.");

double mergedScenarioLogMass = ShadowScenarioChanceMath.LogAddExp(
    Math.Log(0.20d),
    Math.Log(0.30d));
Check(
    Math.Abs(Math.Exp(mergedScenarioLogMass) - 0.50d) < 1e-12d,
    "Exact-equivalent Shadow histories add probability mass instead of keeping only the most likely representative.");

ShadowScenarioProbabilitySet retainedScenarioProbabilities =
    ShadowScenarioChanceMath.NormalizeRetainedLogMasses(
        [Math.Log(0.20d), Math.Log(0.30d)]);
Check(
    Math.Abs(retainedScenarioProbabilities.RetainedProbabilityMass - 0.50d) < 1e-12d
        && Math.Abs(retainedScenarioProbabilities.ConditionalProbabilities[0] - 0.40d) < 1e-12d
        && Math.Abs(retainedScenarioProbabilities.ConditionalProbabilities[1] - 0.60d) < 1e-12d,
    "Retained Shadow scenarios expose both raw behavior-mass coverage and normalized conditional scenario weights.");

MultiplayerChanceDecisionRank luckyButUsuallyBad =
    MultiplayerChanceDecisionMath.Aggregate(
    [
        new MultiplayerChanceOutcome(
            0.10d, true, true,
            0.00d, 0.00d, 0.00d, 0.00d),
        new MultiplayerChanceOutcome(
            0.90d, false, true,
            0.50d, 0.30d, 0.40d, 0.80d),
    ]);
MultiplayerChanceDecisionRank consistentlyModerate =
    MultiplayerChanceDecisionMath.Aggregate(
    [
        new MultiplayerChanceOutcome(
            1.00d, false, true,
            0.10d, 0.10d, 0.10d, 0.35d),
    ]);
Check(
    MultiplayerChanceDecisionMath.Compare(
        consistentlyModerate,
        luckyButUsuallyBad) < 0,
    "Chance-node ranking does not select a locally lucky low-probability teammate outcome over the probability-weighted current-turn decision.");

MultiplayerChanceDecisionRank partiallyCoveredVictory =
    MultiplayerChanceDecisionMath.Aggregate(
    [
        new MultiplayerChanceOutcome(
            0.60d, true, true,
            0.02d, 0.02d, 0.02d, 0d),
    ]);
Check(
    Math.Abs(partiallyCoveredVictory.RetainedProbabilityMass - 0.60d) < 1e-12d
        && !partiallyCoveredVictory.GuaranteedVictory
        && Math.Abs(partiallyCoveredVictory.VictoryProbabilityLower - 0.60d) < 1e-12d
        && Math.Abs(partiallyCoveredVictory.ConservativeTeamDeathProbability - 0.40d) < 1e-12d,
    "Uncovered Shadow probability mass is treated conservatively rather than silently renormalized into guaranteed success.");

double healthyTempoRate =
    MultiplayerCombatObjectiveMath.LossRatioPerTurn(
        enemyDurabilityRatio: 0.1d,
        worstPlayerLossRatio: 0d);
double fragileTempoRate =
    MultiplayerCombatObjectiveMath.LossRatioPerTurn(
        enemyDurabilityRatio: 0.1d,
        worstPlayerLossRatio: 0.8d);
Check(
    fragileTempoRate > healthyTempoRate
        && fragileTempoRate <= MultiplayerCombatObjectiveMath.MaximumExtraLossRatioPerTurn
        && healthyTempoRate > 0d,
    "Tempo pressure remains bounded and rises continuously as accumulated team risk increases.");

double healthyFastScore =
    MultiplayerCombatObjectiveMath.ContinuousTempoScore(
        teamLossRatio: 0.12d,
        worstPlayerLossRatio: 0.10d,
        enemyDurabilityRatio: 0.08d,
        combatEndedTurn: 3,
        startTurnNumber: 1);
double healthySlowScore =
    MultiplayerCombatObjectiveMath.ContinuousTempoScore(
        teamLossRatio: 0.08d,
        worstPlayerLossRatio: 0.10d,
        enemyDurabilityRatio: 0.08d,
        combatEndedTurn: 5,
        startTurnNumber: 1);
Check(
    healthyFastScore < healthySlowScore,
    "Near lethal, a healthy team may rationally accept modest extra loss to finish multiple turns earlier.");

double fullDurabilityFastScore =
    MultiplayerCombatObjectiveMath.ContinuousTempoScore(
        teamLossRatio: 0.12d,
        worstPlayerLossRatio: 0d,
        enemyDurabilityRatio: 1d,
        combatEndedTurn: 3,
        startTurnNumber: 1);
double fullDurabilitySlowScore =
    MultiplayerCombatObjectiveMath.ContinuousTempoScore(
        teamLossRatio: 0.08d,
        worstPlayerLossRatio: 0d,
        enemyDurabilityRatio: 1d,
        combatEndedTurn: 5,
        startTurnNumber: 1);
Check(
    fullDurabilitySlowScore < fullDurabilityFastScore,
    "At full enemy durability the continuous tempo term is zero, so lower team loss remains primary.");


Check(
    !ShadowRoutePruningPolicy.MayUseApproximateBeamPruning(
        exactSurvivorCount: 4,
        beamLimit: 4)
        && ShadowRoutePruningPolicy.MayUseApproximateBeamPruning(
            exactSurvivorCount: 5,
            beamLimit: 4),
    "Shadow heuristic quality pruning is forbidden until exact survivors exceed the beam limit.");

ShadowApproximateQuality approximateBetter = new(
    CompleteVictory: false,
    AllPlayersAlive: true,
    EnemyDurability: 20,
    TeamEffectiveHp: 100,
    WorstPlayerEffectiveHpRatio: 0.80d,
    TeamEnergy: 3,
    TeamStars: 1,
    ActionCount: 2);
ShadowApproximateQuality approximateWorse = new(
    CompleteVictory: false,
    AllPlayersAlive: true,
    EnemyDurability: 30,
    TeamEffectiveHp: 90,
    WorstPlayerEffectiveHpRatio: 0.70d,
    TeamEnergy: 2,
    TeamStars: 1,
    ActionCount: 3);
Check(
    ShadowRoutePruningPolicy.HeuristicQualityDominates(
        approximateBetter,
        approximateWorse)
        && !ShadowRoutePruningPolicy.HeuristicQualityDominates(
            approximateWorse,
            approximateBetter),
    "Shadow summary-quality dominance remains available only as an explicitly heuristic beam relation.");

ShadowApproximateQuality incomparableDeckProxyA = approximateBetter with
{
    EnemyDurability = 15,
    TeamEnergy = 1,
};
ShadowApproximateQuality incomparableDeckProxyB = approximateBetter with
{
    EnemyDurability = 25,
    TeamEnergy = 4,
};
Check(
    !ShadowRoutePruningPolicy.HeuristicQualityDominates(
        incomparableDeckProxyA,
        incomparableDeckProxyB)
        && !ShadowRoutePruningPolicy.HeuristicQualityDominates(
            incomparableDeckProxyB,
            incomparableDeckProxyA),
    "Conflicting summary advantages remain incomparable instead of being mislabeled exact dominance.");
