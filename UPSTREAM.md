# Upstream policy

## Primary upstream

This repository is derived from **Torch1230/CombatSolver** and retains its MIT attribution. The fork intentionally maintains a pinned **STS2 0.107.1** compatibility target plus additional multiplayer, diagnostics, testing, and local-development work.

Upstream may target a newer game version. Therefore upstream updates are reviewed and ported selectively rather than merged wholesale.

## Reviewed updates (2026-10-02)

Reviewed upstream `d231e9e51a0e58d6bfa1373c6265cce13ffd9a45` through
`53c26b26c50bec027de8f7d2c76deb223cd6971d` (manifest 0.47.3, game 0.111.0).
The fork continues to target 0.107.1. Each row records an adapted functional commit;
the corresponding `project-v` tag and version log identify its local implementation.

| Project version | Upstream reference | Adaptation |
| --- | --- | --- |
| 4.28 | [512b142](https://github.com/Torch1230/CombatSolver/commit/512b142909da67e25f640dcaba4fdc75486beea5), [PR 140](https://github.com/Torch1230/CombatSolver/pull/140) | Delay mod ModelId caching until the pinned Ritsu registry freezes; pin mod files at startup; share worker identity across the API and dispatcher, keeping UI monitoring out of the isolated worker. Preserve fork request retries, owned cleanup, diagnostics and release-only worker protocol. |
| 4.29 | [e6dc57a](https://github.com/Torch1230/CombatSolver/commit/e6dc57ae320061b900b2bd5b9a8e91b0a14d9c95), [79f8ff7](https://github.com/Torch1230/CombatSolver/commit/79f8ff7310cc6472d26250d36c39341760ae7c71) | Include ordered energy/star cost modifiers and expiration in card fingerprints, choices and continuation text. Skip impossible duplicate representatives and share tokens within one choice construction. Preserve pinned Abundance behavior, local card-value caching and physical occurrence order. |
| 4.30 | [30e6cd0](https://github.com/Torch1230/CombatSolver/commit/30e6cd03624027452eb476b7e39caee99ba8daf5), [7236330](https://github.com/Torch1230/CombatSolver/commit/72363308c83d7543ddb139ad62ec2161d013d469), [1fd3886](https://github.com/Torch1230/CombatSolver/commit/1fd388693e3abf486316380a0807fac363e71083) | Separate physical capacity from GC pressure in the memory bar; use aggressive managed-heap decommit for manual release instead of paging out live game data. Retain recoverable NoGC retries with a saturated 60-second cooldown; explicit exits remain permanent. Preserve fork lifecycle accounting, configuration caps and the separate, user-requested system cleanup helper. |

The remaining changes are retained as references with these boundaries:

- The large request/frontier/budget/plan-horizon refactor is not substituted for the fork's rolling-horizon pipeline. Its drained-work accounting is useful design evidence, but adapters must preserve local-core multiplayer capture, caller cancellation, route authority and shared ordering. Request hydration remains default-off.
- [Entropic Brew accounting](https://github.com/Torch1230/CombatSolver/commit/6e62cd1b375ac02e45e2d0f4cbb301cc18b69431) is a useful follow-up: generated potions should not be charged again as pre-existing inventory. Importing only the free flag would leave final ordering, forced-slot directives, per-turn cost reporting and retained-route accounting inconsistent. This port does not change those policies; the complete cost-provenance boundary needs a separate adaptation.
- Runtime-type mirror registration (PR 139) adds an adapter surface the fork does not currently expose. Keep the existing exact registration/freeze and unsupported-hook boundary until a concrete pinned mod adapter requires it.
- Newer Swift/Abundance behavior, `AssemblyInfo` APIs, game/Ritsu upgrades and automatic runtime-GC configuration are version-bound. Keep the pinned implementations and frozen game body. New upstream telemetry, private uploads and online services remain excluded.
- Existing local fixes and optimizations, including path-dependent R0 score reconstruction, root-history capture, intrinsic card-value caching and nearest-equal combination pruning, are preserved rather than replaced by upstream equivalents.

## Import rules

Classify upstream changes before porting them:

- **Portable:** generic search correctness, performance work, UI fixes, tooling, documentation patterns, or bug fixes whose game semantics are unchanged.
- **Version-bound:** cards, relics, monsters, native APIs, RitsuLib APIs, reflection members, serialization, or hooks that changed after 0.107.1.
- **Fork-conflicting:** changes that replace local multiplayer architecture, pinned compatibility contracts, repository governance, or intentionally removed background online services.

For portable changes, preserve the upstream attribution and original intent while adapting names and APIs only as needed.

For version-bound changes, verify against the pinned 0.107.1 DLL/game data before changing production behavior. Do not use a current wiki or current upstream implementation as proof of 0.107.1 semantics.

For fork-conflicting changes, document the reason for not importing them. Do not reintroduce background telemetry, automatic private uploads, or server endpoint/token metadata without an explicit product decision.

## Verification after a port

At minimum:

1. verify the pinned target;
2. run architecture/repository hygiene checks;
3. run the narrowest relevant contracts;
4. build Release against pinned 0.107.1 references;
5. run pinned harnesses when search/runtime behavior changed;
6. use real game or Host/Client evidence when native timing or multiplayer behavior is part of the claim.

## Other reference repositories

Repository-management ideas may be borrowed from maintained STS2 projects such as RitsuLib and Mega Crit tooling, but their build/release assumptions are not automatically compatible with this fork.

Third-party code provenance remains documented in [source/THIRD_PARTY_NOTICES.md](source/THIRD_PARTY_NOTICES.md).
