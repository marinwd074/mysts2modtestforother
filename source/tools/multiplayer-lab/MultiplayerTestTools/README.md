# TheBookOfAges test-only integration

This directory is a **test-only multiplayer tool dependency**.

- Upstream: `253506088/TheBookOfAges-public`
- Pinned upstream commit: `234a74ccbaf46d7e385ed318c64857f1f7a90cae`
- Upstream manifest at that revision: `TheBookOfAges v1.0.8`, `min_game_version=0.107.1`
- Purpose here: multiplayer fixture construction / debugging only.
- Full upstream UI, localization, assets, GM services and multiplayer synchronization code are intentionally retained.
- It is not linked into the CombatSolver production project, release package, or normal runtime.
- The project owner confirmed permission from the upstream author to integrate this test copy/dependency.
- Keep upstream author/project metadata intact.
- When multiplayer fixture work is finished, this whole test-only module can be removed without changing CombatSolver runtime code.

Checkout:

~~~powershell
git submodule update --init --recursive -- source/tools/multiplayer-lab/MultiplayerTestTools/TheBookOfAges
~~~

The normal Lab installer can still consume the Workshop-built version for runtime testing.
The submodule exists so Codex can inspect, patch, and build the full GM Console source/UI locally when needed.
