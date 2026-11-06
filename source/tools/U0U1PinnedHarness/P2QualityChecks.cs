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
        SearchPolicySnapshot captured, string fixture, string output)
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
        var policy = captured with { Profile = profile, CurrentTurnOnly = false, IncludeTurnSetup = false,
            RoutePolicy = fixture.EndsWith("-single") ? SearchRoutePolicy.SinglePlayerFullRoute
                : SearchRoutePolicy.MultiplayerSinglePlayerCore,
            FixedBudget = true, VerifyIncrementalSearch = true, StopAtAcceptableBattleHpLoss = false,
            UseBeamWidthPortfolio = false, UseNoveltyPortfolio = false, NoveltySearch = null,
            UseMultiplayerTeamObjective = false, UseMultiplayerTeammateForecast = false,
            UseMultiplayerScenarioReevaluation = false, IgnoreLongTermRewards = true, RelicTargets = [],
            PotionPolicy = SolverPotionPolicy.Disabled, PotionStrategy = new(SolverPotionPolicy.Disabled, []),
            Interaction = null, Diagnostics = new SearchDiagnosticsSink(_ => { }, _ => { }) };
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        var damage = BattleDamageTracker.Observe(live);
        string before = ContinuationStamp.CaptureLive(live).StateText;
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
                if (defended.PlayerDead || defended.EvaluationWindow is not { CoversTarget: true })
                    throw new InvalidOperationException("Defense fixture has no verified surviving witness.");
                defenseWitness = new { defended.PlayerDead, defended.CumulativePlayerHpLost,
                    defended.PlayerHp, defended.EvaluationWindow, Actions = defendActions };
            }
            finally { defended.ReleaseSimulator(); }
        }
        SolverResult result = new CombatBeamSolver(root, names, damage, policy, searchProfile: profile).Solve();
        if (result.BoundaryReason == SearchBoundaryReason.TimeLimit || result.ExpandedNodes > profile.MaxExpandedNodes)
            throw new InvalidOperationException("P2 comparison was not bounded by its fixed logical budget.");
        var replay = verifier.ReplayDiagnosticPrefix(result.BestNode.Actions);
        try
        {
            if (result.PredictedFinalState == null
                || verifier.CaptureDiagnosticContinuation(replay) != result.PredictedFinalState
                || replay.CumulativePlayerHpLost != result.Snapshot.CumulativePlayerHpLost
                || ContinuationStamp.CaptureLive(live).StateText != before)
                throw new InvalidOperationException("Selected P2 route failed independent replay/root isolation.");
            if (fixture == "defense-mp" && (replay.PlayerDead || replay.EvaluationWindow is not { CoversTarget: true }))
                throw new InvalidOperationException("Search lost the independently verified defense route.");
            File.WriteAllText(Path.Combine(output, fixture + ".json"), JsonSerializer.Serialize(new
            {
                Fixture = fixture, DefenseWitness = defenseWitness,
                DllSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(CombatBeamSolver).Assembly.Location))),
                Root = root.ContinuationStamp.StateText, Profile = profile, result.EvaluationWindow,
                result.ExpandedNodes, Boundary = result.BoundaryReason.ToString(),
                result.CombatEndedTurn, InitialDelayed = initialDelayed, replay.AllEnemiesDead, replay.PlayerDead,
                replay.DeathSaveUseCount, replay.CumulativePlayerHpLost, replay.PlayerHp, replay.EnemyHp,
                replay.DelayedDamageValue, replay.StrategicEffects, replay.LongTermResourceValue,
                Actions = result.BestNode.Actions, FinalState = verifier.CaptureDiagnosticContinuation(replay).StateText,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return $"P2Quality PASS fixture={fixture} won={replay.AllEnemiesDead} dead={replay.PlayerDead} loss={replay.CumulativePlayerHpLost} expanded={result.ExpandedNodes}";
        }
        finally { replay.ReleaseSimulator(); }
    }
}
