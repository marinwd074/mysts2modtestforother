# 0.107.1 hook coverage boundary

The 0.107.1 runtime smoke loaded CombatSolver successfully and reported
`63 applied, 0 ignored, 0 failed` patches. The turn-setup patch targets were
verified in the running game through the native second-turn surface and the
full-auto smoke.

This is a runtime patch-registration result, not a claim that every game hook
or every card, power, relic, potion, and monster has an independent semantic
differential. The generated `docs/COMBAT_HOOK_COVERAGE.md` file contains older
catalog snapshots with their original version labels; regenerate it against a
0.107.1 assembly before using its counts as current coverage.

Current validated runtime path:

1. CombatSolver loads beside RitsuLib on STS2 `0.107.1`.
2. The 0.107.1 `SetupPlayerTurn(Player, HookPlayerChoiceContext)` and
   `RunAutoPrePlayPhase(HookPlayerChoiceContext, Task, Player)` targets apply.
3. The native turn-setup choice is observed and driven through the game hand
   surface.

