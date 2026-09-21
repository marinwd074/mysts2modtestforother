# STS2 0.107.1 game-mechanics baseline

This document is the gameplay-semantics gate for CombatSolver's `0.107.1`
target. Its purpose is to prevent a decompiled implementation detail, a later
CombatSolver change, or a current wiki page from being mistaken for the rules
that were actually in force in `0.107.1`.

It is deliberately narrower than a game wiki. Only mechanics that can change
combat prediction, route ranking, continuation validity, RNG use, or
multiplayer behavior belong here.

## Version anchor

Repository truth:

- game version: `v0.107.1`
- game commit: `59260271`
- release timestamp from `game-body/release_info.json`:
  `2026-06-18T15:43:56-07:00`
- assembly: `game-body/data_sts2_windows_x86_64/sts2.dll`
- SHA-256:
  `a1f9e653f1e28e4076558fee1e60d218619cb7e057b887c6417f62c62c6d7a52`

Mega Crit announced Major Update #2 as `v0.107.1` on June 18, 2026. The
community patch-history page labels it June 19 in its own date convention; that
one-day presentation difference does not change the repository build identity.

Public references:

- Mega Crit Steam announcements:
  https://steamcommunity.com/app/2868840/announcements/
- v0.107.1 patch-history page:
  https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3AV0.107.1_-_Major_Update_2
- patch index:
  https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3APatch_Notes

## Evidence hierarchy

Use evidence in this order when deciding whether solver behavior should change.

1. **Pinned 0.107.1 game body.** Decompiled `sts2.dll` is the final source for
   exact command order, DynamicVar values, hidden state, RNG stream use,
   lifecycle hook placement, and version-specific implementation.
2. **Official Mega Crit patch notes.** Use v0.107.1 and later delta notes to
   establish which player-visible behavior changed between versions.
3. **Wiki pages with explicit Update History.** Use them to understand card text,
   terminology, stack type, and interactions, but only when the history can
   anchor the behavior to 0.107.1.
4. **Current wiki text without a 0.107.1 anchor.** Mechanism explanation only.
   Do not use current numbers or current card text as old-version truth.
5. **Later CombatSolver upstream code.** Implementation reference only. It must
   never override the pinned game body.
6. **Community discussion.** Useful for finding edge cases; insufficient by
   itself for an algorithm change.

When sources disagree, record the disagreement and stop. Do not "pick the most
reasonable" behavior.

## Algorithm-change gate

A route-affecting change is allowed only when all applicable checks below pass:

- the player-visible rule is understood from official notes or a versioned
  mechanics/card source;
- the pinned DLL confirms the exact 0.107.1 implementation;
- card, Power, hook, choice, generated-card, and turn-lifecycle effects have
  been followed as one semantic chain rather than inspected in isolation;
- any private counter/list/state that affects future behavior is fork-safe and
  participates in search equivalence when needed;
- multiplayer behavior uses branch-local state and never reads mutable live
  remote/private state from a background search;
- later-version changes are explicitly rejected where they are easy to
  accidentally backport.

If any item is unresolved, document a gap instead of changing the solver.

## Multiplayer combat baseline

The public multiplayer rules describe battles as one shared encounter in which
players play cards and use potions in real time. Enemy Max HP scales with player
count; enemy attack strength itself is not increased, and enemies can attack or
debuff all players while each player blocks damage independently.

Reference:
https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3AMultiplayer

Solver consequences:

- local-player decisions may share public enemy state with teammates;
- teammate hand/draw/discard order and other private information are not
  predictable inputs;
- a multiplayer-only card cannot be reduced to single-player target semantics
  merely because its `OnPlay` method is small;
- redirects, shared buffs/debuffs, and teammate-targeted effects must be modeled
  as multiplayer mechanics first and implementation details second.

### Coordinate

0.107.1-visible rule: give another player 5 Strength this turn, 8 when
upgraded. Coordinate is multiplayer-only.

Reference:
https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3ACoordinate

Its Power is temporary Strength; therefore the search model must preserve the
normal temporary-stat restoration boundary rather than treating it as
permanent Strength.

### Flanking

