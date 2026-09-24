# Slay the Spire 2 0.107.1 compatibility mod

This repository contains a modified *Slay the Spire 2* mod targeting game version `0.107.1`.

The active project, documentation, build files, and tests are under [`source/`](source/).

The project contains substantial modifications for 0.107.1 compatibility, search correctness,
multiplayer support, testing, and local development.

## License and third-party code

Portions of this project are derived from MIT-licensed upstream code. Copyright notices
required by the applicable licenses are retained in [LICENSE](LICENSE).

The combat simulation core also contains portions derived from
[Random Foreseer](https://github.com/hotwords123/StS2.RandomForeseer).
Required attribution and source-history details are retained in
[`source/THIRD_PARTY_NOTICES.md`](source/THIRD_PARTY_NOTICES.md).

## Repository layout

- `source/`: active code, tests, build tooling and current documentation.
- `game-body/`: pinned STS2 `0.107.1` compatibility snapshot used by CI/local verification. It is intentionally retained and tracked with Git LFS.
- Runtime logs, bug-report bundles, generated benchmark output and one-off investigation evidence are local artifacts and are not kept in the active Git tree.

Repository retention rules are documented in [source/docs/REPOSITORY_MAINTENANCE.md](source/docs/REPOSITORY_MAINTENANCE.md).
