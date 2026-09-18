# Runtime evidence metadata

- Time: 2026-09-18 Asia/Shanghai
- Game: Slay the Spire 2 v0.107.1
- Mod: CombatSolver v0.40.2; RitsuLib v0.6.2
- Source commit: `17da0582a2b40425454634251f7905dd0f67e270`
- Reproduction: CUBEX_CONSTRUCT_NORMAL issue bundle `9bd8ad3011e74f8196e9c80de9ab313a`, `CheckpointSelector=start`, `ReplayMode=DeploySolver`.
- Run ID: `2afd3f303e454a9cb680054fa117fd72`
- Result: the copied install contained both the old direct CombatSolver files and the injected headless Mod directory; the game reported a duplicate Mod ID and the launcher timed out without a solver result. This is deployment setup evidence, not a code result.
- Files: `launcher-result.json`, `mod-load-error.md`
