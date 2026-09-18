# Compatibility Smoke evidence

- Timestamp: 2026-09-18 14:11:38–14:12:13 Asia/Shanghai
- Game: Slay the Spire 2 v0.107.1, release commit `59260271`
- RitsuLib: 0.6.2, compatibility branch `0.107.1`
- CombatSolver: 0.40.2
- Source commit: `1601d53`
- Context: isolated headless instance `compat-20260918b`; Ironclad; seed `COMPAT1071`; CompatibilitySmoke NIBBITS encounter
- Command path: `source/tools/run-unattended-test.ps1` with a 2048 MiB isolated reservation

## Result

`compat-smoke-result.txt` reports:

`PASS: native 0.107.1 first-turn search; actions=20; incremental verification enabled`

The game log reports 63/63 CombatSolver patches applied and the 0.107.1 host/RitsuLib versions. The outer unattended launcher reports `launcher_failed` because CompatibilitySmoke intentionally calls `SceneTree.Quit()` after writing its result, so it does not emit the normal request/result protocol file.

## Evidence files

- `compat-smoke-result.txt` — smoke assertion result.
- `launcher-result.json` — outer launcher status and reason.
- `mod-startup-sanitized.log` — version, mod discovery, patch application, and combat-entry excerpts with machine paths and local debug endpoint data removed.
- `native-errors-sanitized.log` — headless renderer shutdown warnings observed after the intentional smoke exit.

No save, screenshot, crash dump, cache, or raw profile data is included.
