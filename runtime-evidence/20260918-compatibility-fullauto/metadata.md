# 0.107.1 full-auto Compatibility Smoke evidence

- Timestamp: 2026-09-18 14:26:34–14:27:23 Asia/Shanghai (result written at 14:27:21)
- Game: Slay the Spire 2 v0.107.1, release commit `59260271`
- RitsuLib: 0.6.2, compatibility branch `0.107.1`
- CombatSolver: 0.40.2
- Source commit: `7a73732`
- Context: isolated headless instance `compat-fullauto-20260918` on D:, Ironclad, seed `COMPAT1071`, NIBBITS encounter
- Mode: `COMPAT1071_FULLAUTO`
- Search budget: production/default settings; no smoke-specific budget override

## Result

`compat-smoke-result.txt` reports:

`PASS: native 0.107.1 full-auto turn setup; setup_turn=2; selected=True; deployed=True; next_turn=3; route_reuse=True; combat_in_progress=True`

This confirms automatic takeover of the native turn-setup choice, deployment, entry into turn 3, and route reuse under the default search budget. The outer launcher reports `launcher_failed` because the compatibility smoke intentionally quits after writing its result instead of emitting the normal request/result protocol file. The smoke stops after route reuse is observed; it does not claim a complete battle victory.

## Evidence files

- `compat-smoke-result.txt` — smoke assertion result.
- `launcher-result.json` — outer launcher status and reason.
- `mod-startup-sanitized.log` — version, mod discovery, patch application, and combat excerpts with local paths and endpoint data removed.
- `native-errors-sanitized.log` — headless renderer and focus warnings observed after the intentional smoke exit.

No save, screenshot, crash dump, cache, or raw profile data is included.
