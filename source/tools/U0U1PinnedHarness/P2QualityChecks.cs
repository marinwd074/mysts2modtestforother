using System.Security.Cryptography;
using System.Text.Json;
using CombatSolver;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace U0U1PinnedHarness;

// One fixed input per invocation, no warmups/repeats. Run the same fixture on both
// builds; the JSON carries scope, complete quality, route identity and logical work.
internal static class P2QualityChecks
{
    internal static string Run(CombatState live, SolverDisplayNames names,
        SearchPolicySnapshot captured, string fixture, string output, string? prefixPath = null,
        bool selectiveDeepening = false, bool detailed = false, string? turnLayers = null,
        int? adaptiveTurnLimit = null, bool adaptivePressure = false)
    {
        if (fixture is not ("setup-single" or "setup-mp" or "retained-mp" or "delayed-mp" or "defense-mp"))
            throw new ArgumentException("Unknown P2 quality fixture: " + fixture);
        var player = LocalContext.GetMe(live)!;
        var piles = player.PlayerCombatState!;
        foreach (var pile in new[] { piles.Hand, piles.DrawPile, piles.DiscardPile, piles.ExhaustPile })
            pile.Clear(silent: true);
        string[] hand = fixture.StartsWith("setup")
            ? ["DEMON_FORM", "BASH", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD"]
            : fixture == "delayed-mp"
                ? ["DEADLY_POISON", "BASH", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD"]
                : fixture == "defense-mp"
                    ? ["DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD"]
                : ["BASH", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD"];
        foreach (string id in hand)
        {
            CardModel card = live.CreateCard(ModelDb.AllCards.Single(model => model.Id.Entry == id), player);
            if (fixture == "retained-mp" && id == "STRIKE_IRONCLAD") card.AddKeyword(CardKeyword.Retain);
            piles.Hand.AddInternal(card, -1);
        }
        foreach (string id in fixture == "defense-mp" ? new[] { "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD" }
                     : new[] { "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD" })
            piles.DrawPile.AddInternal(live.CreateCard(ModelDb.AllCards.Single(model => model.Id.Entry == id), player), -1);
        foreach (PotionModel potion in player.PotionSlots.OfType<PotionModel>().ToArray()) player.RemovePotionInternal(potion);
        if (fixture.StartsWith("setup"))
        {
            live.Enemies.Single().SetMaxHpInternal(100);
            live.Enemies.Single().SetCurrentHpInternal(100);
        }
        if (fixture == "defense-mp") player.Creature.SetCurrentHpInternal(6);
        if (fixture == "delayed-mp")
        {
            if (live.Enemies.Count < 2) throw new InvalidOperationException("Delayed fixture requires separate targets.");
            foreach (var enemy in live.Enemies)
            {
                enemy.SetMaxHpInternal(40);
                enemy.SetCurrentHpInternal(40);
            }
            var target = live.Enemies.First();
            target.SetCurrentHpInternal(5);
            ModelDb.Power<PoisonPower>().ToMutable(0).ApplyInternal(target, 100, false);
        }
        SolverSearchProfile profile = new(8, 1_200, 8, 4, 4, 20_000);
        List<string> deepening = [];
        List<string> memory = [];
        List<string> refinement = [];
        SearchMemoryPressureSignal memorySignal = new();
        int? localCoreTurnLayers = turnLayers switch
        {
            null => SearchEvaluationPolicy.RollingHorizonTurnLayers,
            "battle" => null,
            _ => int.Parse(turnLayers, System.Globalization.CultureInfo.InvariantCulture),
        };
        var policy = captured with { Profile = profile, CurrentTurnOnly = false, IncludeTurnSetup = false,
            RoutePolicy = fixture.EndsWith("-single") ? SearchRoutePolicy.SinglePlayerFullRoute
                : SearchRoutePolicy.MultiplayerSinglePlayerCore,
            FixedBudget = true, VerifyIncrementalSearch = true, StopAtAcceptableBattleHpLoss = false,
            UseBeamWidthPortfolio = false, UseNoveltyPortfolio = false, NoveltySearch = null,
            UseMultiplayerTeamObjective = false, UseMultiplayerTeammateForecast = false,
            UseMultiplayerScenarioReevaluation = false, IgnoreLongTermRewards = true, RelicTargets = [],
            PotionPolicy = SolverPotionPolicy.Disabled, PotionStrategy = new(SolverPotionPolicy.Disabled, []),
            UseSelectiveDeepening = selectiveDeepening,
            LocalCoreTurnLayers = localCoreTurnLayers,
            LocalCoreAdaptiveTurnLimit = adaptiveTurnLimit,
            MemoryPressureSignal = adaptivePressure ? memorySignal : captured.MemoryPressureSignal,
            DetailedDiagnostics = detailed,
            MeasurePhasePerformance = detailed,
            Interaction = null, Diagnostics = new SearchDiagnosticsSink(message =>
            {
                if (message.StartsWith("[CombatSolver/Test] SEARCH_DEEPENING", StringComparison.Ordinal))
                    deepening.Add(message);
                if (message.StartsWith("[CombatSolver/Test] SEARCH_LAYER_MEMORY", StringComparison.Ordinal))
                    memory.Add(message);
                if (message.StartsWith("[CombatSolver/Test] SEARCH_HORIZON_REFINEMENT", StringComparison.Ordinal))
                {
                    refinement.Add(message);
                    if (adaptivePressure && message.Contains("checkpoint_layers=", StringComparison.Ordinal))
                        memorySignal.UseDefaultGcFallback(systemHeadroomConstrained: true);
                }
            }, _ => { }) };
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        var damage = BattleDamageTracker.Observe(live);
        string before = ContinuationStamp.CaptureLive(live).StateText;
        object? prefixEvidence = null;
        PlanAction[]? prefix = null;
        if (prefixPath != null)
        {
            if (fixture != "delayed-mp") throw new ArgumentException("Tail comparison only supports the delayed fixture.");
            using var document = JsonDocument.Parse(File.ReadAllText(prefixPath));
            var recorded = document.RootElement;
            if (recorded.GetProperty("Root").GetString() != before)
                throw new InvalidOperationException("Recorded route belongs to a different fixture root.");
            prefix = recorded.GetProperty("Actions").Deserialize<PlanAction[]>()!;
            var prefixVerifier = new CombatBeamSolver(root, names, damage, policy, searchProfile: profile);
            var predicted = prefixVerifier.ReplayDiagnosticPrefix(prefix);
            try
            {
                string predictedText = prefixVerifier.CaptureDiagnosticContinuation(predicted).StateText;
                if (predictedText != recorded.GetProperty("FinalState").GetString())
                    throw new InvalidOperationException("Recorded prefix no longer replays to its recorded state.");
                var adopted = new CombatBeamSolver(root, names, damage, policy, searchProfile: profile,
                    continuationRouteReplayActions: prefix).Solve();
                if (adopted.ExpandedNodes != 0 || adopted.EvaluationWindow != predicted.EvaluationWindow
                    || JsonSerializer.Serialize(adopted.BestNode.Actions) != JsonSerializer.Serialize(prefix))
                    throw new InvalidOperationException("Ordinary route adoption unexpectedly searched beyond its prefix.");
                prefixEvidence = new { Source = Path.GetFullPath(prefixPath), Actions = prefix.Length,
                    predicted.CumulativePlayerHpLost, StateMatches = true, AdoptionExpanded = adopted.ExpandedNodes };
            }
            finally { predicted.ReleaseSimulator(); }
            policy = policy with { RoutePolicy = SearchRoutePolicy.SinglePlayerFullRoute };
        }
        var verifier = new CombatBeamSolver(root, names, damage, policy, searchProfile: profile);
        var initial = verifier.ReplayDiagnosticPrefix([]);
        int initialDelayed = initial.DelayedDamageValue;
        initial.ReleaseSimulator();
        object? defenseWitness = null;
        if (fixture == "defense-mp")
        {
            // This deliberately defense-heavy deck guarantees three available Defends
            // per turn. Prove the safety witness before judging search's result.
            PlanAction[] defendActions = Enumerable.Range(root.StartTurnNumber, 3).SelectMany(turn =>
                new PlanAction[] { new(PlanActionKind.PlayCard, turn, "DEFEND_IRONCLAD"),
                    new(PlanActionKind.PlayCard, turn, "DEFEND_IRONCLAD"),
                    new(PlanActionKind.PlayCard, turn, "DEFEND_IRONCLAD"), new(PlanActionKind.EndTurn, turn) }).ToArray();
            var defended = verifier.ReplayDiagnosticPrefix(defendActions);
            try
            {
                if (defended.PlayerDead || defended.EvaluationWindow is not { CoversTarget: true }
                    || defended.CumulativePlayerHpLost != 0)
                    throw new InvalidOperationException("Defense fixture has no verified zero-loss witness.");
                defenseWitness = new { defended.PlayerDead, defended.CumulativePlayerHpLost,
                    defended.PlayerHp, defended.EvaluationWindow, Actions = defendActions };
            }
            finally { defended.ReleaseSimulator(); }
        }
        bool coordinator = turnLayers != null || adaptiveTurnLimit != null;
        if (coordinator && prefix != null)
            throw new ArgumentException("Choose a horizon comparison or a prefix replay.");
        SolverResult result = coordinator
            ? CombatSearchCoordinator.Solve(root, names, damage, policy, CancellationToken.None, null)
            : new CombatBeamSolver(root, names, damage, policy, searchProfile: profile,
                continuationRouteReplayActions: prefix, continueRouteSearchForTesting: prefix != null).Solve();
        if (coordinator)
        {
            int? limit = SearchEvaluationPolicy.TurnLayerLimit(policy.RoutePolicy, localCoreTurnLayers);
            int? target = limit is int layers ? root.StartTurnNumber + layers : null;
            if (result.EvaluationWindow is not { } window
                || (target is int minimum && adaptiveTurnLimit is int max
                    ? window.TargetTurn is not int actual || actual < minimum || actual > root.StartTurnNumber + max
                    : window.TargetTurn != target)
                || result.BestNode.Actions.Any(action => action.Turn >= window.TargetTurn))
                throw new InvalidOperationException("Selected route did not honor its requested evaluation horizon.");
            if (adaptivePressure && (window.TargetTurn != target
                || !refinement.Any(message => message.Contains("stopped=pressure_or_budget", StringComparison.Ordinal))))
                throw new InvalidOperationException("Pressure at the completed base window did not preserve the baseline.");
            if (!adaptivePressure && adaptiveTurnLimit is > 3 && fixture == "setup-mp"
                && (window.TargetTurn <= target || !window.CoversTarget || !result.Snapshot.AllEnemiesDead
                    || !refinement.Any(message => message.Contains("committed_layers=", StringComparison.Ordinal))
                    || result.RecoveryBudgetMetrics?.ExpandedNodes != result.TotalExpandedNodes
                    || result.TotalExpandedNodes > profile.MaxExpandedNodes))
                throw new InvalidOperationException("Adaptive horizon failed to commit and account for a verified extension: "
                    + string.Join(" | ", refinement));
            if (fixture == "setup-single" && (!result.Snapshot.AllEnemiesDead
                || result.CombatEndedTurn <= root.StartTurnNumber + 2))
                throw new InvalidOperationException("Singleplayer lost its full-battle prediction.");
        }
        bool expectDeepening = selectiveDeepening && policy.RoutePolicy == SearchRoutePolicy.MultiplayerSinglePlayerCore;
        if ((deepening.Count > 0) != expectDeepening)
            throw new InvalidOperationException("Selective deepening activation did not match the fixed fixture's policy.");
        if (result.BoundaryReason == SearchBoundaryReason.TimeLimit || result.ExpandedNodes > profile.MaxExpandedNodes)
            throw new InvalidOperationException("P2 comparison was not bounded by its fixed logical budget.");
        // PlanAction contains collection-valued annotations; compare their values,
        // not record equality of freshly materialized collection references.
        if (prefix != null && (JsonSerializer.Serialize(result.BestNode.Actions.Take(prefix.Length).ToArray())
                != JsonSerializer.Serialize(prefix) || result.ExpandedNodes < prefix.Length))
        {
            File.WriteAllText(Path.Combine(output, "prefix-mismatch.json"), JsonSerializer.Serialize(
                new { Expected = prefix, Actual = result.BestNode.Actions.Take(prefix.Length).ToArray(), result.ExpandedNodes }));
            throw new InvalidOperationException("Tail search changed its prefix or failed to charge replay work.");
        }
        var replay = verifier.ReplayDiagnosticPrefix(result.BestNode.Actions);
        try
        {
            if (result.PredictedFinalState == null
                || verifier.CaptureDiagnosticContinuation(replay) != result.PredictedFinalState
                || replay.CumulativePlayerHpLost != result.Snapshot.CumulativePlayerHpLost
                || ContinuationStamp.CaptureLive(live).StateText != before)
                throw new InvalidOperationException("Selected P2 route failed independent replay/root isolation.");
            if (fixture == "defense-mp" && (replay.PlayerDead || replay.EvaluationWindow is not { CoversTarget: true }
                || replay.CumulativePlayerHpLost != 0))
                throw new InvalidOperationException("Search lost the independently verified zero-loss defense route.");
            string outputName = fixture + (selectiveDeepening ? "-selective" : "") + (prefixPath == null ? "" : "-tail")
                + (turnLayers == null ? "" : "-horizon-" + (localCoreTurnLayers?.ToString() ?? "battle"))
                + (adaptiveTurnLimit == null ? "" : $"-adaptive-{adaptiveTurnLimit}")
                + (adaptivePressure ? "-pressure" : "");
            File.WriteAllText(Path.Combine(output, outputName + ".json"), JsonSerializer.Serialize(new
            {
                Fixture = fixture, DefenseWitness = defenseWitness, Prefix = prefixEvidence,
                DllSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(CombatBeamSolver).Assembly.Location))),
                Root = root.ContinuationStamp.StateText, Profile = profile, result.EvaluationWindow,
                result.ExpandedNodes, result.TransitionCount, SelectiveDeepening = selectiveDeepening, Deepening = deepening,
                result.SearchedTurns, Memory = memory,
                result.TotalExpandedNodes, result.TotalTransitionCount, Refinement = refinement,
                Boundary = result.BoundaryReason.ToString(),
                result.CombatEndedTurn, InitialDelayed = initialDelayed, replay.AllEnemiesDead, replay.PlayerDead,
                replay.DeathSaveUseCount, replay.CumulativePlayerHpLost, replay.PlayerHp, replay.ProjectedPlayerHp, replay.EnemyHp,
                replay.DelayedDamageValue, replay.StrategicEffects, replay.LongTermResourceValue,
                Actions = result.BestNode.Actions, FinalState = verifier.CaptureDiagnosticContinuation(replay).StateText,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return $"P2Quality PASS fixture={fixture} won={replay.AllEnemiesDead} dead={replay.PlayerDead} loss={replay.CumulativePlayerHpLost} expanded={result.ExpandedNodes}";
        }
        finally { replay.ReleaseSimulator(); }
    }

}
