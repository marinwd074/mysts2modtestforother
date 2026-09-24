# Contributing

This repository is an active CombatSolver fork pinned to **Slay the Spire 2 0.107.1**. Contributions should preserve that target unless a change explicitly updates the compatibility baseline.

## Before opening an issue

Search existing issues first. For route-quality, unexpected recalculation, choice, or automatic-execution bugs, prefer a CombatSolver problem bundle over screenshots alone. Include:

- CombatSolver version or commit;
- game version;
- single-player or multiplayer mode;
- encounter and turn;
- the route CombatSolver chose;
- a legal route you expected to be better, when the report is about quality;
- the exported problem bundle or the smallest useful logs.

Remove personal or unrelated information before uploading logs or bundles.

## Development setup

Requirements:

- .NET 9 SDK;
- PowerShell 7 on Windows;
- the pinned 0.107.1 game references;
- RitsuLib compatibility references for 0.107.1.

The repository keeps the pinned game snapshot in `game-body/` through Git LFS. The normal local build entry is:

```powershell
pwsh -NoLogo -NoProfile -File source/tools/build-local-stack.ps1 -Configuration Release
```

Do not commit `local.props`, absolute machine paths, runtime logs, problem ZIPs, generated benchmark results, `.local/`, `artifacts/`, `bin/`, or `obj/`.

## Change policy

Keep each change focused. For solver changes, distinguish:

1. simulation correctness;
2. search/candidate quality;
3. multiplayer forecasting/authority;
4. live execution;
5. UI/diagnostics.

Do not change ranking weights merely to make one sample pass. Preserve a reproducible failing case or add a minimal contract/fixture when the behavior should remain fixed.

For multiplayer code:

- local-player authority and teammate forecasting must remain separate;
- teammate observations must not silently gain deployment authority;
- runtime evidence must not be represented as passing when only pinned/offline tests were run.

## Validation

Run the narrowest relevant checks first. Before merging a production change, the expected baseline is:

- `source/tools/verify-target-version.ps1`;
- `source/tools/verify-refactor-boundaries.ps1`;
- `source/tools/verify-repository-hygiene.ps1`;
- the relevant contract tests;
- Release build for changes that affect production code.

Changes to search, runtime multiplayer behavior, pinned compatibility, or deployment should also satisfy the repository's pinned 0.107.1 workflow. Real Host/Client behavior must still be tested in-game when the claim depends on network timing or native UI/actions.

## Pull requests

A pull request should explain the problem, the behavioral change, evidence, and remaining unverified boundaries. Do not attach generated evidence to the Git tree; link or attach it to the PR/issue instead.

Prefer **squash merge** for focused pull requests so `main` records one durable change rather than every exploratory commit. Keep separate commits only when their separation has lasting review or bisect value.

## Upstream and third-party code

Read [UPSTREAM.md](UPSTREAM.md) before importing upstream changes. This fork does not bulk-merge newer CombatSolver game-version assumptions into the 0.107.1 branch.

The MIT license and third-party attribution must remain intact. See [LICENSE](LICENSE) and [source/THIRD_PARTY_NOTICES.md](source/THIRD_PARTY_NOTICES.md).
