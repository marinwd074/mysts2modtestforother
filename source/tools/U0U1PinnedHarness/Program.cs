using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using OfflineSearchHarness;
using U2DegenerateHarness;

namespace U0U1PinnedHarness;

internal static class Program
{
    private const int BeamWidth = 24;
    private const int MaxExpandedNodes = 2_000;
    private const int BudgetMilliseconds = 600_000;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static int Main(string[] args)
    {
        bool darkEmbracePactOnly = args.Length > 0 && args[0] == "dark-embrace-pact";
        string outputDirectory = ParseOutput(darkEmbracePactOnly ? args[1..] : args);
        Directory.CreateDirectory(outputDirectory);

        try
        {
            HarnessLog.Language = "eng";
            MainLoopContext loop = new();
            SynchronizationContext.SetSynchronizationContext(loop);

            GameBootstrap.ApplyGodotBypasses();
            GameBootstrap.SkipGodotNodeStaticConstructors();
            Console.WriteLine(GameBootstrap.InitializeStaticState());
            int patchCount = U2Runtime.Initialize(
                Path.Combine(outputDirectory, "logs"),
                BeamWidth,
                MaxExpandedNodes,
                BudgetMilliseconds);
            Console.WriteLine($"search_patches={patchCount}");
            ValidateDarkEmbracePredictionCoverage();
            ValidateViciousStrategicValue();
            ValidateRollingHorizonQualityContract();
            ValidateRouteInvalidationVersionContract();
            ValidateMultiplayerLethalReuseBoundary();
            ValidateLocalCoreShadowNormalizationContract();
            Require(
                R1EvaluationShadowCache.VerifyBeamRankReuseGateForTesting(),
                "R1 Beam-rank reuse requires first exact validation and disables reuse on mismatch.");
            Require(
                R1TransitionHydrationCache.VerifyExactReuseGateForTesting(),
                "R1 transition hydration requires first exact replay validation and rejects only the mismatching key.");
            Require(
                R1FrontierShadowCache.VerifyShadowGateForTesting(),
                "R1 frontier shadow distinguishes exact retained-frontier matches, mismatches and missing depth keys.");
            Require(
                CombatBeamSolver.CanSkipNoveltyFactsForValueOnlyTerminalForTesting(
                    isTerminal: true,
                    hasSimulator: false)
                && !CombatBeamSolver.CanSkipNoveltyFactsForValueOnlyTerminalForTesting(
                    isTerminal: false,
                    hasSimulator: false)
                && !CombatBeamSolver.CanSkipNoveltyFactsForValueOnlyTerminalForTesting(
                    isTerminal: true,
                    hasSimulator: true),
                "Novelty skips fact capture only for simulator-free terminal memo snapshots.");
            ValidateRenderedCurrentTurnTakeoverContract();
            ValidateApprovedForegroundPreviewContract();

            HarnessScenario scenario = new(
                "IRONCLAD",
                "FUZZY_WURM_CRAWLER_WEAK",
                "U0U1PINNED1",
                Ascension: 0,
                ActIndexForTest: 0);
            Task enter = OfflineCombat.EnterCombatRoomAsync(scenario);
            loop.RunUntilCompleted(enter, TimeSpan.FromSeconds(180), "U0/U1 enter combat");
            CombatState combat = OfflineCombat.WaitForPlayableCombat(loop);
            Console.WriteLine(OfflineCombat.DescribeRoot(combat));

            SolverSettingsSnapshot settings = SolverSettings.Capture();
            SolverSearchProfile profile = settings.Profile with
            {
                BeamWidth = BeamWidth,
                MaxExpandedNodes = MaxExpandedNodes,
                SoftTimeBudgetMilliseconds = BudgetMilliseconds,
            };
            SolverDisplayNames names = SolverDisplayNames.Capture(combat);
            BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
            SearchPolicySnapshot captured = SolverController.CaptureSearchPolicy(
                settings,
                combat,
                includeTurnSetup: false,
                theftPolicy: null);

            if (darkEmbracePactOnly)
            {
                ValidateDarkEmbraceBurningPactDraw(combat, names, damage, captured, profile);
                Console.WriteLine("DarkEmbraceBurningPact PASS");
                return 0;
            }

            U0Evidence u0 = RunU0(combat, names, damage, captured, profile);
            U1Evidence u1 = RunU1(combat, names, damage, captured, profile, u0.FirstAction);
            U5Evidence u5 = RunU5(combat, names, damage, captured, profile);
            PhaseBEvidence phaseB = RunDeferredImpactContract(
                combat, names, damage, captured, profile);
            PhaseCEvidence phaseC = RunR0TransitionMemoContract(
                combat, names, captured, profile);
            PhaseCFullSearchEvidence phaseCFullSearch = RunPhaseCFullSearchReuseContract(
                combat, names, captured, profile);

            var evidence = new
            {
                automatedStatus = "PASS",
                pinnedTarget = "0.107.1",
                scenario = new
                {
                    character = "IRONCLAD",
                    encounter = "FUZZY_WURM_CRAWLER_WEAK",
                    seed = "U0U1PINNED1",
                },
                budget = new
                {
                    beamWidth = BeamWidth,
                    maxExpandedNodes = MaxExpandedNodes,
                    budgetMilliseconds = BudgetMilliseconds,
                    maxDegreeOfParallelism = 1,
                },
                u0,
                u1,
                u5,
                phaseB,
                phaseC,
                phaseCFullSearch,
                remainingRuntimeSmoke = new[]
                {
                    "real multiplayer Heavy Blade + native Choice/Brand + following card",
                    "real multiplayer consecutive Offering draws + following-card execution",
                    "real Host/Client remote action inserted between local actions",
                    "real cancellation/network-late-callback timing",
                },
            };

            string evidencePath = Path.Combine(
                outputDirectory,
                "u0-u1-pinned-evidence.json");
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(evidence, Json));
            Console.WriteLine("U0U1PinnedHarness PASS");
            Console.WriteLine($"evidence={evidencePath}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                $"U0U1PinnedHarness FAIL: {error.GetType().Name}: {error.Message}");
            Console.Error.WriteLine(error.StackTrace);
            return 1;
        }
    }

    private static void ValidateViciousStrategicValue()
    {
        PowerModel vicious =
            (PowerModel)RuntimeHelpers.GetUninitializedObject(typeof(ViciousPower));
        StrategicEffectRequirements requirements =
            StrategicEffectModel.Requirements(vicious);
        Require(
            requirements.HasFlag(StrategicEffectRequirements.DebuffApplications)
                && requirements.HasFlag(StrategicEffectRequirements.AverageCardValue),
            "Vicious strategic value must inspect future Vulnerable opportunities and card value.");

        StrategicEffectContext context = new(
            EnemyHp: 200,
            IncomingDamage: 0,
            IncomingHitCount: 0,
            RemainingTurns: 4,
            UsefulCardPlays: 8,
            AttackPlays: 4,
            SkillPlays: 4,
            BlockSkillPlays: 0,
            PowerPlays: 1,
            ExhaustPlays: 0,
            ShivPlays: 0,
            DebuffApplications: 3,
            SkillEnergySpend: 0,
            PowerEnergySpend: 1,
            AverageCardValue: 6,
            BestCardValue: 10,
            AverageAttackValue: 8,
            StatusDrawTriggers: 0)
        {
            VulnerableApplications = 2,
        };
        StrategicEffectVector value = StrategicEffectModel.Evaluate(vicious, context);
        Require(
            value.CardAccessPotential == 12
                && value.DamagePotential == 0
                && value.PreventionPotential == 0
                && value.ResourcePotential == 0
                && value.ScalingPotential == 0,
            $"Vicious strategic value drifted: {value}.");
    }

    private static void ValidateApprovedForegroundPreviewContract()
    {
        SearchInteractionState interaction = new();
        PlanAction approvedAction = new(PlanActionKind.EndTurn, 1);
        SolverFrontierTurn approvedTurn = new(
            Turn: 1,
            Actions: [approvedAction],
            HpLost: 0,
            HpRecovered: 0,
            EnemyHpLost: 0,
            EnergyLeft: 0,
            CombatEnded: false);
        SolverSpeculativeRoutePreview approvedPreview = new(
            CandidateVersion: 7,
            StartTurnNumber: 1,
            ProjectedBattlePotionCount: 0,
            ProjectedBattleHpLost: 0,
            CombatEnded: false,
            OnlyDeathRoutesFound: false,
            HasRisk: false,
            Turns: [approvedTurn]);
        SolverProgress approved = new(
            StartTurnNumber: 1,
            CurrentTurnNumber: 1,
            CompletedTurnLayers: 1,
            PlayDepth: 1,
            ExpandedNodes: 10,
            ReviewedWorldlines: 10,
            MaxNodes: 100,
            FrontierNodes: 1,
            EndedNodes: 0,
            ElapsedMilliseconds: 100,
            Phase: "approved",
            SpeculativeRoutePreview: approvedPreview)
        {
            OfficialPublishedOrigin = new CandidateOrigin(1, 1, 1, 10, 1),
            OfficialPublishedEvaluationContextId = "pinned-e1",
        };
        Require(
            approved.RouteAdoptionSeed == null && approved.HasApprovedForegroundRoute,
            "E1 approved display preview incorrectly requires an executable route seed.");

        interaction.PublishProgress(approved);
        Require(
            ReferenceEquals(interaction.ApprovedForegroundProgress, approved)
                && interaction.ApprovedForegroundProgress!.ApprovedForegroundCandidateVersion == 7,
            "E1 approved display preview was not latched.");

        SolverSpeculativeRoutePreview unapprovedPreview = approvedPreview with
        {
            CandidateVersion = 8,
            ProjectedBattleHpLost = 99,
        };
        interaction.PublishProgress(approved with
        {
            ElapsedMilliseconds = 200,
            SpeculativeRoutePreview = unapprovedPreview,
            OfficialPublishedOrigin = null,
            OfficialPublishedEvaluationContextId = null,
        });
        Require(
            ReferenceEquals(interaction.ApprovedForegroundProgress, approved),
            "Ordinary background progress replaced the approved foreground preview.");

        long now = Environment.TickCount64 + SolverWeights.ProgressUiIntervalMilliseconds + 10;
        Require(
            interaction.TryCreateDisplayProgress(now, out SolverProgress displayed)
                && displayed.SpeculativeRoutePreview?.CandidateVersion == 7
                && displayed.RouteAdoptionSeed == null,
            "E1 display did not preserve the approved preview without granting execution.");

        SolverRouteAdoptionSeed matchingSeed = new(
            candidateVersion: 99,
            actions: [approvedAction],
            materialize: static () => null!);
        interaction.PublishProgress(approved with
        {
            ElapsedMilliseconds = 300,
            RouteAdoptionSeed = matchingSeed,
            OfficialPublishedOrigin = null,
            OfficialPublishedEvaluationContextId = null,
        });
        Require(
            interaction.TryCreateDisplayProgress(
                now + SolverWeights.ProgressUiIntervalMilliseconds + 10,
                out SolverProgress executableDisplay)
                && executableDisplay.SpeculativeRoutePreview?.CandidateVersion == 7
                && ReferenceEquals(executableDisplay.RouteAdoptionSeed, matchingSeed),
            "E1 did not expose execution only after a matching materialized route became available.");
    }

    private static void ValidateRenderedCurrentTurnTakeoverContract()
    {
        SearchInteractionState interaction = new();
        PlanAction displayedAction = new(PlanActionKind.EndTurn, 1);
        SolverRouteAdoptionSeed displayedSeed = new(
            candidateVersion: 17,
            actions: [displayedAction],
            materialize: static () => null!);

        interaction.RenderedCurrentTurnAdoptionSeed = displayedSeed;
        Require(
            interaction.RequestApplyCurrentTurn(
                interaction.RenderedCurrentTurnAdoptionSeed),
            "Rendered current-turn takeover request was rejected.");
        SearchTakeoverRequest? request = interaction.CurrentTakeoverRequest;
        Require(
            request?.Kind == SearchTakeoverKind.ApplyCurrentTurn
                && ReferenceEquals(request.CurrentTurnAdoptionSeed, displayedSeed)
                && request.CurrentTurnAdoptionSeed.CandidateVersion == 17
                && request.CurrentTurnAdoptionSeed.Actions.SequenceEqual([displayedAction]),
            "Apply-current-turn did not freeze the exact rendered candidate.");
        _ = interaction.CompleteTakeover();
        Require(
            interaction.RenderedCurrentTurnAdoptionSeed == null,
            "Rendered current-turn seed survived takeover completion.");
    }