Public rule: other allies deal X times more Attack damage to the affected enemy
this turn. The debuff is multiplayer-specific and intensity-based.

Reference:
https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3ADebuffs

The pinned 0.107.1 DLL remains authoritative for the exact amount, instancing,
applier check, and expiry boundary.

### Tank

The v0.108 patch explicitly changed Tank from the earlier "take double damage /
allies take half damage" behavior to "take 50% additional / allies take 50%
less." Therefore current Tank text must not be backported into 0.107.1.

Reference:
https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3AV0.108.0_-_Beta_Patch

This is a canonical example of using a later patch delta to reconstruct the
old player-facing rule, then using the 0.107.1 DLL for implementation details.

### Intercept

Public rule: gain 9 Block (13 upgraded) and redirect incoming attacks that
would have hit another player this turn to the Intercept user.

Reference:
https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3AIntercept

The same page records a special interaction where two players intercepting one
another can result in zero damage for both during that turn. This proves that
Intercept is an attack-redirection mechanic, not merely "target takes zero
damage" or a generic damage multiplier.

**Status: not safe for algorithm changes yet.** Before implementing or changing
Intercept, trace the pinned DLL's Covered/Intercept pair, private covered-target
state, death cleanup, turn-end cleanup, target selection/redirection order, and
the reciprocal-Intercept edge case. That state must be fork-safe before the
card is marked fully supported.

### Beacon of Hope

Public rule: whenever the owner gains Block on their turn, other players gain
half that much Block; upgrade makes the card Innate.

Reference:
https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3ABeacon_of_Hope

**Status: mechanics understood, implementation audit pending.** The solver must
determine whether "gain Block" observes pre- or post-modifier Block, how integer
rounding works, whether repeated/zero Block events trigger, and how multiplayer
recipient filtering is implemented in 0.107.1 before adding compensation.

## Damage baseline

### Powered Attack scope

Weak and Vulnerable apply to Attack damage rather than arbitrary HP loss.
Vulnerable normally increases Attack damage received by 50%; Weak normally
reduces Attack damage dealt by 25%. Vulnerable's public mechanics page also
identifies these as multiplicative effects, alongside effects such as Double
Damage, Slow, Surrounded, and similar damage multipliers.

References:

- https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3AVulnerable
- https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3ADebuffs

Solver consequences:

- Poison, Doom, direct HP loss, Orb damage, and non-Attack Power damage must not
  inherit Attack-only multipliers unless the pinned implementation explicitly
  marks them as such;
- multiplier order and flooring are semantic, not cosmetic;
- multi-hit cards must settle each hit with the game's normal rounding path.

### Tracking version trap

The current Tracking page describes a later "+50%" presentation while its Power
description is intensity-shaped. That current page is **not** sufficient to
define 0.107.1. The pinned 0.107.1 DLL is the authority for the old
`TrackingPower` amount/multiplier behavior.

Reference:
https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3ATracking

This is the standard example of why current card text cannot override a pinned
old assembly.

## Block baseline

Block prevents damage and is owned per creature/player. Multiplayer enemy
attacks can affect multiple players, but each player resolves their own Block.

Reference:
https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3ABlock

Any card that redistributes, mirrors, redirects, or derives values from Block
must use the predicted creature's branch-local Block value.

## Poison baseline

Poison is an intensity debuff. At the start of the poisoned enemy's turn it
loses HP equal to current Poison, then Poison decreases by one. The public
mechanics page states that Poison bypasses Block. Artifact can negate an
application from one source.

Reference:
https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3APoison

Solver consequences:

- Poison is HP loss, not normal Attack damage;
- application events matter independently of resulting total amount because
  cards/Powers such as Outbreak and Sleight of Flesh can react to application;
- source/applier identity can be semantic;
- the start-of-turn trigger and subsequent decrement are distinct lifecycle
  operations.

## Doom baseline

Doom is an intensity debuff. At the end of the affected creature's turn, a
creature whose HP is less than or equal to Doom dies. It bypasses Block and
does not kill by reducing HP to zero.

Reference:
https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3ADoom

