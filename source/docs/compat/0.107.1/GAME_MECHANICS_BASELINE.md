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

### Targeting and autoplay

The pinned 0.107.1 `CardCmd.AutoPlay` path treats target selection differently
from a normal player choice:

- `AnyEnemy` with no supplied target chooses from `HittableEnemies` using
  `RunState.Rng.CombatTargets`;
- `AnyAlly` with no supplied target chooses a living player ally other than
  the card owner, using the same `CombatTargets` RNG stream;
- if no valid target exists, the autoplayed card is moved to its result pile
  without executing its normal play effect.

This random targeting belongs to AutoPlay, not to ordinary manual targeting.
The solver must not invent random targets for a normal player-directed card.

Because this path consumes `CombatTargets`, multiplayer future prediction must
treat that RNG as shared/version-sensitive evidence. A route must not assume a
future random target remains stable across remote actions unless RNG ownership
and consumption are proven for that boundary.

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
another can result in zero damage for both during that turn.

The pinned 0.107.1 DLL explains the player-facing "redirect" without rewriting
the attack target:

1. `Intercept.OnPlay` first gives Block to the card owner, then applies one
   instanced `CoveredPower` to the chosen ally with the owner as applier.
2. `CoveredPower.ModifyDamageMultiplicative` returns `0` for Powered Attack
   damage targeting the covered ally.
3. `CoveredPower.AfterApplied` creates or reuses an `InterceptPower` on the
   applier and appends the covered creature to a private `coveredCreatures`
   list.
4. `InterceptPower.ModifyDamageMultiplicative` multiplies Powered Attack
   damage targeting its owner by `coveredCreatures.Count + 1`.
5. both Powers remove themselves after the enemy side turn; Covered also
   removes itself if its applier dies.

In the normal multiplayer enemy-attack pattern, this produces the same
player-visible result as redirection: covered allies take zero while the
interceptor's corresponding hit is multiplied to account for the covered
players. Reciprocal Intercept also becomes mechanically explainable: each
player's Covered multiplier can reduce their own incoming Powered Attack to
zero even though each also has an Intercept multiplier.

The private covered-creature list is future-relevant state. Any solver support
must fork it exactly and include it in state equivalence while the Power can
still affect damage. It should enter a continuation/public-state stamp only if
that continuation boundary can occur before the Power's enemy-turn expiry.

The solver source now mirrors this chain explicitly: Intercept has a dedicated
native-order OnPlay mirror; Covered and Intercept have their own damage
multipliers; the private native list is captured from the pinned DLL's
`_internalData` into a forkable prediction state; covered creature identities
participate in branch fingerprints and exact continuation stamps; and native
death/side-turn expiry is mirrored. The list intentionally does not shrink when
a covered creature dies, matching the pinned implementation.

**Status: gameplay/DLL semantics and source implementation confirmed; a focused
reciprocal-Intercept and covered-player-death native differential is still
required before runtime-confirmed.**

### Beacon of Hope

Public rule: whenever the owner gains Block on their turn, other players gain
half that much Block; upgrade makes the card Innate.

Reference:
https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3ABeacon_of_Hope

The pinned DLL resolves the ambiguous details:

- the Power listens to `AfterBlockGained`, so it receives the owner's
  post-`ModifyBlock` decimal amount, not the card's raw Block value;
- it triggers only when the Block event amount is at least 1, the gainer is the
  Power owner, and the combat's current side matches the owner's side;
- it computes `amount * 0.5` without pre-rounding and does nothing if that
  shared amount is below 1;
- recipients are living player teammates other than the owner;
- each recipient receives that decimal value through a fresh
  `CreatureCmd.GainBlock(..., ValueProp.Unpowered, null)`, so the recipient's
  own Block modifiers still run;
- an internal boolean guard is set while sharing, preventing the shared Block
  events from recursively retriggering the same Beacon.

Block storage is integer-backed, so the eventual write truncates as described
in the Block pipeline below. Do not pre-floor the 50% value before the
recipient's own Block modifiers have run.

**Status: gameplay and 0.107.1 implementation semantics confirmed; current
solver hook coverage still needs a targeted audit before code changes.**

### Demonic Shield

Public rule: lose 1 HP, then give another player Block equal to the user's
Block. The base card Exhausts; upgrade removes Exhaust.

Reference:
https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3ADemonic_Shield

The pinned 0.107.1 DLL confirms the order is semantic, not presentation:
`CreatureCmd.Damage` resolves first with Unblockable + Unpowered + Move and
the card as source; only after that completes does `CalculatedBlock` read the
owner's current Block and grant that amount to the selected ally. HP-loss
listeners such as Rupture therefore resolve before the shared Block amount is
calculated.

