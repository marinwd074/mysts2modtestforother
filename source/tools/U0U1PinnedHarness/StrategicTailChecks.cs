using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Potions;

namespace U0U1PinnedHarness;

internal static class StrategicTailChecks
{
    internal static string Run(CombatState live, SolverDisplayNames names, BattleDamageSnapshot damage,
        SearchPolicySnapshot captured, string output)
    {
        int checks = 0, replays = 0;
        void Check(bool ok, string reason)
        {
            if (!ok) throw new InvalidOperationException(reason);
            checks++;
        }
        var player = LocalContext.GetMe(live)!;
        var demon = ModelDb.Power<DemonFormPower>().ToMutable(0);
        demon.ApplyInternal(player.Creature, 2, false);
        ModelDb.Power<StrengthPower>().ToMutable(0).ApplyInternal(player.Creature, 5, false);
        live.Enemies.Single().SetMaxHpInternal(500);
        live.Enemies.Single().SetCurrentHpInternal(500);
        var policy = captured with { CurrentTurnOnly = false, IncludeTurnSetup = false,
            UseMultiplayerTeamObjective = false, UseMultiplayerTeammateForecast = false,
            UseMultiplayerScenarioReevaluation = false, Act3BossStrategy = false,
            IgnoreLongTermRewards = true, RelicTargets = [] };

        // Three attack turns, two attacks each. The first turn's strength is already
        // in the snapshot: 5*6 + (2*2 + 4*2) = 42, not the old 54.
        var context = new StrategicEffectContext(1000, 0, 0, 3, 6, 6, 0, 0, 0, 0, 0, 0,
            0, 0, 10, 10, 6, 0) { CurrentTurnStartResolved = true };
        int growth = StrategicEffectModel.Evaluate(demon, context).DamagePotential;
        int nextGrowth = StrategicEffectModel.Evaluate(demon,
            context with { RemainingTurns = 2, AttackPlays = 4 }).DamagePotential;
        Check(growth == 12 && 5 * 6 + growth == 5 * 2 + 7 * 4 + nextGrowth,
            "Demon Form tail counts already-realized strength or the current turn start twice.");
        Check(StrategicEffectModel.Evaluate(demon, context with { RemainingTurns = 1, AttackPlays = 2 })
            .DamagePotential == 0
            && StrategicEffectModel.Evaluate(demon, context with { CurrentTurnStartResolved = false })
                .DamagePotential == 24, "Pending and completed turn starts were conflated.");
        var creativeAi = ModelDb.Power<CreativeAiPower>().ToMutable(0);
        var furnace = ModelDb.Power<FurnacePower>().ToMutable(0);
        Check(StrategicEffectModel.Evaluate(creativeAi, context).CardAccessPotential == 20
            && StrategicEffectModel.Evaluate(furnace, context).ScalingPotential == 4,
            "Turn-start card generation/forge still counts the already-completed start.");

        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        string before = ContinuationStamp.CaptureLive(live).StateText;
        List<object> evidence = [];
        List<StrategicEffectVector> singleEffects = [];
        foreach (SearchRoutePolicy route in new[] { SearchRoutePolicy.SinglePlayerFullRoute,
                     SearchRoutePolicy.MultiplayerSinglePlayerCore })
        {
            var solver = new CombatBeamSolver(root, names, damage, policy with { RoutePolicy = route });
            for (int ends = 0; ends <= 2; ends++)
            {
                PlanAction[] actions = Enumerable.Range(0, ends)
                    .Select(index => new PlanAction(PlanActionKind.EndTurn, root.StartTurnNumber + index)).ToArray();
                SimulationSnapshot snapshot = solver.ReplayDiagnosticPrefix(actions);
                replays++;
                try
                {
                    var sim = snapshot.Simulator;
                    var combat = (SimulatedCombatState)sim.State.CombatState;
                    var piles = sim.State.GetPlayerCombatState(player);
                    var cards = piles.DiscardPile.Cards.Concat(piles.DrawPile.Cards).Concat(piles.Hand.Cards).ToArray();
                    var tail = StrategicEffectContext.Build(cards, 500, 0, 0,
                        StrategicEffectRequirements.AttackHits | StrategicEffectRequirements.AttackPlays
                            | StrategicEffectRequirements.RemainingTurns, simulator: sim)
                        with { CurrentTurnStartResolved = true };
                    var powers = combat.EffectivePowers().Where(power => power.Owner == player.Creature
                        && power is StrengthPower or DemonFormPower).ToArray();
                    var expected = powers.Aggregate(StrategicEffectVector.Zero,
                        (sum, power) => sum + StrategicEffectModel.Evaluate(power, tail));
                    Check(!snapshot.HasRisk && snapshot.EvaluationWindow is { AtPlayerDecisionPoint: true }
                        && combat.GetAmount<StrengthPower>(player.Creature) == 5 + ends * 2
                        && snapshot.StrategicEffects == expected && tail.RemainingTurns > 3,
                        "Replayed branch failed to separate current strength from future growth.");
                    if (route == SearchRoutePolicy.SinglePlayerFullRoute) singleEffects.Add(snapshot.StrategicEffects);
                    else Check(snapshot.StrategicEffects == singleEffects[ends],
                        "Singleplayer and multiplayer used different power-tail valuation.");
                    evidence.Add(new { Route = route.ToString(), Ends = ends, tail.RemainingTurns,
                        tail.FutureTurnStarts, snapshot.StrategicEffects, snapshot.StrategicHpCredit,
                        snapshot.CumulativePlayerHpLost });
                }
                finally { snapshot.ReleaseSimulator(); }
            }
        }
        Check(ContinuationStamp.CaptureLive(live).StateText == before, "Tail replay mutated the live fixture.");

        foreach (PotionModel potion in player.PotionSlots.OfType<PotionModel>().ToArray())
            player.RemovePotionInternal(potion);
        Check(player.AddPotionInternal(ModelDb.Potion<FirePotion>().ToMutable(), 0, true).success,
            "Terminal fixture potion setup failed.");
        live.Enemies.Single().SetCurrentHpInternal(1);
        root = CombatRootSnapshot.Capture(live);
        before = ContinuationStamp.CaptureLive(live).StateText;
        var terminalSolver = new CombatBeamSolver(root, names, damage,
            policy with { RoutePolicy = SearchRoutePolicy.SinglePlayerFullRoute });
        var won = terminalSolver.ReplayDiagnosticPrefix([new(PlanActionKind.UsePotion, root.StartTurnNumber,
            TargetIndex: 0, TargetCombatId: live.Enemies.Single().CombatId, PotionSlot: 0, PotionId: "FIRE_POTION")]);
        replays++;
        try
        {
            Check(won.AllEnemiesDead && won.StrategicEffects == StrategicEffectVector.Zero
                && ContinuationStamp.CaptureLive(live).StateText == before,
                "Completed battle retained hypothetical power benefits or mutated the root.");
        }
        finally { won.ReleaseSimulator(); }
        File.WriteAllText(Path.Combine(output, "strategic-tail.json"), JsonSerializer.Serialize(
            new { Checks = checks, NativeModelReplays = replays, Cases = evidence },
            new JsonSerializerOptions { WriteIndented = true }));
        return $"StrategicTail PASS checks={checks} native_model_replays={replays}";
    }
}
