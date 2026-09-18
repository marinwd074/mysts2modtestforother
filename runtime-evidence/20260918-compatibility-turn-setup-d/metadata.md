# 0.107.1 turn-setup Compatibility Smoke evidence

- Timestamp: 2026-09-18 14:24:39–14:25:17 Asia/Shanghai
- Game: Slay the Spire 2 v0.107.1, release commit `59260271`
- RitsuLib: 0.6.2, compatibility branch `0.107.1`
- CombatSolver: 0.40.2
- Source commit: `7a73732`
- Context: isolated headless instance `compat-turn-20260918-d` on D:, Ironclad, seed `COMPAT1071`, NIBBITS encounter
- Mode: `COMPAT1071_TURN_SETUP`

## Result

`compat-smoke-result.txt` reports:

`PASS: native 0.107.1 turn setup; turn=2; surface=True; selected=True; continuation=true`

This confirms the 0.107.1 turn-setup patch reached the native hand surface, drove the second-turn choice, and continued back to Play. The outer launcher reports `launcher_failed` because the compatibility smoke intentionally quits the game after writing its result instead of emitting the normal request/result protocol file.

## Evidence files

- `compat-smoke-result.txt` — smoke assertion result.
- `launcher-result.json` — outer launcher status and reason.
- `mod-startup-sanitized.log` — version, mod discovery, patch application, native choice, and turn continuation excerpts with local paths and endpoint data removed.
- `native-errors-sanitized.log` — headless renderer shutdown warnings observed after the intentional smoke exit.

No save, screenshot, crash dump, cache, or raw profile data is included.