The solver now uses a dedicated OnPlay mirror for this sequence rather than a
generic Block recipe. Its existing branch-local Demonic Shield
`CalculatedBlock` multiplier remains the source of the final Block amount.

**Status: gameplay/DLL semantics and source implementation confirmed; focused
multiplayer native differential still required.**

### Sneaky

Public rule: whenever another player plays an Attack, gain 1 Block (2 upgraded).

References:

- https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3ABuffs
- https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3ABlock

The pinned DLL applies `SneakyPower` to the card owner. Its
`AfterCardPlayed` checks only that the played card belongs to a different
creature and is an Attack, then grants `power.Amount` Unpowered Block. The
solver's existing AfterCardPlayed mirror already matched this rule; the missing
piece was the card's Power application, which is now explicit.

**Status: gameplay/DLL semantics and source implementation confirmed; focused
multiplayer native differential still required.**

### Knockdown

Public rule: deal 10 damage and make Attacks from other players deal 2x damage
to that enemy this turn; upgraded values are 14 damage and 3x. Separate
Knockdown applications remain separate instances and therefore multiply.

Reference:
https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3AKnockdown

The pinned 0.107.1 DLL confirms that `KnockdownPower` is an instanced Counter
debuff. Its multiplier applies only to Powered Attack damage targeting the
Power owner, excludes damage from the Power applier, and returns the instance
amount as the multiplier. `AfterSideTurnEnd` removes the specific instance
when the debuffed creature is among that side's participants.

The solver already had deterministic Power application, branch-local applier
display state, and a safe pure-method fallback for the multiplier. The missing
semantic was the instance lifetime; predicted end-turn processing now removes
each instance at the native owner-participation boundary instead of leaving the
multiplier active across later turns.

**Status: gameplay/DLL semantics and source implementation confirmed; focused
multiplayer differential with repeated Knockdown instances and Osty attacks is
still required.**

### Hammer Time

Public rule: whenever the owner Forges, all allies Forge as well.

References:

- https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3ASovereign_Blade
- https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3AFurnace

The pinned DLL implements this in `HammerTimePower.AfterForge`:

- it triggers only when the original forger is the Power owner;
- it does not trigger when the Forge source is already a `HammerTimePower`;
- it iterates every other living player and calls `Forge` for the same amount;
- those secondary Forges use the Hammer Time Power as their source, which
  suppresses recursive Hammer Time chains on every player.

Thus multiple players holding Hammer Time do not create an unbounded Forge
cascade from one Forge event.

The 0.107.1 `CardModel.AfterForged()` call only raises the card's `Forged`
event. The only native subscriber found in the pinned assembly is
`CombatStateTracker`, so omitting that presentation/state-tracker callback
does not omit another combat effect. The only gameplay-relevant
`AbstractModel.AfterForge` override in the pinned assembly is
`HammerTimePower`; the other override is an achievement check.

The solver audit confirmed that its existing Forge path already creates a
Sovereign Blade when none remains outside Exhaust and increases every non-Dupe
Sovereign Blade including exhausted copies. The missing combat semantics were
therefore limited to Hammer Time's global AfterForge propagation and the card's
own Power application; those have now been restored without changing Forge
damage or generation rules.

**Status: gameplay/DLL semantics and source implementation confirmed; a focused
multiplayer native differential is still required before runtime-confirmed.**

## Damage baseline

### 0.107.1 damage pipeline

The pinned DLL keeps damage as `decimal` through the modifier pipeline. For
normal damage calculation, the important order is:

1. card-enchantment damage modification, when applicable;
2. every additive damage hook in hook-listener order;
3. every multiplicative damage hook in hook-listener order;
4. damage-cap hooks;
5. clamp the modified amount to at least zero;
6. consume Block unless the damage is Unblockable;
7. apply HP-loss modification hooks and then write HP loss;
8. run post-damage hooks and death processing.

Do not floor after each multiplier. Fractional values can reach Block/HP
settlement. Creature Block and HP are integer-backed and their internal writes
convert the decimal amount to `int`; therefore premature solver rounding can
produce a different result from native execution.

This also means multiplication is not an abstract unordered set of effects:
hook listener order is part of the executable semantics even when common
multipliers happen to commute.

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

The pinned 0.107.1 `CreatureCmd.GainBlock` order is:

1. `BeforeBlockGained`;
2. `ModifyBlock`;
3. clamp the modified decimal amount to at least zero;
4. `AfterModifyingBlockAmount` for the models that modified it;
5. write Block with `GainBlockInternal`;
6. record Block history;
7. `AfterBlockGained` with the modified decimal amount.

