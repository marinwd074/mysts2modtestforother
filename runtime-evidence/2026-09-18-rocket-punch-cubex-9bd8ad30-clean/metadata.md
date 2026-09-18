# Runtime evidence metadata

- Time: 2026-09-18 12:57–12:59 Asia/Shanghai
- Game: Slay the Spire 2 v0.107.1
- Mod: CombatSolver v0.40.2; RitsuLib v0.6.2
- Source commit: `17da0582a2b40425454634251f7905dd0f67e270`; active source was `D:\yingye\MODDEV\ports\upstream-0.107.1` (no Git metadata)
- Build: Release `CombatSolver.dll`, SHA-256 `657370E1CAA63FCF31349319AAD3736C4E624C14B0810B5B14B4360C7CC7B44A`
- Reproduction: CUBEX_CONSTRUCT_NORMAL issue bundle `9bd8ad3011e74f8196e9c80de9ab313a`, `CheckpointSelector=start`, `ReplayMode=DeploySolver`, Instant headless/deployment timing, 120-second timeout.
- Run: `FIX-ROCKET-PUNCH-CUBEX`, run ID `4aed021648054af0b6d3963c069b7004`
- Result: the isolated game started, loaded two Mods, and applied CombatSolver's 60/60 patches; `DeploySolver` timed out without writing a solver result. This is not recorded as Passed and contains no state-mismatch conclusion.
- Files: `launcher-result.json`, `startup-and-timeout.md`
- Sanitization: full private headless logs were not copied; the retained excerpt removes local paths and environment data while preserving version, patch, load, timeout, and result facts.
