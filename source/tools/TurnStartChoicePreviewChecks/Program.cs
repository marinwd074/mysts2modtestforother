using CombatSolver;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

PlanCardChoice setup = new("ROOT_RELIC");
PlanCardChoice fromTurn1 = new("TURN1_POWER");
PlanCardChoice fromTurn2 = new("TURN2_RELIC");
PlanAction[] actions =
[
    new(1, [fromTurn1]),
    new(2, [fromTurn2]),
    new(3),
];

IReadOnlyList<PlanCardChoice> t1 = TurnStartChoicePreviewPolicy.ChoicesForTurn(1, 1, [setup], actions);
Check(t1.SequenceEqual([setup]), "root setup choice attribution changed");

IReadOnlyList<PlanCardChoice> t2 = TurnStartChoicePreviewPolicy.ChoicesForTurn(2, 1, [setup], actions);
Check(t2.SequenceEqual([fromTurn1]), "turn 2 did not inherit turn 1 EndTurn choice");

IReadOnlyList<PlanCardChoice> t3 = TurnStartChoicePreviewPolicy.ChoicesForTurn(3, 1, [setup], actions);
Check(t3.SequenceEqual([fromTurn2]), "turn 3 did not inherit turn 2 EndTurn choice");

IReadOnlyList<PlanCardChoice> reused = TurnStartChoicePreviewPolicy.ChoicesForTurn(2, 2, [], actions);
Check(reused.SequenceEqual([fromTurn1]), "reused route lost preceding turn choice");

PlanAction[] noChoice = [new(1), new(2)];
Check(TurnStartChoicePreviewPolicy.ChoicesForTurn(2, 1, [setup], noChoice).Count == 0,
    "choice leaked to a later turn without EndTurn publication");

Console.WriteLine($"TURN_START_CHOICE_PREVIEW_CHECKS_PASS checks={checks}");
