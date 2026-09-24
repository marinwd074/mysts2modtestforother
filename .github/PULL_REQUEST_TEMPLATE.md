## Summary

- What problem does this change solve?
- What behavior changes?

## Scope

- [ ] Simulation / card / monster semantics
- [ ] Search / ranking / performance
- [ ] Multiplayer forecast / objective
- [ ] Runtime / Safe Execute / Choice
- [ ] UI / diagnostics
- [ ] Build / CI / repository
- [ ] Docs only

## Validation

- [ ] Target-version guard passes
- [ ] Repository hygiene passes
- [ ] Architecture boundaries pass
- [ ] Relevant contract tests pass
- [ ] Release build passes when production code changed
- [ ] Pinned 0.107.1 checks pass when applicable
- [ ] Real runtime evidence attached when the claim depends on native or Host/Client behavior

## Evidence and boundaries

Describe the failing case before the change, evidence after the change, and anything still **UNVERIFIED**.

## Repository hygiene

- [ ] No runtime logs, generated results, problem ZIPs, local paths, credentials, or user data were committed
- [ ] `game-body/` was not changed unless the pinned baseline intentionally changed
- [ ] Upstream/third-party attribution was preserved where applicable
