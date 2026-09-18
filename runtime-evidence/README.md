# Runtime evidence

This directory stores sanitized evidence from private Slay the Spire 2 and CombatSolver runs.

Create one timestamped directory per run, for example:

```text
runtime-evidence/2026-09-18/20260918-120000-0.107.1/
  metadata.md
  game.log
  mod.log
  errors.md
  bug-report-bundle.zip
```

`metadata.md` should record the run time, game version, Mod version, source commit, reproduction context, and included files. Keep evidence separate from `source/` and `MODS/`. Remove credentials, tokens, cookies, account identifiers, and unrelated personal data before committing; retain stack traces, inner exceptions, action chronology, and relevant gameplay state.