    private static void ValidateLocalCoreShadowNormalizationContract()
    {
        const string parentA =
            "L=local;HC=4/0/0/1/0/11;P=remote-a;R=shared-a;E0=enemy-a;AI0=ai-a;MS0=multi-a";
        const string parentB =
            "L=local;HC=6/0/0/1/0/11;P=remote-b;R=shared-b;E0=enemy-b;AI0=ai-b;MS0=multi-b";
        const string outputA =
            "L=local-after;HC=5/0/0/1/0/11;P=remote-c;R=shared-c;E0=enemy-c;AI0=ai-c;MS0=multi-c";
        const string outputB =
            "L=local-after;HC=7/0/0/1/0/11;P=remote-d;R=shared-d;E0=enemy-d;AI0=ai-d;MS0=multi-d";

        string normalizedParentA =
            LiveCombatStamp.NormalizeLocalCoreSearchValidityText(parentA);
        string normalizedParentB =
            LiveCombatStamp.NormalizeLocalCoreSearchValidityText(parentB);
        string normalizedOutputA =
            LiveCombatStamp.NormalizeLocalCoreSearchValidityText(outputA);
        string normalizedOutputB =
            LiveCombatStamp.NormalizeLocalCoreSearchValidityText(outputB);
        Require(
            normalizedParentA == normalizedParentB
                && normalizedOutputA == normalizedOutputB,
            "Local-core shadow normalization did not remove explicitly allowed remote/shared drift.");

        CombatTransitionMemo owner = new();
        ActionReplayCache cache = ActionReplayCache.ForLocalCoreShadow(owner);
        cache.BindCombat("pinned-local-core-shadow");
        ReplayCacheKey key = new(
            LiveCombatStamp.FingerprintStateText(normalizedParentA),
            "PlayCard:BASH",
            "pinned-policy",
            ActionReplayCache.CurrentLocalCoreContractVersion);
        ReplayCacheObservation firstOutput = new(
            LiveCombatStamp.FingerprintStateText(normalizedOutputA),
            normalizedOutputA,
            Score: 0d,
            SearchBoundaryReason.None,
            Turn: 1,
            PlayerDead: false,
            AllEnemiesDead: false,
            HasRisk: false,
            PredictionGapCount: 0);
        ReplayCacheObservation secondOutput = firstOutput with
        {
            OutputState = LiveCombatStamp.FingerprintStateText(normalizedOutputB),
            OutputStateText = normalizedOutputB,
        };
        Require(
            cache.Observe(key, normalizedParentA, firstOutput)
                == ReplayCacheValidationResult.Stored
                && cache.Observe(key, normalizedParentB, secondOutput)
                    == ReplayCacheValidationResult.ValidatedHit,
            "Local-core normalized shadow cache did not validate equivalent remote/shared drift.");
    }

    private static void ValidateMultiplayerLethalReuseBoundary()
    {
        Require(
            MultiplayerCombatObjectivePolicy.IsInLethalRecalculationWindow(
                enemyDurability: 13,
                initialEnemyMaximumHp: 386),
            "4 HP + 9 block at 386 max HP must invalidate reused routes inside the lethal window.");
        Require(
            !MultiplayerCombatObjectivePolicy.IsInLethalRecalculationWindow(
                enemyDurability: 200,
                initialEnemyMaximumHp: 386),
            "200/386 durability must remain outside the lethal recalculation window.");
    }

    private static void ValidateRouteInvalidationVersionContract()
    {
        MultiplayerRouteChangeTracker.Reset();
        try
        {
            MultiplayerRouteChangeTracker.SignalSchedulingBoundary("pinned_local_turn_boundary");
            Require(
                MultiplayerRouteChangeTracker.Version == 1
                    && MultiplayerRouteChangeTracker.InvalidationVersion == 0,
                "Scheduling-only multiplayer boundary invalidated the current route.");

            MultiplayerRouteChangeTracker.ObserveEnemyHp(
                new StateFingerprint(1, 1),
                allowInvalidation: false,
                "pinned_enemy_baseline");
            bool invalidated = MultiplayerRouteChangeTracker.ObserveEnemyHp(
                new StateFingerprint(2, 2),
                allowInvalidation: true,
                "pinned_enemy_hp_invalidation");
            Require(
                invalidated
                    && MultiplayerRouteChangeTracker.Version == 2
                    && MultiplayerRouteChangeTracker.InvalidationVersion == 1,
                "Enemy-HP route invalidation did not advance both scheduling and invalidation versions.");

            Require(
                !MultiplayerSearchCompletionContracts.IsStale(
                    routeScopedCompletion: true,
                    searchWorldVersion: 7,
                    currentWorldVersion: 22,
                    searchRouteInvalidationVersion: 0,
                    currentRouteInvalidationVersion: 0,
                    fullStampMatches: false,
                    localStampMatches: true),
                "Route-scoped completion rejected scheduling/remote-only drift with an unchanged local core.");
            Require(
                MultiplayerSearchCompletionContracts.IsStale(
                    routeScopedCompletion: true,
                    searchWorldVersion: 7,
                    currentWorldVersion: 22,
                    searchRouteInvalidationVersion: 0,
                    currentRouteInvalidationVersion: 1,
                    fullStampMatches: false,
                    localStampMatches: true),
                "Route-scoped completion accepted a real route invalidation.");
        }
        finally
        {
            MultiplayerRouteChangeTracker.Reset();
        }
    }

    private static void ValidateRollingHorizonQualityContract()
    {
        Require(
            SolverInterimResultOrdering.ComparePrimaryQuality(
                candidateCompleteVictory: true,
                candidateStrategicHpDeficit: 24,
                candidateCombatEndedTurn: 6,
                currentCompleteVictory: false,
                currentStrategicHpDeficit: 0,
                currentCombatEndedTurn: null,
                rollingHorizonLossFirst: true) > 0,
            "Rolling horizon allowed a 24-HP terminal route to outrank a 0-HP live horizon route.");
        Require(
            SolverInterimResultOrdering.ComparePrimaryQuality(
                candidateCompleteVictory: true,
                candidateStrategicHpDeficit: 0,
                candidateCombatEndedTurn: 6,
                currentCompleteVictory: false,
                currentStrategicHpDeficit: 0,
                currentCombatEndedTurn: null,
                rollingHorizonLossFirst: true) < 0,
            "Rolling horizon did not prefer victory when strategic HP loss tied.");

        SolverInterimResult live = new(
            Won: false,
            OutstandingStolenResource: 0,
            ProjectedBattleHpLost: 0,
            StrategicHpDeficit: 0,
            PotionStrategicCost: 0,
            ProjectedBattlePotionCount: 0,
            EnemyHp: 300,
            Score: 0d)
        {
            Survives = true,
            RollingHorizonLossFirst = true,
        };
        SolverInterimResult costlyVictory = live with
        {
            Won = true,
            ProjectedBattleHpLost = 24,
            StrategicHpDeficit = 24,
            EnemyHp = 0,
            CombatEndedTurn = 6,
        };
        Require(
            !SolverInterimResultOrdering.CanPromoteDisplayedResult(costlyVictory, live),
            "Rolling horizon display replaced a 0-HP live route with a 24-HP terminal route.");

        SolverInterimResult worseBackground = live with
        {
            ProjectedBattleHpLost = 2,
            StrategicHpDeficit = 2,
            EnemyHp = 200,
            Score = 1000d,
        };
        Require(
            !SolverInterimResultOrdering.CanPromoteDisplayedResult(worseBackground, live),
            "E2 allowed a higher-loss background candidate to replace the current foreground route.");

        SolverInterimResult betterBackground = live with
        {
            EnemyHp = 250,
            Score = 1d,
        };
        Require(
            SolverInterimResultOrdering.CanPromoteDisplayedResult(betterBackground, live),
            "E2 rejected a same-loss background candidate with strictly better enemy HP.");

        SolverInterimResult scrollsForeground = live with
        {
            Won = false,
            ProjectedBattleHpLost = 19,
            StrategicHpDeficit = 17,
            EnemyHp = 68,
        };
        SolverInterimResult scrollsFinal = live with
        {
            Won = true,
            ProjectedBattleHpLost = 39,
            StrategicHpDeficit = 23,
            EnemyHp = 0,
            CombatEndedTurn = 3,
        };
        Require(
            CombatSearchCoordinator.ShouldPreferForegroundAtCompletion(
                scrollsFinal,
                scrollsForeground),
            "E3 completion guard allowed the 39-loss terminal result to replace the 19-loss rolling-horizon foreground.");
        Require(
            !CombatSearchCoordinator.ShouldPreferForegroundAtCompletion(
                scrollsForeground,
                scrollsFinal),
            "E3 completion guard reversed the strict foreground/final quality relation.");

        PlanAction foregroundAction = new(PlanActionKind.EndTurn, 1);
        SolverSpeculativeRoutePreview foregroundPreview = new(
            CandidateVersion: 23,
            StartTurnNumber: 1,
            ProjectedBattlePotionCount: 0,
            ProjectedBattleHpLost: 12,
            CombatEnded: false,
            OnlyDeathRoutesFound: false,
            HasRisk: false,
            Turns:
            [
                new SolverFrontierTurn(
                    Turn: 1,
                    Actions: [foregroundAction],
                    HpLost: 0,
                    HpRecovered: 0,
                    EnemyHpLost: 0,
                    EnergyLeft: 0,
                    CombatEnded: false),
            ]);
        Require(
            CombatSearchCoordinator.RouteMatchesPreview(
                [foregroundAction],
                foregroundPreview)
            && !CombatSearchCoordinator.RouteMatchesPreview(
                [new PlanAction(PlanActionKind.EndTurn, 2)],
                foregroundPreview),
            "E3 completion backing did not require exact displayed route actions.");

        SolverCurrentTurnPreview currentTurnPreview = new(
            CandidateVersion: 31,
            Turn: 1,
            Actions: [foregroundAction],
            HpLost: 0,
            HpRecovered: 0,
            EnemyHpLost: 0,
            EnergyLeft: 0,
            CombatEnded: false,
            FrontierTurns: foregroundPreview.Turns);
        SolverRouteAdoptionSeed currentTurnSeed = new(
            candidateVersion: 31,
            actions: [foregroundAction],
            materialize: static () => null!);
        Require(
            CombatSearchCoordinator.CurrentTurnSeedMatchesPreview(
                currentTurnSeed,
                currentTurnPreview)
            && !CombatSearchCoordinator.CurrentTurnSeedMatchesPreview(
                new SolverRouteAdoptionSeed(
                    candidateVersion: 32,
                    actions: [new PlanAction(PlanActionKind.EndTurn, 2)],
                    materialize: static () => null!),
                currentTurnPreview),
            "E3 completion current-turn fallback did not require the exact approved foreground prefix.");
    }

    private static void ValidateDarkEmbracePredictionCoverage()
    {
        CardModel darkEmbrace = (CardModel)RuntimeHelpers.GetUninitializedObject(typeof(DarkEmbrace));
        Require(
            CardOnPlayMirrors.CanMirror(darkEmbrace),
            "Dark Embrace OnPlay is not explicitly mirrored.");
        Require(
            CardEffectSpecRegistry.Contains(darkEmbrace),
            "Dark Embrace power application is missing from CardEffectSpecRegistry.");
    }

