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

bool replayCandidateRetentionPoliciesAreCorrect =
    MultiplayerLocalCrossTurnContracts.CanUseFullSearchHeuristics(
        SearchRoutePolicy.MultiplayerSinglePlayerCore)
    && MultiplayerLocalCrossTurnContracts.HasLocalCrossTurnProjection(
        SearchRoutePolicy.MultiplayerSinglePlayerCore)
    && MultiplayerLocalCrossTurnContracts.HasLocalCrossTurnProjection(
        SearchRoutePolicy.MultiplayerLocalCrossTurn)
    && !MultiplayerLocalCrossTurnContracts.HasLocalCrossTurnProjection(
        SearchRoutePolicy.MultiplayerCurrentTurnOnly)
    && !MultiplayerLocalCrossTurnContracts.HasLocalCrossTurnProjection(
        SearchRoutePolicy.SinglePlayerFullRoute)
    && !MultiplayerLocalCrossTurnContracts.HasActiveMultiplayerRouteSemantics(
        SearchRoutePolicy.MultiplayerSinglePlayerCore,
        playerCount: 2);

if (args.Length == 1 && args[0] == "--replay-candidate-retention")
{
    Check(
        replayCandidateRetentionPoliciesAreCorrect,
        "Default multiplayer single-player core retains replay candidates without enabling multiplayer-only route semantics.");

    string p0RefreshBase =
        "combat_identity=seed=s;players=1,2;enemies=10:A;local_net_id=1;round=1;side=Player;phase=Play;turn=1;" +
        "hp=80;max_hp=80;block=0;energy=3;stars=0;gold=10;" +
        "E0=10/A/front/40/50/0/ATTACK;H=A+0;D=B+0;C=;X=;P=;R=1:2:3:4:5/1:2:3:4:5";
    string p0RefreshEnemyDamage = p0RefreshBase.Replace(
        "E0=10/A/front/40/50/0/ATTACK",
        "E0=10/A/front/31/50/0/ATTACK",
        StringComparison.Ordinal);
    string p0RefreshEnemyBlock = p0RefreshBase.Replace(
        "E0=10/A/front/40/50/0/ATTACK",
        "E0=10/A/front/40/50/7/ATTACK",
        StringComparison.Ordinal);
    string p0RefreshTargetDeath = p0RefreshBase.Replace(
        "E0=10/A/front/40/50/0/ATTACK",
        "E0=10/A/front/0/50/0/ATTACK",
        StringComparison.Ordinal);
    string p0RefreshEnergy = p0RefreshBase.Replace("energy=3", "energy=2", StringComparison.Ordinal);
    string p0RefreshRng = p0RefreshBase.Replace(
        "R=1:2:3:4:5/1:2:3:4:5",
        "R=2:2:3:4:5/1:2:3:4:5",
        StringComparison.Ordinal);

    Check(
        MultiplayerPlanRefreshContracts.IsReplayCompatible(
            p0RefreshBase,
            p0RefreshBase,
            out string p0ExactRefreshReason)
            && p0ExactRefreshReason == "exact_local_root"
            && MultiplayerPlanRefreshContracts.IsReplayCompatible(
                p0RefreshBase,
                p0RefreshEnemyDamage,
                out string p0DamageRefreshReason)
            && p0DamageRefreshReason == "living_enemy_hp_or_block_only"
            && MultiplayerPlanRefreshContracts.IsReplayCompatible(
                p0RefreshBase,
                p0RefreshEnemyBlock,
                out string p0BlockRefreshReason)
            && p0BlockRefreshReason == "living_enemy_hp_or_block_only",
        "P0 bounded refresh accepts exact roots plus nonlethal living-enemy HP/block drift.");

    Check(
        !MultiplayerPlanRefreshContracts.IsReplayCompatible(
            p0RefreshBase,
            p0RefreshTargetDeath,
            out string p0DeathRefreshReason)
            && p0DeathRefreshReason == "strong_field_change:E0"
            && !MultiplayerPlanRefreshContracts.IsReplayCompatible(
                p0RefreshBase,
                p0RefreshEnergy,
                out string p0EnergyRefreshReason)
            && p0EnergyRefreshReason == "strong_field_change:energy"
            && !MultiplayerPlanRefreshContracts.IsReplayCompatible(
                p0RefreshBase,
                p0RefreshRng,
                out string p0RngRefreshReason)
            && p0RngRefreshReason == "strong_field_change:R",
        "P0 bounded refresh rejects target death, local resource drift and RNG drift.");
    return;
}

bool continuationSeedActionContractsAreCorrect =
    MultiplayerLocalCrossTurnContracts.CanReplayContinuationSeedAction(
        actionTurn: 3,
        currentTurn: 3,
        isPlayCard: true,
        endsPlayerTurn: false,
        hasChoice: false,
        hasNestedChoices: false,
        hasTurnStartChoices: false,
        hasShadowForecast: false,
        hasCardStateKey: true)
    && !MultiplayerLocalCrossTurnContracts.CanReplayContinuationSeedAction(
        actionTurn: 2,
        currentTurn: 3,
        isPlayCard: true,
        endsPlayerTurn: false,
        hasChoice: false,
        hasNestedChoices: false,
        hasTurnStartChoices: false,
        hasShadowForecast: false,
        hasCardStateKey: true)
    && !MultiplayerLocalCrossTurnContracts.CanReplayContinuationSeedAction(
        actionTurn: 3,
        currentTurn: 3,
        isPlayCard: false,
        endsPlayerTurn: false,
        hasChoice: false,
        hasNestedChoices: false,
        hasTurnStartChoices: false,
        hasShadowForecast: false,
        hasCardStateKey: true)
    && !MultiplayerLocalCrossTurnContracts.CanReplayContinuationSeedAction(
        actionTurn: 3,
        currentTurn: 3,
        isPlayCard: true,
        endsPlayerTurn: false,
        hasChoice: true,
        hasNestedChoices: false,
        hasTurnStartChoices: false,
        hasShadowForecast: false,
        hasCardStateKey: true)
    && !MultiplayerLocalCrossTurnContracts.CanReplayContinuationSeedAction(
        actionTurn: 3,
        currentTurn: 3,
        isPlayCard: true,
        endsPlayerTurn: false,
        hasChoice: false,
        hasNestedChoices: false,
        hasTurnStartChoices: false,
        hasShadowForecast: false,
        hasCardStateKey: false);

if (args.Length == 1 && args[0] == "--continuation-seed-contracts")
{
    Check(
        continuationSeedActionContractsAreCorrect,
        "P1 continuation seeds accept only same-turn ordinary PlayCard actions with exact card-state identity.");
    return;
}

Check(
    continuationSeedActionContractsAreCorrect,
    "P1 continuation-seed action boundary rejects cross-turn, non-card, choice and ambiguous-card suggestions.");

string continuationExact =
    "combat_identity=seed=s;players=1,2;enemies=4:A;local_net_id=2;turn=3;hp=77;H=A;D=B;C=;X=;" +
    "R=224:shuffle/0:cardgen/4:potion/2:select/0:energy/5:targets/0:orbs/10:ai/8:niche";
