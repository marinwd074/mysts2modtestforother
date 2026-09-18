# Known limitations

- The current runtime evidence is Windows headless evidence. It does not
  certify visible Steam rendering, animation timing, audio, or input focus.
- The two compatibility smoke runs intentionally exit through the custom smoke
  result path; their launcher status is not a normal unattended protocol pass.
- Full native differential coverage and a complete per-effect 0.107.1 catalog
  have not been regenerated in this stage.
- The full-auto smoke reaches route reuse and turn 3 but stops while combat is
  still in progress.
- Search/beam/GC/performance changes are outside the smoke's functional claim.
  See `docs/PERFORMANCE_GUARDRAILS.md` for the required baseline procedure.
- Any historical document that names STS2 `0.111.0` remains historical until
  a new 0.107.1 evidence record explicitly supersedes it.
