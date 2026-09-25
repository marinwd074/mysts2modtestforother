using System.Text.Json;
using System.Text.Json.Serialization;
using CombatSolver;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using OfflineSearchHarness;
using U2DegenerateHarness;

namespace P0P1PinnedHarness;

internal static class Program
{
    private const int BeamWidth = 60;
    private const int MaxExpandedNodes = 120_000;
    private const int BudgetMilliseconds = 5_000;
    private const int P0FixedWorkNodeBudget = 1_200;
    private const int P1FixedWorkNodeBudget = 5_000;

    private static readonly string[] AddedCards =
    [
        "BONE_SHARDS",
        "DRAIN_POWER",
        "GRAVE_WARDEN",
        "REAVE",
        "DEVOUR_LIFE",
        "DEATH_MARCH",
        "PULL_FROM_BELOW",
        "SIC_EM",
        "PROWESS",
        "GOLD_AXE",
    ];

    private static readonly string[] AddedRelics =
    [
        "PARRYING_SHIELD",
        "GNARLED_HAMMER",
        "BOWLER_HAT",
        "TUNGSTEN_ROD",
        "REPTILE_TRINKET",
    ];

    private static readonly string[] AddedPotions =
    [
        "DISTILLED_CHAOS",
        "BLESSING_OF_THE_FORGE",
    ];

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

            // P0 must reproduce the unattended launcher's real settings semantics:
            // Medium preset first, then only the explicit P0 test overrides.
            SolverSettingsData p0Settings = SolverSettings.ApplyPerformancePreset(
                new SolverSettingsData
                {
                    PerformanceMigrationVersion = SolverSettings.CurrentPerformanceMigrationVersion,
                    PotionPolicy = SolverPotionPolicy.Smart,
                    SearchMaxDegreeOfParallelism = 1,
                },
                SolverPerformancePreset.Medium);
            SolverSettings.ApplyForTesting(p0Settings);

            Task enter = EnterP0CombatAsync();
            loop.RunUntilCompleted(enter, TimeSpan.FromSeconds(180), "P0 fixed scenario enter combat");
            CombatState combat = OfflineCombat.WaitForPlayableCombat(loop);
            Console.WriteLine(OfflineCombat.DescribeRoot(combat));

            P0FixtureEvidence fixture = CaptureFixture(combat);
            VerifyFixture(fixture);

            SolverSettingsSnapshot settings = SolverSettings.Capture();
            SolverDisplayNames names = SolverDisplayNames.Capture(combat);
            BattleDamageSnapshot battleDamage = BattleDamageTracker.Observe(combat);

            CombatRootSnapshot p0Root = CombatRootSnapshot.Capture(combat);
            SearchPolicySnapshot captured = SolverController.CaptureSearchPolicy(
                settings,
                combat,
                includeTurnSetup: false,
                theftPolicy: null);
            SearchPolicySnapshot p0Policy = captured with
            {
                RoutePolicy = SearchRoutePolicy.SinglePlayerFullRoute,
                CurrentTurnOnly = false,
                UseMultiplayerTeamObjective = false,
                FixedBudget = true,
                MaxDegreeOfParallelism = 1,
                BudgetOverrideMilliseconds = BudgetMilliseconds,
                Interaction = null,
            };

            SolverResult p0Result = CombatSearchCoordinator.Solve(
                p0Root,
                names,
                battleDamage,
                p0Policy,
                CancellationToken.None,
                progressCallback: null);
            P0SearchEvidence p0Search = CaptureSearch(p0Result);
            P0SearchEvidence p0SingleMemberTimed = RunP0SingleMemberTimedProbe(
                p0Root,
                names,
                battleDamage,
                p0Policy);
            E2ResumableSearchEvidence e2Resumable = VerifyE2ResumableSearch(
                p0Root,
                names,
                battleDamage,
                captured,
                settings);
            P0SearchEvidence p0FixedWork = e2Resumable.SingleParent;
            E3PortfolioEvidence e3Portfolio = VerifyE3FixedPortfolioScheduling(
                p0Root,
                names,
                battleDamage,
                captured,
                settings);

            P0JointEvidence joint;
            try
            {
                joint = VerifyContinuationAdmission(
                    p0Result,
                    combat,
                    p0Root,
                    battleDamage);
            }
            catch (Exception error)
            {
                joint = new(
                    Pass: false,
                    ContinuationTurn: null,
                    ExactReuse: false,
                    ExactReason: "not_run",
                    ReusedFromTurn: null,
                    MismatchRejected: false,
                    MismatchReason: "not_run",
                    LocalStateExact: false,
                    Error: $"{error.GetType().Name}: {error.Message}");
            }

            P1Evidence p1;
            try
            {
                p1 = VerifyP1ObjectiveRuntime(
                    combat,
                    settings,
                    names,
                    battleDamage,
                    captured);
            }
            catch (Exception error)
            {
                p1 = new(
                    Pass: false,
                    Adaptive: null,
                    MinimizeTeamLoss: null,
                    AdaptiveFixedWork: null,
                    MinimizeTeamLossFixedWork: null,
                    FixedWorkSemanticPass: false,
                    AdaptiveFastBeatsSlowContract: false,
                    AllPlayersAliveHardBoundaryContract: false,
                    SelectedRoutesDiffer: false,
                    Error: $"{error.GetType().Name}: {error.Message}");
            }