string continuationShuffleDrift = continuationExact.Replace(
    "R=224:shuffle/",
    "R=233:shuffle/",
    StringComparison.Ordinal);
string continuationCardGenerationDrift = continuationExact.Replace(
    "/0:cardgen/",
    "/1:cardgen/",
    StringComparison.Ordinal);
string continuationHandDrift = continuationExact.Replace(
    ";H=A;",
    ";H=C;",
    StringComparison.Ordinal);

Check(
    MultiplayerLocalCrossTurnContracts.IsLocalCoreContinuationStateCompatible(
        continuationExact,
        continuationExact,
        out bool exactShuffleDrift)
    && !exactShuffleDrift
    && MultiplayerLocalCrossTurnContracts.IsLocalCoreContinuationStateCompatible(
        continuationExact,
        continuationShuffleDrift,
        out bool acceptedShuffleDrift)
    && acceptedShuffleDrift
    && !MultiplayerLocalCrossTurnContracts.IsLocalCoreContinuationStateCompatible(
        continuationExact,
        continuationCardGenerationDrift,
        out _)
    && !MultiplayerLocalCrossTurnContracts.IsLocalCoreContinuationStateCompatible(
        continuationExact,
        continuationHandDrift,
        out _),
    "Local-core continuation ignores only shared Shuffle RNG drift; local piles and every other RNG stream remain exact.");

Check(
    !MultiplayerLocalCrossTurnContracts.LocalCoreSearchAcceleratorsEnabled
    && !MultiplayerLocalCrossTurnContracts.ShouldUseP3CrossFamilyScheduling(
        SearchRoutePolicy.MultiplayerSinglePlayerCore, false, true, false, false)
    && !MultiplayerLocalCrossTurnContracts.ShouldUseP3CrossFamilyScheduling(
        SearchRoutePolicy.SinglePlayerFullRoute, false, true, false, false)
    && !MultiplayerLocalCrossTurnContracts.ShouldUseP3CrossFamilyScheduling(
        SearchRoutePolicy.MultiplayerLocalCrossTurn, false, true, false, false),
    "Default multiplayer local-core disables P1/P2/P3 search accelerators so finite-budget exploration keeps single-player ordering.");

Check(
    MultiplayerLocalCrossTurnContracts.ShouldUseLocalCoreDeathHorizonFallback(
        SearchRoutePolicy.MultiplayerSinglePlayerCore,
        playerCount: 2,
        onlyDeathRoutesFound: true,
        hasSurvivingCurrentTurnCandidate: true)
    && !MultiplayerLocalCrossTurnContracts.ShouldUseLocalCoreDeathHorizonFallback(
        SearchRoutePolicy.MultiplayerSinglePlayerCore,
        playerCount: 1,
        onlyDeathRoutesFound: true,
        hasSurvivingCurrentTurnCandidate: true)
    && !MultiplayerLocalCrossTurnContracts.ShouldUseLocalCoreDeathHorizonFallback(
        SearchRoutePolicy.MultiplayerSinglePlayerCore,
        playerCount: 2,
        onlyDeathRoutesFound: false,
        hasSurvivingCurrentTurnCandidate: true)
    && !MultiplayerLocalCrossTurnContracts.ShouldUseLocalCoreDeathHorizonFallback(
        SearchRoutePolicy.MultiplayerSinglePlayerCore,
        playerCount: 2,
        onlyDeathRoutesFound: true,
        hasSurvivingCurrentTurnCandidate: false)
    && !MultiplayerLocalCrossTurnContracts.ShouldUseLocalCoreDeathHorizonFallback(
        SearchRoutePolicy.SinglePlayerFullRoute,
        playerCount: 2,
        onlyDeathRoutesFound: true,
        hasSurvivingCurrentTurnCandidate: true),
    "Local-core multiplayer falls back to the best surviving current-turn boundary only when every full local-only projection dies.");

SolverSearchProfile p2BudgetProfile = SolverSearchProfile.Default with
{
    MaxExpandedNodes = 20_000,
    SoftTimeBudgetMilliseconds = 20_000,
};
SolverSearchProfile p2Probe = ContinuationSeedIncumbentBudget.Probe(p2BudgetProfile)
    ?? throw new InvalidOperationException("P2 seed incumbent budget was unexpectedly unavailable.");
Check(
    p2Probe.MaxExpandedNodes == 1_000
    && p2Probe.SoftTimeBudgetMilliseconds == 1_000,
    "P2 continuation-seed incumbent is capped at five percent of node/time allowance.");
SolverSearchProfile p2Remaining = ContinuationSeedIncumbentBudget.Remaining(
        p2BudgetProfile,
        elapsedMilliseconds: 400,
        expandedNodes: 600)
    ?? throw new InvalidOperationException("P2 measured-work remainder unexpectedly exhausted.");
Check(
    p2Remaining.MaxExpandedNodes == 19_400
    && p2Remaining.SoftTimeBudgetMilliseconds == 19_600,
    "P2 returns unused seed allowance and deducts only measured work from the ordinary search.");


Check(
    ActionSearchOrderingPolicy.VerifyForTesting(),
    "E5 action enumeration prioritizes estimated lethal, urgent defense, strategic value-per-resource, then preserves stable original order for exact ties.");
Check(
    ActionSearchOrderingPolicy.VerifyContinuationSeedPriorityForTesting(),
    "P2 continuation enumeration hint can move the exact next seed action ahead of ordinary action-order heuristics without changing the candidate set.");

MultiplayerContinuationMatchInput Match(
    string combatIdentity = "combat-a",
    long actualWorldVersion = 12,
    string actualConstraint = "Shared")
    => new(
        ExpectedCombatIdentity: "combat-a",
        ActualCombatIdentity: combatIdentity,
        ExpectedLocalNetId: "local-1",
        ActualLocalNetId: "local-1",
        ExpectedMultiplayerScalingHooks: true,
        ActualMultiplayerScalingHooks: true,
        ExpectedCardMultiplayerConstraint: "Shared",
        ActualCardMultiplayerConstraint: actualConstraint,
        ExpectedSourceWorldVersion: 10,
        MinimumWorldVersion: 10,
        ActualWorldVersion: actualWorldVersion);

