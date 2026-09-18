# CombatSolver runtime evidence

- Captured: 2026-09-18 11:48:46 +08:00
- Game: v0.107.1
- Mod: CombatSolver 0.40.2
- Source revision: unavailable; the working source tree has no Git metadata
- Reproduction: solver-only DEFECT combat, Ascension 6, floor 13, encounter `RUBY_RAIDERS_NORMAL` (Ruby Raiders trio)
- Package: `CombatSolver-0.40.2-RUBY_RAIDERS_NORMAL-6ea89c437c20428ba97a3b5eb0d81277.zip`

The solver failed during search action replay at turn 2, action 6, while playing `DEFEND_DEFECT`. The inner exception was:

`JugglingPower does not override AbstractModel.BeforeCardPlayed.`

This came from `BeforeCardPlayedMirrors.CreateRegistry()` registering `JugglingPower` against the wrong native hook. The current game implementation overrides `AfterCardPlayed`; the fix moves the existing prediction handler to that registry.

Included files are the combat log shard and log index only. Profile identifiers, submitter metadata, saves, screenshots, and full game-installation logs were intentionally omitted.