            bool overallPass = p0Search.Pass
                && joint.Pass
                && p1.Pass
                && e2Resumable.Pass
                && e3Portfolio.Pass;
            var evidence = new
            {
                status = overallPass ? "PASS" : "FAIL",
                pinnedTarget = "0.107.1",
                settings = new
                {
                    preset = SolverSettings.ResolvePerformancePreset(SolverSettings.Current).ToString(),
                    profileBeamWidth = settings.Profile.BeamWidth,
                    profileMaxExpandedNodes = settings.Profile.MaxExpandedNodes,
                    profileSoftTimeBudgetMilliseconds = settings.Profile.SoftTimeBudgetMilliseconds,
                    requestBudgetMilliseconds = p0Policy.BudgetOverrideMilliseconds,
                    p0FixedWorkNodeBudget = P0FixedWorkNodeBudget,
                    p1FixedWorkNodeBudget = P1FixedWorkNodeBudget,
                    p0Policy.MaxDegreeOfParallelism,
                    p0Policy.UseBeamWidthPortfolio,
                    p0Policy.UseNoveltyPortfolio,
                    p0Policy.StopAtAcceptableBattleHpLoss,
                    potionPolicy = p0Policy.PotionPolicy.ToString(),
                },
                fixture,
                p0 = new
                {
                    spRegression = p0Search,
                    singleMemberTimed = p0SingleMemberTimed,
                    fixedWork = p0FixedWork,
                    e2Resumable,
                    e3Portfolio,
                    joint,
                    classifier = "covered_by_contract_suite",
                },
                p1,
            };
            string evidencePath = Path.Combine(outputDirectory, "p0-p1-pinned-evidence.json");
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(evidence, Json));

            Console.WriteLine($"P0P1PinnedHarness {(overallPass ? "PASS" : "FAIL")}");
            Console.WriteLine($"evidence={evidencePath}");
            return overallPass ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"P0P1PinnedHarness FAIL: {error.GetType().Name}: {error.Message}");
            Console.Error.WriteLine(error.StackTrace);
            return 1;
        }
    }

    private static async Task EnterP0CombatAsync()
    {
        CharacterModel character = ResolveUnique(ModelDb.AllCharacters, "NECROBINDER", "character");
        EncounterModel encounter = ResolveUnique(ModelDb.AllEncounters, "PHROG_PARASITE_ELITE", "encounter");
        if (RunManager.Instance.IsInProgress)
            throw new InvalidOperationException("A run is already active.");

        UnlockState unlockState = SaveManager.Instance.GenerateUnlockStateFromProgress();
        RunState runState = RunState.CreateForNewRun(
            [Player.CreateForNewRun(character, unlockState, 1uL)],
            ModelDb.ActsByIndex is { }
                ? ActModel.GetDefaultList().Select(act => act.ToMutable()).ToList()
                : [],
            [],
            GameMode.Standard,
            10,
            "GENERATED-COMBAT-001");
        RunManager.Instance.SetUpNewSingleplayer(runState, shouldSave: false);
        await RunManager.Instance.FinalizeStartingRelics();
        RunManager.Instance.Launch();
        await RunManager.Instance.EnterAct(0, doTransition: false);

        Player player = LocalContext.GetMe(runState)
            ?? throw new InvalidOperationException("P0 fixture has no local player.");

        if (!player.Deck.Cards.Any(card => ModelMatches(card, "ASCENDERS_BANE")))
            await AddRunCardAsync(runState, player, "ASCENDERS_BANE");
        if (player.Deck.Cards.Count(card => ModelMatches(card, "ASCENDERS_BANE")) != 1)
            throw new InvalidOperationException("P0 fixture requires exactly one ASCENDERS_BANE.");

        foreach (string cardId in AddedCards)
            await AddRunCardAsync(runState, player, cardId);

        foreach (string relicId in AddedRelics)
        {
            RelicModel relic = ResolveUnique(ModelDb.AllRelics, relicId, "relic").ToMutable();
            player.AddRelicInternal(relic, silent: true);
        }

        foreach (PotionModel potion in player.PotionSlots.OfType<PotionModel>().ToArray())
            potion.Discard();
        if (player.PotionSlots.Count < AddedPotions.Length)
            player.SetMaxPotionCountInternal(AddedPotions.Length);
        for (int slot = 0; slot < AddedPotions.Length; slot++)
        {
            PotionModel potion = ResolveUnique(ModelDb.AllPotions, AddedPotions[slot], "potion").ToMutable();
            if (!player.AddPotionInternal(potion, slot, silent: false).success)
                throw new InvalidOperationException($"Could not add P0 potion {AddedPotions[slot]} to slot {slot}.");
        }

        await RunManager.Instance.EnterRoomDebug(
            RoomType.Elite,
            MapPointType.Elite,
            encounter.ToMutable());
    }

    private static async Task AddRunCardAsync(
        RunState runState,
        Player player,
        string cardId)
    {
        CardModel canonical = ResolveUnique(ModelDb.AllCards, cardId, "card");
        CardModel card = runState.CreateCard(canonical, player);
        var result = await CardPileCmd.Add(card, PileType.Deck);
        if (!result.success)
            throw new InvalidOperationException($"Could not add P0 card {cardId}.");
    }

    private static P0FixtureEvidence CaptureFixture(CombatState combat)
    {
        Player player = LocalContext.GetMe(combat)
            ?? throw new InvalidOperationException("P0 combat has no local player.");
        string[] deckIds = player.Deck.Cards.Select(card => card.Id.Entry).ToArray();
        string[] relicIds = player.Relics.Select(relic => relic.Id.Entry).ToArray();
        string[] potionIds = player.PotionSlots
            .Select(potion => potion?.Id.Entry ?? "-")
            .ToArray();
        return new(
            Character: player.Character.Id.Entry,
            Ascension: combat.RunState.AscensionLevel,
            Encounter: combat.Encounter?.Id.Entry ?? "-",
            RoomType: combat.Encounter?.RoomType.ToString() ?? "-",
            Seed: "GENERATED-COMBAT-001",
            DeckIds: deckIds,
            RelicIds: relicIds,
            PotionIds: potionIds);
    }

    private static void VerifyFixture(P0FixtureEvidence fixture)
    {
        Require(ModelMatches(fixture.Character, "NECROBINDER"), "P0 character mismatch.");
        Require(fixture.Ascension == 10, $"P0 ascension mismatch: {fixture.Ascension}.");
        Require(ModelMatches(fixture.Encounter, "PHROG_PARASITE_ELITE"), "P0 encounter mismatch.");
        foreach (string id in AddedCards.Append("ASCENDERS_BANE"))
            Require(fixture.DeckIds.Any(actual => ModelMatches(actual, id)), $"P0 deck missing {id}.");
        foreach (string id in AddedRelics)
            Require(fixture.RelicIds.Any(actual => ModelMatches(actual, id)), $"P0 relics missing {id}.");
        foreach (string id in AddedPotions)
            Require(fixture.PotionIds.Any(actual => ModelMatches(actual, id)), $"P0 potions missing {id}.");
    }

    private static P0JointEvidence VerifyContinuationAdmission(
        SolverResult result,
        CombatState combat,
        CombatRootSnapshot root,
        BattleDamageSnapshot battleDamage)
    {
        if (result.Continuations.Count == 0)
            throw new InvalidOperationException("P0 Joint admission test requires a real search continuation.");

        List<CachedContinuation> mutable = result.Continuations as List<CachedContinuation>
            ?? result.Continuations.ToList();
        CachedContinuation cached = mutable[0];
        StateFingerprint expectedRemote = new(0x504f5f4a4f494e54UL, 0x52455553455f3031UL);
        MultiplayerContinuationExpectation expectation = new(
            cached.ExpectedState.CombatIdentity,
            root.PlayerIdentity.NetId.ToString(),
            expectedRemote,
            MultiplayerScalingHooks: true,
            CardMultiplayerConstraint: "P0_PINNED",
            SourceWorldVersion: 10);
        mutable[0] = cached with { MultiplayerExpectation = expectation };
        if (!ReferenceEquals(mutable, result.Continuations))
        {
            typeof(SolverResult).GetProperty(nameof(SolverResult.Continuations))!
                .SetValue(result, mutable);
        }

        MultiplayerContinuationValidation exact = new(
            expectation.CombatIdentity,
            expectation.LocalNetId,
            expectation.RemotePublicFingerprint,
            expectation.MultiplayerScalingHooks,
            expectation.CardMultiplayerConstraint,
            CurrentWorldVersion: 12,
            MinimumWorldVersion: 11);

        int currentHp = LocalContext.GetMe(combat)!.Creature.CurrentHp;
        bool reused = result.TryCreateContinuation(
            cached.ExpectedState,
            cached.StartTurnNumber,
            currentHp,
            battleDamage,
            exact,
            out SolverResult? continuation,
            out string exactReason);
        Require(reused, $"P0 Joint reuse rejected: {exactReason}.");
        Require(continuation is { WasReused: true }, "P0 Joint reuse did not materialize a reused result.");
        Require(exactReason == "none", $"P0 Joint exact reuse reason changed: {exactReason}.");

        StateFingerprint mismatchFingerprint = new(
            expectedRemote.First ^ 0x1UL,
            expectedRemote.Second ^ 0x100UL);
        MultiplayerContinuationValidation mismatch = exact with
        {
            RemotePublicFingerprint = mismatchFingerprint,
            CurrentWorldVersion = 13,
            MinimumWorldVersion = 12,
        };
        bool mismatchedReuse = result.TryCreateContinuation(
            cached.ExpectedState,
            cached.StartTurnNumber,
            currentHp,
            battleDamage,
            mismatch,
            out SolverResult? rejected,
            out string mismatchReason);
        Require(!mismatchedReuse && rejected == null, "P0 Joint mismatch incorrectly reused the old route.");
        Require(
            mismatchReason == "remote_public_mismatch",
            $"P0 Joint mismatch reason changed: {mismatchReason}.");

        return new(
            Pass: true,
            ContinuationTurn: cached.StartTurnNumber,
            ExactReuse: reused,
            ExactReason: exactReason,
            ReusedFromTurn: continuation?.ReusedFromTurn,
            MismatchRejected: !mismatchedReuse,
            MismatchReason: mismatchReason,
            LocalStateExact: true,
            Error: null);
    }

    private static P0SearchEvidence RunP0SingleMemberTimedProbe(
        CombatRootSnapshot root,
        SolverDisplayNames names,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot p0Policy)
    {
        SearchPolicySnapshot policy = p0Policy with
        {
            UseBeamWidthPortfolio = false,
            BeamWidthPortfolioWidths = null,
            Interaction = null,
            RequestWorkTotals = null,
        };
        SolverResult result = CombatSearchCoordinator.Solve(
            root,
            names,
            battleDamage,
            policy,
            CancellationToken.None,
            progressCallback: null);
        return CaptureSearch(result);
    }

    private static E2ResumableSearchEvidence VerifyE2ResumableSearch(
        CombatRootSnapshot root,
        SolverDisplayNames names,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot captured,
        SolverSettingsSnapshot settings)
    {
        P0FixedWorkRun continuous = RunP0FixedWorkProbe(
            root, names, battleDamage, captured, settings, parentCommitSlice: null);
        P0FixedWorkRun singleParent = RunP0FixedWorkProbe(
            root, names, battleDamage, captured, settings, parentCommitSlice: 1);
        P0FixedWorkRun coarse = RunP0FixedWorkProbe(
            root, names, battleDamage, captured, settings, parentCommitSlice: 8);

        bool Equivalent(P0FixedWorkRun candidate)
            => candidate.Evidence.Pass
                && continuous.Actions.SequenceEqual(candidate.Actions, StringComparer.Ordinal)
                && continuous.Evidence.Boundary == candidate.Evidence.Boundary
                && continuous.Evidence.ProjectedBattleHpLost == candidate.Evidence.ProjectedBattleHpLost
                && continuous.Evidence.FinalHp == candidate.Evidence.FinalHp
                && continuous.Evidence.FinalEnemyHp == candidate.Evidence.FinalEnemyHp
                && continuous.Evidence.CombatEndedTurn == candidate.Evidence.CombatEndedTurn
                && continuous.Evidence.ContinuationCount == candidate.Evidence.ContinuationCount
                && continuous.Evidence.ExpandedNodes == candidate.Evidence.ExpandedNodes
                && continuous.Evidence.ChoiceBranchesEvaluated == candidate.Evidence.ChoiceBranchesEvaluated
                && continuous.TransitionCount == candidate.TransitionCount
                && continuous.CommittedParents == candidate.CommittedParents;

        bool singleEquivalent = Equivalent(singleParent) && singleParent.YieldCount > 0;
        bool coarseEquivalent = Equivalent(coarse) && coarse.YieldCount > 0;
        Require(
            singleEquivalent && coarseEquivalent,
            "E2 continuous and resumed fixed-work searches diverged in route, terminal state, work totals, or parent commits.");

        E2LifecycleEvidence lifecycle = VerifyE2SessionLifecycle(
            root,
            names,
            battleDamage,
            captured,
            settings);

        return new(
            Pass: singleEquivalent && coarseEquivalent && lifecycle.Pass,
            Continuous: continuous.Evidence,
            SingleParent: singleParent.Evidence,
            Coarse: coarse.Evidence,
            ContinuousActions: continuous.Actions,
            SingleParentActions: singleParent.Actions,
            CoarseActions: coarse.Actions,
            ContinuousTransitionCount: continuous.TransitionCount,
            SingleParentTransitionCount: singleParent.TransitionCount,
            CoarseTransitionCount: coarse.TransitionCount,
            ContinuousCommittedParents: continuous.CommittedParents,
            SingleParentCommittedParents: singleParent.CommittedParents,
            CoarseCommittedParents: coarse.CommittedParents,
            SingleParentYieldCount: singleParent.YieldCount,
            CoarseYieldCount: coarse.YieldCount,
            Lifecycle: lifecycle);
    }

    private static P0FixedWorkRun RunP0FixedWorkProbe(
        CombatRootSnapshot root,
        SolverDisplayNames names,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot captured,
        SolverSettingsSnapshot settings,
        int? parentCommitSlice)
    {
        SolverSearchProfile profile = E2FixedWorkProfile(settings);
        SearchRequestWorkTotals totals = new();
        SearchPolicySnapshot policy = E2FixedWorkPolicy(captured, profile, totals);
        CombatBeamSolver solver = new(
            root,
            names,
            battleDamage,
            policy,
            searchProfile: profile);
        using CombatBeamSolver.SearchMemberExecutionSession session = solver.CreateExecutionSession();
        SearchWorkAllowance allowance = parentCommitSlice is { } slice
            ? new SearchWorkAllowance(slice)
            : SearchWorkAllowance.Unlimited;
        int yieldCount = 0;
        SearchStepResult finalStep;
        while (true)
        {
            SearchStepResult step = session.Step(allowance, CancellationToken.None);
            if (step.Status == SearchStepStatus.Yielded)
            {
                yieldCount++;
                continue;
            }
            Require(
                step.Status == SearchStepStatus.Completed && session.Result != null,
                $"E2 fixed-work member did not complete cleanly: {step.Status}.");
            finalStep = step;
            break;
        }

        SolverResult result = session.Result!;
        SearchRequestWorkSnapshot work = totals.Snapshot();
        P0SearchEvidence evidence = CaptureSearch(result) with
        {
            ExpandedNodes = work.ExpandedNodes,
            ChoiceBranchesEvaluated = work.ChoiceBranchesEvaluated,
        };
        Require(
            evidence.Boundary != SearchBoundaryReason.TimeLimit.ToString(),
            "P0 fixed-work probe unexpectedly hit a wall-clock TimeLimit.");
        Require(
            evidence.ExpandedNodes <= P0FixedWorkNodeBudget,
            $"P0 fixed-work probe exceeded node budget: {evidence.ExpandedNodes}/{P0FixedWorkNodeBudget}.");
        return new(
            evidence,
            result.BestNode.Actions.Select(ActionToken).ToArray(),
            work.TransitionCount,
            finalStep.TotalCommittedParents,
            yieldCount);
    }

    private static E2LifecycleEvidence VerifyE2SessionLifecycle(
        CombatRootSnapshot root,
        SolverDisplayNames names,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot captured,
        SolverSettingsSnapshot settings)
    {
        CombatBeamSolver CreateProbeSolver()
        {
            SolverSearchProfile profile = E2FixedWorkProfile(settings);
            SearchPolicySnapshot policy = E2FixedWorkPolicy(
                captured,
                profile,
                new SearchRequestWorkTotals());
            return new CombatBeamSolver(
                root,
                names,
                battleDamage,
                policy,
                searchProfile: profile);
        }

        bool cancelReleased;
        int cancelCommitted;
        using (CombatBeamSolver.SearchMemberExecutionSession canceled =
               CreateProbeSolver().CreateExecutionSession())
        {
            SearchStepResult first = canceled.Step(
                SearchWorkAllowance.SingleParent,
                CancellationToken.None);
            Require(first.Status == SearchStepStatus.Yielded,
                "E2 cancellation probe did not reach a resumable safe point.");
            cancelCommitted = first.TotalCommittedParents;
            using CancellationTokenSource cts = new();
            cts.Cancel();
            SearchStepResult canceledStep = canceled.Step(
                SearchWorkAllowance.Unlimited,
                cts.Token);
            cancelReleased = canceledStep.Status == SearchStepStatus.Canceled
                && canceledStep.TotalCommittedParents == cancelCommitted
                && !canceled.HasLiveSimulatorsForTesting;
        }

        CombatBeamSolver.SearchMemberExecutionSession disposed =
            CreateProbeSolver().CreateExecutionSession();
        SearchStepResult disposeFirst = disposed.Step(
            SearchWorkAllowance.SingleParent,
            CancellationToken.None);
        Require(disposeFirst.Status == SearchStepStatus.Yielded,
            "E2 dispose probe did not reach a resumable safe point.");
        int disposeCommitted = disposeFirst.TotalCommittedParents;
        disposed.Dispose();
        bool disposeReleased = !disposed.HasLiveSimulatorsForTesting;
        bool disposeRejectsResume = false;
        try
        {
            _ = disposed.Step(SearchWorkAllowance.SingleParent, CancellationToken.None);
        }
        catch (ObjectDisposedException)
        {
            disposeRejectsResume = true;
        }

        bool pass = cancelReleased && disposeReleased && disposeRejectsResume;
        Require(pass, "E2 cancel/dispose lifecycle left resumable work or live simulators behind.");
        return new(
            Pass: pass,
            CancelCommittedParents: cancelCommitted,
            CancelReleasedSimulators: cancelReleased,
            DisposeCommittedParents: disposeCommitted,
            DisposeReleasedSimulators: disposeReleased,
            DisposeRejectsResume: disposeRejectsResume);
    }

    private static SolverSearchProfile E2FixedWorkProfile(SolverSettingsSnapshot settings)
        => settings.Profile with
        {
            BeamWidth = BeamWidth,
            MaxExpandedNodes = P0FixedWorkNodeBudget,
            SoftTimeBudgetMilliseconds = BudgetMilliseconds,
        };

    private static SearchPolicySnapshot E2FixedWorkPolicy(
        SearchPolicySnapshot captured,
        SolverSearchProfile profile,
        SearchRequestWorkTotals totals)
        => captured with
        {
            Profile = profile,
            RoutePolicy = SearchRoutePolicy.SinglePlayerFullRoute,
            CurrentTurnOnly = false,
            VerifyIncrementalSearch = true,
            FixedBudget = true,
            MaxDegreeOfParallelism = 1,
            BudgetOverrideMilliseconds = null,
            UseNoveltyPortfolio = false,
            NoveltySearch = null,
            UseBeamWidthPortfolio = false,
            BeamWidthPortfolioWidths = null,
            Interaction = null,
            RequestWorkTotals = totals,
        };

    private static E3PortfolioEvidence VerifyE3FixedPortfolioScheduling(
        CombatRootSnapshot root,
        SolverDisplayNames names,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot captured,
        SolverSettingsSnapshot settings)
    {
        SolverSearchProfile profile = settings.Profile with
        {
            BeamWidth = BeamWidth,
            // E3 only needs enough deterministic work to prove real interleaving and
            // serial/fixed semantic equivalence; keep it smaller than the P0 quality probe.
            MaxExpandedNodes = 512,
            SoftTimeBudgetMilliseconds = 120_000,
        };

        E3PortfolioRunEvidence Run(bool useFixedRoundRobin)
        {
            SearchPolicySnapshot policy = captured with
            {
                Profile = profile,
                RoutePolicy = SearchRoutePolicy.SinglePlayerFullRoute,
                CurrentTurnOnly = false,
                UseMultiplayerTeamObjective = false,
                VerifyIncrementalSearch = false,
                FixedBudget = true,
                MaxDegreeOfParallelism = 1,
                BudgetOverrideMilliseconds = null,
                UseNoveltyPortfolio = false,
                NoveltySearch = null,
                UseBeamWidthPortfolio = false,
                BeamWidthPortfolioWidths = null,
                UseE3FixedPortfolioScheduling = useFixedRoundRobin,
                Interaction = null,
                RequestWorkTotals = null,
                PortfolioTelemetry = null,
            };
            SolverResult result = CombatSearchCoordinator.Solve(
                root,
                names,
                battleDamage,
                policy,
                CancellationToken.None,
                progressCallback: null);
            BeamWidthPortfolioTelemetry telemetry = result.PortfolioTelemetry
                ?? throw new InvalidOperationException("E3 A/B missing request telemetry.");
            SearchEfficiencyMemberReport[] members = telemetry.SearchMembers.ToArray();
            SearchEfficiencyMemberReport[] potionMembers = members
                .Where(member => string.Equals(
                    member.Kind,
                    "potion_required",
                    StringComparison.Ordinal))
                .OrderBy(member => member.MemberId)
                .ToArray();
            return new(
                Pass: result.BoundaryReason != SearchBoundaryReason.TimeLimit
                    && result.BestNode.Actions.Count > 0,
                Actions: result.BestNode.Actions.Select(ActionToken).ToArray(),
                Boundary: result.BoundaryReason.ToString(),
                ProjectedBattleHpLost: result.ProjectedBattleHpLost,
                FinalHp: result.Snapshot.PlayerHp,
                FinalEnemyHp: result.Snapshot.EnemyHp,
                CombatEndedTurn: result.CombatEndedTurn,
                ExplicitPotionCount: result.ExplicitPotionCount,
                SearchMemberExpanded: members.Sum(member => member.ExpandedNodes),
                SearchMemberTransitions: members.Sum(member => member.TransitionCount),
                PotionRequiredMembers: potionMembers.Length,
                PotionRequiredTransitions: potionMembers
                    .Select(member => member.TransitionCount)
                    .ToArray(),
                PotionRequiredStartMilliseconds: potionMembers
                    .Select(member => telemetry.ToRequestMilliseconds(member.StartedTicks))
                    .ToArray(),
                PotionRequiredFirstWorkMilliseconds: potionMembers
                    .Select(member => member.FirstWorkTicks is { } firstWork
                        ? telemetry.ToRequestMilliseconds(firstWork)
                        : double.PositiveInfinity)
                    .ToArray(),
                PotionRequiredCompletedMilliseconds: potionMembers
                    .Select(member => member.CompletedTicks is { } completed
                        ? telemetry.ToRequestMilliseconds(completed)
                        : double.PositiveInfinity)
                    .ToArray());
        }

        E3PortfolioRunEvidence serial = Run(useFixedRoundRobin: false);
        E3PortfolioRunEvidence fixedRoundRobin = Run(useFixedRoundRobin: true);
        bool sameQuality = serial.Actions.SequenceEqual(
                fixedRoundRobin.Actions,
                StringComparer.Ordinal)
            && serial.Boundary == fixedRoundRobin.Boundary
            && serial.ProjectedBattleHpLost == fixedRoundRobin.ProjectedBattleHpLost
            && serial.FinalHp == fixedRoundRobin.FinalHp
            && serial.FinalEnemyHp == fixedRoundRobin.FinalEnemyHp
            && serial.CombatEndedTurn == fixedRoundRobin.CombatEndedTurn
            && serial.ExplicitPotionCount == fixedRoundRobin.ExplicitPotionCount;
        bool interleaved = fixedRoundRobin.PotionRequiredMembers >= 2
            && fixedRoundRobin.PotionRequiredTransitions[0] > 0
            && fixedRoundRobin.PotionRequiredTransitions[1] > 0
            && fixedRoundRobin.PotionRequiredFirstWorkMilliseconds[1]
                < fixedRoundRobin.PotionRequiredCompletedMilliseconds[0];
        Require(
            serial.Pass && fixedRoundRobin.Pass && sameQuality && interleaved,
            "E3 fixed round-robin diverged from serial Smart quality or failed to give a later potion member real work.");

        return new(
            Pass: serial.Pass && fixedRoundRobin.Pass && sameQuality && interleaved,
            Serial: serial,
            FixedRoundRobin: fixedRoundRobin,
            SameQuality: sameQuality,
            LaterMemberReceivedWork: interleaved);
    }

    private static P1Evidence VerifyP1ObjectiveRuntime(
        CombatState combat,
        SolverSettingsSnapshot settings,
        SolverDisplayNames names,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot captured)
    {
        SolverSearchProfile profile = settings.Profile with
        {
            BeamWidth = BeamWidth,
            MaxExpandedNodes = MaxExpandedNodes,
            SoftTimeBudgetMilliseconds = BudgetMilliseconds,
        };

        P1SearchEvidence adaptive = RunP1(
            CombatRootSnapshot.Capture(combat),
            names,
            battleDamage,
            captured,
            profile,
            MultiplayerCombatObjectiveStrategy.AdaptiveLethalTempo);
        P1SearchEvidence minimize = RunP1(
            CombatRootSnapshot.Capture(combat),
            names,
            battleDamage,
            captured,
            profile,
            MultiplayerCombatObjectiveStrategy.MinimizeTeamLoss);

        SolverSearchProfile fixedWorkProfile = profile with
        {
            MaxExpandedNodes = P1FixedWorkNodeBudget,
        };
        P1SearchEvidence adaptiveFixedWork = RunP1(
            CombatRootSnapshot.Capture(combat),
            names,
            battleDamage,
            captured,
            fixedWorkProfile,
            MultiplayerCombatObjectiveStrategy.AdaptiveLethalTempo,
            deterministicFixedWork: true);
        P1SearchEvidence minimizeFixedWork = RunP1(
            CombatRootSnapshot.Capture(combat),
            names,
            battleDamage,
            captured,
            fixedWorkProfile,
            MultiplayerCombatObjectiveStrategy.MinimizeTeamLoss,
            deterministicFixedWork: true);
        bool fixedWorkSemanticPass = adaptiveFixedWork.Pass && minimizeFixedWork.Pass;

        MultiplayerCombatObjectiveRank fast = MultiplayerCombatObjectiveMath.BuildRank(
            MultiplayerCombatObjectiveStrategy.AdaptiveLethalTempo,
            completeVictory: true,
            allPlayersAlive: true,
            teamLossRatio: 0.12d,
            worstPlayerLossRatio: 0.10d,
            enemyDurabilityRatio: 0d,
            terminalTempoReferenceEnemyDurabilityRatio: 0.08d,
            combatEndedTurn: 3,
            startTurnNumber: 1);
        MultiplayerCombatObjectiveRank slow = MultiplayerCombatObjectiveMath.BuildRank(
            MultiplayerCombatObjectiveStrategy.AdaptiveLethalTempo,
            completeVictory: true,
            allPlayersAlive: true,
            teamLossRatio: 0.08d,
            worstPlayerLossRatio: 0.10d,
            enemyDurabilityRatio: 0d,
            terminalTempoReferenceEnemyDurabilityRatio: 0.08d,
            combatEndedTurn: 5,
            startTurnNumber: 1);
        Require(
            MultiplayerCombatObjectiveMath.Compare(fast, slow) < 0,
            "P1 adaptive terminal objective no longer prefers the documented fast route.");

        MultiplayerCombatObjectiveRank alive = MultiplayerCombatObjectiveMath.BuildRank(
            MultiplayerCombatObjectiveStrategy.AdaptiveLethalTempo,
            completeVictory: false,
            allPlayersAlive: true,
            teamLossRatio: 0.04d,
            worstPlayerLossRatio: 0.05d,
            enemyDurabilityRatio: 0.30d,
            terminalTempoReferenceEnemyDurabilityRatio: 0.30d,
            combatEndedTurn: null,
            startTurnNumber: 1);
        MultiplayerCombatObjectiveRank dead = MultiplayerCombatObjectiveMath.BuildRank(
            MultiplayerCombatObjectiveStrategy.AdaptiveLethalTempo,
            completeVictory: false,
            allPlayersAlive: false,
            teamLossRatio: 0.01d,
            worstPlayerLossRatio: 0.01d,
            enemyDurabilityRatio: 0.05d,
            terminalTempoReferenceEnemyDurabilityRatio: 0.30d,
            combatEndedTurn: null,
            startTurnNumber: 1);
        Require(
            MultiplayerCombatObjectiveMath.Compare(alive, dead) < 0,
            "P1 all-player survival hard boundary regressed.");

        bool routesDiffer = !adaptive.Actions.SequenceEqual(
            minimize.Actions,
            StringComparer.Ordinal);
        return new(
            Pass: adaptive.Pass
                && minimize.Pass
                && MultiplayerCombatObjectiveMath.Compare(fast, slow) < 0
                && MultiplayerCombatObjectiveMath.Compare(alive, dead) < 0,
            Adaptive: adaptive,
            MinimizeTeamLoss: minimize,
            AdaptiveFixedWork: adaptiveFixedWork,
            MinimizeTeamLossFixedWork: minimizeFixedWork,
            FixedWorkSemanticPass: fixedWorkSemanticPass,
            AdaptiveFastBeatsSlowContract: true,
            AllPlayersAliveHardBoundaryContract: true,
            SelectedRoutesDiffer: routesDiffer,
            Error: null);
    }

    private static P1SearchEvidence RunP1(
        CombatRootSnapshot root,
        SolverDisplayNames names,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot captured,
        SolverSearchProfile profile,
        MultiplayerCombatObjectiveStrategy strategy,
        bool deterministicFixedWork = false)
    {
        SearchRequestWorkTotals totals = new();
        SearchPolicySnapshot policy = captured with
        {
            Profile = profile,
            RoutePolicy = SearchRoutePolicy.MultiplayerLocalCrossTurn,
            CurrentTurnOnly = false,
            UseMultiplayerTeamObjective = true,
            MultiplayerCombatObjectiveStrategy = strategy,
            VerifyIncrementalSearch = deterministicFixedWork,
            FixedBudget = true,
            MaxDegreeOfParallelism = 1,
            BudgetOverrideMilliseconds = deterministicFixedWork ? null : BudgetMilliseconds,
            UseNoveltyPortfolio = false,
            NoveltySearch = null,
            UseBeamWidthPortfolio = false,
            BeamWidthPortfolioWidths = null,
            Interaction = null,
            RequestWorkTotals = totals,
        };
        CombatBeamSolver solver = new(root, names, battleDamage, policy, searchProfile: profile);
        SolverResult result = solver.Solve();
        SearchRequestWorkSnapshot work = totals.Snapshot();
        bool pass = result.BoundaryReason != SearchBoundaryReason.TimeLimit
            && result.BestNode.Actions.Count > 0
            && (!deterministicFixedWork
                || result.Snapshot.EnemyHp == 0 && result.Snapshot.AllPlayersAlive);
        return new(
            Pass: pass,
            Strategy: strategy.ToString(),
            FirstAction: result.BestNode.Actions.FirstOrDefault() is { } first
                ? ActionToken(first)
                : "<none>",
            Actions: result.BestNode.Actions.Select(ActionToken).ToArray(),
            Boundary: result.BoundaryReason.ToString(),
            ProjectedBattleHpLost: result.ProjectedBattleHpLost,
            TeamLossRatio: result.Snapshot.TeamLossRatio,
            WorstPlayerLossRatio: result.Snapshot.WorstPlayerLossRatio,
            AllPlayersAlive: result.Snapshot.AllPlayersAlive,
            FinalHp: result.Snapshot.PlayerHp,
            FinalEnemyHp: result.Snapshot.EnemyHp,
            CombatEndedTurn: result.CombatEndedTurn,
            ExpandedNodes: work.ExpandedNodes,
            TransitionCount: work.TransitionCount);
    }

    private static P0SearchEvidence CaptureSearch(SolverResult result)
        => new(
            Pass: result.BoundaryReason != SearchBoundaryReason.TimeLimit
                && result.BestNode.Actions.Count > 0,
            FirstAction: result.BestNode.Actions.FirstOrDefault() is { } first
                ? ActionToken(first)
                : "<none>",
            ActionCount: result.BestNode.Actions.Count,
            Boundary: result.BoundaryReason.ToString(),
            ProjectedBattleHpLost: result.ProjectedBattleHpLost,
            FinalHp: result.Snapshot.PlayerHp,
            FinalEnemyHp: result.Snapshot.EnemyHp,
            CombatEndedTurn: result.CombatEndedTurn,
            ExpandedNodes: result.TotalExpandedNodes,
            ChoiceBranchesEvaluated: result.TotalChoiceBranchesEvaluated,
            ContinuationCount: result.Continuations.Count,
            PortfolioMembers: result.PortfolioTelemetry?.Members.Count ?? 0);

    private static string ActionToken(PlanAction action)
        => $"{action.Turn}:{action.Kind}:{action.CardId ?? action.PotionId ?? "-"}"
            + $":target={action.TargetCombatId?.ToString() ?? "-"}";

    private static T ResolveUnique<T>(
        IEnumerable<T> candidates,
        string input,
        string kind)
        where T : AbstractModel
    {
        T[] matches = candidates
            .Where(candidate => ModelMatches(candidate.Id.Entry, input)
                || ModelMatches(candidate.Id.ToString(), input)
                || ModelMatches(candidate.GetType().Name, input))
            .DistinctBy(candidate => candidate.Id)
            .Take(2)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"Could not find {kind} {input}."),
            _ => throw new InvalidOperationException($"{kind} {input} is ambiguous."),
        };
    }

    private static bool ModelMatches(AbstractModel model, string input)
        => ModelMatches(model.Id.Entry, input)
            || ModelMatches(model.Id.ToString(), input)
            || ModelMatches(model.GetType().Name, input);

    private static bool ModelMatches(string actual, string expected)
        => actual.Equals(expected, StringComparison.OrdinalIgnoreCase)
            || actual.EndsWith("." + expected, StringComparison.OrdinalIgnoreCase);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static string ParseOutput(string[] args)
    {
        string output = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../.local/p0-p1-pinned"));
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

    internal sealed record P0FixtureEvidence(
        string Character,
        int Ascension,
        string Encounter,
        string RoomType,
        string Seed,
        string[] DeckIds,
        string[] RelicIds,
        string[] PotionIds);

    internal sealed record P0FixedWorkRun(
        P0SearchEvidence Evidence,
        string[] Actions,
        long TransitionCount,
        int CommittedParents,
        int YieldCount);

    internal sealed record E2LifecycleEvidence(
        bool Pass,
        int CancelCommittedParents,
        bool CancelReleasedSimulators,
        int DisposeCommittedParents,
        bool DisposeReleasedSimulators,
        bool DisposeRejectsResume);

    internal sealed record E2ResumableSearchEvidence(
        bool Pass,
        P0SearchEvidence Continuous,
        P0SearchEvidence SingleParent,
        P0SearchEvidence Coarse,
        string[] ContinuousActions,
        string[] SingleParentActions,
        string[] CoarseActions,
        long ContinuousTransitionCount,
        long SingleParentTransitionCount,
        long CoarseTransitionCount,
        int ContinuousCommittedParents,
        int SingleParentCommittedParents,
        int CoarseCommittedParents,
        int SingleParentYieldCount,
        int CoarseYieldCount,
        E2LifecycleEvidence Lifecycle);

    internal sealed record P0SearchEvidence(
        bool Pass,
        string FirstAction,
        int ActionCount,
        string Boundary,
        int ProjectedBattleHpLost,
        int FinalHp,
        int FinalEnemyHp,
        int? CombatEndedTurn,
        long ExpandedNodes,
        long ChoiceBranchesEvaluated,
        int ContinuationCount,
        int PortfolioMembers);

    internal sealed record P0JointEvidence(
        bool Pass,
        int? ContinuationTurn,
        bool ExactReuse,
        string ExactReason,
        int? ReusedFromTurn,
        bool MismatchRejected,
        string MismatchReason,
        bool LocalStateExact,
        string? Error);

    internal sealed record E3PortfolioRunEvidence(
        bool Pass,
        string[] Actions,
        string Boundary,
        int ProjectedBattleHpLost,
        int FinalHp,
        int FinalEnemyHp,
        int? CombatEndedTurn,
        int ExplicitPotionCount,
        long SearchMemberExpanded,
        long SearchMemberTransitions,
        int PotionRequiredMembers,
        long[] PotionRequiredTransitions,
        double[] PotionRequiredStartMilliseconds,
        double[] PotionRequiredFirstWorkMilliseconds,
        double[] PotionRequiredCompletedMilliseconds);

    internal sealed record E3PortfolioEvidence(
        bool Pass,
        E3PortfolioRunEvidence Serial,
        E3PortfolioRunEvidence FixedRoundRobin,
        bool SameQuality,
        bool LaterMemberReceivedWork);

    internal sealed record P1SearchEvidence(
        bool Pass,
        string Strategy,
        string FirstAction,
        string[] Actions,
        string Boundary,
        int ProjectedBattleHpLost,
        double TeamLossRatio,
        double WorstPlayerLossRatio,
        bool AllPlayersAlive,
        int FinalHp,
        int FinalEnemyHp,
        int? CombatEndedTurn,
        long ExpandedNodes,
        long TransitionCount);

    internal sealed record P1Evidence(
        bool Pass,
        P1SearchEvidence? Adaptive,
        P1SearchEvidence? MinimizeTeamLoss,
        P1SearchEvidence? AdaptiveFixedWork,
        P1SearchEvidence? MinimizeTeamLossFixedWork,
        bool FixedWorkSemanticPass,
        bool AdaptiveFastBeatsSlowContract,
        bool AllPlayersAliveHardBoundaryContract,
        bool SelectedRoutesDiffer,
        string? Error);
}