Check(
    !MultiplayerLocalCrossTurnContracts.IsCurrentTurnOnly(SearchRoutePolicy.SinglePlayerFullRoute)
        && MultiplayerLocalCrossTurnContracts.IsCurrentTurnOnly(SearchRoutePolicy.MultiplayerCurrentTurnOnly)
        && !MultiplayerLocalCrossTurnContracts.IsCurrentTurnOnly(SearchRoutePolicy.MultiplayerLocalCrossTurn)
        && MultiplayerLocalCrossTurnContracts.CanUseFullSearchHeuristics(SearchRoutePolicy.SinglePlayerFullRoute)
        && !MultiplayerLocalCrossTurnContracts.CanUseFullSearchHeuristics(SearchRoutePolicy.MultiplayerCurrentTurnOnly)
        && replayCandidateRetentionPoliciesAreCorrect
        && MultiplayerLocalCrossTurnContracts.CanUseFullSearchHeuristics(SearchRoutePolicy.MultiplayerLocalCrossTurn)
        && !MultiplayerLocalCrossTurnContracts.HasActiveMultiplayerRouteSemantics(
            SearchRoutePolicy.MultiplayerLocalCrossTurn,
            playerCount: 1)
        && MultiplayerLocalCrossTurnContracts.HasActiveMultiplayerRouteSemantics(
            SearchRoutePolicy.MultiplayerLocalCrossTurn,
            playerCount: 2)
        && !MultiplayerLocalCrossTurnContracts.HasActiveMultiplayerRouteSemantics(
            SearchRoutePolicy.SinglePlayerFullRoute,
            playerCount: 2)
        && MultiplayerLocalCrossTurnContracts.CanUsePersistentRouteCache(SearchRoutePolicy.SinglePlayerFullRoute)
        && !MultiplayerLocalCrossTurnContracts.CanUsePersistentRouteCache(SearchRoutePolicy.MultiplayerLocalCrossTurn),
    "Local cross-turn projection includes the default single-player core without activating multiplayer-only ranking/RNG semantics.");

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
        multiplayerRouteSemanticsActive: false,
        completeVictory: false)
        && !MultiplayerLocalCrossTurnContracts.DelayAngerCopyPreferenceUntilAfterEnemyHp(
            multiplayerRouteSemanticsActive: true,
            completeVictory: true)
        && MultiplayerLocalCrossTurnContracts.DelayAngerCopyPreferenceUntilAfterEnemyHp(
            multiplayerRouteSemanticsActive: true,
            completeVictory: false),
    "Quality bad-route 25b905c1322b41e6b9a8e10baeae5606 keeps deterministic enemy-HP progress ahead of Anger copy cost only for incomplete real multiplayer routes.");

Check(
    MultiplayerLocalCrossTurnContracts.ShouldStopBeforeSharedRngShuffle(
        multiplayerRouteSemanticsActive: true,
        rootSetup: false,
        sharedShuffleForecastTrusted: false,
        willShuffle: true)
        && !MultiplayerLocalCrossTurnContracts.ShouldStopBeforeSharedRngShuffle(
            multiplayerRouteSemanticsActive: true,
            rootSetup: false,
            sharedShuffleForecastTrusted: true,
            willShuffle: true)
        && !MultiplayerLocalCrossTurnContracts.ShouldStopBeforeSharedRngShuffle(
            multiplayerRouteSemanticsActive: true,
            rootSetup: true,
            sharedShuffleForecastTrusted: false,
            willShuffle: true)
        && !MultiplayerLocalCrossTurnContracts.ShouldStopBeforeSharedRngShuffle(
            multiplayerRouteSemanticsActive: true,
            rootSetup: false,
            sharedShuffleForecastTrusted: false,
            willShuffle: false)
        && !MultiplayerLocalCrossTurnContracts.ShouldStopBeforeSharedRngShuffle(
            multiplayerRouteSemanticsActive: false,
            rootSetup: false,
            sharedShuffleForecastTrusted: false,
            willShuffle: true)
        && !MultiplayerLocalCrossTurnContracts.ShouldStopBeforeSharedRngShuffle(
            multiplayerRouteSemanticsActive: false,
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
    !MultiplayerLocalCrossTurnContracts.IsExactContinuation(Match(actualWorldVersion: 10))
        && MultiplayerLocalCrossTurnContracts.DescribeContinuationMismatch(Match(actualWorldVersion: 10))
            == "world_version_not_advanced"
        && !MultiplayerLocalCrossTurnContracts.IsExactContinuation(Match(actualConstraint: "LocalOnly"))
        && MultiplayerLocalCrossTurnContracts.DescribeContinuationMismatch(Match(actualConstraint: "LocalOnly"))
            == "card_constraint_mismatch",
    "Continuation reuse keeps WorldVersion and shared-rule boundaries without a teammate-state fingerprint gate.");

string refreshBase =
    "combat_identity=seed=s;players=1,2;enemies=10:A;local_net_id=1;round=1;side=Player;phase=Play;turn=1;" +
    "hp=80;max_hp=80;block=0;energy=3;stars=0;gold=10;" +
    "E0=10/A/front/40/50/0/ATTACK;H=A+0;D=B+0;C=;X=;P=;R=1:2:3:4:5/1:2:3:4:5";
string refreshEnemyDamage = refreshBase.Replace(
    "E0=10/A/front/40/50/0/ATTACK",
    "E0=10/A/front/31/50/0/ATTACK",
    StringComparison.Ordinal);
string refreshEnemyBlock = refreshBase.Replace(
    "E0=10/A/front/40/50/0/ATTACK",
    "E0=10/A/front/40/50/7/ATTACK",
    StringComparison.Ordinal);
string refreshTargetDeath = refreshBase.Replace(
    "E0=10/A/front/40/50/0/ATTACK",
    "E0=10/A/front/0/50/0/ATTACK",
    StringComparison.Ordinal);
string refreshEnergy = refreshBase.Replace("energy=3", "energy=2", StringComparison.Ordinal);
string refreshRng = refreshBase.Replace(
    "R=1:2:3:4:5/1:2:3:4:5",
    "R=2:2:3:4:5/1:2:3:4:5",
    StringComparison.Ordinal);
Check(
    MultiplayerPlanRefreshContracts.IsReplayCompatible(
        refreshBase,
        refreshBase,
        out string exactRefreshReason)
        && exactRefreshReason == "exact_local_root"
        && MultiplayerPlanRefreshContracts.IsReplayCompatible(
            refreshBase,
            refreshEnemyDamage,
            out string damageRefreshReason)
        && damageRefreshReason == "living_enemy_hp_or_block_only"
        && MultiplayerPlanRefreshContracts.IsReplayCompatible(
            refreshBase,
            refreshEnemyBlock,
            out string blockRefreshReason)
        && blockRefreshReason == "living_enemy_hp_or_block_only",
    "Quality-first bounded refresh admits an exact root and nonlethal living-enemy HP/block drift.");

Check(
    !MultiplayerPlanRefreshContracts.IsReplayCompatible(
        refreshBase,
        refreshTargetDeath,
        out string deathRefreshReason)
        && deathRefreshReason == "strong_field_change:E0"
        && !MultiplayerPlanRefreshContracts.IsReplayCompatible(
            refreshBase,
            refreshEnergy,
            out string energyRefreshReason)
        && energyRefreshReason == "strong_field_change:energy"
        && !MultiplayerPlanRefreshContracts.IsReplayCompatible(
            refreshBase,
            refreshRng,
            out string rngRefreshReason)
        && rngRefreshReason == "strong_field_change:R",
    "Quality-first bounded refresh rejects target death, local resource drift and RNG drift as full-search signals.");

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
        multiplayerRouteSemanticsActive: true,
        candidateHasCurrentTurnCard: true,
        currentHasCurrentTurnCard: false)
        && !MultiplayerLocalCrossTurnContracts.PreferCurrentTurnPlayableRoute(
            multiplayerRouteSemanticsActive: true,
            candidateHasCurrentTurnCard: false,
            currentHasCurrentTurnCard: true)
        && !MultiplayerLocalCrossTurnContracts.PreferCurrentTurnPlayableRoute(
            multiplayerRouteSemanticsActive: false,
            candidateHasCurrentTurnCard: true,
            currentHasCurrentTurnCard: false),
    "Quality bad-route X1 T3 keeps a playable current-turn card over an equivalent EndTurn-only route; the tie-break stays inactive in the U2 single-player degenerate fixture.");


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