    private static void ValidateDarkEmbraceBurningPactDraw(
        CombatState combat,
        SolverDisplayNames names,
        BattleDamageSnapshot damage,
        SearchPolicySnapshot policy,
        SolverSearchProfile profile)
    {
        Player player = LocalContext.GetMe(combat)
            ?? throw new InvalidOperationException("Dark Embrace fixture has no local player.");
        var state = player.PlayerCombatState
            ?? throw new InvalidOperationException("Dark Embrace fixture has no local combat state.");
        foreach (string id in new[] { "DARK_EMBRACE", "BURNING_PACT", "HELLRAISER" })
            state.Hand.AddInternal(combat.CreateCard(ResolveCard(id), player), -1);
        foreach (string id in new[] { "OFFERING", "NOT_YET", "STOKE", "DEFEND_IRONCLAD" })
            state.DrawPile.AddInternal(combat.CreateCard(ResolveCard(id), player), 0);
        int turn = state.TurnNumber;
        PlanAction embrace = new(PlanActionKind.PlayCard, turn, CardId: "DARK_EMBRACE");
        PlanAction pact = new(PlanActionKind.PlayCard, turn, CardId: "BURNING_PACT",
            Choice: new PlanCardChoice(PlanChoiceEffect.Exhaust, PileType.Hand,
                [new PlanCardToken("HELLRAISER", 0, "", 0, 0, "Hellraiser")]));
        CombatBeamSolver replay = new(CombatRootSnapshot.Capture(combat), names, damage, policy,
            searchProfile: profile);
        SimulationSnapshot afterEmbrace = replay.ReplayDiagnosticPrefix([embrace]);
        try
        {
            Require(afterEmbrace.BoundaryReason == SearchBoundaryReason.None,
                $"Dark Embrace replay reached {afterEmbrace.BoundaryReason}.");
            Require(((SimulatedCombatState)afterEmbrace.Simulator.State.CombatState)
                    .GetAmount<DarkEmbracePower>(player.Creature) == 1,
                "Dark Embrace applied more than one power stack.");
        }
        finally { afterEmbrace.ReleaseSimulator(); }

        SimulationSnapshot afterPact = replay.ReplayDiagnosticPrefix([embrace, pact]);
        try
        {
            Require(afterPact.BoundaryReason == SearchBoundaryReason.None,
                $"Burning Pact replay reached {afterPact.BoundaryReason}.");
            SimPlayerCombatState predicted = afterPact.Simulator.State.GetPlayerCombatState(player);
            string[] hand = predicted.Hand.Cards.Select(card => card.Preview.Id.Entry).ToArray();
            Require(hand.Contains("DEFEND_IRONCLAD") && hand.Contains("STOKE") && hand.Contains("NOT_YET")
                && !hand.Contains("OFFERING") && predicted.DrawPile.Cards[0].Preview.Id.Entry == "OFFERING",
                $"Burning Pact predicted the wrong draw: hand={string.Join(',', hand)}; "
                + $"next={predicted.DrawPile.Cards[0].Preview.Id.Entry}.");
        }
        finally { afterPact.ReleaseSimulator(); }
    }

