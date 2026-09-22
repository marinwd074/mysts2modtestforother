# STS2 0.107.1 multiplayer-card coverage

> **2026-09-22 boundary update:** the multiplayer prediction root may capture teammate combat state that is already materialized in the local process. `RootActionPlayers` remains local-only. Teammate state is a frozen root snapshot for one search; teammate future actions are not generated. At a real later local turn, any changed readable-teammate fingerprint rejects the old continuation and forces a fresh search. A short-lived `282393c9` post-yield stale-read guard was reverted because prediction Fork/Attach itself rereads captured players and therefore incorrectly truncated valid T2/T3 local-cross-turn routes. Older `local-player-only` / `remote private not materialized` wording below is historical policy, not a claim that the client cannot hold those values.

This is the source-audit ledger for every card marked multiplayer-only in the
repository-pinned `v0.107.1` assembly. It exists so later work does not mix
current-beta cards, later patch behavior, or teammate-private state into the
local-player cross-turn solver.

Truth source:

- game version: `v0.107.1`, game commit `59260271`
- `sts2.dll` SHA-256:
  `a1f9e653f1e28e4076558fee1e60d218619cb7e057b887c6417f62c62c6d7a52`

中文括号内容是源码本地化未随当前仓库提供时的简述名；代码与证据仍使用原始
card id。
- the pinned DLL contains exactly **21** multiplayer-only card model types

Status vocabulary:

- **source-confirmed**: the full route-affecting native chain reviewed so far is
  represented in solver source. This is not a runtime claim.
- **checked match**: the existing generic/specialized path was compared with
  0.107.1 and no source mismatch was found; no corrective code was required.
- **boundary / fail closed**: the readable root may contain the required teammate
  state, but the exact cross-player effect is not yet promoted through search /
  execution contracts or lacks focused 0.107.1 Host/Client evidence.
- **isolation-only**: the needed teammate state is detached into the root, but
  some ownership / generation / mutation semantics still require explicit
  modeling or validation before the card can leave fail-closed staging.

No row below is "runtime-confirmed" unless a future entry explicitly links a
native Host/Client differential. GitHub Actions compatibility/L1 tests are
static/contract evidence, not a substitute for that differential.

## Coverage matrix