IReadOnlyList<MultiplayerScenarioSpec> u3ScenarioSpecs =
    MultiplayerScenarioReevaluationPolicy.ScenarioSpecs;
Check(
    u3ScenarioSpecs.Count
        == MultiplayerScenarioReevaluationPolicy.MaximumScenariosPerDecision
        && u3ScenarioSpecs.Select(spec => spec.Id).SequenceEqual(
        [
            "aggressive",
            "defensive",
            "conserve",
            "no_action",
        ])
        && u3ScenarioSpecs.Select(spec => spec.Kind).SequenceEqual(
        [
            ShadowTeammateScenarioKind.Aggressive,
            ShadowTeammateScenarioKind.Defensive,
            ShadowTeammateScenarioKind.Conserve,
            ShadowTeammateScenarioKind.NoAction,
        ]),
    "U3 uses one fixed ScenarioSpec portfolio for every compared current decision.");

ShadowTeammateScenarioKind[] u3CompleteCoverage =
[
    ShadowTeammateScenarioKind.NoAction,
    ShadowTeammateScenarioKind.Conserve,
    ShadowTeammateScenarioKind.Aggressive,
    ShadowTeammateScenarioKind.Defensive,
];
Check(
    MultiplayerScenarioReevaluationPolicy.HasCompleteCoverage(u3CompleteCoverage)
        && MultiplayerScenarioReevaluationPolicy.HasCompleteCoverage(
            u3CompleteCoverage.Reverse())
        && !MultiplayerScenarioReevaluationPolicy.HasCompleteCoverage(
            u3CompleteCoverage.Where(kind =>
                kind != ShadowTeammateScenarioKind.Defensive)),
    "U3 fair coverage is independent of candidate/scenario enumeration order and missing one required scenario remains incomplete.");

Check(
    MultiplayerScenarioReevaluationPolicy.CanRerank([true, true])
        && MultiplayerScenarioReevaluationPolicy.CanRerank([true, true, true, true])
        && !MultiplayerScenarioReevaluationPolicy.CanRerank([true])
        && !MultiplayerScenarioReevaluationPolicy.CanRerank([true, false])
        && !MultiplayerScenarioReevaluationPolicy.CanRerank([true, true, false]),
    "U3 reranking requires the same complete ScenarioSpec coverage for every compared decision; an Unknown/budget-interrupted decision forces the shared baseline fallback.");


int u3ReservedBudget =
    MultiplayerScenarioReevaluationPolicy.ReserveExpandedBranchBudget(
        totalExpandedNodeBudget: 5_000,
        enabled: true);
int u3PerDecisionBudget =
    MultiplayerScenarioReevaluationPolicy.ExpandedBranchBudgetPerDecision(
        u3ReservedBudget,
        comparedDecisionCount: 4);
Check(
    MultiplayerScenarioReevaluationPolicy.ReserveExpandedBranchBudget(
        totalExpandedNodeBudget: 5_000,
        enabled: false) == 0
        && MultiplayerScenarioReevaluationPolicy.ReserveExpandedBranchBudget(
            totalExpandedNodeBudget: 1_200,
            enabled: true) == 150
        && u3ReservedBudget
            == MultiplayerScenarioReevaluationPolicy.MaximumReservedExpandedBranches
        && u3PerDecisionBudget
            == MultiplayerScenarioReevaluationPolicy.MaximumExpandedBranchesPerDecision
        && u3PerDecisionBudget
            * MultiplayerScenarioReevaluationPolicy.MaximumCurrentDecisions
            <= u3ReservedBudget
        && MultiplayerScenarioReevaluationPolicy.MainSearchExpandedNodeBudget(
            totalExpandedNodeBudget: 5_000,
            enabled: true) + u3ReservedBudget == 5_000
        && MultiplayerScenarioReevaluationPolicy.MainSearchExpandedNodeBudget(
            totalExpandedNodeBudget: 5_000,
            enabled: false) == 5_000,
    "U3 reevaluation uses a deterministic bounded reserve carved from the existing work budget and splits it equally across current decisions.");

PlanAction u3AggressiveFuture = new(
    PlanActionKind.EndTurn,
    Turn: 7,
    ShadowForecast: new ShadowForecastPlan(
        [],
        ScenarioKind: ShadowTeammateScenarioKind.Aggressive));
PlanAction u3NoActionFuture = u3AggressiveFuture with
{
    TurnStartChoices = [],
    ShadowForecast = new ShadowForecastPlan(
        [],
        ScenarioKind: ShadowTeammateScenarioKind.NoAction),
};
string u3AggressiveDecisionKey =
    MultiplayerChanceDecisionIdentity.CurrentTurnDecisionKey(
        [u3AggressiveFuture],
        rootTurn: 7);
string u3NoActionDecisionKey =
    MultiplayerChanceDecisionIdentity.CurrentTurnDecisionKey(
        [u3NoActionFuture],
        rootTurn: 7);
Check(
    string.Equals(
        u3AggressiveDecisionKey,
        u3NoActionDecisionKey,
        StringComparison.Ordinal),
    "U3 current-decision identity excludes future teammate scenario and TurnStart observations, preventing clairvoyant current-action splitting.");


MultiplayerScenarioOutcome[] u3ClairvoyantTrap =
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
        ShadowTeammateScenarioKind.Defensive,
        CompleteVictory: false,
        AllPlayersAlive: true,
        LossEquivalent: 0.80d,
        WorstPlayerLossRatio: 0.70d,
        TeamLossRatio: 0.75d,
        EnemyDurabilityRatio: 0.90d),
    new(
        ShadowTeammateScenarioKind.Conserve,
        CompleteVictory: false,
        AllPlayersAlive: true,
        LossEquivalent: 0.70d,
        WorstPlayerLossRatio: 0.60d,
        TeamLossRatio: 0.65d,
        EnemyDurabilityRatio: 0.80d),
    new(
        ShadowTeammateScenarioKind.NoAction,
        CompleteVictory: false,
        AllPlayersAlive: true,
        LossEquivalent: 0.90d,
        WorstPlayerLossRatio: 0.80d,
        TeamLossRatio: 0.85d,
        EnemyDurabilityRatio: 0.95d),
];
MultiplayerScenarioOutcome[] u3StableDecision =
    MultiplayerScenarioReevaluationPolicy.ScenarioSpecs
        .Select((spec, index) => new MultiplayerScenarioOutcome(
            spec.Kind,
            CompleteVictory: false,
            AllPlayersAlive: true,
            LossEquivalent: 0.15d + index * 0.01d,
            WorstPlayerLossRatio: 0.12d + index * 0.01d,
            TeamLossRatio: 0.11d + index * 0.01d,
            EnemyDurabilityRatio: 0.30d + index * 0.02d))
        .ToArray();
