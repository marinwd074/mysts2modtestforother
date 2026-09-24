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
        string outputDirectory = ParseOutput(args);
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

            U0Evidence u0 = RunU0(combat, names, damage, captured, profile);
            U1Evidence u1 = RunU1(combat, names, damage, captured, profile, u0.FirstAction);
            U5Evidence u5 = RunU5(combat, names, damage, captured, profile);

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
        Require(
            replayA.RemoteFingerprint == replayB.RemoteFingerprint,
            "U1 production remote fingerprint is not deterministic.");

        MultiplayerSafeActionRevalidationFacts matchedWithLegacyDisagreement =
            RevalidationFacts() with
            {
                LocalCardRemovedFromHand = false,
                EnergyStateConsistent = false,
                TargetIdentityStable = false,
                RemotePublicStateUnchanged = false,
                EnemyStateMatchesExpectedTarget = false,
            };
        MultiplayerSafeActionRevalidationDecision matchedDecision =
            MultiplayerSafeExecutePolicy.RevalidateAction(matchedWithLegacyDisagreement);
        MultiplayerSafeActionRevalidationDecision remoteMismatchDecision =
            MultiplayerSafeExecutePolicy.RevalidateAction(
                RevalidationFacts() with { ExpectedRemoteStateMatched = false });
        MultiplayerSafeActionRevalidationDecision semanticMismatchDecision =
            MultiplayerSafeExecutePolicy.RevalidateAction(
                RevalidationFacts() with { ExpectedContinuationStateMatched = false });

        Require(
            matchedDecision == MultiplayerSafeActionRevalidationDecision.SafeToContinue,
            $"U1 legal modeled chain was rejected: {matchedDecision}.");
        Require(
            remoteMismatchDecision == MultiplayerSafeActionRevalidationDecision.RemoteOrUnknownChange,
            $"U1 remote mismatch decision changed: {remoteMismatchDecision}.");
        Require(
            semanticMismatchDecision == MultiplayerSafeActionRevalidationDecision.ActionMismatch,
            $"U1 semantic mismatch decision changed: {semanticMismatchDecision}.");

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
            EvidenceLevel: "pinned_production_one_action_replay_plus_fault_injection",
            FirstAction: ActionToken(action),
            ReplayDeterministic: true,
            ContinuationFingerprint: replayA.ContinuationFingerprint,
            RemoteFingerprint: replayA.RemoteFingerprint,
            MatchedLegacyDisagreementDecision: matchedDecision.ToString(),
            RemoteMismatchDecision: remoteMismatchDecision.ToString(),
            SemanticMismatchDecision: semanticMismatchDecision.ToString(),
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
            StateFingerprint remote =
                MultiplayerContinuationRemoteFingerprint.CapturePredicted(
                    snapshot.Simulator,
                    root.PlayerIdentity);
            StateFingerprintBuilder builder = new();
            builder.Add(continuation.StateText);
            StateFingerprint continuationFingerprint = builder.Finish();
            return new ReplayEvidence(
                continuation.StateText,
                Format(continuationFingerprint),
                Format(remote));
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
            ExpectedContinuationStateMatched: true,
            ExpectedRemoteStateMatched: true,
            LocalCardRemovedFromHand: true,
            LocalPlayerIdentityStable: true,
            EnergyStateConsistent: true,
            TargetIdentityStable: true,
            RemotePublicStateUnchanged: true,
            EnemyStateMatchesExpectedTarget: true,
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
        string ContinuationFingerprint,
        string RemoteFingerprint,
        string MatchedLegacyDisagreementDecision,
        string RemoteMismatchDecision,
        string SemanticMismatchDecision,
        bool NormalNextActionAuthorized,
        string RemoteInsertionRejectedReason,
        string CancelledRetryRejectedReason);

    private sealed record ReplayEvidence(
        string ContinuationStateText,
        string ContinuationFingerprint,
        string RemoteFingerprint);
}