A later v0.108 change moved end-of-turn Orb passives before Doom. Therefore
0.107.1 must **not** silently inherit the newer ordering. The exact 0.107.1
turn-end order must stay pinned to the DLL and runtime evidence.

Reference:
https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3AV0.108.0_-_Beta_Patch

## Orb baseline

Channel places an Orb into an empty slot; if there is no empty slot, an
existing Orb is Evoked to make room. Orbs have Passive and Evoke effects.
Lightning, Frost, Dark, and Glass use end-of-turn passives; Plasma uses a
start-of-turn passive. Focus changes non-Plasma Orb effectiveness.

Reference:
https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3AOrbs

The current Orb page carries beta-content warnings, so its high-level concepts
are useful but version-sensitive values/order must still be checked against the
0.107.1 assembly.

## Replay baseline

Replay means a card is played additional times.

Reference:
https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3AReplay

Replay must not be treated as "repeat only the damage number." Every replay can
interact with card-play hooks, first/last-in-series logic, generated cards,
choices, Power counters, and card history. Exact replay/autoplay distinctions
remain DLL- and runtime-test territory.

## Power stack/lifetime vocabulary

Public Buff/Debuff pages use stack categories such as Intensity, Duration,
Does Not Stack, Removed, Decremented, and Reset. These labels are useful for
understanding player-facing intent but do not replace `PowerInstanceType`,
applier identity, private fields, or hook code in the pinned DLL.

References:

- https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3ABuffs
- https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3ADebuffs

In particular, an instanced Power must not be collapsed into a
`(owner, type) -> amount` aggregate when different appliers or targets change
future behavior.

## Known version traps

These later patch notes are especially useful as negative evidence for the
0.107.1 target:

- v0.108 reworked Tank and changed end-of-turn Orb/Doom ordering.
- v0.110 reworked Haze to add Weak, reworked Outbreak into an immediate-Poison
  Skill, and increased Sacrifice from double to triple Osty Max HP.
- v0.111 later changed Guiding Star draw from this turn to next turn and
  reworked Expect a Fight and Hyperbeam again.

Official Steam announcement stream:
https://steamcommunity.com/app/2868840/announcements/

These deltas explain why later upstream solver code is frequently unsafe to
copy into the 0.107.1 compatibility branch.

## Safe audit workflow

For each candidate card or Power:

1. Read its player-facing rule and Update History.
2. Search patch notes for changes after 0.107.1.
3. Read the 0.107.1 card `OnPlay` and `OnUpgrade`.
4. Follow every called Power, hook, choice, generated-card, pile, damage,
   block, RNG, and turn-lifecycle path.
5. Identify all hidden future-relevant fields/counters/lists.
6. Determine whether those fields must enter:
   - forked prediction state;
   - search/transposition fingerprint;
   - exact continuation stamp;
   - multiplayer public-state fingerprint.
7. Compare the complete chain with the current solver.
8. Only then change code and add a regression guard.
9. If runtime order or multiplayer target semantics remain ambiguous, require a
   focused native smoke/differential test instead of guessing.

## Current hold list

Do not change these from public text alone:

| Mechanic/card | Current status | Required evidence before code change |
|---|---|---|
| Intercept | public redirect semantics known; private target list matters | full Covered/Intercept DLL trace + fork/fingerprint design + reciprocal-intercept runtime case |
| Beacon of Hope | public half-Block sharing known | 0.107.1 Block-event amount/rounding/recipient trace |
| Hammer Time | high-level "all allies Forge" interaction known | 0.107.1 Forge trigger ordering, creator/applier ownership, multiplayer recipient/RNG trace |
| current Tracking text | known to be later presentation | pinned DLL only for 0.107.1 values |
| current Tank text | changed in v0.108 | pinned DLL + v0.108 delta |
| current Haze/Outbreak/Sacrifice text | changed after 0.107.1 | pinned DLL + later patch delta |

## Definition of done

A mechanic is "baseline-confirmed" only when the player-facing meaning and the
0.107.1 implementation agree. A card is "solver-confirmed" only when its whole
semantic chain is represented in branch-local prediction state and, where
necessary, state equivalence/continuation logic.

Passing static compilation or finding the card type in a compensation catalog
does not by itself prove semantic correctness.