| Card | 0.107.1 route-relevant behavior | Solver/source status | Local cross-turn boundary / remaining evidence |
|---|---|---|---|
| 希望 beacon（Beacon of Hope） | 出牌者获得格挡，存活队友获得修正后格挡的一半；防递归 | **source-confirmed / Safe Execute 已开放** | 仍依赖分数值格挡与递归边界合同 |
| Believe in You | selected ally gains 2/3 Energy | **boundary / fail closed** | teammate Energy is now root-captured; selected-ally Energy mutation and Safe Execute targeting still need focused source/runtime promotion |
| Coordinate | selected ally gains temporary Strength 5/8 | **source-confirmed** | public Power-state differential; preserve temporary-Strength restoration |
| Demonic Shield | lose 1 HP first, then selected ally gains Block equal to owner's resulting current Block | **source-confirmed** | differential with HP-loss hooks and Block modifiers |
| Energy Surge | every living player ally gains 2/3 Energy | **boundary / fail closed** | teammate Energy is root-captured; all-player Energy fan-out still needs exact 0.107.1 differential and deployment-contract promotion |
| 夹击（Flanking） | 施加实例减益；非出牌者造成的攻击伤害在目标侧回合结束前为 2 倍 | **source-confirmed / Safe Execute 已开放** | 需保持出牌者排除、叠层和到期语义 |
| 围攻（Gang Up） | 基础伤害加上本回合同阵营、非出牌者对目标造成的强化攻击次数加成 | **checked match / Safe Execute 已开放** | 分支历史分别统计多段攻击与 Osty 出牌者 |
| Glimpse Beyond | creates Soul cards for each living player and inserts them into each owner's Draw pile | **boundary / fail closed** | teammate Draw piles are root-captured, but multi-owner generated-card insertion/ownership has not been promoted or runtime-differentialed |
| Hammer Time | when owner Forges, every other living player Forges same amount; HammerTime-sourced Forge does not recurse | **source-confirmed** | teammate card state is now root-captured, so source prediction can inspect it; focused multi-player Forge differential and execution staging are still required |
| Huddle Up | every living player ally draws 2/3 cards | **boundary / fail closed** | teammate Draw/Hand is root-captured; all-player draw ordering, hooks, and Safe Execute world-delta attribution still need promotion/evidence |
| Ignition | selected ally channels Plasma | **boundary / fail closed** | teammate Orb queues are root-captured; selected-ally channel/evoke semantics and target execution still need focused validation |
| Intercept | owner gains Block; Covered zeros covered ally Powered Attack damage and Intercept multiplies owner's corresponding damage by covered-count+1 | **source-confirmed** | reciprocal-Intercept and death/expiry native differential |
| 击倒（Knockdown） | 其他己方强化攻击获得 2/3 倍实例倍率；减益方回合结束时消耗 | **source-confirmed / Safe Execute 已开放** | 需保持多实例、出牌者排除与 Osty 出牌者身份 |
| Largesse | select from target ally's unlocked Colorless pool; generated card is owned by target and enters target Hand | **isolation-only** | target generation pool and Hand can be detached into the root; ownership-sensitive generation/choice/write semantics still require focused modeling and runtime evidence |
| Legion of Bone | summon/heal Osty for each living player | **boundary / fail closed** | readable player/pet state can be captured, but all-player Osty summon/heal lifecycle and death interactions still need explicit source/runtime validation |
| Lift | selected ally gains 11/16 Block | **checked match** | straight-line AnyAlly Block recipe matches native command |
| Mimic | selected ally supplies current Block calculation; Mimic owner receives that Block | **source-confirmed** | differential with target Block modifiers and zero/high Block |
| Rally | every living player ally gains 12/17 Block | **checked match** | generic AllAllies Block recipe uses branch-local player/liveness filtering |
| 鬼祟（Sneaky） | 施加 1/2 层鬼祟；其他生物出攻击牌时，出牌者获得等量无强化格挡 | **source-confirmed / Safe Execute 已开放** | 不生成未来队友动作，只处理已观察到的动作 |
| 组队（Tag Team） | 先攻击，再施加实例减益；其他玩家的合格攻击会重放并在修改出牌次数后消耗 | **checked match / Safe Execute 关闭** | 现有 TagTeamPower 镜像支持单敌人与全敌人目标；队友后续重放仍不纳入安全执行 |
| 坦克（Tank） | 出牌者获得坦克并承受 2 倍强化攻击伤害；存活队友获得 0.5 倍防护，出牌者死亡时清理 | **source-confirmed / Safe Execute 关闭** | 保持 0.107.1 的 2 倍/0.5 倍语义，不采用 v0.108 改写 |

## Online patch-history cross-check

The current multiplayer wiki mixes the target main build with later beta cards and
later balance changes. For 0.107.1 work, use the pinned assembly above as truth and
treat public patch history only as a cross-check.

Important version traps confirmed against public patch history:

- `Beacon of Hope`: v0.100 made the Power non-stacking. v0.108 then raised its
  Energy cost from 1 to 2, so the 0.107.1 target is still the pre-v0.108 version.
- `Believe in You`: old pre-release history includes a temporary 0 -> 1 cost
  change, but the Early Access card was reintroduced in v0.98 and v0.100 changed
  the granted Energy from 3/4 to 2/3. Do not reconstruct 0.107.1 from the old
  v0.83 pre-release card.
- `Mimic`: the old v0.74 pre-release card temporarily lost Exhaust, but Early
  Access v0.98 reintroduced the card. In the target-era card, base Mimic has
  Exhaust and the upgraded card removes it; do not reuse the old v0.74 result.
- `Radiate`: v0.101 fixed multiplayer counting so Stars gained by other players
  do not contribute. The mirror reads `GetStarsGainedThisTurn(model.Owner)`.
- `Haunt`: v0.101 fixed it proccing when another player plays a Soul. The mirror
  requires the Soul owner creature to equal the Haunt power owner.
- `Huddle Up`: v0.100 added Exhaust and clarified the text to `ALL players`.
  The draw effect remains 2/3 cards; generic card result-location handling owns
  the Exhaust behavior.
- `Tag Team`: v0.104 expanded Replay to attacks that deal damage to ALL enemies.
  The 0.107.1 mirror must therefore preserve both single-enemy and all-enemy
  qualifying attack semantics.
- `Largesse`: v0.104 fixed ownership-sensitive interactions so Pillar of Creation,
  Supermassive, and Arsenal proc for the player who played Largesse, not the ally
  receiving the generated card. In the mirror, the generated card owner is the
  selected target while `creator` remains the Largesse player; these are
  intentionally different identities.
