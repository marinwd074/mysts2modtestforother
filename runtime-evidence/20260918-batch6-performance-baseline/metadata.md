# Batch 6 performance baseline evidence

- Time: 2026-09-18 (Asia/Shanghai)
- Game: Slay the Spire 2 `0.107.1`
- RitsuLib: `0.107.1` compatibility package
- Mod: CombatSolver `0.40.2`
- Source commit: `43c11c6` parent plus the Batch 6 working-tree changes recorded in this task commit
- Fixture: `COMPAT1071_PERFORMANCE_BASELINE`, IRONCLAD, `NIBBITS_NORMAL`, run seed `COMPAT1071`
- Search profile: effective Medium profile, beam `60`, DOP `1`, fixed budget `5000 ms`, Smart potion policy, Beam Width Portfolio enabled, Novelty Portfolio disabled
- Collection: three `baseline-*` samples and three `candidate-control-*` samples in A-B-B-A-A-B order; the control group used the same binary because Batch 6 intentionally changed no algorithm
- Runner: Windows headless native game process with a 2048 MiB instance reservation; the instance root was moved to `D:\yingye` because `C:` had only about 92 MiB free
- Scope: dedicated smoke JSON only; the launcher’s missing normal `result.json` exit warning is expected for this mode and is not a search failure

The six JSON files contain the sanitized search metrics and winning route/result identity. No account, token, cookie, save, crash dump, or complete game installation was copied here.
