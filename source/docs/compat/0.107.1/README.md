# STS2 0.107.1 compatibility evidence

This directory is the current evidence chain for the `0.107.1` target. Older
coverage and adaptation reports retain their historical version labels and are
not silently reinterpreted as 0.107.1 results.

| Topic | Current record |
|---|---|
| Build and static target guard | [BUILD_VERIFICATION.md](BUILD_VERIFICATION.md) |
| Source compatibility boundary | [Compatibility README](../../../src/Compatibility/README.md) |
| Layered test entry points | [测试分层与入口](../../TESTING_LAYERS.md) |
| Hook/patch coverage | [HOOK_COVERAGE.md](HOOK_COVERAGE.md) |
| Native-vs-predicted differential | [NATIVE_DIFFERENTIAL.md](NATIVE_DIFFERENTIAL.md) |
| First-turn and incremental search | [SEARCH_SMOKE.md](SEARCH_SMOKE.md) |
| Native turn setup | [TURN_SETUP_SMOKE.md](TURN_SETUP_SMOKE.md) |
| Full-auto continuation | [FULL_AUTO_SMOKE.md](FULL_AUTO_SMOKE.md) |
| Full battle lifecycle | [FULL_BATTLE_SMOKE.md](FULL_BATTLE_SMOKE.md) |
| Known limitations and unrun claims | [KNOWN_LIMITATIONS.md](KNOWN_LIMITATIONS.md) |

Runtime evidence is stored under the repository root's
[`runtime-evidence/`](../../../../runtime-evidence/) directory. Each run keeps
the source commit, game/RitsuLib versions, context, result, and sanitized
startup/error excerpts together.
