# Upstream policy

## Primary upstream

This repository is derived from **Torch1230/CombatSolver** and retains its MIT attribution. The fork intentionally maintains a pinned **STS2 0.107.1** compatibility target plus additional multiplayer, diagnostics, testing, and local-development work.

Upstream may target a newer game version. Therefore upstream updates are reviewed and ported selectively rather than merged wholesale.

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
