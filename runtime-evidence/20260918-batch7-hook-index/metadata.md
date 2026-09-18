# Batch 7 Hook listener index evidence

- Time: 2026-09-18 (Asia/Shanghai)
- Game: Slay the Spire 2 `0.107.1`
- RitsuLib: `0.107.1` compatibility package (`0.6.2`)
- Mod: CombatSolver `0.40.2`
- Baseline source: `e2fefb3` (`perf: establish 0.107.1 search baseline`)
- Candidate source: baseline plus the working-tree `MirroredHookListenerLayout` / `HookListenerEnumerable` index change, recorded in the Batch 7 task commit
- Fixture: `COMPAT1071_PERFORMANCE_BASELINE`, IRONCLAD, `NIBBITS_NORMAL`, run seed `COMPAT1071`
- Search profile: effective Medium, beam `60`, DOP `1`, fixed budget `5000 ms`, Smart potion policy, Beam Width Portfolio enabled, Novelty Portfolio disabled
- Performance collection: `baseline-01`, `candidate-01`, `candidate-02`, `baseline-02`, `baseline-03`, `candidate-03` in A-B-B-A-A-B order; each sample used a separate Windows headless instance and frozen artifact directory
- Semantic smoke: `batch7-first-turn.json` used the candidate artifact and wrote `PASS: native 0.107.1 first-turn search; actions=20; incremental verification enabled`
- Scope: dedicated smoke JSON only; the launcher’s missing normal `result.json` exit warning is expected for this mode and is not a search failure
- Files: six sanitized performance JSON files and one sanitized FIRST_TURN result; no account, token, cookie, save, crash dump, complete game installation, or raw game log copied
