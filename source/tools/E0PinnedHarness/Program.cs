using System.Diagnostics;
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
        bool publishProgress = args.Contains(
            "--publish-progress",
            StringComparer.Ordinal);
        bool legacyActionOrder = args.Contains(
            "--legacy-action-order",
            StringComparer.Ordinal);
        bool multiplayerPrediction = args.Contains(
            "--multiplayer-prediction",
            StringComparer.Ordinal);
        bool p2Ab = args.Contains(
            "--p2-ab",
            StringComparer.Ordinal);
        CombatBeamSolver.UseLegacyActionSearchOrderForTesting(legacyActionOrder);
        bool teammate = string.Equals(scenario, "teammate", StringComparison.Ordinal);
        if (teammate)
        {
            Environment.SetEnvironmentVariable(
                SolverSessionCapabilities.MultiplayerModeEnvironmentVariable,
                "advisor");
        }
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
                    UseMultiplayerPrediction = teammate && multiplayerPrediction,
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
            if (teammate)
            {
                var captureProbe = root.ForkSimulator();
                int expectedCapturedPlayers = multiplayerPrediction ? 2 : 1;
                if (captureProbe.State.Players.Count != 2
                    || captureProbe.State.RootCapturedPlayers.Count != expectedCapturedPlayers
                    || captureProbe.State.RootCapturedPlayerCreatures.Count != expectedCapturedPlayers)
                {
                    throw new InvalidOperationException(
                        $"Multiplayer prediction target roster mismatch: public={captureProbe.State.Players.Count} " +
                        $"captured={captureProbe.State.RootCapturedPlayers.Count} " +
                        $"targets={captureProbe.State.RootCapturedPlayerCreatures.Count} " +
                        $"prediction={multiplayerPrediction}.");
                }

                Player remote = combat.Players.Single(player => !ReferenceEquals(player, local));
                SimulatedCombatState capturedCombat =
                    (SimulatedCombatState)captureProbe.State.CombatState;
                bool remoteRootTurnReadable;
                try
                {
                    _ = capturedCombat.GetRootPlayerTurnNumber(remote);
                    remoteRootTurnReadable = true;
                }
                catch (InvalidOperationException)
                {
                    remoteRootTurnReadable = false;
                }
                if (remoteRootTurnReadable != multiplayerPrediction)
                {
                    throw new InvalidOperationException(
                        $"Multiplayer remote root-turn boundary mismatch: readable={remoteRootTurnReadable} " +
                        $"prediction={multiplayerPrediction}.");
                }

                var deathProbe = root.ForkSimulator();
                if (!deathProbe.Kill(local.Creature, force: true))
                    throw new InvalidOperationException("Multiplayer death-boundary probe did not complete.");
                bool shouldEndOnLocalDeath = !multiplayerPrediction;
                if (deathProbe.IsOverOrEnding != shouldEndOnLocalDeath)
                {
                    throw new InvalidOperationException(
                        $"Multiplayer local-death terminal mismatch: ended={deathProbe.IsOverOrEnding} " +
                        $"expected={shouldEndOnLocalDeath} prediction={multiplayerPrediction}.");
                }
            }
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
            SearchRoutePolicy expectedMultiplayerRoute = multiplayerPrediction
                ? SearchRoutePolicy.MultiplayerLocalCrossTurn
                : SearchRoutePolicy.MultiplayerSinglePlayerCore;
            if (teammate
                && (captured.RoutePolicy != expectedMultiplayerRoute
                    || captured.UseMultiplayerTeamObjective != multiplayerPrediction
                    || captured.UseMultiplayerTeammateForecast != multiplayerPrediction
                    || captured.UseMultiplayerScenarioReevaluation != multiplayerPrediction))
            {
                throw new InvalidOperationException(
                    $"Production teammate fixture did not capture requested multiplayer prediction mode: " +
                    $"requested={multiplayerPrediction} route={captured.RoutePolicy} " +
                    $"expected_route={expectedMultiplayerRoute} team={captured.UseMultiplayerTeamObjective} " +
                    $"forecast={captured.UseMultiplayerTeammateForecast} " +
                    $"scenario={captured.UseMultiplayerScenarioReevaluation}.");
            }
            SearchPolicySnapshot policy = captured with
            {
                Profile = profile,
                RoutePolicy = teammate
                    ? expectedMultiplayerRoute
                    : SearchRoutePolicy.SinglePlayerFullRoute,
                CurrentTurnOnly = false,
                UseNoveltyPortfolio = false,
                UseBeamWidthPortfolio = true,
                BeamWidthPortfolioWidths = null,
                FixedBudget = true,
                MaxDegreeOfParallelism = 1,
                BudgetOverrideMilliseconds = BudgetMilliseconds,
                Interaction = null,
            };

            if (teammate
                && (combat.Players.Count != 2
                    || root.PlayerCount != 2
                    || !root.AllowsLocalPlayerOnlySearch))
            {
                throw new InvalidOperationException(
                    $"Detached teammate fixture is not an admitted local-player multiplayer root: " +
                    $"combat={combat.Players.Count} root={root.PlayerCount} " +
                    $"local_only={root.AllowsLocalPlayerOnlySearch}.");
            }
            if (teammate)
            {
                var historyProbe = root.ForkSimulator();
                _ = historyProbe.History.GetCounters(local);
                var historyFork = historyProbe.Fork();
                _ = historyFork.History.GetCounters(local);
            }

            if (p2Ab)
            {
                if (!teammate || multiplayerPrediction)
                    throw new InvalidOperationException("P2 A/B requires teammate fixture with multiplayer prediction disabled.");
                return RunP2ContinuationSeedAb(
                    output,
                    root,
                    names,
                    damage,
                    policy,
                    local.PlayerCombatState!.TurnNumber);
            }

            string[] rootHand = local.PlayerCombatState!.Hand.Cards.Select(card => card.Id.Entry).ToArray();
            Action<SolverProgress>? progressCallback =
                publishProgress ? static _ => { } : null;
            SolverResult result = CombatSearchCoordinator.Solve(
                root,
                names,
                damage,
                policy,
                CancellationToken.None,
                progressCallback);
            Evidence evidence = CaptureEvidence(
                scenario,
                legacyActionOrder ? "legacy_hand_order" : "e5_strategy_order",
                combat,
                rootHand,
                result);

            if (string.Equals(scenario, "draw_energy", StringComparison.Ordinal)
                && (!rootHand.Contains("OFFERING", StringComparer.Ordinal)
                    || !result.BestNode.Actions.Any(action =>
                        string.Equals(action.CardId, "OFFERING", StringComparison.Ordinal))))
            {
                throw new InvalidOperationException(
                    "Draw/energy fixture did not exercise OFFERING in the selected route.");
            }
            if (teammate
                && !multiplayerPrediction
                && evidence.Phases.Any(phase =>
                    string.Equals(phase.Phase, "shadow", StringComparison.Ordinal)
                    && phase.CallCount > 0))
            {
                throw new InvalidOperationException(
                    "Production local-single-core fixture unexpectedly performed Shadow teammate work.");
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
            PotionModel remotePotion = ResolveUnique(ModelDb.AllPotions, "FIRE_POTION", "potion").ToMutable();
            if (!remote.AddPotionInternal(remotePotion, 0, silent: false).success)
                throw new InvalidOperationException("Could not add teammate FIRE_POTION to slot 0.");
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

    private static int RunP2ContinuationSeedAb(
        string output,
        CombatRootSnapshot root,
        SolverDisplayNames names,
        BattleDamageSnapshot damage,
        SearchPolicySnapshot basePolicy,
        int currentTurn)
    {
        SolverSearchProfile profile = basePolicy.Profile with
        {
            BeamWidth = BeamWidth,
            MaxExpandedNodes = MaxExpandedNodes,
            SoftTimeBudgetMilliseconds = 15_000,
        };

        SearchPolicySnapshot CommonPolicy(SearchInteractionState interaction)
            => basePolicy with
            {
                Profile = profile,
                RoutePolicy = SearchRoutePolicy.MultiplayerSinglePlayerCore,
                CurrentTurnOnly = false,
                UseMultiplayerTeamObjective = false,
                UseMultiplayerTeammateForecast = false,
                UseMultiplayerScenarioReevaluation = false,
                PotionPolicy = SolverPotionPolicy.Disabled,
                PotionStrategy = new PotionStrategySnapshot(SolverPotionPolicy.Disabled, []),
                UseNoveltyPortfolio = false,
                NoveltySearch = null,
                UseBeamWidthPortfolio = false,
                UseE3FixedPortfolioScheduling = false,
                UseE3AdaptivePortfolioScheduling = false,
                StopAtAcceptableBattleHpLoss = false,
                FixedBudget = true,
                MaxDegreeOfParallelism = 1,
                BudgetOverrideMilliseconds = null,
                Interaction = interaction,
                ContinuationSeedActions = [],
            };

        static string[] Route(SolverResult result)
            => result.BestNode.Actions.Select(action =>
                $"{action.Turn}:{action.Kind}:{action.CardId ?? action.PotionId ?? "-"}").ToArray();

        static bool CompleteVictory(SolverResult result)
            => SolverInterimResultOrdering.IsCompleteVictory(
                result.BestNode.ActionCount,
                result.Snapshot.AllEnemiesDead,
                result.Snapshot.PlayerDead,
                result.Snapshot.ProjectedPlayerHp);

        static bool SameQuality(SolverResult left, SolverResult right)
            => CompleteVictory(left) == CompleteVictory(right)
                && left.ProjectedBattleHpLost == right.ProjectedBattleHpLost
                && left.ProjectedBattlePotionCount == right.ProjectedBattlePotionCount
                && left.CombatEndedTurn == right.CombatEndedTurn
                && left.Snapshot.EnemyHp == right.Snapshot.EnemyHp
                && left.Snapshot.PlayerHp == right.Snapshot.PlayerHp
                && left.BoundaryReason == right.BoundaryReason;

        static double FinalCandidatePublishedMs(SolverResult result)
        {
            BeamWidthPortfolioTelemetry telemetry = result.PortfolioTelemetry
                ?? throw new InvalidOperationException("P2 A/B result has no request telemetry.");
            CandidateOrigin origin = result.SearchEfficiencyOrigin
                ?? throw new InvalidOperationException("P2 A/B final result has no candidate origin.");
            string context = result.SearchEfficiencyEvaluationContextId
                ?? throw new InvalidOperationException("P2 A/B final result has no evaluation context.");
            CandidateMilestones milestones = telemetry.FindCandidateMilestones(origin, context)
                ?? throw new InvalidOperationException("P2 A/B final candidate has no milestones.");
            long published = milestones.PublishedTicks
                ?? throw new InvalidOperationException("P2 A/B final candidate was never published.");
            return telemetry.ToRequestMilliseconds(published);
        }

        static (SolverResult Result, double? FirstAdoptableMs) Run(
            CombatRootSnapshot root,
            SolverDisplayNames names,
            BattleDamageSnapshot damage,
            SearchPolicySnapshot policy)
        {
            long started = Stopwatch.GetTimestamp();
            double? firstAdoptableMs = null;
            Action<SolverProgress> progress = item =>
            {
                if (firstAdoptableMs == null && item.RouteAdoptionSeed != null)
                    firstAdoptableMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            };
            SolverResult result = CombatSearchCoordinator.Solve(
                root,
                names,
                damage,
                policy,
                CancellationToken.None,
                progress);
            return (result, firstAdoptableMs);
        }

        SearchInteractionState coldInteraction = new();
        SearchPolicySnapshot coldPolicy = CommonPolicy(coldInteraction);
        (SolverResult cold, double? coldAdoptableMs) = Run(
            root, names, damage, coldPolicy);

        PlanAction[] seed = cold.BestNode.Actions
            .TakeWhile(action => MultiplayerLocalCrossTurnContracts.CanReplayContinuationSeedAction(
                action.Turn,
                currentTurn,
                action.Kind == PlanActionKind.PlayCard,
                action.EndsPlayerTurn,
                action.Choice != null,
                action.NestedChoices is { Count: > 0 },
                action.TurnStartChoices is { Count: > 0 },
                action.ShadowForecast != null,
                !string.IsNullOrEmpty(action.CardStateKey)))
            .Take(2)
            .ToArray();
        if (seed.Length == 0)
            throw new InvalidOperationException("P2 A/B cold route produced no replayable current-turn seed.");

        SearchInteractionState seededInteraction = new();
        SearchPolicySnapshot seededPolicy = CommonPolicy(seededInteraction) with
        {
            ContinuationSeedActions = seed,
        };
        (SolverResult seeded, double? seededAdoptableMs) = Run(
            root, names, damage, seededPolicy);

        SearchEfficiencyMemberReport? seedMember = seeded.PortfolioTelemetry?.SearchMembers
            .FirstOrDefault(member => string.Equals(
                member.Kind,
                "continuation_seed_incumbent",
                StringComparison.Ordinal));
        if (seedMember == null)
            throw new InvalidOperationException("P2 A/B did not execute continuation_seed_incumbent member.");

        string[] coldRoute = Route(cold);
        string[] seededRoute = Route(seeded);
        bool sameRoute = coldRoute.SequenceEqual(seededRoute, StringComparer.Ordinal);
        bool sameQuality = SameQuality(cold, seeded);
        bool seededEarlier = coldAdoptableMs.HasValue
            && seededAdoptableMs.HasValue
            && seededAdoptableMs.Value < coldAdoptableMs.Value;
        bool withinNodeBudget = seeded.TotalExpandedNodes <= MaxExpandedNodes;
        bool probeWithinFivePercent = seedMember.ExpandedNodes
            <= Math.Max(1, MaxExpandedNodes / ContinuationSeedIncumbentBudget.WorkDivisor);

        SearchInteractionState hintedInteraction = new();
        SearchPolicySnapshot hintedPolicy = CommonPolicy(hintedInteraction) with
        {
            ContinuationEnumerationHintActions = seed,
        };
        (SolverResult hinted, double? hintedAdoptableMs) = Run(
            root, names, damage, hintedPolicy);
        string[] hintedRoute = Route(hinted);
        bool hintSameRoute = coldRoute.SequenceEqual(hintedRoute, StringComparer.Ordinal);
        bool hintSameQuality = SameQuality(cold, hinted);
        bool hintWithinNodeBudget = hinted.TotalExpandedNodes <= MaxExpandedNodes;
        double coldReferencePublishedMs = FinalCandidatePublishedMs(cold);
        double hintedReferencePublishedMs = FinalCandidatePublishedMs(hinted);
        bool hintReachedReferenceEarlier =
            hintedReferencePublishedMs < coldReferencePublishedMs;

        var evidence = new
        {
            schemaVersion = 1,
            source = "pinned-0.107.1-p2-independent-incumbent-ab",
            routePolicy = seededPolicy.RoutePolicy.ToString(),
            seed = seed.Select(action =>
                $"{action.Turn}:{action.Kind}:{action.CardId}:{action.CardStateKey}").ToArray(),
            cold = new
            {
                route = coldRoute,
                completeVictory = CompleteVictory(cold),
                cold.ProjectedBattleHpLost,
                cold.ProjectedBattlePotionCount,
                cold.CombatEndedTurn,
                cold.BoundaryReason,
                finalEnemyHp = cold.Snapshot.EnemyHp,
                finalPlayerHp = cold.Snapshot.PlayerHp,
                cold.TotalExpandedNodes,
                cold.TotalTransitionCount,
                firstAdoptableMs = coldAdoptableMs,
            },
            seeded = new
            {
                route = seededRoute,
                completeVictory = CompleteVictory(seeded),
                seeded.ProjectedBattleHpLost,
                seeded.ProjectedBattlePotionCount,
                seeded.CombatEndedTurn,
                seeded.BoundaryReason,
                finalEnemyHp = seeded.Snapshot.EnemyHp,
                finalPlayerHp = seeded.Snapshot.PlayerHp,
                seeded.TotalExpandedNodes,
                seeded.TotalTransitionCount,
                firstAdoptableMs = seededAdoptableMs,
                probeExpandedNodes = seedMember.ExpandedNodes,
                probeTransitionCount = seedMember.TransitionCount,
            },
            hinted = new
            {
                route = hintedRoute,
                completeVictory = CompleteVictory(hinted),
                hinted.ProjectedBattleHpLost,
                hinted.ProjectedBattlePotionCount,
                hinted.CombatEndedTurn,
                hinted.BoundaryReason,
                finalEnemyHp = hinted.Snapshot.EnemyHp,
                finalPlayerHp = hinted.Snapshot.PlayerHp,
                hinted.TotalExpandedNodes,
                hinted.TotalTransitionCount,
                firstAdoptableMs = hintedAdoptableMs,
                finalCandidatePublishedMs = hintedReferencePublishedMs,
            },
            acceptance = new
            {
                sameRoute,
                sameQuality,
                seededEarlier,
                withinNodeBudget,
                probeWithinFivePercent,
                hintSameRoute,
                hintSameQuality,
                hintWithinNodeBudget,
                coldFinalCandidatePublishedMs = coldReferencePublishedMs,
                hintedFinalCandidatePublishedMs = hintedReferencePublishedMs,
                hintReachedReferenceEarlier,
            },
        };

        string path = Path.Combine(output, "p2-continuation-seed-ab.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));

        if (!sameQuality || !withinNodeBudget || !probeWithinFivePercent)
        {
            throw new InvalidOperationException(
                $"P2 A/B failed hard gates: same_quality={sameQuality} " +
                $"within_budget={withinNodeBudget} probe_5pct={probeWithinFivePercent} " +
                $"same_route={sameRoute} seeded_earlier={seededEarlier}.");
        }
        if (!seededEarlier)
        {
            throw new InvalidOperationException(
                $"P2 A/B did not improve first adoptable timing: " +
                $"cold_ms={coldAdoptableMs?.ToString("F3") ?? "-"} " +
                $"seeded_ms={seededAdoptableMs?.ToString("F3") ?? "-"}.");
        }
        if (!hintSameQuality || !hintWithinNodeBudget)
        {
            throw new InvalidOperationException(
                $"P2 enumeration-hint A/B regressed a hard gate: " +
                $"same_quality={hintSameQuality} within_budget={hintWithinNodeBudget} " +
                $"same_route={hintSameRoute}.");
        }

        Console.WriteLine(
            $"P2_AB_PASS same_route={sameRoute} same_quality={sameQuality} " +
            $"cold_adoptable_ms={coldAdoptableMs:F3} seeded_adoptable_ms={seededAdoptableMs:F3} " +
            $"seed_probe_nodes={seedMember.ExpandedNodes} total_nodes={seeded.TotalExpandedNodes}");
        Console.WriteLine(
            $"P2_HINT_AB_{(hintReachedReferenceEarlier ? "GO" : "NO_GO")} " +
            $"same_route={hintSameRoute} same_quality={hintSameQuality} " +
            $"cold_reference_ms={coldReferencePublishedMs:F3} " +
            $"hinted_reference_ms={hintedReferencePublishedMs:F3} " +
            $"cold_nodes={cold.TotalExpandedNodes} hinted_nodes={hinted.TotalExpandedNodes}");
        Console.WriteLine($"evidence={path}");
        return 0;
    }

    private static Evidence CaptureEvidence(
        string scenario,
        string actionOrder,
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
            ActionOrder: actionOrder,
            PlayerCount: combat.Players.Count,
            RootHand: rootHand,
            Route: result.BestNode.Actions.Select(action =>
                $"{action.Turn}:{action.Kind}:{action.CardId ?? action.PotionId ?? "-"}").ToArray(),
            ProjectedBattleHpLost: result.ProjectedBattleHpLost,
            ProjectedBattlePotionCount: result.ProjectedBattlePotionCount,
            CombatEndedTurn: result.CombatEndedTurn,
            BoundaryReason: result.BoundaryReason.ToString(),
            TotalExpandedNodes: result.TotalExpandedNodes,
            TotalTransitionCount: result.TotalTransitionCount,
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
        string ActionOrder,
        int PlayerCount,
        string[] RootHand,
        string[] Route,
        int ProjectedBattleHpLost,
        int ProjectedBattlePotionCount,
        int? CombatEndedTurn,
        string BoundaryReason,
        long TotalExpandedNodes,
        long TotalTransitionCount,
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
