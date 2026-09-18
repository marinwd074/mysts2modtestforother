# Runtime evidence

This directory stores sanitized evidence from private Slay the Spire 2 and CombatSolver runs.

The Mod's runtime collector keeps its complete local capture outside GitHub, normally at
`D:\CombatSolverLab\`. It writes `index.jsonl` and compact `summaries/` entries for every
completed combat. A fixed in-memory ring buffer is flushed only at combat end. A new failure
fingerprint creates `raw/<date>/<run-id>/` and `regression-corpus/<fingerprint>/`; repeated or
diagnostically equivalent failures only increment the local occurrence count.

The raw directory contains the existing full bug-report ZIP plus `metadata.json` and
`events.jsonl`. The summary directory contains only `metadata.json` and `performance.json`.
Only a sanitized, genuinely new finding should be copied into this repository and committed.

Create one timestamped directory only when the run adds a new failure mode, a new validation conclusion, or new key evidence for an existing bug. Repeated runs and diagnostically equivalent failures are not committed. For an eligible run, use a timestamped directory such as:

```text
runtime-evidence/2026-09-18/20260918-120000-0.107.1/
  metadata.md
  game.log
  mod.log
  errors.md
  bug-report-bundle.zip
```

`metadata.md` should record the run time, game version, Mod version, source commit, reproduction context, and included files. Keep evidence separate from `source/` and `MODS/`. Remove credentials, tokens, cookies, account identifiers, and unrelated personal data before committing; retain stack traces, inner exceptions, action chronology, and relevant gameplay state.
