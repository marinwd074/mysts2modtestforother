using System.Text.Json;
using System.Text.Json.Serialization;
using CombatSolver;
using MegaCrit.Sts2.Core.Combat;
using OfflineSearchHarness;

namespace U2DegenerateHarness;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static int Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }

        Directory.CreateDirectory(options.OutputDirectory);
        try
        {
            HarnessLog.Language = "eng";
            MainLoopContext loop = new();
            SynchronizationContext.SetSynchronizationContext(loop);

            GameBootstrap.ApplyGodotBypasses();
            GameBootstrap.SkipGodotNodeStaticConstructors();
            Console.WriteLine(GameBootstrap.InitializeStaticState());
            int patchCount = U2Runtime.Initialize(
                Path.Combine(options.OutputDirectory, "logs"),
                options.BeamWidth,
                options.MaxExpandedNodes,
                options.BudgetMilliseconds);
            Console.WriteLine($"search_patches={patchCount}");

            HarnessScenario scenario = new(
                options.CharacterId,
                options.EncounterId,
                options.Seed,
                Ascension: 0,
                ActIndexForTest: 0);
            Task enter = OfflineCombat.EnterCombatRoomAsync(scenario);
            loop.RunUntilCompleted(enter, TimeSpan.FromSeconds(180), "U2 enter combat");
            CombatState combat = OfflineCombat.WaitForPlayableCombat(loop);
            Console.WriteLine(OfflineCombat.DescribeRoot(combat));

            SolverSettingsSnapshot settings = SolverSettings.Capture();
            SolverSearchProfile profile = settings.Profile with
            {
                BeamWidth = options.BeamWidth,
                MaxExpandedNodes = options.MaxExpandedNodes,
                SoftTimeBudgetMilliseconds = options.BudgetMilliseconds,
            };

            SolverDisplayNames names = SolverDisplayNames.Capture(combat);
            BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);

            CombatRootSnapshot singleRoot = CombatRootSnapshot.Capture(combat);
            CombatRootSnapshot multiplayerRouteRoot = CombatRootSnapshot.Capture(combat);
            RequireEqual(
                singleRoot.ContinuationStamp.StateText,
                multiplayerRouteRoot.ContinuationStamp.StateText,
                "root continuation stamp");
            RequireEqual(singleRoot.PlayerCount, multiplayerRouteRoot.PlayerCount, "root player count");
            if (singleRoot.PlayerCount != 1)
                throw new InvalidOperationException($"U2 degenerate fixture requires one captured player, got {singleRoot.PlayerCount}.");

            SearchPolicySnapshot captured = SolverController.CaptureSearchPolicy(
                settings,
                combat,
                includeTurnSetup: false,
                theftPolicy: null);
            SearchPolicySnapshot basePolicy = captured with
            {
                Profile = profile,
                FixedBudget = true,
                MaxDegreeOfParallelism = 1,
                BudgetOverrideMilliseconds = options.BudgetMilliseconds,
                UseNoveltyPortfolio = false,
                NoveltySearch = null,
                UseBeamWidthPortfolio = false,
                BeamWidthPortfolioWidths = null,
                Interaction = null,
                UseMultiplayerTeamObjective = false,
            };

            RunResult single = Run(
                singleRoot,
                names,
                damage,
                basePolicy,
                SearchRoutePolicy.SinglePlayerFullRoute);
            RunResult multiDegenerate = Run(
                multiplayerRouteRoot,
                names,
                damage,
                basePolicy,
                SearchRoutePolicy.MultiplayerLocalCrossTurn);

            Compare(single, multiDegenerate);

            var evidence = new
            {
                status = "PASS",
                scenario = new
                {
                    options.CharacterId,
                    options.EncounterId,
                    options.Seed,
                },
                budget = new
                {
                    options.BeamWidth,
                    options.MaxExpandedNodes,
                    options.BudgetMilliseconds,
                    maxDegreeOfParallelism = 1,
                    noveltyPortfolio = false,
                    beamWidthPortfolio = false,
                },
                root = new
                {
                    playerCount = singleRoot.PlayerCount,
                    continuationStamp = singleRoot.ContinuationStamp.StateText,
                },
                single,
                multiDegenerate,
            };

            string evidencePath = Path.Combine(
                options.OutputDirectory,
                "u2-degenerate-equivalence.json");
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(evidence, Json));
            Console.WriteLine("U2DegenerateEquivalence PASS");
            Console.WriteLine($"evidence={evidencePath}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"U2DegenerateEquivalence FAIL: {error.GetType().Name}: {error.Message}");
            Console.Error.WriteLine(error.StackTrace);
            return 1;
        }
    }

    private static RunResult Run(
        CombatRootSnapshot root,
        SolverDisplayNames names,
        BattleDamageSnapshot damage,
        SearchPolicySnapshot basePolicy,
        SearchRoutePolicy routePolicy)
    {
        SearchRequestWorkTotals totals = new();
        SearchPolicySnapshot policy = basePolicy with
        {
            RoutePolicy = routePolicy,
            CurrentTurnOnly = false,
            UseMultiplayerTeamObjective = false,
            RequestWorkTotals = totals,
        };

        CombatBeamSolver solver = new(
            root,
            names,
            damage,
            policy,
            searchProfile: policy.Profile);
        SolverResult result = solver.Solve();
        SearchRequestWorkSnapshot work = totals.Snapshot();
        string[] actions = result.BestNode.Actions.Select(ActionToken).ToArray();
        return new RunResult(
            RoutePolicy: routePolicy.ToString(),
            FirstAction: actions.FirstOrDefault() ?? "<none>",
            Actions: actions,
            Boundary: result.BoundaryReason.ToString(),
            ProjectedBattleHpLost: result.ProjectedBattleHpLost,
            FinalHp: result.Snapshot.PlayerHp,
            FinalEnemyHp: result.Snapshot.EnemyHp,
            CombatEndedTurn: result.CombatEndedTurn,
            PotionCount: result.PotionCount,
            OnlyDeathRoutes: result.OnlyDeathRoutesFound,
            Score: result.BestNode.Score,
            ExpandedNodes: work.ExpandedNodes,
            TransitionCount: work.TransitionCount,
            ChoiceBranchesEvaluated: work.ChoiceBranchesEvaluated);
    }

    private static void Compare(RunResult single, RunResult multi)
    {
        RequireEqual(single.FirstAction, multi.FirstAction, "first action");
        RequireEqual(single.Boundary, multi.Boundary, "boundary");
        RequireEqual(single.ProjectedBattleHpLost, multi.ProjectedBattleHpLost, "projected battle HP lost");
        RequireEqual(single.FinalHp, multi.FinalHp, "final HP");
        RequireEqual(single.FinalEnemyHp, multi.FinalEnemyHp, "final enemy HP");
        RequireEqual(single.CombatEndedTurn, multi.CombatEndedTurn, "combat ended turn");
        RequireEqual(single.PotionCount, multi.PotionCount, "potion count");
        RequireEqual(single.OnlyDeathRoutes, multi.OnlyDeathRoutes, "death-route flag");
        RequireEqual(single.Score, multi.Score, "final score");
        RequireEqual(single.ExpandedNodes, multi.ExpandedNodes, "expanded nodes");
        RequireEqual(single.TransitionCount, multi.TransitionCount, "transition count");
        RequireEqual(single.ChoiceBranchesEvaluated, multi.ChoiceBranchesEvaluated, "choice branches");
        if (!single.Actions.SequenceEqual(multi.Actions, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "fixed-tie-break action sequence mismatch\n"
                + $"single=[{string.Join(", ", single.Actions)}]\n"
                + $"multi=[{string.Join(", ", multi.Actions)}]");
        }
    }

    private static void RequireEqual<T>(T left, T right, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(left, right))
            throw new InvalidOperationException($"{label} mismatch: single={left} multi={right}");
    }

    private static string ActionToken(PlanAction action)
        => $"{action.Turn}:{action.Kind}:{action.CardId ?? action.PotionId ?? "-"}"
            + $":target={action.TargetCombatId?.ToString() ?? "-"}"
            + $":key={action.CardStateKey}";

    internal sealed record RunResult(
        string RoutePolicy,
        string FirstAction,
        string[] Actions,
        string Boundary,
        int ProjectedBattleHpLost,
        int FinalHp,
        int FinalEnemyHp,
        int? CombatEndedTurn,
        int PotionCount,
        bool OnlyDeathRoutes,
        double Score,
        long ExpandedNodes,
        long TransitionCount,
        long ChoiceBranchesEvaluated);

    private sealed record Options(
        string CharacterId,
        string EncounterId,
        string Seed,
        int BeamWidth,
        int MaxExpandedNodes,
        int BudgetMilliseconds,
        string OutputDirectory)
    {
        internal static Options Parse(string[] args)
        {
            string character = "IRONCLAD";
            string encounter = "FUZZY_WURM_CRAWLER_WEAK";
            string seed = "U2DEGENERATE1";
            int beam = 24;
            int nodes = 4000;
            int budget = 600000;
            string output = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../.local/u2-degenerate-equivalence"));

            for (int index = 0; index < args.Length; index++)
            {
                string key = args[index];
                string Value()
                {
                    if (index + 1 >= args.Length)
                        throw new ArgumentException($"Missing value for {key}.");
                    return args[++index];
                }

                switch (key)
                {
                    case "--character": character = Value(); break;
                    case "--encounter": encounter = Value(); break;
                    case "--seed": seed = Value(); break;
                    case "--beam": beam = int.Parse(Value()); break;
                    case "--nodes": nodes = int.Parse(Value()); break;
                    case "--budget-ms": budget = int.Parse(Value()); break;
                    case "--out": output = Path.GetFullPath(Value()); break;
                    default: throw new ArgumentException($"Unknown option {key}.");
                }
            }

            if (beam < 1 || nodes < 1 || budget < 1)
                throw new ArgumentException("beam, nodes and budget-ms must be positive.");
            return new(character, encounter, seed, beam, nodes, budget, output);
        }
    }
}
