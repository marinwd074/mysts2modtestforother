# STS2 0.107.1 multiplayer-card coverage

This is the source-audit ledger for every card marked multiplayer-only in the
repository-pinned `v0.107.1` assembly. It exists so later work does not mix
current-beta cards, later patch behavior, or teammate-private state into the
local-player cross-turn solver.

Truth source:

- game version: `v0.107.1`, game commit `59260271`
- `sts2.dll` SHA-256:
  `a1f9e653f1e28e4076558fee1e60d218619cb7e057b887c6417f62c62c6d7a52`
- the pinned DLL contains exactly **21** multiplayer-only card model types

Status vocabulary:

- **source-confirmed**: the full route-affecting native chain reviewed so far is
  represented in solver source. This is not a runtime claim.
- **checked match**: the existing generic/specialized path was compared with
  0.107.1 and no source mismatch was found; no corrective code was required.
- **boundary / fail closed**: exact native semantics require remote state that
  the local-only solver intentionally does not materialize or predict.
- **isolation-only**: safe root metadata is frozen to prevent background live
  reads, but the effect still stops at a remote-private write boundary.

No row below is "runtime-confirmed" unless a future entry explicitly links a
native Host/Client differential. GitHub Actions compatibility/L1 tests are
static/contract evidence, not a substitute for that differential.

## Coverage matrix

| Card | 0.107.1 route-relevant behavior | Solver/source status | Local cross-turn boundary / remaining evidence |
|---|---|---|---|
| Beacon of Hope | owner gains Block -> living teammates gain half the post-modifier amount; recursion guarded | **source-confirmed** | focused multiplayer differential for fractional Block, recipient modifiers, and recursion |
| Believe in You | selected ally gains 2/3 Energy | **boundary / fail closed** | remote Energy is not part of the local root PlayerCombatState; require a detached public-resource design before support |
| Coordinate | selected ally gains temporary Strength 5/8 | **source-confirmed** | public Power-state differential; preserve temporary-Strength restoration |
| Demonic Shield | lose 1 HP first, then selected ally gains Block equal to owner's resulting current Block | **source-confirmed** | differential with HP-loss hooks and Block modifiers |
| Energy Surge | every living player ally gains 2/3 Energy | **boundary / fail closed** | same remote-resource boundary as Believe in You; do not capture teammate piles just to obtain Energy |
| Flanking | instanced debuff; attacks from creatures other than applier deal 2x to target until target-side end | **source-confirmed** | multiplayer differential for applier exclusion, stacking, and expiry |
| Gang Up | damage = base plus bonus per same-side, non-owner Powered Attack hit on target this turn | **checked match** | calculated-var uses branch/root damage-event history; multi-hit and Osty dealer events are counted separately |
| Glimpse Beyond | creates Soul cards for each living player and inserts them into each owner's Draw pile | **boundary / fail closed** | directly mutates remote private Draw piles |
| Hammer Time | when owner Forges, every other living player Forges same amount; HammerTime-sourced Forge does not recurse | **source-confirmed** | remote Forge can require teammate private card state, so local-only prediction may stop at that boundary; runtime differential still needed |
| Huddle Up | every living player ally draws 2/3 cards | **boundary / fail closed** | directly reads/mutates remote Draw/Hand state |
| Ignition | selected ally channels Plasma | **boundary / fail closed** | remote Orb queue is not materialized by the local-only root |
| Intercept | owner gains Block; Covered zeros covered ally Powered Attack damage and Intercept multiplies owner's corresponding damage by covered-count+1 | **source-confirmed** | reciprocal-Intercept and death/expiry native differential |
| Knockdown | instanced 2x/3x multiplier for other allied Powered Attacks; instance expires on debuffed side-turn end | **source-confirmed** | differential with multiple instances, applier exclusion, Osty dealer identity |
| Largesse | select from target ally's unlocked Colorless pool; generated card is owned by target and enters target Hand | **isolation-only** | target Colorless eligibility is root-frozen for the public roster; remote Hand remains private and write must fail closed |
| Legion of Bone | summon/heal Osty for each living player | **boundary / fail closed** | freeze remote/public pet identity before cross-player summon support; never reread mutable live pet state in background |
| Lift | selected ally gains 11/16 Block | **checked match** | straight-line AnyAlly Block recipe matches native command |
| Mimic | selected ally supplies current Block calculation; Mimic owner receives that Block | **source-confirmed** | differential with target Block modifiers and zero/high Block |
| Rally | every living player ally gains 12/17 Block | **checked match** | generic AllAllies Block recipe uses branch-local player/liveness filtering |
| Sneaky | apply 1/2 SneakyPower; each Attack played by another creature grants owner that much Unpowered Block | **source-confirmed** | future teammate actions are not invented by local planning; differential for observed remote Attack/Replay interactions |
| Tag Team | attack, then instanced debuff replays a qualifying Attack by another player; consumed after modifying play count | **checked match** | existing TagTeamPower play-count mirror handles AnyEnemy and AllEnemies target semantics and consumes the instance |
| Tank | apply TankPower; owner takes 2x Powered Attack damage and living teammates receive Guarded for 0.5x, with applier-death cleanup | **source-confirmed** | focused multiplayer differential; keep 0.107.1 2x/0.5x semantics rather than v0.108 rewrite |

## Local-only policy

The matrix deliberately does **not** aim for "all multiplayer cards simulate
everything." The multiplayer route predicts only the local player's decisions.
Public enemy/creature/Power state may be branch-local input, but teammate
Hand/Draw/Discard/Exhaust order, Orb queue, and similar private combat state are
not prediction inputs.

For remote-resource cards such as Believe in You and Energy Surge, a future
implementation must first prove that the resource is a safe public input and
store it in a detached root sidecar. Expanding `RootCapturedPlayers` to obtain
that value is not acceptable because it would also expose private piles.

For private-state cards such as Huddle Up, Ignition, Glimpse Beyond, and
Largesse, stopping the local future route is the correct behavior until there
is an explicit architecture for that information. Do not fill the gap with a
teammate model or guessed future actions.

## Version boundary

This list comes from the pinned 0.107.1 assembly, not the current multiplayer
wiki page. Names introduced only in later beta builds must not be added to this
matrix without proving they exist in the target DLL. For example,
`Soulbound` is not present as a target card/Power in the audited 0.107.1
assembly.

## Next runtime differentials

Highest-value native Host/Client cases, without broad regression farming:

1. Intercept: reciprocal coverage plus interceptor/covered-player death.
2. Knockdown: two instances from different appliers plus an Osty attack.
3. Beacon of Hope: odd/fractional post-modifier Block and recursion guard.
4. Tank/Flanking: multiplier/applier identity and cleanup.
5. Mimic/Demonic Shield: selected-ally public Block combined with owner-side
   Block/HP-loss modifiers.

These are focused semantic differentials. They do not require enabling
teammate-private-state prediction.