MultiplayerScenarioDecisionRank u3TrapRank =
    MultiplayerScenarioReevaluationPolicy.Aggregate(u3ClairvoyantTrap);
MultiplayerScenarioDecisionRank u3TrapReversedRank =
    MultiplayerScenarioReevaluationPolicy.Aggregate(
        u3ClairvoyantTrap.Reverse().ToArray());
MultiplayerScenarioDecisionRank u3StableRank =
    MultiplayerScenarioReevaluationPolicy.Aggregate(u3StableDecision);
Check(
    MultiplayerScenarioReevaluationPolicy.Compare(
        u3TrapRank,
        u3TrapReversedRank) == 0,
    "U3 robust scenario rank is invariant to scenario enumeration order.");
Check(
    MultiplayerScenarioReevaluationPolicy.Compare(
        u3StableRank,
        u3TrapRank) < 0,
    "U3 rejects the clairvoyance trap: a decision that is excellent only if the future teammate lane is known cannot beat a decision with uniformly safer outcomes across the same ScenarioSpec set.");


MultiplayerScenarioEvaluation[] u3CompletedMatrix =
    MultiplayerScenarioReevaluationPolicy.ScenarioSpecs
        .Select((spec, index) => new MultiplayerScenarioEvaluation(
            spec,
            index == 0
                ? MultiplayerScenarioEvaluationStatus.Terminal
                : MultiplayerScenarioEvaluationStatus.Completed,
            new MultiplayerScenarioOutcome(
                spec.Kind,
                CompleteVictory: index == 0,
                AllPlayersAlive: true,
                LossEquivalent: 0.10d + index * 0.01d,
                WorstPlayerLossRatio: 0.05d,
                TeamLossRatio: 0.04d,
                EnemyDurabilityRatio: 0.20d),
            ExpandedBranches: index + 1))
        .ToArray();
MultiplayerScenarioDecisionEvaluation u3CompleteDecision =
    new("same-current-decision", u3CompletedMatrix, SharedExpandedBranches: 7);
MultiplayerScenarioEvaluation[] u3InterruptedMatrix =
    u3CompletedMatrix
        .Select((evaluation, index) => index == 2
            ? evaluation with
            {
                Status = MultiplayerScenarioEvaluationStatus.Unknown,
                Outcome = null,
            }
            : evaluation)
        .ToArray();
MultiplayerScenarioDecisionEvaluation u3InterruptedDecision =
    new("same-current-decision", u3InterruptedMatrix, SharedExpandedBranches: 7);
Check(
    u3CompleteDecision.CompleteCoverage
        && u3CompleteDecision.ExpandedBranches == 17
        && !u3InterruptedDecision.CompleteCoverage,
    "U3 matrix records Completed/Terminal cells as complete, counts shared planner work once plus scenario-specific replay work, and an Unknown budget-interrupted cell cannot masquerade as full scenario coverage.");

MultiplayerScenarioOutcome[] e4IncumbentOutcomes =
[
    new(
        ShadowTeammateScenarioKind.Aggressive,
        CompleteVictory: false,
        AllPlayersAlive: true,
        LossEquivalent: 0.14d,
        WorstPlayerLossRatio: 0.10d,
        TeamLossRatio: 0.09d,
        EnemyDurabilityRatio: 0.28d),
    new(
        ShadowTeammateScenarioKind.Defensive,
        CompleteVictory: false,
        AllPlayersAlive: true,
        LossEquivalent: 0.16d,
        WorstPlayerLossRatio: 0.11d,
        TeamLossRatio: 0.10d,
        EnemyDurabilityRatio: 0.32d),
    new(
        ShadowTeammateScenarioKind.Conserve,
        CompleteVictory: false,
        AllPlayersAlive: true,
        LossEquivalent: 0.18d,
        WorstPlayerLossRatio: 0.12d,
        TeamLossRatio: 0.11d,
        EnemyDurabilityRatio: 0.36d),
    new(
        ShadowTeammateScenarioKind.NoAction,
        CompleteVictory: false,
        AllPlayersAlive: true,
        LossEquivalent: 0.20d,
        WorstPlayerLossRatio: 0.13d,
        TeamLossRatio: 0.12d,
        EnemyDurabilityRatio: 0.40d),
];
MultiplayerScenarioDecisionRank e4IncumbentRank =
    MultiplayerScenarioReevaluationPolicy.Aggregate(e4IncumbentOutcomes);
MultiplayerScenarioDecisionEvaluation e4IncumbentDecision = new(
    "e4-incumbent",
    MultiplayerScenarioReevaluationPolicy.ScenarioSpecs
        .Select(spec => new MultiplayerScenarioEvaluation(
            spec,
            MultiplayerScenarioEvaluationStatus.Completed,
            e4IncumbentOutcomes.First(outcome => outcome.Kind == spec.Kind),
            ExpandedBranches: 1))
        .ToArray());

IReadOnlyList<MultiplayerScenarioSpec> e4PressureOrder =
    MultiplayerScenarioReevaluationPolicy.StrictEvaluationOrder(
        e4IncumbentDecision);
Check(
    e4PressureOrder[0].Kind == ShadowTeammateScenarioKind.NoAction
        && e4PressureOrder[^1].Kind == ShadowTeammateScenarioKind.Aggressive,
    "E4 strict reevaluation visits the incumbent's most stressful fixed lane first without changing the ScenarioSpec set.");

MultiplayerScenarioOutcome[] e4LateFlipOutcomes =
[
    new(
        ShadowTeammateScenarioKind.Aggressive,
        CompleteVictory: false,
        AllPlayersAlive: true,
        LossEquivalent: 0.30d,
        WorstPlayerLossRatio: 0.15d,
        TeamLossRatio: 0.14d,
        EnemyDurabilityRatio: 0.42d),
    new(
        ShadowTeammateScenarioKind.Defensive,
        CompleteVictory: false,
        AllPlayersAlive: true,
        LossEquivalent: 0.12d,
        WorstPlayerLossRatio: 0.08d,
        TeamLossRatio: 0.07d,
        EnemyDurabilityRatio: 0.24d),
    new(
        ShadowTeammateScenarioKind.Conserve,
        CompleteVictory: false,
        AllPlayersAlive: true,
        LossEquivalent: 0.11d,
        WorstPlayerLossRatio: 0.08d,
        TeamLossRatio: 0.07d,
        EnemyDurabilityRatio: 0.22d),
    new(
        ShadowTeammateScenarioKind.NoAction,
        CompleteVictory: false,
        AllPlayersAlive: true,
        LossEquivalent: 0.10d,
        WorstPlayerLossRatio: 0.07d,
        TeamLossRatio: 0.06d,
        EnemyDurabilityRatio: 0.20d),
];
List<MultiplayerScenarioOutcome> e4LateFlipPartial = [];
foreach (MultiplayerScenarioSpec spec in e4PressureOrder.Take(3))
{
    e4LateFlipPartial.Add(
        e4LateFlipOutcomes.First(outcome => outcome.Kind == spec.Kind));
}
MultiplayerScenarioDecisionRank e4OptimisticBeforeLateFlip =
    MultiplayerScenarioReevaluationPolicy.StrictOptimisticLowerBound(
        e4LateFlipPartial);
