# Issue-fix compatibility smoke evidence

- Timestamp: 2026-09-18 19:44 Asia/Shanghai
- Game: Slay the Spire 2 v0.107.1, release commit `59260271`
- RitsuLib: 0.6.2, compatibility branch `0.107.1`
- CombatSolver: 0.40.2
- Source baseline: `482d215` plus the uncommitted issue-fix changes in this task
- Context: isolated private headless game runtime; `CompatibilitySmoke=true`; mode `FIRST_TURN`; Ironclad/NIBBITS fixture with `COMPAT1071` seed
- Scope: integration startup, patch loading, first-turn search and incremental verification; not a full replay of the three submitted issue packages

## Result

`compat-smoke-result.txt`:

`PASS: native 0.107.1 first-turn search; actions=20; incremental verification enabled`

The sanitized startup evidence records `63/63` CombatSolver patches applied successfully. The original issue-package replays remain user-side validation because the 0.107.1 production assembly intentionally excludes `src/Testing`.
