# CombatSolver — STS2 0.113.1 fork

[![CombatSolver compatibility consistency](https://github.com/marinwd074/mysts2modtestforother/actions/workflows/compatibility.yml/badge.svg?branch=main)](https://github.com/marinwd074/mysts2modtestforother/actions/workflows/compatibility.yml)

This repository contains an actively maintained CombatSolver fork targeting **Slay the Spire 2 v0.113.1**.

The fork keeps the upstream solver/simulation foundation while adding substantial 0.107.1 compatibility work, multiplayer search and execution support, pinned test harnesses, diagnostics, and repository tooling.

## Current scope

- Game target: **0.113.1**
- Runtime: C# / .NET 9 / Godot 4.5.1
- RitsuLib compatibility target: **0.113.0** (framework 0.6.11 or newer)
- Production project: [`source/`](source/)
- Historical 0.107.1 game snapshot: [`game-body/`](game-body/) through Git LFS
- Project milestone version: [source/PROJECT_VERSION](source/PROJECT_VERSION); [version history](source/docs/PROJECT_VERSION_HISTORY.md)
- Current project state: [`source/docs/CODEX_HANDOFF.md`](source/docs/CODEX_HANDOFF.md)
- Documentation index: [`source/docs/README.md`](source/docs/README.md)

Single-player and multiplayer share the same main search/simulation core. Network multiplayer starts in read-only Probe unless `COMBATSOLVER_MULTIPLAYER_MODE` explicitly enables `advisor` or `safe-execute`. Once search is enabled, production multiplayer defaults to the local single-player quality core with world-version tracking and local-player-only execution boundaries; teammate forecasting, team objectives, and scenario reevaluation remain behind the experimental multiplayer prediction switch. See [current multiplayer capabilities](source/docs/multiplayer/LIMITATIONS.md).

Rolling-horizon reuse product work is complete through final quality/response acceptance. F0 scenario prewarming remains shadow-only inside the experimental multiplayer prediction mode, and SSD cold storage is deferred; neither is required by the default local-single-core path.

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

The current Release build uses the local v0.113.1 game installation and the
RitsuLib `0.113.0` compatibility variant. The Git LFS snapshot and its pinned
validation workflows refer to the historical 0.107.1 source and game.

## Installation / compatibility

Place a matching local build in `mods/CombatSolver/` and enable RitsuLib.
See [current verification and limits](source/docs/TEST_MATRIX.md).

## Reporting bugs

For wrong routes, unexpected recalculation, Choice failures, multiplayer execution issues, or crashes, use the structured GitHub bug template. Route-quality reports are most useful when they include:

- the exported CombatSolver problem bundle;
- encounter and turn;
- the route selected by CombatSolver;
- a legal route you expected to be better.

Do not commit runtime evidence or problem ZIPs into the repository.

## Releases

Project milestone versions start at **1.01**: small updates add **0.01**, large feature or architecture updates add **0.10**. Each functional commit has a `project-vX.XX` tag and a detailed history entry. These tags record development milestones; they do not invoke the package-release workflow or change the upstream Mod/game compatibility version.

The tag workflow needs v0.113.1 game references before it can package this target. A release package contains:

- `CombatSolver.dll`
- `CombatSolver.json`
- `CombatSolver.MemoryCleaner.exe`
- `LICENSE`
- `THIRD_PARTY_NOTICES.md`
- SHA-256 checksum

The tag, project version, and manifest version must match.

## Upstream and contribution policy

Before porting upstream changes, read [UPSTREAM.md](UPSTREAM.md). Port upstream API and simulation changes selectively into the fork.

Contribution and validation rules are in [CONTRIBUTING.md](CONTRIBUTING.md).

## Repository layout

Every architectural directory has a local `README.md` describing its ownership and boundaries.

- [`source/`](source/README.md): active project.
  - [`source/src/`](source/src/README.md): production architecture and game-internal tests.
  - [`source/docs/`](source/docs/README.md): current documentation truth sources.
  - [`source/tools/`](source/tools/README.md): build, validation, harnesses and research tooling.
- [`game-body/`](game-body/README.md): historical STS2 0.107.1 compatibility snapshot.
- [`.github/`](.github/README.md): CI, issue forms, PR template and release automation.

Runtime logs, generated benchmark results, problem ZIPs, temporary evidence, `bin/`, `obj/`, `.local/`, and `artifacts/` are intentionally excluded from the active Git tree. See [repository maintenance rules](source/docs/REPOSITORY_MAINTENANCE.md).

Use the [tool task map](source/tools/README.md) to select a build or validation entry. The fast push/PR gate also checks local Markdown destinations against Git, so generated local reports cannot serve as permanent documentation links.

## License and source history

This fork retains the MIT license and upstream attribution.

Portions of the combat simulation foundation are derived from Random Foreseer. Required attribution and source-history details are retained in:

- [LICENSE](LICENSE)
- [source/THIRD_PARTY_NOTICES.md](source/THIRD_PARTY_NOTICES.md)

Primary CombatSolver upstream: [Torch1230/CombatSolver](https://github.com/Torch1230/CombatSolver).