- `Stratagem`: v0.104 removed the multiplayer card-pool ban after the original
  multiplayer bug was fixed. It is not a multiplayer-exclusive card, but it is
  legal in the 0.107.1 multiplayer colorless pool.
- `Gold Axe`: v0.105 changed its multiplayer scaling to count cards played by ALL
  players rather than only its owner. This is another multiplayer semantic on a
  non-exclusive card and should not be confused with the 21-card exclusive set.
- v0.108 added 15 more multiplayer cards (`Midnight`, `Blaze`, `Outrage`,
  `Blade Symphony`, `Concoct`, `Fade`, `Plot`, `Constellation`, `Underworld`,
  `Soulbound`, `Cacophony`, `Hibernate`, `One for All`, `Imitation Learning`,
  `The Ball`). They are outside the 0.107.1 target and must not be added to this
  coverage matrix.
- v0.109 added `Tutor` and re-enabled Well-Laid Plans in multiplayer. Both are
  also outside the 0.107.1 target.

This cross-check is intentionally descriptive. Numeric/effect truth for the
target remains the pinned v0.107.1 model/DLL, not the live wiki card page.

## Safe Execute staging (source-only; no runtime promotion)

The first source-confirmed deterministic subset is now admitted by the Safe
Execute classifier. Admission remains limited to local ownership, readable
targets, no Choice, no replay action, and the bounded deployment/session gates.

### Stage A — local/public execution candidates

These cards are the first candidates for a future source/contract-only whitelist
because playing them does not require a teammate target or teammate-private state:

- `Beacon of Hope` — 已开放
- `Flanking` — 已开放
- `Gang Up` — 已开放
- `Knockdown` — 已开放
- `Sneaky` — 已开放
- `Tag Team` — 保持关闭；后续队友攻击重放不属于本批安全执行

The five opened cards use the existing deterministic card/power mirrors and
still require the normal live revalidation boundary. Runtime Host/Client
evidence is only required if source behavior and local simulation diverge.

### Stage B — public teammate target or intentional remote-public mutation

Defer these until Safe Execute can distinguish an expected cross-player public
mutation from unrelated remote interference:

- `Coordinate`
- `Demonic Shield`
- `Hammer Time`
- `Intercept`
- `Lift`
- `Mimic`
- `Rally`
- `Tank`

### Stage C — keep fail closed pending cross-player semantic promotion

These depend on teammate resources, piles, Orbs, generated-card ownership, or
pet lifecycle. Those values may now be readable in the detached root, but
readability alone is not enough to enable deployment: exact mutation semantics,
choice/RNG ordering, world-delta attribution, and Host/Client evidence still
have to be established.

- `Believe in You`
- `Energy Surge`
- `Glimpse Beyond`
- `Huddle Up`
- `Ignition`
- `Largesse`
- `Legion of Bone`

### Non-exclusive multiplayer semantics already cross-checked

- `Gold Axe`: finished-card count is global across the observed combat history;
  predicted history only adds actions actually simulated on the local branch.
- `Radiate`: Stars are counted for `model.Owner`, not all players.
- `Haunt`: only a Soul owned by the Haunt owner can trigger the power.
- `Strangle`: the before-play pair is created only when the card owner equals the
  Strangle applier's player, so teammate card plays do not proc it.
- `Stratagem`: remains implemented and is legal in the 0.107.1 multiplayer
  colorless pool.

## Readable-root / local-action policy

The multiplayer route predicts only the local player's decisions, but the root
may detach any teammate combat state already materialized in the local game
process. That currently includes teammate Hand/Draw/Discard/Exhaust, Energy,
Stars, Orb queue, potions, relics, Powers, generation pools, creature state and
other captured hook state. `RootActionPlayers` remains local-only.

Readable teammate state is a **frozen root input**, not a teammate-behavior
model. Search may evaluate deterministic effects that consume or mutate that
detached state only where the exact 0.107.1 semantics have been implemented.
It must never invent a teammate card choice, future action, or hidden network
read. When the real multiplayer world changes, the teammate readable-state
fingerprint invalidates continuation reuse and forces a fresh capture/search.

Therefore cards such as Believe in You, Energy Surge, Huddle Up, Ignition,
Glimpse Beyond, Largesse, and Legion of Bone remain staged fail-closed because
their cross-player mutation / choice / RNG / lifecycle contracts are not yet
fully promoted and runtime-validated — **not** because the root is forbidden
from reading teammate state.

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