Check(
    double.IsNegativeInfinity(e4OptimisticBeforeLateFlip.MeanLossEquivalent)
        && !MultiplayerScenarioReevaluationPolicy.CanStrictlyEliminate(
            e4LateFlipPartial,
            e4IncumbentRank,
            out _),
    "E4 keeps unknown mean cost at negative infinity and cannot prune a candidate whose unseen final lane could still preserve or improve the Robust result.");

e4LateFlipPartial.Add(
    e4LateFlipOutcomes.First(outcome =>
        outcome.Kind == e4PressureOrder[^1].Kind));
Check(
    MultiplayerScenarioReevaluationPolicy.CanStrictlyEliminate(
        e4LateFlipPartial,
        e4IncumbentRank,
        out string e4LateFlipReason)
        && e4LateFlipReason == "worst_loss_bound"
        && MultiplayerScenarioReevaluationPolicy.Compare(
            e4IncumbentRank,
            MultiplayerScenarioReevaluationPolicy.Aggregate(
                e4LateFlipOutcomes)) < 0,
    "E4 late-flip counterexample evaluates the final unseen lane before rejecting the candidate; early agreement alone never triggers strict stopping.");

MultiplayerScenarioOutcome[] e4ImmediateLoserOutcomes =
    e4IncumbentOutcomes
        .Select(outcome => outcome.Kind == ShadowTeammateScenarioKind.NoAction
            ? outcome with
            {
                LossEquivalent = 0.50d,
                WorstPlayerLossRatio = 0.40d,
                TeamLossRatio = 0.35d,
                EnemyDurabilityRatio = 0.70d,
            }
            : outcome with
            {
                LossEquivalent = Math.Max(0d, outcome.LossEquivalent - 0.02d),
            })
        .ToArray();
MultiplayerScenarioOutcome[][] e4EnumerableMatrix =
[
    e4IncumbentOutcomes,
    e4LateFlipOutcomes,
    e4IncumbentOutcomes.ToArray(),
    e4ImmediateLoserOutcomes,
];
MultiplayerScenarioDecisionRank[] e4FullRanks =
    e4EnumerableMatrix
        .Select(MultiplayerScenarioReevaluationPolicy.Aggregate)
        .ToArray();
int e4FullWinner =
    MultiplayerScenarioReevaluationPolicy.SelectPreferredIndex(
        MultiplayerScenarioRiskStrategy.Robust,
        e4FullRanks);
int e4StrictWinner = 0;
int e4StrictSkippedReplays = 0;
int[] e4StrictEvaluatedLanes = new int[e4EnumerableMatrix.Length];
for (int candidateIndex = 1;
     candidateIndex < e4EnumerableMatrix.Length;
     candidateIndex++)
{
    MultiplayerScenarioDecisionRank currentIncumbent =
        e4FullRanks[e4StrictWinner];
    MultiplayerScenarioDecisionEvaluation currentIncumbentEvaluation = new(
        $"e4-incumbent-{e4StrictWinner}",
        MultiplayerScenarioReevaluationPolicy.ScenarioSpecs
            .Select(spec => new MultiplayerScenarioEvaluation(
                spec,
                MultiplayerScenarioEvaluationStatus.Completed,
                e4EnumerableMatrix[e4StrictWinner]
                    .First(outcome => outcome.Kind == spec.Kind),
                ExpandedBranches: 1))
            .ToArray());
    List<MultiplayerScenarioOutcome> partial = [];
    bool eliminated = false;
    IReadOnlyList<MultiplayerScenarioSpec> order =
        MultiplayerScenarioReevaluationPolicy.StrictEvaluationOrder(
            currentIncumbentEvaluation);
    for (int scenarioIndex = 0; scenarioIndex < order.Count; scenarioIndex++)
    {
        partial.Add(
            e4EnumerableMatrix[candidateIndex]
                .First(outcome => outcome.Kind == order[scenarioIndex].Kind));
        e4StrictEvaluatedLanes[candidateIndex]++;
        if (MultiplayerScenarioReevaluationPolicy.CanStrictlyEliminate(
                partial,
                currentIncumbent,
                out _))
        {
            e4StrictSkippedReplays += order.Count - scenarioIndex - 1;
            eliminated = true;
            break;
        }
    }

    if (!eliminated
        && MultiplayerScenarioReevaluationPolicy.Compare(
            e4FullRanks[candidateIndex],
            currentIncumbent) < 0)
    {
        e4StrictWinner = candidateIndex;
    }
}
Check(
    e4StrictWinner == e4FullWinner
        && e4StrictWinner == 0
        && e4StrictEvaluatedLanes[1]
            == MultiplayerScenarioReevaluationPolicy.MaximumScenariosPerDecision
        && e4StrictEvaluatedLanes[2]
            == MultiplayerScenarioReevaluationPolicy.MaximumScenariosPerDecision
        && e4StrictEvaluatedLanes[3] == 1
        && e4StrictSkippedReplays == 3,
    "E4 strict progressive reevaluation chooses the same Robust winner and baseline tie-break as full enumeration while skipping only lanes whose best possible completion is already dominated.");

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

MultiplayerScenarioDecisionRank u4RobustFavorite = new(
    ScenarioCount: 4,
    AllScenariosAlive: true,
    GuaranteedVictory: false,
    WorstLossEquivalent: 0.09d,
    MeanLossEquivalent: 0.09d,
    WorstPlayerLossRatio: 0.10d,
    WorstTeamLossRatio: 0.09d,
    WorstEnemyDurabilityRatio: 0.40d);
MultiplayerScenarioDecisionRank u4NominalFavorite = new(
    ScenarioCount: 4,
    AllScenariosAlive: true,
    GuaranteedVictory: false,
    WorstLossEquivalent: 0.16d,
    MeanLossEquivalent: 0.03d,
    WorstPlayerLossRatio: 0.10d,
    WorstTeamLossRatio: 0.09d,
    WorstEnemyDurabilityRatio: 0.40d);
MultiplayerScenarioDecisionRank u4BoundedFavorite = new(
    ScenarioCount: 4,
    AllScenariosAlive: true,
    GuaranteedVictory: false,
    WorstLossEquivalent: 0.11d,
    MeanLossEquivalent: 0.05d,
    WorstPlayerLossRatio: 0.10d,
    WorstTeamLossRatio: 0.09d,
    WorstEnemyDurabilityRatio: 0.40d);
