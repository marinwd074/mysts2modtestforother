# Upstream policy

## Primary upstream

This repository is derived from **Torch1230/CombatSolver** and retains its MIT attribution. The fork intentionally maintains a pinned **STS2 0.107.1** compatibility target plus additional multiplayer, diagnostics, testing, and local-development work.

Upstream may target a newer game version. Therefore upstream updates are reviewed and ported selectively rather than merged wholesale.

## Follow-up review (2026-11-05)

GitHub still reports `5c773caaec742bc55588bc9ee21c361326a732f0` as the mainline head.
The upstream [archived multiplayer ENet runner](https://github.com/Torch1230/CombatSolver/blob/14b36a28/tools/run-multiplayer-p0-enet.ps1)
is a useful transport-test reference: it launches separate peers and checks each peer's result,
but pins game 0.111.0 and launches actual headless game instances. It has not been imported or run here.

For the no-game layer, borrow the controlled-event-order and reproducible-failure approach described in
[Microsoft Coyote](https://github.com/microsoft/coyote/blob/926a8da7e36b150755ea4884be900f8bd8552d5d/docs/get-started/using-coyote.md).
`MultiplayerInterleavingChecks` directly compiles the fork's production observer/session/policy contracts;
it does not add Coyote, rewrite game binaries, or claim to simulate the full controller/transport.
The instance observer accepts monotonic time; the production facade retains the 200-ms debounce and main-thread ownership.
Schedules cover duplicate observations, stale read/confirm races, cancellation, turn/route/world identities,
at-most-once completion/EndTurn and independent observer/local authority boundaries. Failure output includes a replay ID.

Selectively adapt the cost-snapshot idea from upstream
[6e62cd1b](https://github.com/Torch1230/CombatSolver/commit/6e62cd1b375ac02e45e2d0f4cbb301cc18b69431):
capture `ExplicitPotionStrategicCost` in the existing branch-ledger aggregation loop and use the frozen value
in beam final-policy qualification and final route ordering. This removes repeated ID-price lookups and one
action-list sum per candidate without retaining the simulator or changing request budgets.
Automatic consumption stays in total cost but is excluded from explicit cost.
That first cost-snapshot change was validated against 17 unchanged requests before the provenance adaptation below.

The follow-up completes Entropic Brew accounting for the pinned branch:

- Track root inventory, ordinary procurement and Brew generation in branch-owned COW state; include origin in inventory/cycle keys and consumed root identities in the combat key. Generated consumption, including automatic use and a generated Brew, costs zero; ordinary refill resets origin.
- Root slot directives apply only to the original bottle. Evaluate forced cost from actual uses/value snapshots, never by repricing the latest same-ID refill. The forced-only baseline still excludes optional generated uses, preserving the gradient scheduler contract.
- Generated Ambergris has no paid opportunity premium. Beam qualification, final ordering, coordinator comparison, per-turn reports and retained continuations use the same recorded costs. Results retain a read-only value ledger with turns, not simulators.
- Admit generated-use layers from eligible root Brews (including forced Brews), within the existing shared work budgets. Do not apply the root-price-times-use-count Smart bound to a Brew root. This is bounded search admission, not an exhaustive guarantee for every chain of generated Brews.
- Origins are relative to the frozen request root. A retained plan keeps its generated costs; a newly captured root treats bottles already present as inventory. No live game/save provenance is introduced.

`entropic-cost` checks the pinned native factory sequence, origin/Fork/key isolation, ordinary refill,
automatic consumption, generated Brew/Ambergris, admission, full search/materialization and independent replay.
Cross-turn costs use native-model replay plus production annotations and a synthetic retained-result contract;
the production route-adoption gate still rejects potion routes. These checks do not establish UI/Host-Client timing.
Ambergris is absent from the pinned 0.107.1 catalog: its opportunity-premium behavior is verified only
by the pure policy contract, not by a fabricated native model.

## Current integration (2026-11-02)

Compared upstream `53c26b26c50bec027de8f7d2c76deb223cd6971d` through
`5c773caaec742bc55588bc9ee21c361326a732f0` (0.50.1, game 0.111.0; 341 commits including merges).
Project **4.32** adapts the state/choice fixes below; **4.42** adds the bounded search adaptations that follow, all for **0.107.1**.
This is an integration review marker, not a wholesale Git merge or a claim that all intervening changes were imported.

| Upstream reference | Adaptation |
| --- | --- |
| [8312bdf6](https://github.com/Torch1230/CombatSolver/commit/8312bdf6) | Cached DynamicVarSet field bridge with public-enumeration fallback; retain local card-value and strategic-context caches. |
| [aa970774](https://github.com/Torch1230/CombatSolver/commit/aa970774) | Reject changed live card collections during root capture, including same-count writes; preserve local-player-only capture. |
| [21a5abdf](https://github.com/Torch1230/CombatSolver/commit/21a5abdf), [58427631](https://github.com/Torch1230/CombatSolver/commit/58427631) | Preserve card identity across COW previews; invalidate listener projections on pile moves; clean temporary star costs; preserve batch-transform/Seeker option order and terminal AfterCardPlayed capture. Keep pinned CardPlay ownership through `Card.Owner`. |
| [b61224f6](https://github.com/Torch1230/CombatSolver/commit/b61224f6) | Carry nested card/potion effect ownership through execution checkpoints; defer empty-hand callbacks and reject ordinary Fork inside an active effect. |
| [1171c5f5](https://github.com/Torch1230/CombatSolver/commit/1171c5f5) | Saturate overflowing block multiplication at settlement, preserving later modifiers and zero block. Check ordinary Shadowmeld factors against pinned native behavior. |
| [89798a78](https://github.com/Torch1230/CombatSolver/commit/89798a78), [c866ada7](https://github.com/Torch1230/CombatSolver/commit/c866ada7) | Include readable players' captured hand limits in state keys/continuation. Preserve distinct residual pile orders under an active Sandpit countdown, retaining ordinary routing representatives and configured budgets. |
| [2bdbf437](https://github.com/Torch1230/CombatSolver/commit/2bdbf437), [5249110e](https://github.com/Torch1230/CombatSolver/commit/5249110e) | Report multiple pile differences, preserving all-stream RNG diagnostics and caller limits. Validate ChooseCard instances, native opening time and accepted completion; retain existing deployment-session ownership. |

Already covered locally: root-owned calculated history (including Voltaic), intrinsic card-value caching,
cost-expiration identity, branch-owned turn energy and local-core multiplayer isolation. Keep those implementations.

Search adaptations in **4.42** retain the fork's coordinator, rolling-horizon ownership and shared budgets:

| Upstream reference | Adaptation |
| --- | --- |
| [a820dcff](https://github.com/Torch1230/CombatSolver/commit/a820dcff), [45f87cd3](https://github.com/Torch1230/CombatSolver/commit/45f87cd3) | A separate pinned healing certificate admits only audited starter/status cards, powers, relics, potions and FuzzyWurmCrawler/Nibbit enemies. Root/run listeners, mod subscribers and foreign patches to the native assembly are checked at root capture. Unknown sources or risky branches retain full healing headroom. The newer native certificate is not reused. |
| [d9f8bc0e](https://github.com/Torch1230/CombatSolver/commit/d9f8bc0e), [9d08f8df](https://github.com/Torch1230/CombatSolver/commit/9d08f8df) | Request-owned, exact-root sharing of finalized, eligible no-potion victory bounds and their complete route witnesses. Only closed zero-potion members consume the shared bound; strictly worse optimistic HP can be pruned. Final selection can retain the witness. No simulator or execution authority is transferred. |
| [be19b370](https://github.com/Torch1230/CombatSolver/commit/be19b370), [PR 207](https://github.com/Torch1230/CombatSolver/pull/207) | Single-player Smart admission sums actual eligible slot costs, keeps possible healing of root missing HP, and prunes certified exact-use branches that cannot meet the audited no-potion threshold. Final ranking and exact-layer incumbents honor optional-potion opportunity cost; saving a death-prevention resource remains protected. Keep the existing gradient scheduler and early-stop policy. |
| [5d9a2d92](https://github.com/Torch1230/CombatSolver/commit/5d9a2d92), [c9ebae2f](https://github.com/Torch1230/CombatSolver/commit/c9ebae2f), [057bb3c7](https://github.com/Torch1230/CombatSolver/commit/057bb3c7) | Reuse existing retention seats for shield-breaking attack continuations, safe discard continuations and delayed-lethal stand-pat probes. Heuristic hand/delayed values select probes; exact simulation still determines victory. No larger beam or budget. |
| [002aa636](https://github.com/Torch1230/CombatSolver/commit/002aa636) | A real Runtime reclamation checkpoint replaces the cumulative-allocation estimate for portfolio memory admission. Node/time limits and per-batch memory pressure remain; estimate multiplication saturates rather than overflowing. |

The mathematical search bounds exclude growth/relic goals, forced potion directives, stolen-resource protection,
team objectives and multiplayer raw-loss-first routes. Multiplayer retains its existing primary ordering and
Smart admission contract. `UseUpstreamSearchOptimizations` is an internal default-on A/B control, not a UI setting.
Pinned native evidence: MVID `97f10687-c306-4798-ab75-8b9f23f34dfb`, SHA256
`a1f9e653f1e28e4076558fee1e60d218619cb7e057b887c6417f62c62c6d7a52`.

Remaining work must not be treated as imported:

- **Search policies:** broad component-specific healing certificates, growth/resource incumbent buckets,
  the newer request/frontier scheduler, dedicated Smart-layer scheduling and distinct power-capability retention.
  The narrow bounds and existing-seat continuations above do not import those complete systems.
  The Entropic Brew cost-provenance gap below remains open.
- **Native semantics:** gold/max-HP healing hooks, extra-turn registries, temporary-stat listener order,
  Joss Paper/Pael state and other card/power/monster fixes require pinned native differential cases.
  Newer upstream code alone does not establish 0.107.1 behavior.
- **Runtime/product:** default-GC admission/Mono support, overlay visibility/deployment presentation,
  content-mod attribution/adapter APIs and checkpoint replay/schema changes remain separate work.
  The optional phase-selection detachment API was excluded with its deployment callers.
- **Excluded:** game/Ritsu upgrades, repository/test-directory reorganization, monitoring services,
  telemetry and private uploads; preserve this fork's build target, repository rules and product boundaries.

Validation: zero-warning/error pinned Release; `U0U1PinnedHarness upstream-port` covers 12 groups (32 parallel COW forks,
native star cleanup, 9 routing combinations, 12 Seeker RNG states, batch transform, effect-scope/Fork boundaries,
collection writes, hand-limit identity, diagnostics, native UI field shapes and block saturation).
Existing card-choice-identity, continuation-replay, completion-scope, rolling-review, request-hydration,
choice-rng and continuation-audit regressions pass. Retain origin/main's candidate-identity fix and update its
positive fixture alongside explicit wrong-version rejection and mutable-list isolation checks.
These are pinned/offline contracts: real ChooseCard timing, full native effect/checkpoint differentials
and Host/Client behavior remain unverified by this integration.

Search validation: `upstream-search` completes 17 requests and 17 independent full-final-state replays, including
ABBA controls, actual shared-bound hits and unknown-source fallback. In the artificial 100-HP/no-relic fixture,
expanded/transitions fall from 520/3634 to 258/1758; with two Fire Potions, 671/9071 to 277/3901.
Complete victory quality is unchanged. Incremental verification is enabled, so these are logical-work results,
not general wall-clock benchmarks. Potion strategy, portfolio (82 checks), multiplayer cross-turn (86 checks),
rolling-review, continuation-replay, completion-scope and request-hydration regressions pass.

## Earlier integration (2026-10-02)

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
- [Entropic Brew accounting](https://github.com/Torch1230/CombatSolver/commit/6e62cd1b375ac02e45e2d0f4cbb301cc18b69431) was subsequently adapted in the 2026-11-05 follow-up above, with explicit origin and forced-slot/continuation contracts.
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
