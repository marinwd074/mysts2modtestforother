using CombatSolver;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

PotionStrategySnapshot strategy = new(
    SolverPotionPolicy.Smart,
    [
        new PotionSlotDirective(0, "ENERGY_POTION", SolverPotionDirective.Force),
        new PotionSlotDirective(1, "FIRE_POTION", SolverPotionDirective.Smart),
        new PotionSlotDirective(2, "GHOST_IN_A_JAR", SolverPotionDirective.Disabled),
    ]);

Check(strategy.HasForcedDirectives, "forced directive not detected");
Check(strategy.ForcedDirectiveCount == 1, "forced directive count wrong");
Check(strategy.AllowsExplicitUse(0, "ENERGY_POTION", SolverPotionPolicy.Smart, false), "forced potion blocked");
Check(strategy.AllowsExplicitUse(1, "FIRE_POTION", SolverPotionPolicy.Smart, false), "smart potion blocked");
Check(!strategy.AllowsExplicitUse(2, "GHOST_IN_A_JAR", SolverPotionPolicy.Smart, false), "disabled potion allowed");

PotionStrategySnapshot forced = strategy.ForForcedBaseline();
Check(forced.ForcedDirectiveCount == 1, "forced baseline lost directive count");
Check(forced.AllowsExplicitUse(0, "ENERGY_POTION", SolverPotionPolicy.Smart, false), "forced baseline blocked mandatory potion");
Check(!forced.AllowsExplicitUse(1, "FIRE_POTION", SolverPotionPolicy.Smart, false), "forced baseline allowed optional potion");
Check(!forced.AllowsExplicitUse(99, "GENERATED_POTION", SolverPotionPolicy.Smart, false), "forced baseline allowed unknown potion");
Check(strategy.AllowsExplicitUse(1, "FIRE_POTION", SolverPotionPolicy.Smart, false), "forced baseline mutated original strategy");

ForcedPotionUseEvaluation eval = strategy.EvaluateForcedUses(
    [new PlanAction(PlanActionKind.UsePotion, 0, "ENERGY_POTION")],
    renewablePotionShapedRock: false);
Check(eval.AllForcedUsesSatisfied && eval.ForcedUseCount == 1, "forced use evaluation changed");

PotionStrategySnapshot ambergris = new(
    SolverPotionPolicy.Smart,
    [new PotionSlotDirective(3, "AMBERGRIS", SolverPotionDirective.Force)]);
ForcedPotionUseEvaluation ambergrisEval = ambergris.EvaluateForcedUses(
    [new PlanAction(PlanActionKind.UsePotion, 3, "AMBERGRIS")],
    renewablePotionShapedRock: false);
Check(ambergrisEval.ForcedAmbergrisCount == 1 && ambergrisEval.ForcedStrategicHpCost == 4,
    "forced opportunity cost accounting changed");

Console.WriteLine($"POTION_STRATEGY_CHECKS_PASS checks={checks}");