Check(
    MultiplayerScenarioReevaluationPolicy.CompareByRiskStrategy(
        MultiplayerScenarioRiskStrategy.Robust,
        u4RobustFavorite,
        u4BoundedFavorite) < 0
        && MultiplayerScenarioReevaluationPolicy.CompareByRiskStrategy(
            MultiplayerScenarioRiskStrategy.NominalReference,
            u4NominalFavorite,
            u4BoundedFavorite) < 0
        && MultiplayerScenarioReevaluationPolicy.CompareByRiskStrategy(
            MultiplayerScenarioRiskStrategy.BoundedRisk,
            u4BoundedFavorite,
            u4RobustFavorite) < 0
        && MultiplayerScenarioReevaluationPolicy.CompareByRiskStrategy(
            MultiplayerScenarioRiskStrategy.BoundedRisk,
            u4BoundedFavorite,
            u4NominalFavorite) < 0,
    "U4 experiment keeps Robust, equal-lane nominal reference, and BoundedRisk as distinct orderings without changing the production comparator.");

Check(
    Math.Abs(
        MultiplayerScenarioReevaluationPolicy.BoundedRiskLossEquivalent(
            u4BoundedFavorite) - 0.08d) < 1e-12d,
    "U4 BoundedRisk prices half of the equal-lane mean-to-worst loss gap.");

MultiplayerScenarioDecisionRank[] u4RiskAbRanks =
[
    u4RobustFavorite,
    u4NominalFavorite,
    u4BoundedFavorite,
];
Check(
    MultiplayerScenarioReevaluationPolicy.SelectPreferredIndex(
        MultiplayerScenarioRiskStrategy.Robust,
        u4RiskAbRanks) == 0
        && MultiplayerScenarioReevaluationPolicy.SelectPreferredIndex(
            MultiplayerScenarioRiskStrategy.NominalReference,
            u4RiskAbRanks) == 1
        && MultiplayerScenarioReevaluationPolicy.SelectPreferredIndex(
            MultiplayerScenarioRiskStrategy.BoundedRisk,
            u4RiskAbRanks) == 2,
    "U4 same-matrix selector can report distinct Robust, nominal-reference, and BoundedRisk winners without another search.");

MultiplayerScenarioDecisionRank[] qualityScenarioOverrideRanks =
[
    u4NominalFavorite,
    u4RobustFavorite,
    u4BoundedFavorite,
];
MultiplayerScenarioStrategySelection qualityStrategySelection =
    MultiplayerScenarioReevaluationPolicy.CompareStrategies(
        qualityScenarioOverrideRanks,
        baselineIndex: 0);
Check(
    qualityStrategySelection.BaselineIndex == 0
        && qualityStrategySelection.RobustIndex == 1
        && qualityStrategySelection.NominalReferenceIndex == 0
        && qualityStrategySelection.BoundedRiskIndex == 2
        && qualityStrategySelection.RobustOverridesBaseline
        && !qualityStrategySelection.RobustAgreesWithNominal
        && !qualityStrategySelection.RobustAgreesWithBoundedRisk
        && qualityStrategySelection.HasDisputedRobustOverride,
    "Quality-first attribution distinguishes a disputed Robust scenario override from the shared-core baseline without changing any risk weight or search budget.");

MultiplayerQualityLayerAttribution qualityBaselineLayer =
    MultiplayerScenarioReevaluationPolicy.AttributeQualityLayer(1, 1, 1);
MultiplayerQualityLayerAttribution qualityScenarioLayer =
    MultiplayerScenarioReevaluationPolicy.AttributeQualityLayer(1, 3, 3);
MultiplayerQualityLayerAttribution qualityChanceLayer =
    MultiplayerScenarioReevaluationPolicy.AttributeQualityLayer(1, 3, 2);
Check(
    qualityBaselineLayer.OverrideLayer == "baseline"
        && qualityBaselineLayer.FinalSelectedBaselineRank == 1
        && qualityScenarioLayer.OverrideLayer == "scenario_robust"
        && qualityScenarioLayer.ScenarioSelectedBaselineRank == 3
        && qualityChanceLayer.OverrideLayer == "shadow_chance"
        && qualityChanceLayer.FinalSelectedBaselineRank == 2,
    "Quality-first final-layer attribution remains available when strict E4 pruning prevents a complete U4 strategy matrix, and it distinguishes later Shadow chance reranking from Robust reranking.");

Check(
    MultiplayerScenarioReevaluationPolicy.SelectNominalToleranceExperimentIndex(
        u4RiskAbRanks,
        nominalLossTolerance: 0d) == 1
        && MultiplayerScenarioReevaluationPolicy.SelectNominalToleranceExperimentIndex(
            u4RiskAbRanks,
            nominalLossTolerance: 0.02d) == 2,
    "U4 nominal-loss tolerance remains an independent experiment: zero tolerance keeps the nominal winner while a caller-supplied tolerance can admit a safer worst-case route.");

MultiplayerScenarioOutcome[] u4MeasuredOutcomes =
[
    new(
        ShadowTeammateScenarioKind.Aggressive,
        CompleteVictory: false,
        AllPlayersAlive: true,
        LossEquivalent: 0.04d,
        WorstPlayerLossRatio: 0.10d,
        TeamLossRatio: 0.05d,
        EnemyDurabilityRatio: 0.20d,
        TeamRemainingHpRatio: 0.82d,
        WorstPlayerRemainingHpRatio: 0.70d),
    new(
        ShadowTeammateScenarioKind.Defensive,
        CompleteVictory: false,
        AllPlayersAlive: true,
        LossEquivalent: 0.05d,
        WorstPlayerLossRatio: 0.08d,
        TeamLossRatio: 0.04d,
        EnemyDurabilityRatio: 0.30d,
        TeamRemainingHpRatio: 0.86d,
        WorstPlayerRemainingHpRatio: 0.74d),
    new(
        ShadowTeammateScenarioKind.Conserve,
        CompleteVictory: false,
        AllPlayersAlive: true,
        LossEquivalent: 0.06d,
        WorstPlayerLossRatio: 0.12d,
        TeamLossRatio: 0.06d,
        EnemyDurabilityRatio: 0.35d,
        TeamRemainingHpRatio: 0.80d,
        WorstPlayerRemainingHpRatio: 0.68d),
    new(
        ShadowTeammateScenarioKind.NoAction,
        CompleteVictory: false,
        AllPlayersAlive: true,
        LossEquivalent: 0.10d,
        WorstPlayerLossRatio: 0.20d,
        TeamLossRatio: 0.12d,
        EnemyDurabilityRatio: 0.55d,
        TeamRemainingHpRatio: 0.65d,
        WorstPlayerRemainingHpRatio: 0.50d),
];
MultiplayerScenarioRiskMetrics u4MeasuredRisk =
    MultiplayerScenarioReevaluationPolicy.MeasureRisk(u4MeasuredOutcomes);
Check(
    Math.Abs(u4MeasuredRisk.MeanTeamRemainingHpRatio - 0.7825d) < 1e-12d
        && Math.Abs(u4MeasuredRisk.WorstTeamRemainingHpRatio - 0.65d) < 1e-12d
        && Math.Abs(u4MeasuredRisk.WorstPlayerRemainingHpRatio - 0.50d) < 1e-12d
        && u4MeasuredRisk.CooperationTeamLossBenefit > 0d
        && u4MeasuredRisk.CooperationProgressBenefit > 0d,
    "U4 reports final team HP, worst-player final HP, cooperation loss benefit, and cooperation progress benefit from the same fixed scenario matrix.");