    private static U0Evidence RunU0(
        CombatState combat,
        SolverDisplayNames names,
        BattleDamageSnapshot damage,
        SearchPolicySnapshot captured,
        SolverSearchProfile profile)
    {
        ConcurrentDictionary<SearchPathObservationStage, int> stageCounts = new();
        ConcurrentQueue<string> infoLines = new();
        SearchPathObserver observer = new(
            wantsState: _ => true,
            observe: observation =>
                stageCounts.AddOrUpdate(observation.Stage, 1, (_, count) => count + 1));
        SearchDiagnosticsSink diagnostics = new(
            info: message => infoLines.Enqueue(message),
            debug: _ => { },
            pathObserver: observer);
        SearchRequestWorkTotals totals = new();

        SearchPolicySnapshot policy = captured with
        {
            Profile = profile,
            RoutePolicy = SearchRoutePolicy.SinglePlayerFullRoute,
            CurrentTurnOnly = false,
            UseMultiplayerTeamObjective = false,
            DetailedDiagnostics = true,
            VerifyIncrementalSearch = true,
            FixedBudget = true,
            MaxDegreeOfParallelism = 1,
            BudgetOverrideMilliseconds = null,
            UseNoveltyPortfolio = false,
            NoveltySearch = null,
            UseBeamWidthPortfolio = false,
            BeamWidthPortfolioWidths = null,
            Interaction = null,
            Diagnostics = diagnostics,
            RequestWorkTotals = totals,
        };

        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver solver = new(
            root,
            names,
            damage,
            policy,
            searchProfile: profile);
        SolverResult result = solver.Solve();
        SearchRequestWorkSnapshot work = totals.Snapshot();
        Require(result.BestNode.Actions.Count > 0, "U0 search produced no actions.");
        Require(
            result.BoundaryReason != SearchBoundaryReason.TimeLimit,
            "U0 pinned search unexpectedly hit TimeLimit.");

        foreach (SearchPathObservationStage required in new[]
        {
            SearchPathObservationStage.Root,
            SearchPathObservationStage.Generated,
            SearchPathObservationStage.Expanded,
            SearchPathObservationStage.ActionAdmitted,
        })
        {
            Require(
                stageCounts.TryGetValue(required, out int count) && count > 0,
                $"U0 path observer never saw {required}.");
        }

        int candidateLines = infoLines.Count(line =>
            line.Contains("[CombatSolver/U0] FINAL_CANDIDATE ", StringComparison.Ordinal));
        int selectionLines = infoLines.Count(line =>
            line.Contains("[CombatSolver/U0] FINAL_SELECTION ", StringComparison.Ordinal));
        Require(candidateLines > 0, "U0 emitted no FINAL_CANDIDATE diagnostics.");
        Require(selectionLines == 1, $"U0 expected one FINAL_SELECTION, got {selectionLines}.");

        CombatRootSnapshot noEventRoot = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver noEventSolver = new(
            noEventRoot,
            names,
            damage,
            policy,
            searchProfile: profile);
        SimulationSnapshot noEventSnapshot = noEventSolver.ReplayDiagnosticPrefix([]);
        StateFingerprint noEventFingerprint;
        try
        {
            noEventFingerprint = U0BaselineFixture.ReplayNoTeammateEvents(
                noEventSnapshot.Simulator,
                new HashSet<uint>());
        }
        finally
        {
            noEventSnapshot.ReleaseSimulator();
        }

        PlanAction first = result.BestNode.Actions[0];
        Require(
            first.Kind == PlanActionKind.PlayCard,
            $"U1 fixture requires the first selected action to be PlayCard, got {first.Kind}.");

        return new U0Evidence(
            Status: "PASS",
            EvidenceLevel: "pinned_offline_production_search",
            FirstAction: ActionToken(first),
            Actions: result.BestNode.Actions.Select(ActionToken).ToArray(),
            Boundary: result.BoundaryReason.ToString(),
            ExpandedNodes: work.ExpandedNodes,
            TransitionCount: work.TransitionCount,
            CandidateDiagnosticLines: candidateLines,
            SelectionDiagnosticLines: selectionLines,
            PathStages: stageCounts
                .OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key.ToString(), pair => pair.Value),
            NoTeammateReplayFingerprint: Format(noEventFingerprint));
    }

    private static U1Evidence RunU1(
        CombatState combat,
        SolverDisplayNames names,
        BattleDamageSnapshot damage,
        SearchPolicySnapshot captured,
        SolverSearchProfile profile,
        string expectedFirstActionToken)
    {
        SearchPolicySnapshot replayPolicy = captured with
        {
            Profile = profile,
            RoutePolicy = SearchRoutePolicy.MultiplayerLocalCrossTurn,
            CurrentTurnOnly = false,
            UseMultiplayerTeamObjective = false,
            DetailedDiagnostics = false,
            VerifyIncrementalSearch = true,
            FixedBudget = true,
            MaxDegreeOfParallelism = 1,
            BudgetOverrideMilliseconds = null,
            UseNoveltyPortfolio = false,
            NoveltySearch = null,
            UseBeamWidthPortfolio = false,
            BeamWidthPortfolioWidths = null,
            Interaction = null,
            RequestWorkTotals = new SearchRequestWorkTotals(),
        };

        PlanAction action = FindFirstAction(combat, names, damage, replayPolicy, profile);
        Require(
            ActionToken(action) == expectedFirstActionToken,
            "U1 replay fixture did not resolve the same first action as U0.");

        ReplayEvidence replayA = ReplayOneAction(
            combat, names, damage, replayPolicy, profile, action);
        ReplayEvidence replayB = ReplayOneAction(
            combat, names, damage, replayPolicy, profile, action);
        Require(
            replayA.ContinuationStateText == replayB.ContinuationStateText,
            "U1 production one-action replay is not deterministic.");

        MultiplayerSafeActionRevalidationDecision matchedDecision =
            MultiplayerSafeExecutePolicy.RevalidateAction(RevalidationFacts());
        MultiplayerSafeActionRevalidationDecision queueBusyDecision =
            MultiplayerSafeExecutePolicy.RevalidateAction(
                RevalidationFacts() with { ActionQueueIdle = false });
        MultiplayerSafeActionRevalidationDecision worldUnstableDecision =
            MultiplayerSafeExecutePolicy.RevalidateAction(
                RevalidationFacts() with { WorldVersionStable = false });

        Require(
            matchedDecision == MultiplayerSafeActionRevalidationDecision.SafeToContinue,
            $"U1 settled native action was rejected: {matchedDecision}.");
        Require(
            queueBusyDecision == MultiplayerSafeActionRevalidationDecision.ActionMismatch,
            $"U1 busy native action queue was not rejected: {queueBusyDecision}.");
        Require(
            worldUnstableDecision == MultiplayerSafeActionRevalidationDecision.WorldUnstable,
            $"U1 unstable WorldVersion was not rejected: {worldUnstableDecision}.");

        int turn = LocalContext.GetMe(combat)?.PlayerCombatState?.TurnNumber
            ?? throw new InvalidOperationException("U1 fixture has no local turn.");
        const int generation = 77;
        const long version0 = 100;

        MultiplayerSafeExecutionSession normal = new(turn, generation, version0, maxActions: 2);
        Require(
            normal.TryBeginAction(0, ActionToken(action), turn, generation, version0, out _)
            && normal.MarkAwaitingWorldUpdate()
            && normal.BeginRevalidation()
            && normal.AcceptAction(version0 + 1, hasNextAction: true)
            && normal.TryBeginAction(
                1, ActionToken(action), turn, generation, version0 + 1, out _),
            "U1 unchanged world did not authorize the next action.");

        MultiplayerSafeExecutionSession remoteInserted = new(
            turn, generation, version0, maxActions: 2);
        Require(
            remoteInserted.TryBeginAction(
                0, ActionToken(action), turn, generation, version0, out _)
            && remoteInserted.MarkAwaitingWorldUpdate()
            && remoteInserted.BeginRevalidation()
            && remoteInserted.AcceptAction(version0 + 1, hasNextAction: true),
            "U1 remote-insertion setup failed.");
        bool staleAccepted = remoteInserted.TryBeginAction(
            1,
            ActionToken(action),
            turn,
            generation,
            version0 + 2,
            out string remoteInsertionReason);
        Require(
            !staleAccepted && remoteInsertionReason == "world_version_not_accepted",
            $"U1 pre-action remote insertion was not rejected: {remoteInsertionReason}.");

        MultiplayerSafeExecutionSession cancelled = new(
            turn, generation + 1, version0, maxActions: 1);
        Require(
            cancelled.TryBeginAction(
                0, ActionToken(action), turn, generation + 1, version0, out _),
            "U1 cancellation setup failed.");
        cancelled.Abort("pinned_cancel");
        bool staleRetry = cancelled.TryBeginAction(
            0,
            ActionToken(action),
            turn,
            generation + 1,
            version0,
            out string cancelReason);
        Require(
            !staleRetry && cancelReason == "session_state_Aborted",
            $"U1 cancelled session reauthorized a stale callback: {cancelReason}.");

        return new U1Evidence(
            Status: "PASS",
            EvidenceLevel: "pinned_world_version_revalidation_plus_diagnostic_replay",
            FirstAction: ActionToken(action),
            ReplayDeterministic: true,
            SettledDecision: matchedDecision.ToString(),
            QueueBusyDecision: queueBusyDecision.ToString(),
            WorldUnstableDecision: worldUnstableDecision.ToString(),
            NormalNextActionAuthorized: true,
            RemoteInsertionRejectedReason: remoteInsertionReason,
            CancelledRetryRejectedReason: cancelReason);
    }

    private static U5Evidence RunU5(
        CombatState combat,
        SolverDisplayNames names,
        BattleDamageSnapshot damage,
        SearchPolicySnapshot captured,
        SolverSearchProfile profile)
    {
        SearchPolicySnapshot replayPolicy = captured with
        {
            Profile = profile,
            RoutePolicy = SearchRoutePolicy.MultiplayerLocalCrossTurn,
            CurrentTurnOnly = false,
            UseMultiplayerTeamObjective = false,
            DetailedDiagnostics = false,
            VerifyIncrementalSearch = true,
            FixedBudget = true,
            MaxDegreeOfParallelism = 1,
            BudgetOverrideMilliseconds = null,
            UseNoveltyPortfolio = false,
            NoveltySearch = null,
            UseBeamWidthPortfolio = false,
            BeamWidthPortfolioWidths = null,
            Interaction = null,
            RequestWorkTotals = new SearchRequestWorkTotals(),
        };

        CombatRootSnapshot routeRoot = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver routeSolver = new(
            routeRoot,
            names,
            damage,
            replayPolicy,
            searchProfile: profile);
        SolverResult route = routeSolver.Solve();
        PlanAction[] turnOneCards = route.BestNode.Actions
            .Where(action => action.Turn == route.StartTurnNumber
                && action.Kind == PlanActionKind.PlayCard)
            .Take(2)
            .ToArray();
        Require(
            turnOneCards.Length == 2,
            $"U5 pinned fixture needs two current-turn PlayCard actions, got {turnOneCards.Length}.");
        PlanAction bash = turnOneCards[0];
        PlanAction strike = turnOneCards[1];
        Require(
            string.Equals(bash.CardId, "BASH", StringComparison.Ordinal)
                && string.Equals(strike.CardId, "STRIKE_IRONCLAD", StringComparison.Ordinal),
            $"U5 pinned fixture drifted: first={bash.CardId} second={strike.CardId}.");

        U5OrderReplay bashThenStrike = ReplayU5Order(
            combat,
            names,
            damage,
            replayPolicy,
            profile,
            [bash, strike]);
        U5OrderReplay strikeThenBash = ReplayU5Order(
            combat,
            names,
            damage,
            replayPolicy,
            profile,
            [strike, bash]);

        Require(
            bashThenStrike.FutureFingerprint != strikeThenBash.FutureFingerprint,
            "U5 order-sensitive Bash/Strike pair collapsed to the same complete future fingerprint.");
        Require(
            bashThenStrike.EnemyHp < strikeThenBash.EnemyHp,
            $"U5 vulnerable ordering lost its expected effect: " +
            $"bash_then_strike_enemy_hp={bashThenStrike.EnemyHp} " +
            $"strike_then_bash_enemy_hp={strikeThenBash.EnemyHp}.");
        Require(
            !MultiplayerInterleaveOrderPolicy.CanCollapseOrder(
                MultiplayerInterleaveOrderRelation.OrderSensitive)
                && !MultiplayerInterleaveOrderPolicy.CanCollapseOrder(
                    MultiplayerInterleaveOrderRelation.ReverseUnavailable)
                && MultiplayerInterleaveOrderPolicy.CanCollapseOrder(
                    MultiplayerInterleaveOrderRelation.ExactEquivalent),
            "U5 exact-collapse policy changed.");

        U5TerminalOrderEvidence terminalOrder = RunU5TerminalOrder(
            combat,
            names,
            replayPolicy,
            profile,
            bash,
            strike);
        U5GenerationOrderEvidence generationOrder = RunU5GenerationOrder(
            combat,
            names,
            replayPolicy,
            profile);
        U5ResourceOrderEvidence resourceOrder = RunU5ResourceOrder(
            combat,
            names,
            replayPolicy,
            profile,
            bash);
        U5DrawOrderEvidence drawOrder = RunU5DrawOrder(
            combat,
            names,
            replayPolicy,
            profile);

        return new U5Evidence(
            Status: "PASS",
            EvidenceLevel: "pinned_offline_production_replay",
            ForwardOrder: $"{bash.CardId}->{strike.CardId}",
            ReverseOrder: $"{strike.CardId}->{bash.CardId}",
            ForwardFutureFingerprint: Format(bashThenStrike.FutureFingerprint),
            ReverseFutureFingerprint: Format(strikeThenBash.FutureFingerprint),
            ForwardEnemyHp: bashThenStrike.EnemyHp,
            ReverseEnemyHp: strikeThenBash.EnemyHp,
            OrderSensitive: true,
            ExactCollapseRejected: true,
            TerminalForwardRejected: terminalOrder.ForwardRejected,
            TerminalReverseCompleted: terminalOrder.ReverseCompleted,
            TerminalReverseEnemyHp: terminalOrder.ReverseEnemyHp,
            TerminalReverseEnergy: terminalOrder.ReverseEnergy,
            GenerationForwardFingerprint: Format(generationOrder.ForwardFingerprint),
            GenerationReverseFingerprint: Format(generationOrder.ReverseFingerprint),
            GenerationForwardHand: generationOrder.ForwardHand,
            GenerationReverseHand: generationOrder.ReverseHand,
            GenerationHandMultisetDifferent: generationOrder.HandMultisetDifferent,
            ResourceForwardCompleted: resourceOrder.ForwardCompleted,
            ResourceForwardEnergy: resourceOrder.ForwardEnergy,
            ResourceReverseRejected: resourceOrder.ReverseRejected,
            ResourceReverseReason: resourceOrder.ReverseReason,
            DrawInitialTopTwo: drawOrder.InitialTopTwo,
            DrawForwardFingerprint: Format(drawOrder.ForwardFingerprint),
            DrawReverseFingerprint: Format(drawOrder.ReverseFingerprint),
            DrawForwardHand: drawOrder.ForwardHand,
            DrawReverseHand: drawOrder.ReverseHand,
            DrawForwardExhaust: drawOrder.ForwardExhaust,
            DrawReverseExhaust: drawOrder.ReverseExhaust,
            DrawPileStateDifferent: drawOrder.PileStateDifferent,
            RealMultiplayerOwnershipVerified: false);
    }

    private static U5DrawOrderEvidence RunU5DrawOrder(
        CombatState combat,
        SolverDisplayNames names,
        SearchPolicySnapshot policy,
        SolverSearchProfile profile)
    {
        Player player = LocalContext.GetMe(combat)
            ?? throw new InvalidOperationException("U5 draw fixture has no local player.");
        var playerState = player.PlayerCombatState
            ?? throw new InvalidOperationException("U5 draw fixture has no local combat state.");
        Require(
            playerState.DrawPile.Cards.Count >= 2,
            $"U5 draw fixture requires at least two draw-pile cards, got {playerState.DrawPile.Cards.Count}.");

        string[] initialTopTwo = playerState.DrawPile.Cards
            .Take(2)
            .Select(card => card.Id.Entry)
            .ToArray();
        Require(
            !string.Equals(initialTopTwo[0], initialTopTwo[1], StringComparison.Ordinal),
            $"U5 draw fixture top two cards are not decisive: {initialTopTwo[0]},{initialTopTwo[1]}.");

        playerState.Hand.AddInternal(combat.CreateCard(ResolveCard("POMMEL_STRIKE"), player), -1);
        playerState.Hand.AddInternal(combat.CreateCard(ResolveCard("HAVOC"), player), -1);
        SetLiveEnergyForU5(player, 3);

        int turn = playerState.TurnNumber;
        uint enemyCombatId = combat.Enemies.Single().CombatId
            ?? throw new InvalidOperationException("U5 draw fixture enemy has no CombatId.");
        PlanAction pommel = new(
            PlanActionKind.PlayCard,
            turn,
            CardId: "POMMEL_STRIKE",
            CardOccurrence: 0,
            TargetIndex: 0,
            TargetCombatId: enemyCombatId,
            CardTitle: "Pommel Strike");
        PlanAction havoc = new(
            PlanActionKind.PlayCard,
            turn,
            CardId: "HAVOC",
            CardOccurrence: 0,
            CardTitle: "Havoc");

        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        U5DrawReplay forward = ReplayU5DrawOrder(
            combat, names, damage, policy, profile, player, [pommel, havoc]);
        U5DrawReplay reverse = ReplayU5DrawOrder(
            combat, names, damage, policy, profile, player, [havoc, pommel]);

        Require(
            forward.FutureFingerprint != reverse.FutureFingerprint,
            "U5 draw-order pair collapsed to the same complete future fingerprint.");

        bool pileStateDifferent =
            !forward.Hand.SequenceEqual(reverse.Hand)
            || !forward.Exhaust.SequenceEqual(reverse.Exhaust)
            || !forward.Draw.SequenceEqual(reverse.Draw);
        Require(
            pileStateDifferent,
            "U5 draw-order fixture produced identical hand/exhaust/draw pile states.");

        return new U5DrawOrderEvidence(
            InitialTopTwo: initialTopTwo,
            ForwardFingerprint: forward.FutureFingerprint,
            ReverseFingerprint: reverse.FutureFingerprint,
            ForwardHand: forward.Hand,
            ReverseHand: reverse.Hand,
            ForwardExhaust: forward.Exhaust,
            ReverseExhaust: reverse.Exhaust,
            PileStateDifferent: true);
    }

    private static U5DrawReplay ReplayU5DrawOrder(
        CombatState combat,
        SolverDisplayNames names,
        BattleDamageSnapshot damage,
        SearchPolicySnapshot policy,
        SolverSearchProfile profile,
        Player player,
        IReadOnlyList<PlanAction> actions)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver replay = new(
            root,
            names,
            damage,
            policy,
            searchProfile: profile);
        SimulationSnapshot snapshot = replay.ReplayDiagnosticPrefix(actions);
        try
        {
            Require(
                snapshot.BoundaryReason == SearchBoundaryReason.None,
                $"U5 draw replay {string.Join("->", actions.Select(action => action.CardId))} " +
                $"reached {snapshot.BoundaryReason}.");

            StateFingerprint future = ShadowFutureStateFingerprint.Capture(
                snapshot.Simulator,
                snapshot.ProcessedEnemyDeaths,
                new HashSet<string>(StringComparer.Ordinal),
                Array.Empty<ShadowTeammateActionCandidate>());
            SimPlayerCombatState state = snapshot.Simulator.State.GetPlayerCombatState(player);
            return new U5DrawReplay(
                FutureFingerprint: future,
                Hand: state.Hand.Cards.Select(card => card.Preview.Id.Entry).ToArray(),
                Exhaust: state.ExhaustPile.Cards.Select(card => card.Preview.Id.Entry).ToArray(),
                Draw: state.DrawPile.Cards.Select(card => card.Preview.Id.Entry).ToArray());
        }
        finally
        {
            snapshot.ReleaseSimulator();
        }
    }

    private static U5ResourceOrderEvidence RunU5ResourceOrder(
        CombatState combat,
        SolverDisplayNames names,
        SearchPolicySnapshot policy,
        SolverSearchProfile profile,
        PlanAction bash)
    {
        Player player = LocalContext.GetMe(combat)
            ?? throw new InvalidOperationException("U5 resource fixture has no local player.");
        var playerState = player.PlayerCombatState
            ?? throw new InvalidOperationException("U5 resource fixture has no local combat state.");
        CardModel offering = combat.CreateCard(ResolveCard("OFFERING"), player);
        playerState.Hand.AddInternal(offering, -1);

        SetLiveEnergyForU5(player, 1);
        Require(
            playerState.Energy == 1,
            $"U5 resource fixture could not set live energy to 1; actual={playerState.Energy}.");

        int turn = playerState.TurnNumber;
        PlanAction offeringAction = new(
            PlanActionKind.PlayCard,
            turn,
            CardId: "OFFERING",
            CardOccurrence: 0,
            CardTitle: "Offering");
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);

        U5OrderReplay forward = ReplayU5Order(
            combat,
            names,
            damage,
            policy,
            profile,
            [offeringAction, bash]);
        Require(
            forward.Energy == 1,
            $"U5 resource forward order should end at 1 energy, got {forward.Energy}.");

        bool reverseRejected = false;
        string reverseReason = "-";
        try
        {
            _ = ReplayU5Order(
                combat,
                names,
                damage,
                policy,
                profile,
                [bash, offeringAction]);
        }
        catch (InvalidOperationException ex)
            when (ex.Message.Contains("BASH", StringComparison.Ordinal)
                && ex.Message.Contains("energy=1", StringComparison.Ordinal)
                && ex.Message.Contains("cost=2", StringComparison.Ordinal))
        {
            reverseRejected = true;
            reverseReason = ex.Message;
        }

        Require(
            reverseRejected,
            "U5 resource reverse order did not reject Bash at energy=1/cost=2.");

        return new U5ResourceOrderEvidence(
            ForwardCompleted: true,
            ForwardEnergy: forward.Energy,
            ReverseRejected: true,
            ReverseReason: reverseReason);
    }

    private static void SetLiveEnergyForU5(Player player, int value)
    {
        object state = player.PlayerCombatState
            ?? throw new InvalidOperationException("U5 resource fixture has no player combat state.");
        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        PropertyInfo? energyProperty = state.GetType().GetProperty("Energy", flags);
        MethodInfo? setter = energyProperty?.GetSetMethod(nonPublic: true);
        if (setter != null)
        {
            setter.Invoke(state, [value]);
        }
        else
        {
            FieldInfo? field = state.GetType().GetField("<Energy>k__BackingField", flags)
                ?? state.GetType().GetField("_energy", flags);
            if (field == null)
            {
                throw new MissingMemberException(
                    state.GetType().FullName,
                    "Energy setter/backing field");
            }
            field.SetValue(state, value);
        }

        int actual = (int)(energyProperty?.GetValue(state)
            ?? throw new InvalidOperationException("U5 resource fixture cannot read Energy."));
        if (actual != value)
        {
            throw new InvalidOperationException(
                $"U5 resource fixture energy write failed: expected={value} actual={actual}.");
        }
    }

    private static U5TerminalOrderEvidence RunU5TerminalOrder(
        CombatState combat,
        SolverDisplayNames names,
        SearchPolicySnapshot policy,
        SolverSearchProfile profile,
        PlanAction bash,
        PlanAction strike)
    {
        if (combat.Enemies.Count != 1)
            throw new InvalidOperationException(
                $"U5 terminal fixture requires one enemy, got {combat.Enemies.Count}.");

        var enemy = combat.Enemies[0];
        int originalHp = enemy.CurrentHp;
        const int lethalFixtureHp = 7;
        if (enemy.MaxHp < lethalFixtureHp)
            throw new InvalidOperationException(
                $"U5 terminal fixture enemy max HP is only {enemy.MaxHp}.");

        try
        {
            enemy.SetCurrentHpInternal(lethalFixtureHp);
            BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);

            bool forwardRejected = false;
            string forwardReason = "-";
            try
            {
                CombatRootSnapshot forwardRoot = CombatRootSnapshot.Capture(combat);
                CombatBeamSolver forwardReplay = new(
                    forwardRoot,
                    names,
                    damage,
                    policy,
                    searchProfile: profile);
                SimulationSnapshot unexpected = forwardReplay.ReplayDiagnosticPrefix([bash, strike]);
                unexpected.ReleaseSimulator();
            }
            catch (InvalidOperationException ex)
                when (ex.Message.Contains(
                    "回放包含已锁定战斗终局之后的动作",
                    StringComparison.Ordinal))
            {
                forwardRejected = true;
                forwardReason = ex.Message;
            }

            Require(
                forwardRejected,
                "U5 terminal fixture did not reject Bash->Strike after Bash ended combat.");

            CombatRootSnapshot reverseRoot = CombatRootSnapshot.Capture(combat);
            CombatBeamSolver reverseReplay = new(
                reverseRoot,
                names,
                damage,
                policy,
                searchProfile: profile);
            SimulationSnapshot reverse = reverseReplay.ReplayDiagnosticPrefix([strike, bash]);
            try
            {
                Require(
                    reverse.AllEnemiesDead,
                    "U5 terminal reverse order Strike->Bash did not end combat.");
                Require(
                    reverse.Energy == 0,
                    $"U5 terminal reverse order should consume all 3 energy, got {reverse.Energy}.");

                return new U5TerminalOrderEvidence(
                    ForwardRejected: true,
                    ForwardReason: forwardReason,
                    ReverseCompleted: true,
                    ReverseEnemyHp: reverse.EnemyHp,
                    ReverseEnergy: reverse.Energy);
            }
            finally
            {
                reverse.ReleaseSimulator();
            }
        }
        finally
        {
            enemy.SetCurrentHpInternal(originalHp);
        }
    }

    private static U5GenerationOrderEvidence RunU5GenerationOrder(
        CombatState combat,
        SolverDisplayNames names,
        SearchPolicySnapshot policy,
        SolverSearchProfile profile)
    {
        Player player = LocalContext.GetMe(combat)
            ?? throw new InvalidOperationException("U5 generation fixture has no local player.");
        var hand = player.PlayerCombatState?.Hand
            ?? throw new InvalidOperationException("U5 generation fixture has no local hand.");

        CardModel infernalBlade = combat.CreateCard(
            ResolveCard("INFERNAL_BLADE"),
            player);
        CardModel distraction = combat.CreateCard(
            ResolveCard("DISTRACTION"),
            player);
        hand.AddInternal(infernalBlade, -1);
        hand.AddInternal(distraction, -1);

        int turn = player.PlayerCombatState!.TurnNumber;
        PlanAction infernalAction = new(
            PlanActionKind.PlayCard,
            turn,
            CardId: "INFERNAL_BLADE",
            CardOccurrence: 0,
            CardTitle: "Infernal Blade");
        PlanAction distractionAction = new(
            PlanActionKind.PlayCard,
            turn,
            CardId: "DISTRACTION",
            CardOccurrence: 0,
            CardTitle: "Distraction");

        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        U5GeneratedReplay forward = ReplayU5GeneratedOrder(
            combat,
            names,
            damage,
            policy,
            profile,
            player,
            [infernalAction, distractionAction]);
        U5GeneratedReplay reverse = ReplayU5GeneratedOrder(
            combat,
            names,
            damage,
            policy,
            profile,
            player,
            [distractionAction, infernalAction]);

        Require(
            forward.FutureFingerprint != reverse.FutureFingerprint,
            "U5 generation-order pair collapsed to the same complete future fingerprint.");

        string[] forwardSorted = forward.Hand.OrderBy(static id => id, StringComparer.Ordinal).ToArray();
        string[] reverseSorted = reverse.Hand.OrderBy(static id => id, StringComparer.Ordinal).ToArray();
        bool handMultisetDifferent = !forwardSorted.SequenceEqual(reverseSorted);
        Require(
            handMultisetDifferent,
            "U5 generation-order fixture was not decisive: both orders produced the same hand multiset.");

        return new U5GenerationOrderEvidence(
            ForwardFingerprint: forward.FutureFingerprint,
            ReverseFingerprint: reverse.FutureFingerprint,
            ForwardHand: forward.Hand,
            ReverseHand: reverse.Hand,
            HandMultisetDifferent: true);
    }

    private static U5GeneratedReplay ReplayU5GeneratedOrder(
        CombatState combat,
        SolverDisplayNames names,
        BattleDamageSnapshot damage,
        SearchPolicySnapshot policy,
        SolverSearchProfile profile,
        Player player,
        IReadOnlyList<PlanAction> actions)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver replay = new(
            root,
            names,
            damage,
            policy,
            searchProfile: profile);
        SimulationSnapshot snapshot = replay.ReplayDiagnosticPrefix(actions);
        try
        {
            Require(
                snapshot.BoundaryReason == SearchBoundaryReason.None,
                $"U5 generation replay {string.Join("->", actions.Select(action => action.CardId))} " +
                $"reached {snapshot.BoundaryReason}.");
            StateFingerprint future = ShadowFutureStateFingerprint.Capture(
                snapshot.Simulator,
                snapshot.ProcessedEnemyDeaths,
                new HashSet<string>(StringComparer.Ordinal),
                Array.Empty<ShadowTeammateActionCandidate>());
            string[] hand = snapshot.Simulator.State
                .GetPlayerCombatState(player)
                .Hand.Cards
                .Select(card => card.Preview.Id.Entry)
                .ToArray();
            return new U5GeneratedReplay(future, hand);
        }
        finally
        {
            snapshot.ReleaseSimulator();
        }
    }

    private static PhaseBEvidence RunDeferredImpactContract(
        CombatState combat,
        SolverDisplayNames names,
        BattleDamageSnapshot damage,
        SearchPolicySnapshot captured,
        SolverSearchProfile profile)
    {
        Player player = LocalContext.GetMe(combat)
            ?? throw new InvalidOperationException("Phase B fixture has no local player.");
        var playerState = player.PlayerCombatState
            ?? throw new InvalidOperationException("Phase B fixture has no local combat state.");
        Require(
            combat.Enemies.Count == 1,
            $"Phase B fixture requires one enemy, got {combat.Enemies.Count}.");

        SearchPolicySnapshot replayPolicy = captured with
        {
            Profile = profile,
            RoutePolicy = SearchRoutePolicy.SinglePlayerFullRoute,
            CurrentTurnOnly = false,
            UseMultiplayerTeamObjective = false,
            DetailedDiagnostics = false,
            VerifyIncrementalSearch = true,
            FixedBudget = true,
            MaxDegreeOfParallelism = 1,
            BudgetOverrideMilliseconds = null,
            UseNoveltyPortfolio = false,
            NoveltySearch = null,
            UseBeamWidthPortfolio = false,
            BeamWidthPortfolioWidths = null,
            Interaction = null,
            RequestWorkTotals = new SearchRequestWorkTotals(),
        };

        CardModel biasedCognitionModel = ModelDb.AllCards
            .Single(static card => card is BiasedCognition);
        SetLiveEnergyForU5(player, 3);
        playerState.Hand.AddInternal(
            combat.CreateCard(biasedCognitionModel, player), -1);
        playerState.Hand.AddInternal(
            combat.CreateCard(ResolveCard("OUTMANEUVER"), player), -1);

        int turn = playerState.TurnNumber;
        PlanAction endTurn = new(PlanActionKind.EndTurn, turn);
        PlanAction biasedCognition = new(
            PlanActionKind.PlayCard,
            turn,
            CardId: biasedCognitionModel.Id.Entry,
            CardOccurrence: 0,
            CardTitle: "Biased Cognition");
        PlanAction outmaneuver = new(
            PlanActionKind.PlayCard,
            turn,
            CardId: "OUTMANEUVER",
            CardOccurrence: 0,
            CardTitle: "Outmaneuver");

        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SimulationSnapshot standPat = ReplayPhaseB(
            root, names, damage, replayPolicy, profile, [endTurn]);
        SimulationSnapshot biasedPending = ReplayPhaseB(
            root, names, damage, replayPolicy, profile, [biasedCognition]);
        SimulationSnapshot biasedSettled = ReplayPhaseB(
            root, names, damage, replayPolicy, profile, [biasedCognition, endTurn]);
        SimulationSnapshot outmaneuverPending = ReplayPhaseB(
            root, names, damage, replayPolicy, profile, [outmaneuver]);
        SimulationSnapshot outmaneuverSettled = ReplayPhaseB(
            root, names, damage, replayPolicy, profile, [outmaneuver, endTurn]);

        try
        {
            Require(
                standPat.BoundaryReason == SearchBoundaryReason.None
                    && biasedPending.BoundaryReason == SearchBoundaryReason.None
                    && biasedSettled.BoundaryReason == SearchBoundaryReason.None
                    && outmaneuverPending.BoundaryReason == SearchBoundaryReason.None
                    && outmaneuverSettled.BoundaryReason == SearchBoundaryReason.None,
                "Phase B deferred-effect replay reached an unexpected search boundary.");

            SimulatedCombatState biasedPendingState =
                (SimulatedCombatState)biasedPending.Simulator.State.CombatState;
            SimulatedCombatState biasedSettledState =
                (SimulatedCombatState)biasedSettled.Simulator.State.CombatState;
            int biasedDecay =
                biasedPendingState.GetAmount<BiasedCognitionPower>(player.Creature);
            int biasedPendingFocus =
                biasedPendingState.GetAmount<FocusPower>(player.Creature);
            int biasedSettledFocus =
                biasedSettledState.GetAmount<FocusPower>(player.Creature);
            Require(
                biasedDecay > 0
                    && biasedSettledState.GetAmount<BiasedCognitionPower>(player.Creature)
                        == biasedDecay,
                "Biased Cognition deferred Focus loss power was not retained.");
            Require(
                biasedSettledFocus == biasedPendingFocus - biasedDecay,
                $"Biased Cognition deferred Focus loss did not settle exactly once: "
                + $"pending={biasedPendingFocus} decay={biasedDecay} settled={biasedSettledFocus}.");

            SimulatedCombatState outmaneuverPendingState =
                (SimulatedCombatState)outmaneuverPending.Simulator.State.CombatState;
            SimulatedCombatState outmaneuverSettledState =
                (SimulatedCombatState)outmaneuverSettled.Simulator.State.CombatState;
            Require(
                outmaneuverPendingState.GetAmount<EnergyNextTurnPower>(player.Creature) > 0,
                "Outmaneuver future energy was not pending before EndTurn.");
            Require(
                outmaneuverSettledState.GetAmount<EnergyNextTurnPower>(player.Creature) == 0,
                "Outmaneuver future energy did not settle at the next turn start.");

            DeferredImpactCoverage standPatCoverage = DeferredImpactCoverage.Capture(
                default,
                turn,
                standPat,
                semanticEvidenceAvailable: true,
                semanticStateChanged: false,
                modeledQualityDominatedByStandPat: false);
            DeferredImpactCoverage biasedPendingCoverage = DeferredImpactCoverage.Capture(
                default,
                turn,
                biasedPending,
                semanticEvidenceAvailable: true,
                semanticStateChanged: true,
                modeledQualityDominatedByStandPat: false);
            DeferredImpactCoverage biasedSettledCoverage = DeferredImpactCoverage.Capture(
                default,
                turn,
                biasedSettled,
                semanticEvidenceAvailable: true,
                semanticStateChanged: true,
                modeledQualityDominatedByStandPat: false);
            DeferredImpactCoverage outmaneuverPendingCoverage = DeferredImpactCoverage.Capture(
                default,
                turn,
                outmaneuverPending,
                semanticEvidenceAvailable: true,
                semanticStateChanged: true,
                modeledQualityDominatedByStandPat: true);
            DeferredImpactCoverage outmaneuverSettledCoverage = DeferredImpactCoverage.Capture(
                default,
                turn,
                outmaneuverSettled,
                semanticEvidenceAvailable: true,
                semanticStateChanged: true,
                modeledQualityDominatedByStandPat: false);

            Require(
                biasedPendingCoverage.CoverageThrough
                    == DeferredImpactCoverageBoundary.CurrentTurn
                    && biasedPendingCoverage.RequiresFurtherClosure,
                "Biased Cognition deferred cost was incorrectly marked closed.");
            Require(
                biasedSettledCoverage.CoverageThrough
                    == DeferredImpactCoverageBoundary.NextLocalTurnStart
                    && !biasedSettledCoverage.RequiresFurtherClosure,
                "Biased Cognition deferred cost did not reach a closed next-turn boundary.");

            Require(
                outmaneuverPendingCoverage.CoverageThrough
                    == DeferredImpactCoverageBoundary.CurrentTurn
                    && outmaneuverPendingCoverage.RequiresFurtherClosure,
                "Outmaneuver slow benefit was incorrectly marked closed before EndTurn.");
            Require(
                outmaneuverSettledCoverage.CoverageThrough
                    == DeferredImpactCoverageBoundary.NextLocalTurnStart
                    && !outmaneuverSettledCoverage.RequiresFurtherClosure,
                "Outmaneuver did not close at the next local turn start.");
            Require(
                outmaneuverSettledCoverage.Outcome.Energy > standPatCoverage.Outcome.Energy,
                $"Outmaneuver next-turn energy benefit was lost: outmaneuver={outmaneuverSettledCoverage.Outcome.Energy} "
                + $"stand_pat={standPatCoverage.Outcome.Energy}.");

            var enemy = combat.Enemies.Single();
            int originalEnemyHp = enemy.CurrentHp;
            try
            {
                enemy.SetCurrentHpInternal(7);
                SetLiveEnergyForU5(player, 3);
                CombatRootSnapshot lethalRoot = CombatRootSnapshot.Capture(combat);
                uint targetCombatId = enemy.CombatId
                    ?? throw new InvalidOperationException("Phase B lethal enemy has no CombatId.");
                PlanAction bash = new(
                    PlanActionKind.PlayCard,
                    turn,
                    CardId: "BASH",
                    CardOccurrence: 0,
                    TargetIndex: 0,
                    TargetCombatId: targetCombatId,
                    CardTitle: "Bash");
                SimulationSnapshot lethal = ReplayPhaseB(
                    lethalRoot, names, BattleDamageTracker.Observe(combat),
                    replayPolicy, profile, [bash]);
                try
                {
                    Require(lethal.AllEnemiesDead,
                        "Phase B lethal fixture did not end combat.");
                    DeferredImpactCoverage lethalCoverage = DeferredImpactCoverage.Capture(
                        default,
                        turn,
                        lethal,
                        semanticEvidenceAvailable: true,
                        semanticStateChanged: true,
                        modeledQualityDominatedByStandPat: true);
                    Require(
                        lethalCoverage.CoverageThrough
                            == DeferredImpactCoverageBoundary.CombatTerminal
                            && !lethalCoverage.RequiresFurtherClosure,
                        "Current-turn lethal route retained nonexistent combat debt.");
                    Require(
                        lethalCoverage.Outcome.ProjectedPlayerHp
                            == lethalCoverage.Outcome.PlayerHp,
                        "Current-turn lethal route still charged a future enemy attack.");

                    return new PhaseBEvidence(
                        Status: "PASS",
                        EvidenceLevel: "pinned_exact_simulation_plus_sidecar_contract",
                        BiasedCognitionPendingCoverage:
                            biasedPendingCoverage.CoverageThrough.ToString(),
                        BiasedCognitionSettledCoverage:
                            biasedSettledCoverage.CoverageThrough.ToString(),
                        BiasedCognitionPendingFocus: biasedPendingFocus,
                        BiasedCognitionSettledFocus: biasedSettledFocus,
                        StandPatNextTurnEnergy: standPatCoverage.Outcome.Energy,
                        OutmaneuverPendingCoverage:
                            outmaneuverPendingCoverage.CoverageThrough.ToString(),
                        OutmaneuverSettledCoverage:
                            outmaneuverSettledCoverage.CoverageThrough.ToString(),
                        OutmaneuverNextTurnEnergy:
                            outmaneuverSettledCoverage.Outcome.Energy,
                        LethalCoverage: lethalCoverage.CoverageThrough.ToString(),
                        LethalProjectedHp: lethalCoverage.Outcome.ProjectedPlayerHp,
                        LethalPlayerHp: lethalCoverage.Outcome.PlayerHp);
                }
                finally
                {
                    lethal.ReleaseSimulator();
                }
            }
            finally
            {
                enemy.SetCurrentHpInternal(originalEnemyHp);
            }
        }
        finally
        {
            standPat.ReleaseSimulator();
            biasedPending.ReleaseSimulator();
            biasedSettled.ReleaseSimulator();
            outmaneuverPending.ReleaseSimulator();
            outmaneuverSettled.ReleaseSimulator();
        }
    }

    private static PhaseCEvidence RunR0TransitionMemoContract(
        CombatState combat,
        SolverDisplayNames names,
        SearchPolicySnapshot captured,
        SolverSearchProfile profile)
    {
        Player player = LocalContext.GetMe(combat)
            ?? throw new InvalidOperationException("Phase C fixture has no local player.");
        var enemy = combat.Enemies.Single();
        int originalEnemyHp = enemy.CurrentHp;
        try
        {
            enemy.SetCurrentHpInternal(7);
            SetLiveEnergyForU5(player, 3);
            BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
            CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
            SearchPolicySnapshot basePolicy = captured with
            {
                Profile = profile,
                RoutePolicy = SearchRoutePolicy.SinglePlayerFullRoute,
                CurrentTurnOnly = false,
                UseMultiplayerTeamObjective = false,
                DetailedDiagnostics = false,
                VerifyIncrementalSearch = false,
                FixedBudget = true,
                MaxDegreeOfParallelism = 1,
                BudgetOverrideMilliseconds = null,
                UseNoveltyPortfolio = false,
                NoveltySearch = null,
                UseBeamWidthPortfolio = false,
                BeamWidthPortfolioWidths = null,
                Interaction = null,
                RequestWorkTotals = new SearchRequestWorkTotals(),
                R0TransitionMemo = null,
                R0TransitionPolicyIdentity = string.Empty,
            };
            ShadowReplaySamplingBudget productionSampling =
                new(CombatBeamSolver.ProductionShadowReplayObservationLimit);
            for (int index = 0;
                 index < CombatBeamSolver.ProductionShadowReplayObservationLimit;
                 index++)
            {
                Require(
                    productionSampling.TryAcquire(),
                    $"Phase C production shadow sampling ended early at {index}.");
            }
            Require(
                !productionSampling.TryAcquire()
                    && productionSampling.Capture().Used
                        == CombatBeamSolver.ProductionShadowReplayObservationLimit
                    && productionSampling.Capture().Capped,
                "Phase C production shadow sampling request-wide cap drifted.");
            ShadowReplaySamplingBudget horizonSampling = new(
                CombatBeamSolver.ProductionShadowReplayObservationLimit,
                CombatBeamSolver.ProductionShadowReplayFutureTurnReserve);
            int currentTurnQuota =
                CombatBeamSolver.ProductionShadowReplayObservationLimit
                - CombatBeamSolver.ProductionShadowReplayFutureTurnReserve;
            for (int index = 0; index < currentTurnQuota; index++)
            {
                Require(
                    horizonSampling.TryAcquire(ShadowReplaySampleClass.CurrentTurn),
                    $"Phase C current-turn shadow reserve ended early at {index}.");
            }
            Require(
                !horizonSampling.TryAcquire(ShadowReplaySampleClass.CurrentTurn)
                    && horizonSampling.CurrentTurnLimited
                    && horizonSampling.Capture().Used == currentTurnQuota
                    && !horizonSampling.Capture().Capped,
                "Phase C future-turn reserve was consumed by current-turn sampling.");
            for (int index = 0;
                 index < CombatBeamSolver.ProductionShadowReplayFutureTurnReserve;
                 index++)
            {
                Require(
                    horizonSampling.TryAcquire(ShadowReplaySampleClass.FutureTurn),
                    $"Phase C future-turn shadow reserve ended early at {index}.");
            }
            ShadowReplaySamplingSnapshot horizonSnapshot = horizonSampling.Capture();
            Require(
                !horizonSampling.TryAcquire(ShadowReplaySampleClass.FutureTurn)
                    && horizonSampling.Capture().Capped
                    && horizonSnapshot.CurrentTurnUsed == currentTurnQuota
                    && horizonSnapshot.FutureTurnUsed
                        == CombatBeamSolver.ProductionShadowReplayFutureTurnReserve,
                "Phase C horizon-aware sampling did not preserve the 48/16 request budget.");
            string policyIdentity = CombatTransitionMemo.CapturePolicyIdentity(basePolicy);
            CombatTransitionMemo memo = new();
            memo.BindCombat(root.ContinuationStamp.CombatIdentity);
            SearchPolicySnapshot memoPolicy = basePolicy with
            {
                R0TransitionMemo = memo,
                R0TransitionPolicyIdentity = policyIdentity,
            };

            uint targetCombatId = enemy.CombatId
                ?? throw new InvalidOperationException("Phase C enemy has no CombatId.");
            PlanAction bash = new(
                PlanActionKind.PlayCard,
                root.StartTurnNumber,
                CardId: "BASH",
                CardOccurrence: 0,
                TargetIndex: 0,
                TargetCombatId: targetCombatId,
                CardTitle: "Bash");

            CombatBeamSolver first = new(root, names, damage, memoPolicy, searchProfile: profile);
            SimulationSnapshot parent = first.ReplayDiagnosticPrefix([]);
            StateFingerprint parentKey = parent.StateKey;
            parent.ReleaseSimulator();

            SimulationSnapshot fresh = first.ReplayDiagnosticActionWithR0Memo(bash);
            CombatBeamSolver second = new(root, names, damage, memoPolicy, searchProfile: profile);
            SimulationSnapshot cached = second.ReplayDiagnosticActionWithR0Memo(bash);
            CombatBeamSolver cacheOff = new(root, names, damage, basePolicy, searchProfile: profile);
            SimulationSnapshot uncached = cacheOff.ReplayDiagnosticActionWithR0Memo(bash);
            try
            {
                Require(fresh.AllEnemiesDead && cached.AllEnemiesDead && uncached.AllEnemiesDead,
                    "Phase C terminal fixture did not remain terminal across cache modes.");
                Require(
                    fresh.StateKey == cached.StateKey
                        && fresh.StateKey == uncached.StateKey
                        && fresh.Score == cached.Score
                        && fresh.Score == uncached.Score
                        && DeferredImpactOutcome.Capture(fresh) == DeferredImpactOutcome.Capture(cached)
                        && DeferredImpactOutcome.Capture(fresh) == DeferredImpactOutcome.Capture(uncached),
                    "Phase C cache on/off output state or evaluation differs.");
                Require(fresh.HasSimulator && !cached.HasSimulator && uncached.HasSimulator,
                    "Phase C hit did not use the simulator-free value snapshot boundary.");
                Require(second.R0TransitionCacheHitsForTesting == 1 && memo.Hits == 1,
                    $"Phase C exact reuse did not register one hit: solver={second.R0TransitionCacheHitsForTesting} memo={memo.Hits}.");
                Require(memo.ContainsIndexForTesting(parentKey, bash, policyIdentity),
                    "Phase C exact parent/action/policy index was not stored.");

                StateFingerprint changedDynamicsKey = new(parentKey.First ^ 1UL, parentKey.Second);
                Require(!memo.ContainsIndexForTesting(changedDynamicsKey, bash, policyIdentity),
                    "Phase C changed dynamics fingerprint produced a false R0 hit.");
                Require(!memo.ContainsIndexForTesting(parentKey, bash, policyIdentity + "-other"),
                    "Phase C policy/version namespace change produced a false R0 hit.");

                PlanAction choiceAction = bash with
                {
                    Choice = new PlanCardChoice(
                        PlanChoiceEffect.MoveToHand,
                        PileType.Draw,
                        []),
                };
                Require(!CombatTransitionMemo.IsActionEligibleForTesting(choiceAction),
                    "Phase C choice-bearing action entered the exact terminal memo.");
                Require(
                    !memo.TryReadTerminal(
                        parentKey, bash, policyIdentity, "semantic-collision-fixture", out _)
                    && memo.CollisionRejects == 1,
                    "Phase C fingerprint collision verifier did not fail closed.");

                enemy.SetCurrentHpInternal(originalEnemyHp);
                BattleDamageSnapshot nonterminalDamage = BattleDamageTracker.Observe(combat);
                CombatRootSnapshot nonterminalRoot = CombatRootSnapshot.Capture(combat);
                SearchPolicySnapshot shadowPolicy = memoPolicy with
                {
                    DetailedDiagnostics = true,
                };
                PlanAction nonterminalBash = bash with
                {
                    Turn = nonterminalRoot.StartTurnNumber,
                };
                ActionReplayCache shadowCache = ActionReplayCache.For(memo);
                CombatBeamSolver shadowFreshSolver = new(
                    nonterminalRoot,
                    names,
                    nonterminalDamage,
                    shadowPolicy,
                    searchProfile: profile);
                SimulationSnapshot shadowFresh =
                    shadowFreshSolver.ReplayDiagnosticActionWithR0Memo(nonterminalBash);
                CombatBeamSolver shadowRepeatedSolver = new(
                    nonterminalRoot,
                    names,
                    nonterminalDamage,
                    shadowPolicy,
                    searchProfile: profile);
                SimulationSnapshot shadowRepeated =
                    shadowRepeatedSolver.ReplayDiagnosticActionWithR0Memo(nonterminalBash);
                SimulationSnapshot shadowCacheOff = new CombatBeamSolver(
                    nonterminalRoot,
                    names,
                    nonterminalDamage,
                    basePolicy,
                    searchProfile: profile).ReplayDiagnosticActionWithR0Memo(nonterminalBash);
                try
                {
                    Require(
                        !shadowFresh.AllEnemiesDead
                            && !shadowRepeated.AllEnemiesDead
                            && !shadowCacheOff.AllEnemiesDead,
                        "Phase C nonterminal shadow fixture unexpectedly became terminal.");
                    Require(
                        shadowFresh.HasSimulator
                            && shadowRepeated.HasSimulator
                            && shadowCacheOff.HasSimulator,
                        "Phase C nonterminal shadow validation must keep real replay simulators.");
                    Require(
                        shadowFresh.StateKey == shadowRepeated.StateKey
                            && shadowFresh.StateKey == shadowCacheOff.StateKey
                            && shadowFresh.Score == shadowRepeated.Score
                            && shadowFresh.Score == shadowCacheOff.Score,
                        "Phase C nonterminal shadow cache on/off output differs.");
                    Require(
                        shadowCache.Count == 1
                            && shadowCache.ValidatedHits == 1
                            && shadowCache.CollisionRejects == 0
                            && shadowCache.OutputMismatches == 0,
                        $"Phase C shadow replay validation drifted: entries={shadowCache.Count} " +
                        $"hits={shadowCache.ValidatedHits} collisions={shadowCache.CollisionRejects} " +
                        $"mismatches={shadowCache.OutputMismatches}.");
                    var shadowTelemetry = shadowRepeatedSolver.ShadowReplayTelemetryForTesting;
                    Require(
                        shadowTelemetry.Observations == 1
                            && shadowTelemetry.Stores == 0
                            && shadowTelemetry.ValidatedHits == 1
                            && shadowTelemetry.CollisionRejects == 0
                            && shadowTelemetry.OutputMismatches == 0
                            && shadowTelemetry.DroppedStores == 0
                            && shadowTelemetry.ValidationTicks > 0
                            && shadowTelemetry.PotentialSavedTicks > 0,
                        $"Phase C shadow telemetry drifted: observations={shadowTelemetry.Observations} " +
                        $"stores={shadowTelemetry.Stores} hits={shadowTelemetry.ValidatedHits} " +
                        $"collisions={shadowTelemetry.CollisionRejects} mismatches={shadowTelemetry.OutputMismatches} " +
                        $"dropped={shadowTelemetry.DroppedStores} validation_ticks={shadowTelemetry.ValidationTicks} " +
                        $"potential_saved_ticks={shadowTelemetry.PotentialSavedTicks}.");

                    return new PhaseCEvidence(
                        "PASS",
                        "pinned_terminal_r0_plus_nonterminal_shadow_replay",
                        memo.EntryCount,
                        memo.Hits,
                        memo.CollisionRejects,
                        cached.HasSimulator,
                        fresh.StateKey == cached.StateKey,
                        fresh.StateKey == uncached.StateKey,
                        !memo.ContainsIndexForTesting(changedDynamicsKey, bash, policyIdentity),
                        !memo.ContainsIndexForTesting(parentKey, bash, policyIdentity + "-other"),
                        !CombatTransitionMemo.IsActionEligibleForTesting(choiceAction),
                        shadowCache.Count,
                        shadowCache.ValidatedHits,
                        shadowCache.CollisionRejects,
                        shadowCache.OutputMismatches,
                        shadowFresh.StateKey == shadowCacheOff.StateKey,
                        shadowTelemetry.Observations,
                        shadowTelemetry.ValidatedHits,
                        shadowTelemetry.ValidationTicks > 0,
                        shadowTelemetry.PotentialSavedTicks > 0);
                }
                finally
                {
                    shadowFresh.ReleaseSimulator();
                    shadowRepeated.ReleaseSimulator();
                    shadowCacheOff.ReleaseSimulator();
                }
            }
            finally
            {
                fresh.ReleaseSimulator();
                cached.ReleaseSimulator();
                uncached.ReleaseSimulator();
            }
        }
        finally
        {
            enemy.SetCurrentHpInternal(originalEnemyHp);
        }
    }

    private static PhaseCFullSearchEvidence RunPhaseCFullSearchReuseContract(
        CombatState combat,
        SolverDisplayNames names,
        SearchPolicySnapshot captured,
        SolverSearchProfile profile)
    {
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SearchPolicySnapshot cacheOffPolicy = captured with
        {
            Profile = profile,
            RoutePolicy = SearchRoutePolicy.SinglePlayerFullRoute,
            CurrentTurnOnly = false,
            UseMultiplayerTeamObjective = false,
            DetailedDiagnostics = true,
            VerifyIncrementalSearch = false,
            FixedBudget = true,
            MaxDegreeOfParallelism = 1,
            BudgetOverrideMilliseconds = null,
            UseNoveltyPortfolio = false,
            NoveltySearch = null,
            UseBeamWidthPortfolio = false,
            BeamWidthPortfolioWidths = null,
            Interaction = null,
            RequestWorkTotals = new SearchRequestWorkTotals(),
            R0TransitionMemo = null,
            R0TransitionPolicyIdentity = string.Empty,
        };
        string policyIdentity = CombatTransitionMemo.CapturePolicyIdentity(cacheOffPolicy);
        CombatTransitionMemo sharedMemo = new();
        CombatRootSnapshot identityRoot = CombatRootSnapshot.Capture(combat);
        sharedMemo.BindCombat(identityRoot.ContinuationStamp.CombatIdentity);

        SolverResult Run(SearchPolicySnapshot policy)
        {
            CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
            CombatBeamSolver solver = new(
                root,
                names,
                damage,
                policy with { RequestWorkTotals = new SearchRequestWorkTotals() },
                searchProfile: profile);
            return solver.Solve();
        }

        string[] Route(SolverResult result)
            => result.BestNode.Actions.Select(ActionToken).ToArray();

        bool SameQuality(SolverResult left, SolverResult right)
            => left.ProjectedBattleHpLost == right.ProjectedBattleHpLost
                && left.ProjectedBattlePotionCount == right.ProjectedBattlePotionCount
                && left.CombatEndedTurn == right.CombatEndedTurn
                && left.Snapshot.EnemyHp == right.Snapshot.EnemyHp
                && left.Snapshot.PlayerHp == right.Snapshot.PlayerHp
                && left.Snapshot.PlayerDead == right.Snapshot.PlayerDead
                && left.Snapshot.AllEnemiesDead == right.Snapshot.AllEnemiesDead
                && left.BoundaryReason == right.BoundaryReason;

        SearchPolicySnapshot sharedPolicy = cacheOffPolicy with
        {
            R0TransitionMemo = sharedMemo,
            R0TransitionPolicyIdentity = policyIdentity,
        };

        SolverResult cacheOff = Run(cacheOffPolicy);
        SolverResult warm = Run(sharedPolicy);
        ActionReplayCache shadowCache = ActionReplayCache.For(sharedMemo);
        int entriesAfterWarm = shadowCache.Count;
        SolverResult repeated = Run(sharedPolicy);

        string[] cacheOffRoute = Route(cacheOff);
        string[] warmRoute = Route(warm);
        string[] repeatedRoute = Route(repeated);
        bool sameRoute =
            cacheOffRoute.SequenceEqual(warmRoute, StringComparer.Ordinal)
            && cacheOffRoute.SequenceEqual(repeatedRoute, StringComparer.Ordinal);
        bool sameQuality =
            SameQuality(cacheOff, warm)
            && SameQuality(cacheOff, repeated);
        bool sameFixedWork =
            cacheOff.ExpandedNodes == warm.ExpandedNodes
            && cacheOff.ExpandedNodes == repeated.ExpandedNodes
            && cacheOff.TransitionCount == warm.TransitionCount
            && cacheOff.TransitionCount == repeated.TransitionCount;
        double repeatedHitRatio = repeated.ShadowReplayObservations == 0
            ? 0d
            : repeated.ShadowReplayValidatedHits / (double)repeated.ShadowReplayObservations;

        Require(sameRoute,
            "Phase C full-search cross-request reuse changed the selected route.");
        Require(sameQuality,
            "Phase C full-search cross-request reuse changed final quality.");
        Require(sameFixedWork,
            $"Phase C full-search fixed work drifted: off={cacheOff.ExpandedNodes}/{cacheOff.TransitionCount} " +
            $"warm={warm.ExpandedNodes}/{warm.TransitionCount} repeated={repeated.ExpandedNodes}/{repeated.TransitionCount}.");
        Require(entriesAfterWarm > 0,
            "Phase C full-search warm request stored no nonterminal shadow transitions.");
        Require(repeated.ShadowReplayObservations > 0
                && repeated.ShadowReplayValidatedHits > 0,
            "Phase C full-search repeated request produced no cross-request shadow hits.");
        Require(repeated.ShadowReplayCollisionRejects == 0
                && repeated.ShadowReplayOutputMismatches == 0
                && shadowCache.CollisionRejects == 0
                && shadowCache.OutputMismatches == 0,
            $"Phase C full-search shadow reuse failed closed: solver_collision={repeated.ShadowReplayCollisionRejects} " +
            $"solver_mismatch={repeated.ShadowReplayOutputMismatches} cache_collision={shadowCache.CollisionRejects} " +
            $"cache_mismatch={shadowCache.OutputMismatches}.");
        Require(repeated.ShadowReplayPotentialSavedDuration > TimeSpan.Zero,
            "Phase C full-search repeated request recorded no potential replay savings.");

        return new PhaseCFullSearchEvidence(
            Status: "PASS",
            EvidenceLevel: "pinned_full_search_cross_request_fixed_work_ab",
            SameRoute: sameRoute,
            SameQuality: sameQuality,
            SameFixedWork: sameFixedWork,
            CacheOffExpanded: cacheOff.ExpandedNodes,
            WarmExpanded: warm.ExpandedNodes,
            RepeatedExpanded: repeated.ExpandedNodes,
            CacheOffTransitions: cacheOff.TransitionCount,
            WarmTransitions: warm.TransitionCount,
            RepeatedTransitions: repeated.TransitionCount,
            EntriesAfterWarm: entriesAfterWarm,
            WarmObservations: warm.ShadowReplayObservations,
            WarmStores: warm.ShadowReplayStores,
            WarmValidatedHits: warm.ShadowReplayValidatedHits,
            RepeatedObservations: repeated.ShadowReplayObservations,
            RepeatedStores: repeated.ShadowReplayStores,
            RepeatedValidatedHits: repeated.ShadowReplayValidatedHits,
            RepeatedHitRatio: repeatedHitRatio,
            RepeatedCollisionRejects: repeated.ShadowReplayCollisionRejects,
            RepeatedOutputMismatches: repeated.ShadowReplayOutputMismatches,
            RepeatedDroppedStores: repeated.ShadowReplayDroppedStores,
            RepeatedValidationMs: repeated.ShadowReplayValidationDuration.TotalMilliseconds,
            RepeatedPotentialSavedMs: repeated.ShadowReplayPotentialSavedDuration.TotalMilliseconds,
            TerminalCacheHits: repeated.TransitionCacheHits,
            Route: repeatedRoute);
    }

    private static SimulationSnapshot ReplayPhaseB(
        CombatRootSnapshot root,
        SolverDisplayNames names,
        BattleDamageSnapshot damage,
        SearchPolicySnapshot policy,
        SolverSearchProfile profile,
        IReadOnlyList<PlanAction> actions)
    {
        CombatBeamSolver replay = new(
            root,
            names,
            damage,
            policy,
            searchProfile: profile);
        return replay.ReplayDiagnosticPrefix(actions);
    }

    private static CardModel ResolveCard(string cardId)
    {
        CardModel[] matches = ModelDb.AllCards
            .Where(card => card.Id.Entry.Equals(cardId, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"U5 generation fixture cannot find card {cardId}."),
            _ => throw new InvalidOperationException($"U5 generation fixture card {cardId} is ambiguous."),
        };
    }

    private static U5OrderReplay ReplayU5Order(
        CombatState combat,
        SolverDisplayNames names,
        BattleDamageSnapshot damage,
        SearchPolicySnapshot policy,
        SolverSearchProfile profile,
        IReadOnlyList<PlanAction> actions)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver replay = new(
            root,
            names,
            damage,
            policy,
            searchProfile: profile);
        SimulationSnapshot snapshot = replay.ReplayDiagnosticPrefix(actions);
        try
        {
            Require(
                snapshot.BoundaryReason == SearchBoundaryReason.None,
                $"U5 replay {string.Join("->", actions.Select(action => action.CardId))} " +
                $"reached {snapshot.BoundaryReason}.");

            StateFingerprint future = ShadowFutureStateFingerprint.Capture(
                snapshot.Simulator,
                snapshot.ProcessedEnemyDeaths,
                new HashSet<string>(StringComparer.Ordinal),
                Array.Empty<ShadowTeammateActionCandidate>());
            return new U5OrderReplay(
                future,
                snapshot.EnemyHp,
                snapshot.PlayerHp,
                snapshot.Energy,
                snapshot.HandCount);
        }
        finally
        {
            snapshot.ReleaseSimulator();
        }
    }

    private static PlanAction FindFirstAction(
        CombatState combat,
        SolverDisplayNames names,
        BattleDamageSnapshot damage,
        SearchPolicySnapshot policy,
        SolverSearchProfile profile)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver solver = new(
            root,
            names,
            damage,
            policy,
            searchProfile: profile);
        SolverResult result = solver.Solve();
        Require(result.BestNode.Actions.Count > 0, "U1 search produced no actions.");
        PlanAction first = result.BestNode.Actions[0];
        Require(
            first.Kind == PlanActionKind.PlayCard,
            $"U1 first action is {first.Kind}, not PlayCard.");
        return first;
    }

    private static ReplayEvidence ReplayOneAction(
        CombatState combat,
        SolverDisplayNames names,
        BattleDamageSnapshot damage,
        SearchPolicySnapshot policy,
        SolverSearchProfile profile,
        PlanAction action)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver replay = new(
            root,
            names,
            damage,
            policy,
            searchProfile: profile);
        SimulationSnapshot snapshot = replay.ReplayDiagnosticPrefix([action]);
        try
        {
            Require(
                snapshot.BoundaryReason == SearchBoundaryReason.None,
                $"U1 one-action replay reached {snapshot.BoundaryReason}.");
            ContinuationStamp continuation = replay.CaptureDiagnosticContinuation(snapshot);
            return new ReplayEvidence(continuation.StateText);
        }
        finally
        {
            snapshot.ReleaseSimulator();
        }
    }

    private static MultiplayerSafeActionRevalidationFacts RevalidationFacts()
        => new(
            NativeLocalActionCaptured: true,
            ActionQueueIdle: true,
            WorldVersionAdvanced: true,
            WorldVersionStable: true,
            HasNextAction: true);

    private static string ActionToken(PlanAction action)
        => $"{action.Turn}:{action.Kind}:{action.CardId ?? action.PotionId ?? "-"}"
            + $":target={action.TargetCombatId?.ToString() ?? "-"}"
            + $":key={action.CardStateKey}";

    private static string Format(StateFingerprint value)
        => $"{value.First:X16}:{value.Second:X16}";

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static string ParseOutput(string[] args)
    {
        string output = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../.local/u0-u1-pinned"));
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] != "--out")
                throw new ArgumentException($"Unknown option {args[index]}.");
            if (++index >= args.Length)
                throw new ArgumentException("Missing value for --out.");
            output = Path.GetFullPath(args[index]);
        }
        return output;
    }

    internal sealed record U0Evidence(
        string Status,
        string EvidenceLevel,
        string FirstAction,
        string[] Actions,
        string Boundary,
        long ExpandedNodes,
        long TransitionCount,
        int CandidateDiagnosticLines,
        int SelectionDiagnosticLines,
        IReadOnlyDictionary<string, int> PathStages,
        string NoTeammateReplayFingerprint);

    internal sealed record PhaseCEvidence(
        string Status,
        string EvidenceLevel,
        int Entries,
        int Hits,
        int CollisionRejects,
        bool CachedHasSimulator,
        bool StateKeyEqual,
        bool CacheOffStateKeyEqual,
        bool DynamicsChangeRejected,
        bool PolicyChangeRejected,
        bool ChoiceRejected,
        int ShadowEntries,
        int ShadowValidatedHits,
        int ShadowCollisionRejects,
        int ShadowOutputMismatches,
        bool ShadowCacheOffStateKeyEqual,
        int ShadowSolverObservations,
        int ShadowSolverValidatedHits,
        bool ShadowValidationCostRecorded,
        bool ShadowPotentialSavedCostRecorded);

    internal sealed record PhaseCFullSearchEvidence(
        string Status,
        string EvidenceLevel,
        bool SameRoute,
        bool SameQuality,
        bool SameFixedWork,
        int CacheOffExpanded,
        int WarmExpanded,
        int RepeatedExpanded,
        int CacheOffTransitions,
        int WarmTransitions,
        int RepeatedTransitions,
        int EntriesAfterWarm,
        int WarmObservations,
        int WarmStores,
        int WarmValidatedHits,
        int RepeatedObservations,
        int RepeatedStores,
        int RepeatedValidatedHits,
        double RepeatedHitRatio,
        int RepeatedCollisionRejects,
        int RepeatedOutputMismatches,
        int RepeatedDroppedStores,
        double RepeatedValidationMs,
        double RepeatedPotentialSavedMs,
        int TerminalCacheHits,
        string[] Route);

    internal sealed record PhaseBEvidence(
        string Status,
        string EvidenceLevel,
        string BiasedCognitionPendingCoverage,
        string BiasedCognitionSettledCoverage,
        int BiasedCognitionPendingFocus,
        int BiasedCognitionSettledFocus,
        int StandPatNextTurnEnergy,
        string OutmaneuverPendingCoverage,
        string OutmaneuverSettledCoverage,
        int OutmaneuverNextTurnEnergy,
        string LethalCoverage,
        int LethalProjectedHp,
        int LethalPlayerHp);

    internal sealed record U5Evidence(
        string Status,
        string EvidenceLevel,
        string ForwardOrder,
        string ReverseOrder,
        string ForwardFutureFingerprint,
        string ReverseFutureFingerprint,
        int ForwardEnemyHp,
        int ReverseEnemyHp,
        bool OrderSensitive,
        bool ExactCollapseRejected,
        bool TerminalForwardRejected,
        bool TerminalReverseCompleted,
        int TerminalReverseEnemyHp,
        int TerminalReverseEnergy,
        string GenerationForwardFingerprint,
        string GenerationReverseFingerprint,
        string[] GenerationForwardHand,
        string[] GenerationReverseHand,
        bool GenerationHandMultisetDifferent,
        bool ResourceForwardCompleted,
        int ResourceForwardEnergy,
        bool ResourceReverseRejected,
        string ResourceReverseReason,
        string[] DrawInitialTopTwo,
        string DrawForwardFingerprint,
        string DrawReverseFingerprint,
        string[] DrawForwardHand,
        string[] DrawReverseHand,
        string[] DrawForwardExhaust,
        string[] DrawReverseExhaust,
        bool DrawPileStateDifferent,
        bool RealMultiplayerOwnershipVerified);

    private sealed record U5DrawOrderEvidence(
        string[] InitialTopTwo,
        StateFingerprint ForwardFingerprint,
        StateFingerprint ReverseFingerprint,
        string[] ForwardHand,
        string[] ReverseHand,
        string[] ForwardExhaust,
        string[] ReverseExhaust,
        bool PileStateDifferent);

    private sealed record U5DrawReplay(
        StateFingerprint FutureFingerprint,
        string[] Hand,
        string[] Exhaust,
        string[] Draw);

    private sealed record U5ResourceOrderEvidence(
        bool ForwardCompleted,
        int ForwardEnergy,
        bool ReverseRejected,
        string ReverseReason);

    private sealed record U5GenerationOrderEvidence(
        StateFingerprint ForwardFingerprint,
        StateFingerprint ReverseFingerprint,
        string[] ForwardHand,
        string[] ReverseHand,
        bool HandMultisetDifferent);

    private sealed record U5GeneratedReplay(
        StateFingerprint FutureFingerprint,
        string[] Hand);

    private sealed record U5TerminalOrderEvidence(
        bool ForwardRejected,
        string ForwardReason,
        bool ReverseCompleted,
        int ReverseEnemyHp,
        int ReverseEnergy);

    private sealed record U5OrderReplay(
        StateFingerprint FutureFingerprint,
        int EnemyHp,
        int PlayerHp,
        int Energy,
        int HandCount);

    internal sealed record U1Evidence(
        string Status,
        string EvidenceLevel,
        string FirstAction,
        bool ReplayDeterministic,
        string SettledDecision,
        string QueueBusyDecision,
        string WorldUnstableDecision,
        bool NormalNextActionAuthorized,
        string RemoteInsertionRejectedReason,
        string CancelledRetryRejectedReason);

    private sealed record ReplayEvidence(string ContinuationStateText);
}
