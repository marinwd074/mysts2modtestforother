using System.Text.Json;
using CombatSolver;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using OfflineSearchHarness;
using U2DegenerateHarness;

namespace E0PinnedHarness;

internal static class Program
{
    private const int BeamWidth = 60;
    private const int MaxExpandedNodes = 20_000;
    private const int BudgetMilliseconds = 5_000;

    private static int Main(string[] args)
    {
        string scenario = Value(args, "--scenario") ?? "simple";
        string output = Value(args, "--out") ?? ".";
        Directory.CreateDirectory(output);
        try
        {
            HarnessLog.Language = "eng";
            MainLoopContext loop = new();
            SynchronizationContext.SetSynchronizationContext(loop);
            GameBootstrap.ApplyGodotBypasses();
            GameBootstrap.SkipGodotNodeStaticConstructors();
            Console.WriteLine(GameBootstrap.InitializeStaticState());
            _ = U2Runtime.Initialize(
                Path.Combine(output, "logs"),
                BeamWidth,
                MaxExpandedNodes,
                BudgetMilliseconds);

            SolverSettingsData settingsData = SolverSettings.ApplyPerformancePreset(
                new SolverSettingsData
                {
                    PerformanceMigrationVersion = SolverSettings.CurrentPerformanceMigrationVersion,
                    PotionPolicy = SolverPotionPolicy.Smart,
                    SearchMaxDegreeOfParallelism = 1,
                },
                SolverPerformancePreset.Medium);
            SolverSettings.ApplyForTesting(settingsData);

            Task enter = EnterCombatAsync(scenario);
            loop.RunUntilCompleted(enter, TimeSpan.FromSeconds(180), $"E0 {scenario} enter combat");
            CombatState combat = OfflineCombat.WaitForPlayableCombat(loop);
            Player local = LocalContext.GetMe(combat)
                ?? throw new InvalidOperationException("E0 fixture has no local player.");

            SolverSettingsSnapshot settings = SolverSettings.Capture();
            SolverDisplayNames names = SolverDisplayNames.Capture(combat);
            BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
            CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
            SearchPolicySnapshot captured = SolverController.CaptureSearchPolicy(
                settings,
                combat,
                includeTurnSetup: false,
                theftPolicy: null);
            SolverSearchProfile profile = settings.Profile with
            {
                BeamWidth = BeamWidth,
                MaxExpandedNodes = MaxExpandedNodes,
                SoftTimeBudgetMilliseconds = BudgetMilliseconds,
            };
            bool teammate = string.Equals(scenario, "teammate", StringComparison.Ordinal);
            SearchPolicySnapshot policy = captured with
            {
                Profile = profile,
                RoutePolicy = teammate
                    ? SearchRoutePolicy.MultiplayerLocalCrossTurn
                    : SearchRoutePolicy.SinglePlayerFullRoute,
                CurrentTurnOnly = false,
                UseMultiplayerTeamObjective = teammate,
                UseMultiplayerTeammateForecast = teammate,
                UseMultiplayerScenarioReevaluation = teammate,
                UseNoveltyPortfolio = false,
                UseBeamWidthPortfolio = true,
                BeamWidthPortfolioWidths = null,
                FixedBudget = true,
                MaxDegreeOfParallelism = 1,
                BudgetOverrideMilliseconds = BudgetMilliseconds,
                Interaction = null,
            };

            if (teammate && (combat.Players.Count != 2 || root.PlayerCount != 2))
                throw new InvalidOperationException(
                    $"Detached teammate fixture did not produce two players: combat={combat.Players.Count} root={root.PlayerCount}.");

            string[] rootHand = local.PlayerCombatState!.Hand.Cards.Select(card => card.Id.Entry).ToArray();
            SolverResult result = CombatSearchCoordinator.Solve(
                root,
                names,
                damage,
                policy,
                CancellationToken.None,
                progressCallback: null);
            Evidence evidence = CaptureEvidence(scenario, combat, rootHand, result);

            if (string.Equals(scenario, "draw_energy", StringComparison.Ordinal)
                && (!rootHand.Contains("OFFERING", StringComparer.Ordinal)
                    || !result.BestNode.Actions.Any(action =>
                        string.Equals(action.CardId, "OFFERING", StringComparison.Ordinal))))
            {
                throw new InvalidOperationException(
                    "Draw/energy fixture did not exercise OFFERING in the selected route.");
            }
            if (teammate
                && !evidence.Phases.Any(phase =>
                    string.Equals(phase.Phase, "shadow", StringComparison.Ordinal)
                    && phase.CallCount > 0))
            {
                throw new InvalidOperationException(
                    "Detached teammate fixture produced no Shadow teammate work.");
            }

            string path = Path.Combine(output, $"e0-{scenario}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(
                evidence,
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"E0PinnedHarness PASS scenario={scenario} evidence={path}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                $"E0PinnedHarness FAIL scenario={scenario}: {error.GetType().Name}: {error.Message}");
            Console.Error.WriteLine(error.StackTrace);
            return 1;
        }
    }

    private static async Task EnterCombatAsync(string scenario)
    {
        bool teammate = string.Equals(scenario, "teammate", StringComparison.Ordinal);
        string encounterId = string.Equals(scenario, "simple", StringComparison.Ordinal)
            ? "FUZZY_WURM_CRAWLER_WEAK"
            : "MAWLER_NORMAL";
        CharacterModel ironclad = ResolveUnique(ModelDb.AllCharacters, "IRONCLAD", "character");
        EncounterModel encounter = ResolveUnique(ModelDb.AllEncounters, encounterId, "encounter");
        UnlockState unlockState = SaveManager.Instance.GenerateUnlockStateFromProgress();
        Player local = Player.CreateForNewRun(ironclad, unlockState, 1uL);
        Player[] players = teammate
            ? [local, Player.CreateForNewRun(ironclad, unlockState, 2uL)]
            : [local];

        RunState runState = RunState.CreateForNewRun(
            players,
            ModelDb.ActsByIndex is { }
                ? ActModel.GetDefaultList().Select(act => act.ToMutable()).ToList()
                : [],
            [],
            GameMode.Standard,
            0,
            $"E0-{scenario.ToUpperInvariant()}");
        RunManager.Instance.SetUpNewSingleplayer(runState, shouldSave: false);
        await RunManager.Instance.FinalizeStartingRelics();
        RunManager.Instance.Launch();
        await RunManager.Instance.EnterAct(0, doTransition: false);

        SetDeck(runState, local, scenario switch
        {
            "draw_energy" => ["OFFERING", "BASH", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "ANGER"],
            _ => ["BASH", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD"],
        });
        if (teammate)
        {
            Player remote = players[1];
            SetDeck(runState, remote,
                ["BASH", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD"]);
        }

        await RunManager.Instance.EnterRoomDebug(
            encounter.RoomType,
            encounter.RoomType == RoomType.Elite ? MapPointType.Elite : MapPointType.Monster,
            encounter.ToMutable());
    }

    private static void SetDeck(RunState runState, Player player, IReadOnlyList<string> cardIds)
    {
        CardModel[] oldCards = player.Deck.Cards.ToArray();
        player.Deck.Clear(silent: true);
        foreach (CardModel card in oldCards)
            runState.RemoveCard(card);

        foreach (string cardId in cardIds)
        {
            CardModel canonical = ResolveUnique(ModelDb.AllCards, cardId, "card");
            CardModel card = runState.CreateCard(canonical, player);
            player.Deck.AddInternal(card, -1);
        }
        if (player.Deck.Cards.Count != cardIds.Count)
            throw new InvalidOperationException("E0 fixture deck setup failed.");
    }

    private static Evidence CaptureEvidence(
        string scenario,
        CombatState combat,
        string[] rootHand,
        SolverResult result)
    {
        BeamWidthPortfolioTelemetry telemetry = result.PortfolioTelemetry
            ?? throw new InvalidOperationException("E0 result has no request telemetry.");
        CandidateOrigin origin = result.SearchEfficiencyOrigin
            ?? throw new InvalidOperationException("E0 selected result has no candidate origin.");
        string context = result.SearchEfficiencyEvaluationContextId
            ?? throw new InvalidOperationException("E0 selected result has no evaluation context.");
        CandidateMilestones milestones = telemetry.FindCandidateMilestones(origin, context)
            ?? throw new InvalidOperationException("E0 selected result has no candidate milestones.");
        if (milestones.EvaluatedTicks is not { } evaluated
            || milestones.SelectedTicks is not { } selected
            || milestones.PublishedTicks is not { } published)
        {
            throw new InvalidOperationException("E0 selected timeline is incomplete.");
        }

        double generatedMs = telemetry.ToRequestMilliseconds(origin.FirstGeneratedTicks);
        double evaluatedMs = telemetry.ToRequestMilliseconds(evaluated);
        double selectedMs = telemetry.ToRequestMilliseconds(selected);
        double publishedMs = telemetry.ToRequestMilliseconds(published);
        if (generatedMs > evaluatedMs || evaluatedMs > selectedMs || selectedMs > publishedMs)
            throw new InvalidOperationException("E0 selected timeline is not monotonic.");

        SearchEfficiencyMemberReport? member = telemetry.FindSearchMember(origin.SearchMemberId);
        return new Evidence(
            Scenario: scenario,
            PlayerCount: combat.Players.Count,
            RootHand: rootHand,
            Route: result.BestNode.Actions.Select(action =>
                $"{action.Turn}:{action.Kind}:{action.CardId ?? action.PotionId ?? "-"}").ToArray(),
            CandidateId: origin.CandidateId,
            SearchMemberId: origin.SearchMemberId,
            MemberKind: member?.Kind ?? "unknown",
            GeneratedMs: generatedMs,
            EvaluatedMs: evaluatedMs,
            SelectedMs: selectedMs,
            PublishedMs: publishedMs,
            ExpandedAtGeneration: origin.ExpandedAtGeneration,
            TurnDepth: origin.TurnDepth,
            EvaluationContextId: context,
            Members: telemetry.SearchMembers.Select(item => new MemberEvidence(
                item.MemberId,
                item.Kind,
                item.BeamWidth,
                item.SecondRankBand,
                item.BaseScoreOnly,
                item.Novelty,
                item.CompletedTicks.HasValue
                    ? BeamWidthPortfolioTelemetry.DurationMilliseconds(
                        Math.Max(0, item.CompletedTicks.Value - item.StartedTicks))
                    : null,
                item.ExpandedNodes,
                item.TransitionCount)).ToArray(),
            Phases: telemetry.SearchPhases.Select(item => new PhaseEvidence(
                item.SearchMemberId,
                item.Phase,
                item.CallCount,
                BeamWidthPortfolioTelemetry.DurationMilliseconds(item.ExclusiveTicks))).ToArray());
    }

    private static T ResolveUnique<T>(IEnumerable<T> values, string id, string domain)
        where T : AbstractModel
    {
        T[] matches = values.Where(value =>
            string.Equals(value.Id.Entry, id, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException(
                $"{domain} {id} resolved to {matches.Length} models.");
    }

    private static string? Value(string[] args, string key)
    {
        for (int index = 0; index + 1 < args.Length; index++)
            if (string.Equals(args[index], key, StringComparison.Ordinal))
                return args[index + 1];
        return null;
    }

    private sealed record Evidence(
        string Scenario,
        int PlayerCount,
        string[] RootHand,
        string[] Route,
        long CandidateId,
        int SearchMemberId,
        string MemberKind,
        double GeneratedMs,
        double EvaluatedMs,
        double SelectedMs,
        double PublishedMs,
        long ExpandedAtGeneration,
        int TurnDepth,
        string EvaluationContextId,
        MemberEvidence[] Members,
        PhaseEvidence[] Phases);

    private sealed record MemberEvidence(
        int MemberId,
        string Kind,
        int BeamWidth,
        bool SecondRankBand,
        bool BaseScoreOnly,
        bool Novelty,
        double? ElapsedMs,
        long ExpandedNodes,
        long TransitionCount);

    private sealed record PhaseEvidence(
        int SearchMemberId,
        string Phase,
        int CallCount,
        double ExclusiveMs);
}