Check(
    MultiplayerScenarioReevaluationPolicy.NoActionScopeDiagnosticValue
        == "current_joint_forecast_only",
    "U4 NoAction is explicitly scoped to the current Joint forecast window and cannot be interpreted as the teammate doing nothing for the rest of combat.");


Check(
    ShadowTeammateScenarioPolicy.DefaultScenarioCount
        == MultiplayerScenarioReevaluationPolicy.MaximumScenariosPerDecision,
    "P3 shared production scenario count exactly fits the four stress-scenario lanes; ShadowTeammatePlanner consumes the same constant.");

Check(
    teammateScenarioChoices
        .Where(choice => choice.Kind != ShadowTeammateScenarioKind.NoAction)
        .Select(choice => teammateScenarioObservations[choice.Index].ActionOrderKey)
        .Where(key => !string.IsNullOrEmpty(key))
        .Distinct(StringComparer.Ordinal)
        .Count() >= 3,
    "P3 default four-scenario portfolio retains distinct modeled action orders inside the production beam, not only in a wider test-only beam.");

ShadowTeammateScenarioObservation[] sharedBestOrderObservations =
[
    new(2, false, true, 10, 80, 0.90d, 3, 2, -0.1d, "A:Vulnerable>B:Attack"),
    new(2, false, true, 12, 75, 0.85d, 2, 1, -0.2d, "B:Attack>A:Vulnerable"),
    new(1, false, true, 30, 70, 0.80d, 4, 2, -0.3d, "A:Power"),
    new(0, false, true, 40, 60, 0.70d, 5, 3, -0.4d, ""),
];
IReadOnlyList<ShadowTeammateScenarioChoice> sharedBestOrderChoices =
    ShadowTeammateScenarioPolicy.SelectProtected(
        sharedBestOrderObservations,
        limit: 4);
Check(
    sharedBestOrderChoices.Count == 4
        && sharedBestOrderChoices
            .Where(choice => choice.Kind != ShadowTeammateScenarioKind.NoAction)
            .Select(choice => sharedBestOrderObservations[choice.Index].ActionOrderKey)
            .Distinct(StringComparer.Ordinal)
            .Count() == 3,
    "P3 reuses the four production slots to preserve distinct key action orders across stress lanes whenever such order variants exist.");

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

double interimHealthyDistribution =
    MultiplayerCombatObjectiveMath.InterimLossEquivalent(
        teamLossRatio: 0.040d,
        worstPlayerLossRatio: 0.05d,
        enemyDurabilityRatio: 0.20d);
double interimFragileDistribution =
    MultiplayerCombatObjectiveMath.InterimLossEquivalent(
        teamLossRatio: 0.040d,
        worstPlayerLossRatio: 0.80d,
        enemyDurabilityRatio: 0.20d);
MultiplayerCombatObjectiveRank interimHealthyRank =
    MultiplayerCombatObjectiveMath.BuildRank(
        MultiplayerCombatObjectiveStrategy.AdaptiveLethalTempo,
        completeVictory: false,
        allPlayersAlive: true,
        teamLossRatio: 0.040d,
        worstPlayerLossRatio: 0.05d,
        enemyDurabilityRatio: 0.20d,
        terminalTempoReferenceEnemyDurabilityRatio: 0.20d,
        combatEndedTurn: null,
        startTurnNumber: 1);
MultiplayerCombatObjectiveRank interimFragileRank =
    MultiplayerCombatObjectiveMath.BuildRank(
        MultiplayerCombatObjectiveStrategy.AdaptiveLethalTempo,
        completeVictory: false,
        allPlayersAlive: true,
        teamLossRatio: 0.040d,
        worstPlayerLossRatio: 0.80d,
        enemyDurabilityRatio: 0.20d,
        terminalTempoReferenceEnemyDurabilityRatio: 0.20d,
        combatEndedTurn: null,
        startTurnNumber: 1);
Check(
    Math.Abs(interimHealthyDistribution - interimFragileDistribution) < 1e-12d
        && MultiplayerCombatObjectiveMath.Compare(
            interimHealthyRank,
            interimFragileRank) < 0,
    "U4 interim progress credit is independent of fragility, so concentrating the same team loss on one player cannot become an implicit reward.");

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


PlanAction u5LocalBeforeForecast = new(
    PlanActionKind.PlayCard,
    Turn: 7,
    CardId: "LOCAL_A");
PlanAction u5ForecastBoundary = new(
    PlanActionKind.TeammateForecast,
    Turn: 7,
    CardId: "REMOTE_B",
    ShadowForecast: new ShadowForecastPlan([]));
PlanAction u5ContingentLocal = new(
    PlanActionKind.PlayCard,
    Turn: 7,
    CardId: "LOCAL_C");
string u5DecisionKeyWithForecast =
    MultiplayerChanceDecisionIdentity.CurrentTurnDecisionKey(
        [u5LocalBeforeForecast, u5ForecastBoundary, u5ContingentLocal],
        rootTurn: 7);
string u5DecisionKeyBeforeForecast =
    MultiplayerChanceDecisionIdentity.CurrentTurnDecisionKey(
        [u5LocalBeforeForecast],
        rootTurn: 7);
Check(
    string.Equals(
        u5DecisionKeyWithForecast,
        u5DecisionKeyBeforeForecast,
        StringComparison.Ordinal),
    "U5 forecast observation is not part of the deployable current decision and contingent local suffixes do not leak across the observation boundary.");

Check(
    !MultiplayerInterleaveOrderPolicy.AllowsProactiveWaitForTeammate
        && MultiplayerInterleaveOrderPolicy.MaximumForecastObservationsPerTurn == 1
        && MultiplayerInterleaveOrderPolicy.MaximumSingleObservationRoutes == 4
        && MultiplayerInterleaveOrderPolicy.IsForecastBoundaryReason(
            MultiplayerInterleaveOrderPolicy.ForecastBoundaryReason),
    "U5 scheduling is bounded to one forecast observation per turn and never invents an unbounded proactive teammate wait.");

Check(
    MultiplayerInterleaveOrderPolicy.CanCollapseOrder(
        MultiplayerInterleaveOrderRelation.ExactEquivalent)
        && !MultiplayerInterleaveOrderPolicy.CanCollapseOrder(
            MultiplayerInterleaveOrderRelation.OrderSensitive)
        && !MultiplayerInterleaveOrderPolicy.CanCollapseOrder(
            MultiplayerInterleaveOrderRelation.ReverseUnavailable),
    "U5 order collapse is allowed only after both A->B and B->A replay to the same conservative complete future-state identity.");

Check(
    MultiplayerInterleaveOrderPolicy.CanProbeReverseOrder(parentSimulatorAvailable: true)
        && !MultiplayerInterleaveOrderPolicy.CanProbeReverseOrder(parentSimulatorAvailable: false),
    "U5 reverse-order equivalence probing fails closed when the historical parent simulator has already been released.");
