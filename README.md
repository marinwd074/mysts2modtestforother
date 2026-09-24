# CombatSolver — STS2 0.107.1 fork

[![0.107.1 compatibility consistency](https://github.com/marinwd074/mysts2modtestforother/actions/workflows/compatibility.yml/badge.svg?branch=main)](https://github.com/marinwd074/mysts2modtestforother/actions/workflows/compatibility.yml)
[![Pinned 0.107.1 Release build](https://github.com/marinwd074/mysts2modtestforother/actions/workflows/pinned-release-build.yml/badge.svg?branch=main)](https://github.com/marinwd074/mysts2modtestforother/actions/workflows/pinned-release-build.yml)

This repository contains an actively maintained CombatSolver fork pinned to **Slay the Spire 2 0.107.1**.

The fork keeps the upstream solver/simulation foundation while adding substantial 0.107.1 compatibility work, multiplayer search and execution support, pinned test harnesses, diagnostics, and repository tooling.

## Current scope

- Game target: **0.107.1**
- Runtime: C# / .NET 9 / Godot 4.5.1
- RitsuLib compatibility target: **0.107.1**
- Production project: [`source/`](source/)
- Pinned game snapshot: [`game-body/`](game-body/) through Git LFS
- Current project state: [`source/docs/CODEX_HANDOFF.md`](source/docs/CODEX_HANDOFF.md)
- Documentation index: [`source/docs/README.md`](source/docs/README.md)

Single-player and multiplayer share the same main search/simulation core. Multiplayer adds teammate forecasting, team objectives, world-version tracking, and local-player-only execution boundaries.

Background presence telemetry, automatic run-statistics upload, server update checks, and automatic private Showcase upload were intentionally removed. User-triggered problem-report upload remains separate.

## Development

Windows:

```powershell
pwsh -NoLogo -NoProfile -File source/tools/build-local-stack.ps1 -Configuration Release
```

Repository checks:

```powershell
pwsh -NoLogo -NoProfile -File source/tools/verify-target-version.ps1
pwsh -NoLogo -NoProfile -File source/tools/verify-refactor-boundaries.ps1
pwsh -NoLogo -NoProfile -File source/tools/verify-repository-hygiene.ps1
```

The pinned Release workflow builds against the checked-in 0.107.1 game snapshot plus the pinned RitsuLib compatibility package.

## Installation / compatibility

This fork is **not a latest-game build**. Use it only with the pinned STS2 `0.107.1` environment and the compatible RitsuLib target.

For a GitHub Release build, extract the release contents into the game's `mods/CombatSolver/` directory and ensure the matching RitsuLib compatibility runtime is installed. Do not mix the 0.107.1 fork DLL with a newer-game CombatSolver/RitsuLib installation.

If you are running the current retail game rather than 0.107.1, use the primary upstream CombatSolver instead of assuming this fork is compatible.

## Reporting bugs

For wrong routes, unexpected recalculation, Choice failures, multiplayer execution issues, or crashes, use the structured GitHub bug template. Route-quality reports are most useful when they include:

- the exported CombatSolver problem bundle;
- encounter and turn;
- the route selected by CombatSolver;
- a legal route you expected to be better.

Do not commit runtime evidence or problem ZIPs into the repository.

## Releases

Pushing a version tag in the form `vMAJOR.MINOR.PATCH` runs the pinned 0.107.1 Release build and prepares a **draft GitHub Release** containing:

- `CombatSolver.dll`
- `CombatSolver.json`
- `CombatSolver.MemoryCleaner.exe`
- `LICENSE`
- `THIRD_PARTY_NOTICES.md`
- SHA-256 checksum

The tag, project version, and manifest version must match.

## Upstream and contribution policy

Before porting upstream changes, read [UPSTREAM.md](UPSTREAM.md). Newer upstream game-version assumptions must not be bulk-merged into the pinned 0.107.1 branch.

Contribution and validation rules are in [CONTRIBUTING.md](CONTRIBUTING.md).

## Repository layout

- `source/`: active code, tests, build tooling, and current documentation.
- `game-body/`: pinned STS2 0.107.1 compatibility snapshot used by CI/local verification.
- `.github/`: CI, issue forms, PR template, and tag-release automation.

Runtime logs, generated benchmark results, problem ZIPs, temporary evidence, `bin/`, `obj/`, `.local/`, and `artifacts/` are intentionally excluded from the active Git tree. See [repository maintenance rules](source/docs/REPOSITORY_MAINTENANCE.md).

## License and source history

This fork retains the MIT license and upstream attribution.

Portions of the combat simulation foundation are derived from Random Foreseer. Required attribution and source-history details are retained in:

- [LICENSE](LICENSE)
- [source/THIRD_PARTY_NOTICES.md](source/THIRD_PARTY_NOTICES.md)

Primary CombatSolver upstream: [Torch1230/CombatSolver](https://github.com/Torch1230/CombatSolver).
