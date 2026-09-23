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
                Profile = settings.Profile with
                {
                    BeamWidth = BeamWidth,
                    MaxExpandedNodes = MaxExpandedNodes,
                    SoftTimeBudgetMilliseconds = BudgetMilliseconds,
                },
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
            if (p0Result.BoundaryReason == SearchBoundaryReason.TimeLimit)
                throw new InvalidOperationException("P0 SP regression ended at TimeLimit.");
            if (p0Result.BestNode.Actions.Count == 0)
                throw new InvalidOperationException("P0 SP regression produced an empty route.");

            P0JointEvidence joint = VerifyContinuationAdmission(
                p0Result,
                combat,
                p0Root,
                battleDamage);

            P1Evidence p1 = VerifyP1ObjectiveRuntime(
                combat,
                settings,
                names,
                battleDamage,
                captured);

            var evidence = new
            {
                status = "PASS",
                pinnedTarget = "0.107.1",
                fixture,
                p0 = new
                {
                    spRegression = p0Search,
                    joint,
                    classifier = "covered_by_contract_suite",
                },
                p1,
            };
            string evidencePath = Path.Combine(outputDirectory, "p0-p1-pinned-evidence.json");
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(evidence, Json));

            Console.WriteLine("P0P1PinnedHarness PASS");
            Console.WriteLine($"evidence={evidencePath}");
            return 0;
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
            ContinuationTurn: cached.StartTurnNumber,
            ExactReuse: reused,
            ExactReason: exactReason,
            ReusedFromTurn: continuation?.ReusedFromTurn,
            MismatchRejected: !mismatchedReuse,
            MismatchReason: mismatchReason,
            LocalStateExact: true);
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

        return new(
            Adaptive: adaptive,
            MinimizeTeamLoss: minimize,
            AdaptiveFastBeatsSlowContract: true,
            AllPlayersAliveHardBoundaryContract: true,
            SelectedRoutesDiffer: !adaptive.Actions.SequenceEqual(
                minimize.Actions,
                StringComparer.Ordinal));
    }

    private static P1SearchEvidence RunP1(
        CombatRootSnapshot root,
        SolverDisplayNames names,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot captured,
        SolverSearchProfile profile,
        MultiplayerCombatObjectiveStrategy strategy)
    {
        SearchRequestWorkTotals totals = new();
        SearchPolicySnapshot policy = captured with
        {
            Profile = profile,
            RoutePolicy = SearchRoutePolicy.MultiplayerLocalCrossTurn,
            CurrentTurnOnly = false,
            UseMultiplayerTeamObjective = true,
            MultiplayerCombatObjectiveStrategy = strategy,
            FixedBudget = true,
            MaxDegreeOfParallelism = 1,
            BudgetOverrideMilliseconds = BudgetMilliseconds,
            UseNoveltyPortfolio = false,
            NoveltySearch = null,
            UseBeamWidthPortfolio = false,
            BeamWidthPortfolioWidths = null,
            Interaction = null,
            RequestWorkTotals = totals,
        };
        CombatBeamSolver solver = new(root, names, battleDamage, policy, searchProfile: profile);
        SolverResult result = solver.Solve();
        if (result.BoundaryReason == SearchBoundaryReason.TimeLimit)
            throw new InvalidOperationException($"P1 {strategy} search ended at TimeLimit.");
        SearchRequestWorkSnapshot work = totals.Snapshot();
        return new(
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

    internal sealed record P0SearchEvidence(
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
        int ContinuationTurn,
        bool ExactReuse,
        string ExactReason,
        int? ReusedFromTurn,
        bool MismatchRejected,
        string MismatchReason,
        bool LocalStateExact);

    internal sealed record P1SearchEvidence(
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
        P1SearchEvidence Adaptive,
        P1SearchEvidence MinimizeTeamLoss,
        bool AdaptiveFastBeatsSlowContract,
        bool AllPlayersAliveHardBoundaryContract,
        bool SelectedRoutesDiffer);
}
