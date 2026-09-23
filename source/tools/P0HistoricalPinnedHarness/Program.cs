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

namespace P0HistoricalPinnedHarness;

internal static class Program
{
    private const int BootstrapBeamWidth = 60;
    private const int BootstrapMaxExpandedNodes = 120_000;
    private const int BudgetMilliseconds = 5_000;
    private const int FixedWorkNodeBudget = 1_200;

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
                BootstrapBeamWidth,
                BootstrapMaxExpandedNodes,
                BudgetMilliseconds);
            Console.WriteLine($"search_patches={patchCount}");

            // Match P0's documented unattended semantics: Medium preset, Smart potions,
            // DOP=1, normal production NoGC + acceptable-loss defaults.
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
            loop.RunUntilCompleted(enter, TimeSpan.FromSeconds(180), "historical P0 enter combat");
            CombatState combat = OfflineCombat.WaitForPlayableCombat(loop);
            Console.WriteLine(OfflineCombat.DescribeRoot(combat));

            FixtureEvidence fixture = CaptureFixture(combat);
            VerifyFixture(fixture);

            SolverSettingsSnapshot settings = SolverSettings.Capture();
            SolverDisplayNames names = SolverDisplayNames.Capture(combat);
            BattleDamageSnapshot battleDamage = BattleDamageTracker.Observe(combat);
            CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);

            SearchPolicySnapshot captured = SolverController.CaptureSearchPolicy(
                settings,
                combat,
                includeTurnSetup: false,
                theftPolicy: null);
            SearchPolicySnapshot policy = captured with
            {
                RoutePolicy = SearchRoutePolicy.SinglePlayerFullRoute,
                CurrentTurnOnly = false,
                FixedBudget = true,
                MaxDegreeOfParallelism = 1,
                BudgetOverrideMilliseconds = BudgetMilliseconds,
                Interaction = null,
            };

            SolverResult result = CombatSearchCoordinator.Solve(
                root,
                names,
                battleDamage,
                policy,
                CancellationToken.None,
                progressCallback: null);

            SearchPolicySnapshot singleMemberTimedPolicy = policy with
            {
                UseBeamWidthPortfolio = false,
                BeamWidthPortfolioWidths = null,
                Interaction = null,
                RequestWorkTotals = null,
            };
            SolverResult singleMemberTimedResult = CombatSearchCoordinator.Solve(
                root,
                names,
                battleDamage,
                singleMemberTimedPolicy,
                CancellationToken.None,
                progressCallback: null);

            SolverSearchProfile fixedWorkProfile = settings.Profile with
            {
                BeamWidth = BootstrapBeamWidth,
                MaxExpandedNodes = FixedWorkNodeBudget,
                SoftTimeBudgetMilliseconds = BudgetMilliseconds,
            };
            SearchPolicySnapshot fixedWorkPolicy = captured with
            {
                Profile = fixedWorkProfile,
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
                RequestWorkTotals = new SearchRequestWorkTotals(),
            };
            SolverResult fixedWorkResult = new CombatBeamSolver(
                root,
                names,
                battleDamage,
                fixedWorkPolicy,
                searchProfile: fixedWorkProfile).Solve();
            bool fixedWorkPass = fixedWorkResult.BoundaryReason != SearchBoundaryReason.TimeLimit
                && fixedWorkResult.BestNode.Actions.Count > 0;
            Require(fixedWorkPass, "Historical P0 fixed-work probe produced no comparable route.");
            Require(
                fixedWorkResult.ExpandedNodes <= FixedWorkNodeBudget,
                $"Historical P0 fixed-work probe exceeded node budget: {fixedWorkResult.ExpandedNodes}/{FixedWorkNodeBudget}.");

            bool pass = result.BoundaryReason != SearchBoundaryReason.TimeLimit
                && result.BestNode.Actions.Count > 0;

            var evidence = new
            {
                status = pass ? "PASS" : "INVALID_TIME_BOUNDARY",
                pinnedTarget = "0.107.1",
                fixture,
                settings = new
                {
                    preset = SolverSettings.ResolvePerformancePreset(SolverSettings.Current).ToString(),
                    profileBeamWidth = settings.Profile.BeamWidth,
                    profileMaxExpandedNodes = settings.Profile.MaxExpandedNodes,
                    profileSoftTimeBudgetMilliseconds = settings.Profile.SoftTimeBudgetMilliseconds,
                    requestBudgetMilliseconds = policy.BudgetOverrideMilliseconds,
                    fixedWorkNodeBudget = FixedWorkNodeBudget,
                    policy.MaxDegreeOfParallelism,
                    policy.UseBeamWidthPortfolio,
                    policy.UseNoveltyPortfolio,
                    policy.StopAtAcceptableBattleHpLoss,
                    settings.EnableNoGcRegion,
                    potionPolicy = policy.PotionPolicy.ToString(),
                },
                search = new
                {
                    pass,
                    firstAction = result.BestNode.Actions.FirstOrDefault() is { } first
                        ? ActionToken(first)
                        : "<none>",
                    actionCount = result.BestNode.Actions.Count,
                    boundary = result.BoundaryReason.ToString(),
                    projectedBattleHpLost = result.ProjectedBattleHpLost,
                    finalHp = result.Snapshot.PlayerHp,
                    finalEnemyHp = result.Snapshot.EnemyHp,
                    combatEndedTurn = result.CombatEndedTurn,
                    expandedNodes = result.TotalExpandedNodes,
                    choiceBranchesEvaluated = result.TotalChoiceBranchesEvaluated,
                    continuationCount = result.Continuations.Count,
                    portfolioMembers = result.PortfolioTelemetry?.Members.Count ?? 0,
                },
                singleMemberTimed = new
                {
                    pass = singleMemberTimedResult.BoundaryReason != SearchBoundaryReason.TimeLimit
                        && singleMemberTimedResult.BestNode.Actions.Count > 0,
                    firstAction = singleMemberTimedResult.BestNode.Actions.FirstOrDefault() is { } singleMemberFirst
                        ? ActionToken(singleMemberFirst)
                        : "<none>",
                    actionCount = singleMemberTimedResult.BestNode.Actions.Count,
                    boundary = singleMemberTimedResult.BoundaryReason.ToString(),
                    projectedBattleHpLost = singleMemberTimedResult.ProjectedBattleHpLost,
                    finalHp = singleMemberTimedResult.Snapshot.PlayerHp,
                    finalEnemyHp = singleMemberTimedResult.Snapshot.EnemyHp,
                    combatEndedTurn = singleMemberTimedResult.CombatEndedTurn,
                    expandedNodes = singleMemberTimedResult.TotalExpandedNodes,
                    choiceBranchesEvaluated = singleMemberTimedResult.TotalChoiceBranchesEvaluated,
                    continuationCount = singleMemberTimedResult.Continuations.Count,
                    portfolioMembers = singleMemberTimedResult.PortfolioTelemetry?.Members.Count ?? 0,
                },
                fixedWork = new
                {
                    pass = fixedWorkPass,
                    nodeBudget = FixedWorkNodeBudget,
                    firstAction = fixedWorkResult.BestNode.Actions.FirstOrDefault() is { } fixedWorkFirst
                        ? ActionToken(fixedWorkFirst)
                        : "<none>",
                    actionCount = fixedWorkResult.BestNode.Actions.Count,
                    boundary = fixedWorkResult.BoundaryReason.ToString(),
                    projectedBattleHpLost = fixedWorkResult.ProjectedBattleHpLost,
                    finalHp = fixedWorkResult.Snapshot.PlayerHp,
                    finalEnemyHp = fixedWorkResult.Snapshot.EnemyHp,
                    combatEndedTurn = fixedWorkResult.CombatEndedTurn,
                    expandedNodes = fixedWorkResult.ExpandedNodes,
                    choiceBranchesEvaluated = fixedWorkResult.ChoiceBranchesEvaluated,
                    continuationCount = fixedWorkResult.Continuations.Count,
                },
            };

            string path = Path.Combine(outputDirectory, "p0-historical-pinned-evidence.json");
            File.WriteAllText(path, JsonSerializer.Serialize(evidence, Json));
            Console.WriteLine($"P0HistoricalPinnedHarness {(pass ? "PASS" : "INVALID_TIME_BOUNDARY")}");
            Console.WriteLine($"evidence={path}");
            // TimeLimit is an invalid sample, not an infrastructure failure. Always return 0
            // after a successful run so the A/B classifier can decide the meaning.
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"P0HistoricalPinnedHarness FAIL: {error.GetType().Name}: {error.Message}");
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

    private static async Task AddRunCardAsync(RunState runState, Player player, string cardId)
    {
        CardModel canonical = ResolveUnique(ModelDb.AllCards, cardId, "card");
        CardModel card = runState.CreateCard(canonical, player);
        var result = await CardPileCmd.Add(card, PileType.Deck);
        if (!result.success)
            throw new InvalidOperationException($"Could not add P0 card {cardId}.");
    }

    private static FixtureEvidence CaptureFixture(CombatState combat)
    {
        Player player = LocalContext.GetMe(combat)
            ?? throw new InvalidOperationException("P0 combat has no local player.");
        return new(
            Character: player.Character.Id.Entry,
            Ascension: combat.RunState.AscensionLevel,
            Encounter: combat.Encounter?.Id.Entry ?? "-",
            Seed: "GENERATED-COMBAT-001",
            DeckIds: player.Deck.Cards.Select(card => card.Id.Entry).ToArray(),
            RelicIds: player.Relics.Select(relic => relic.Id.Entry).ToArray(),
            PotionIds: player.PotionSlots.Select(potion => potion?.Id.Entry ?? "-").ToArray());
    }

    private static void VerifyFixture(FixtureEvidence fixture)
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

    private static string ActionToken(PlanAction action)
        => $"{action.Turn}:{action.Kind}:{action.CardId ?? action.PotionId ?? "-"}"
            + $":target={action.TargetCombatId?.ToString() ?? "-"}";

    private static T ResolveUnique<T>(IEnumerable<T> candidates, string input, string kind)
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
            "../../../../../.local/p0-historical-pinned"));
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

    internal sealed record FixtureEvidence(
        string Character,
        int Ascension,
        string Encounter,
        string Seed,
        string[] DeckIds,
        string[] RelicIds,
        string[] PotionIds);
}
