# Runtime evidence metadata

- Time: 2026-09-18 Asia/Shanghai
- Game: Slay the Spire 2 v0.107.1
- Mod: CombatSolver v0.40.2
- Source commit: `17da0582a2b40425454634251f7905dd0f67e270`
- Reproduction: CUBEX_CONSTRUCT_NORMAL issue bundle `9bd8ad3011e74f8196e9c80de9ab313a`, attempted `DeploySolver`.
- Run ID: `504dae2e89554865b0eb89a10dfabb93`
- Result: launcher preflight failed because the explicitly supplied build directory did not contain its required manifest and MemoryCleaner companion; the game did not start. This is a harness invocation failure, not a code result.
- Files: `launcher-result.json`, `preflight-failure.md`