`GainBlockInternal` stores Block as an integer and converts only at the final
write. Therefore a hook such as Beacon of Hope can observe a fractional
post-modifier amount even though the creature's stored Block is integral.

Any card that redistributes, mirrors, redirects, or derives values from Block
must use the predicted creature's branch-local Block value and preserve this
hook order.

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

## Forge baseline

Public Forge semantics are documented on Sovereign Blade: Forge creates a
Sovereign Blade when needed and increases Sovereign Blade damage. Spoils of
Battle also documents that Forge happens before its card draw.

References:

- https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3ASovereign_Blade
- https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3ASpoils_of_Battle

The pinned 0.107.1 `ForgeCmd.Forge` order is more precise:

1. collect non-Dupe Sovereign Blades, excluding cards currently in Exhaust;
2. if none remain outside Exhaust, create a new Sovereign Blade in Hand and
   mark it `CreatedThroughForge`;
3. increase the damage of every non-Dupe Sovereign Blade, this time including
   exhausted copies;
4. run each blade's `AfterForged` behavior;
5. only then run the global `Hook.AfterForge(amount, forger, source)`.

This ordering is observable: generated-card hooks can happen before AfterForge,
and Hammer Time is an AfterForge effect. A solver must not represent Forge as
only a scalar "Sovereign Blade damage +X" resource.

## Replay and AutoPlay baseline

Replay means a card is played additional times.

Reference:
https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3AReplay

In the pinned DLL, Replay is implemented inside one `OnPlayWrapper` series.
The wrapper sets `PlayCount = replayCount + 1` (subject to card-play-count
hooks) and executes each iteration with a `CardPlay` carrying:

- `PlayIndex`;
- `PlayCount`;
- `IsFirstInSeries` / `IsLastInSeries`;
- the same play resources and autoplay flag for that series.

Each iteration runs normal Before/After-card-play hooks and the card's complete
OnPlay behavior. Therefore Replay must not be represented as "repeat the damage
number" or as independent new player input.

AutoPlay is a different mechanism. `CardCmd.AutoPlay`:

- runs `ShouldPlay` with an `AutoPlayType`;
- can select a missing enemy/ally target through `CombatTargets` RNG;
- invokes `BeforeCardAutoPlayed`;
- records zero resources actually spent while preserving the card's effective
  energy/star value fields;
- calls the same `OnPlayWrapper` with `IsAutoPlay = true`.

An autoplayed card can still have Replay, in which case every replay iteration
belongs to that autoplay series. Hooks that distinguish `IsAutoPlay`,
`PlayIndex`, first/last-in-series, or resources must therefore remain distinct
in prediction.

Mayhem is a useful public example of true AutoPlay: its update history contains
multiple fixes specifically about cards being auto-played at turn start rather
than manually played.

Reference:
https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2%3AMayhem

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
| Intercept | gameplay/DLL semantics confirmed; hidden covered-creature state, damage multipliers, cleanup, fingerprint and continuation support restored in solver source | reciprocal-intercept + covered-player-death native multiplayer differential |
| Beacon of Hope | gameplay/DLL semantics confirmed; solver Block mirror already matched and card application gap has been corrected | focused native multiplayer differential for fractional/post-modifier sharing and recursion guard |
| Hammer Time | gameplay/DLL semantics confirmed; Forge propagation and recursion suppression restored in solver source | focused multiplayer native differential covering one and multiple Hammer Time owners plus exhausted Sovereign Blades |
| Demonic Shield | native self-damage-before-shared-Block order restored in solver source | focused multiplayer differential including Rupture/Tungsten/Block-modifier interactions |
| Sneaky | native Power application restored; existing cross-player Attack trigger mirror matches DLL | focused multiplayer differential with remote Attack, Replay, and Shadowmeld Block modification |
| Mimic | native calculation/recipient split restored: selected ally supplies Block value, owner receives Block | focused multiplayer differential with target Block modifiers and zero/high Block values |
| Knockdown | native instanced multiplier semantics matched; missing side-turn expiry restored | focused multiplayer differential with two separate instances, applier exclusion, and Osty dealer identity |
| Energy Surge / Believe in You | native behavior changes teammate Energy, but the local-only root intentionally does not capture remote PlayerCombatState | introduce a detached remote-public resource sidecar only after proving Energy/Stars are safe public inputs; do not relax remote private-pile capture |
| Huddle Up / Ignition / Largesse / Glimpse Beyond | native behavior mutates teammate draw/hand/orb state | explicit remote-private-state policy; local cross-turn solver must fail closed rather than materialize teammate piles/orbs |
| Legion of Bone | native behavior summons/heals Osty for every living player, but current GetOsty fallback can reread player.Osty from the live graph | freeze remote/public pet identity in the root before adding cross-player summon support |
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
