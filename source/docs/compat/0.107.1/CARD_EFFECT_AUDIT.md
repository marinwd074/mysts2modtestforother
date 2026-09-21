# 0.107.1 card-effect source audit

This audit pins card simulation semantics to the repository's own STS2
`v0.107.1` game-body snapshot instead of interpreting later upstream
CombatSolver behavior as old-version truth.

Algorithm changes in this audit are also gated by
[`GAME_MECHANICS_BASELINE.md`](GAME_MECHANICS_BASELINE.md). Public-facing game
rules and version history are checked before a decompiled implementation detail
is interpreted as intended gameplay semantics. Current-version wiki text alone
is never sufficient to rewrite 0.107.1 behavior.

## Truth source

The Git LFS game-body objects were pulled and inspected directly:

- game version: `v0.107.1`
- game commit: `59260271`
- release date: `2026-06-18T15:43:56-07:00`
- assembly: `game-body/data_sts2_windows_x86_64/sts2.dll`
- SHA-256: `a1f9e653f1e28e4076558fee1e60d218619cb7e057b887c6417f62c62c6d7a52`
- discovered card model types: `585`

The assembly was decompiled with ILSpy and card implementations under
`MegaCrit.Sts2.Core.Models.Cards` were compared with CombatSolver's card,
power, choice, and hook mirrors. This is a static semantic audit; it does not
replace the native-vs-predicted runtime differential.

## Corrected version drift

Forty-four route-affecting mismatches have been confirmed in the 0.107.1 source audit:

| Card | 0.107.1 native behavior | Incorrect solver behavior | Correction |
|---|---|---|---|
| Tracking | first play applies TrackingPower 2, later plays +1; against Weak targets the power amount is the damage multiplier | applied 50 and interpreted it as percentage bonus | use 2 then +1 and multiply by the branch power amount |
| Big Bang | after its draw finishes, gains Stars, then Energy, then Forges; `AfterStarsGained` completes before the Energy/Forge suffix | gained Energy before Stars, so a star-gain hook that suspended could observe post-star Energy too early | preserve the native Stars -> Energy -> Forge order and keep Energy/Forge untouched when the star hook suspends |
| Replay enchantment | v0.107.1 continues the already-generated repeated card plays even when the first attack ends combat; v0.108 introduced skipping the remaining plays in that case | the simulator unconditionally broke the repeated-play loop whenever combat became over/ending, importing the v0.108 fix | route the end-of-combat stop through `Sts2CardPlayCompatibility`; 0.107.1 keeps replaying while newer builds may stop |
| Swift enchantment / Hellraiser / Shining Strike | in v0.107.1 Swift awaits its draw before disabling itself, so a Swift Shining Strike drawn under Hellraiser can be autoplayed, return to the Draw pile, and be drawn again; v0.108 fixed this recursion | disabled Swift before starting its draw, importing the later fix and suppressing the 0.107.1 recursive interaction | version-gate the Swift order; on 0.107.1 keep Swift active through the draw and resume the disable suffix with a fork-safe execution frame if the draw suspends |
| Sacrifice | calculates block as 2x the living Osty's max HP, then kills Osty and gains that block | both the OnPlay mirror and calculated-var registry had x3 drift | restore x2 in both execution and calculated-variable paths |
| Haze | applies Poison to all hittable enemies; upgrade increases Poison | also applied Weak | remove the Weak application |
| Outbreak | applies OutbreakPower 11/15; every third positive Poison application by the owner deals that amount as Unpowered damage to all hittable enemies | immediately applied Poison to all enemies and triggered Poison damage when the card was played | restore the persistent power, its hidden 0/1/2 poison counter, and its third-application damage trigger |
| Guiding Star | attacks, then immediately draws Cards this turn | also added DrawCardsNextTurnPower | remove the deferred draw compensation and rely on the native-order inferred draw |
| Well-Laid Plans | applies WellLaidPlansPower using RetainAmount (1, upgraded to 2) | always applied one stack | read RetainAmount from the 0.107.1 card model |
| Expect a Fight | gains energy equal to the number of Attack cards currently in hand, then applies one NoEnergyGainPower | calculated energy from Strength and had no complete OnPlay mirror | count hand Attacks, gain that energy first, then apply NoEnergyGainPower |
| Hyperbeam | attacks all enemies, then applies -3 FocusPower to its owner; upgrade only raises damage | 0.107.1 compatibility branch declared an empty post-attack effect | apply negative FocusPower from the card's FocusPower dynamic var after the attack |
| Expertise | draws only enough cards to bring the current hand to 6 cards (7 upgraded) | drew 6/7 additional cards and gave the drawn cards single-turn Retain | draw target minus current hand size and remove the later-version Retain behavior |
| Scrape | after attacking and drawing 4/5 cards, discards each drawn X-cost card or card whose **local** Energy cost is nonzero; global combat-cost hooks are ignored | tested each drawn card with the solver's all-modifiers cost helper, so a global temporary 0-cost effect could incorrectly save a card from Scrape | use the pinned DLL's `EnergyCost.GetWithModifiers(CostModifiers.Local)` predicate and keep X-cost handling separate |
| Null | attacks, applies Weak 2/3 to the target, then channels one Dark Orb | the mirror attacked and channeled the Dark Orb but omitted Weak entirely | apply Weak to the target between the attack and Dark-Orb channel, preserving native order |
| Voltaic | counts all Lightning Orbs channeled by its owner earlier in combat, then channels that many Lightning Orbs | dedicated OnPlay reread mutable live combat history instead of the frozen root snapshot | use `GetLightningChannelsForCalculatedVar`, combining frozen root history with branch-local channels only |
| Forgotten Ritual | gains 3/4 Energy only if one of its owner's cards was Exhausted earlier this turn; otherwise playing it has no Energy effect | shared the unconditional Luminesce compensation and always granted Energy | split the case and gate Energy gain on the branch-local exhausted-card history for the owner |
| Eidolon | Exhausts the owner's current Hand one card at a time; if at least 9 cards were Exhausted this way, applies 1 Intangible | used the v0.109 redesign and auto-played all playable Ethereal cards from the Exhaust Pile | snapshot the current Hand, Exhaust each card through the simulator, preserve the remaining snapshot/count in a fork-safe continuation across Exhaust-triggered choices, then apply Intangible at the native threshold |
| Well-Laid Plans | during 0.107.1 `BeforeFlushLate`, if the owner's hand will flush, choose 0..1 cards (0..2 upgraded) that are not already retained and give those exact cards single-turn Retain before `FlushPlayerHand` | the Power was applied but its end-turn selector was never mirrored, so the normal flush discarded every non-Retain card | reuse the EndTurn choice pipeline at `PlayerTurnEnd` timing, resolve the 0..Amount hand choice before flush, and call `GiveSingleTurnRetain()` rather than adding a permanent Retain keyword |
| Scare | applies 1 Weak to every hittable enemy; base card Exhausts and the upgrade only removes Exhaust | no explicit mirror or compensation represented the Weak application, while the later Sidestep replacement has unrelated Energy-next-turn semantics | register the 0.107.1 Scare path explicitly and apply one Weak to every hittable enemy; leave Exhaust handling to the pinned card keyword |
| Feral | while FeralPower has remaining uses, any owner 0-Energy Attack is returned to the owner's Hand; 0.107.1 does not exclude gameplay dupes/copies | the result-pile mirror excluded `IsDupe` cards, importing the v0.108 fix for History Course's copied Helix Drill | remove the dupe exclusion so the pinned 0.107.1 result-pile hook is mirrored exactly |
| Shining Strike | attacks, gains 2 Stars, then—unless it has Exhaust or ExhaustOnNextPlay—moves itself from Play to the top of Draw; 0.107.1 does not exclude dupes | only the inferred attack and Star gain were represented, so normal plays and History Course copies fell through to the ordinary result pile instead of returning to Draw | extend the ordered post-attack spec with the native Exhaust checks and Draw-top move, preserving a continuation if AfterStarsGained suspends |
| Mad Science (Chaos rider) | creates one random unlocked character card, sets it free this turn, then inserts it with ordinary `CardPileCmd.Add`; 0.107.1 does not emit a CardGenerated history entry or run generated-card hooks for this rider | used `AddGeneratedCardsToCombat`, so Supermassive counted the card and hooks such as Regalite/Rocket Punch could fire | keep the same RNG/card/free-this-turn behavior, but insert with ordinary simulated `AddToPile` and no generated-card hook dispatch |
| Mad Science (Violence rider) | when the card is an Attack, Violence executes its 3 hits as 3 separate AttackCommands; one-attack effects such as Vigor resolve and are consumed after the first command | collapsed the rider into one AttackCommand with `WithHitCount(3)`, causing Vigor and other command-scoped attack state to persist across all three hits | execute one `DamageCmd.Attack` per hit and preserve the next-hit index plus remaining rider work in a fork-safe continuation |
| Stoke / generated-card async chain | Stoke snapshots the current Hand count, Exhausts that snapshot one card at a time, then generates the same number of random character cards; each generated card fully resolves its generated-card hook before the next card, and Trash to Treasure resumes its Orb loop using the current Power amount | Stoke returned on the first suspended Exhaust; generated-card batches returned on the first suspended hook without resolving that CardGenerated entry or later cards; Trash to Treasure also lost remaining Orb channels | preserve the Stoke Hand snapshot/index, defer and later resolve the pending CardGenerated history entry before continuing the batch, and preserve Trash to Treasure's Power instance plus next loop index |
| Forge / Hammer Time async chain | Forge awaits a newly generated Sovereign Blade before increasing every non-dupe Blade, then awaits AfterForge; Hammer Time lazily walks the stable player roster and awaits one recursive Forge per other living player, with HammerTimePower as the recursion-stop source | a suspended generated-card hook returned out of Forge before Blade growth, and a suspended teammate Forge returned out of Hammer Time before later players were processed | preserve Forge's stage, source Power, player-roster order, and next-player index; resume Blade growth before Hammer Time propagation and re-check player life at each lazy-enumerator position |
| Misery | snapshots the target's Debuffs before attacking, then copies them to each other hittable enemy in native Power order; temporary Power copies suppress their own internal stat application, so 0.107.1 can consume Artifact on the copied negative Strength first and still leave the Enfeebling Touch recovery marker | aggregated Debuffs by type after the attack and folded temporary-marker amount back into its internal Strength entry, losing native order and even cancelling ordinary temporary Strength loss | use a dedicated OnPlay mirror: capture ordered type/amount/applier snapshots before the attack, resume after any attack continuation, then replay them one-by-one without aggregation |
| Flanking | applies an instanced FlankingPower(2) to one enemy; powered attacks from creatures other than the applier deal 2x damage to that target until that target's side turn ends | the card had no deterministic Power application or FlankingPower damage/expiry mirror | apply the instanced debuff, mirror its applier-sensitive multiplier, and remove every instance at the native side-turn boundary |
| Coordinate | applies CoordinatePower equal to Strength (5, upgraded to 8) to one ally; the power is a TemporaryStrengthPower | pure PowerCmd.Apply was not inferable and had no explicit card effect | apply CoordinatePower from the Strength dynamic var through the existing temporary-Strength gain path |
| Tank | applies TankPower(1) to self; Tank takes 2x powered-attack damage while every other living player gets an instanced GuardedPower(1) for 0.5x powered-attack damage, removed if the Tank owner dies | the pure Power card had no explicit application/AfterApplied compensation | apply TankPower, create Guarded instances for living teammates, and remove those instances on applier death |
| Beacon of Hope | applies one BeaconOfHopePower; after the owner gains post-modifier Block on their side, living teammates each receive half that decimal amount through normal Block gain, guarded against recursion | the AfterBlockGained mirror already existed but the card itself never applied BeaconOfHopePower in prediction | register the card's deterministic Power application so the existing fork-safe Block-sharing mirror becomes reachable |
| Hammer Time | applies HammerTimePower(1); after its owner Forges, every other living player Forges the same amount, while HammerTimePower-sourced secondary Forges do not recurse | the card never applied HammerTimePower and simulated Forge stopped before the only gameplay-relevant global AfterForge listener | apply HammerTimePower and run branch-local teammate Forge propagation after the normal Sovereign Blade mutation, passing HammerTimePower as the recursion-suppression source |
| Intercept | gains 9/13 Block, applies instanced CoveredPower to the ally, records each unique covered creature in the interceptor's hidden list, zeros Powered Attack damage to covered allies, and multiplies Powered Attack damage to the interceptor by covered-count + 1 until enemy-side end | no explicit OnPlay chain, Covered/Intercept damage mirrors, hidden-list prediction state, or lifecycle cleanup existed | add native-order OnPlay, exact hidden-state root capture/Fork/fingerprint/stamp, both multipliers, enemy-side expiry, and Covered cleanup when the interceptor dies |
| Demonic Shield | first loses 1 HP as Unblockable/Unpowered/Move card damage, then gives the chosen ally Block equal to the owner's current Block; upgrade only removes Exhaust | the calculated-Block formula existed, but there was no exact compensated OnPlay path for the preceding self-damage and its HP-loss hooks | use a dedicated native-order mirror: resolve card-sourced self-damage first, then calculate and grant the ally Block from the post-damage branch state |
| Sneaky | applies SneakyPower 1 (2 upgraded); after any other player's Attack finishes, the owner gains that much Unpowered Block | the AfterCardPlayed SneakyPower mirror already existed, but the card never applied SneakyPower in prediction | register Sneaky's deterministic owner Power application so the existing cross-player Attack trigger becomes reachable |
| Mimic | reads the selected ally's current Block, then gives that calculated amount of Block to the Mimic owner; upgrade only removes Exhaust | generic AnyAlly block inference used the selected ally as the Block recipient even though the selected ally is only the calculation source | use a dedicated OnPlay mirror that keeps the selected ally as the CalculatedBlock target but grants the resulting Block to the card owner |
| Knockdown | applies an instanced KnockdownPower(2/3); other allied Powered Attacks are multiplied by that instance while the applier's own damage is excluded; each instance is removed when the debuffed creature participates in side-turn end | Power application, applier identity, and pure damage multiplier were already represented, but no predicted side-turn expiry removed the instance | remove each predicted KnockdownPower instance at the native owner-participation boundary without aggregating instances |
| Bulk Up | applies Dexterity, waits for that Power application and its amount-change listeners to finish, removes Orb slots, then applies Strength | removed Orb slots first, queued Strength before Dexterity, and deferred both Power amount-change lifecycles until the card tail | apply Dexterity first and resolve its pending Power amount changes, then remove Orb capacity, then apply Strength for the normal card-tail lifecycle flush |

| History-sensitive calculated vars | Gold Axe counts every finished card play in the combat; Voltaic counts its owner's Lightning channels; Tear Asunder uses 1 + its owner's unblocked-damage event count; Pull From Below counts its owner's finished Ethereal plays; Murder counts its owner's draws; Supermassive counts CardGenerated entries created by its owner | five formulas still read the mutable live `CombatManager.Instance.History` after root capture, while Murder alone froze its root draw history; none of the six cumulative values were part of branch fingerprints or cross-turn continuation stamps | freeze all required native entry families in `RootCombatHistorySnapshot`, calculate every value as frozen root + branch-local prediction history, and include the six cumulative semantics in StateFingerprint plus live/predicted continuation stamps |

These differences materially affect route ranking, so source-shape guards in
`tools/verify-refactor-boundaries.ps1` reject the later-version semantics.
Outbreak's hidden poison counter is also included in the search fingerprint and
live/predicted continuation stamps so branch deduplication and cross-turn reuse
cannot erase or silently mismatch the next trigger.



### Single-player post-0.107.1 version traps

A focused later-patch pass also checked single-player cards whose semantics were
substantially changed after 0.107.1, while intentionally deferring multiplayer
cards.

- **Mirage:** keep the 0.107.1 calculated Block path: sum Poison on every living
  enemy in the simulated branch. Do not import the v0.109 Energy-next-turn
  redesign. The calculation remains in `CalculatedVarSpecRegistry`, so forked
  Poison state rather than the live combat graph determines the result.
- **Compact / Fuel:** Compact still transforms transformable Status cards in the
  Hand into Fuel and propagates Compact's upgrade to those Fuel cards. Fuel must
  resolve its 0.107.1 sequence in order: gain its Energy, then draw its Cards
  amount. The v0.108 Fuel redesign that removed card draw is not valid here.
- **Rocket Punch:** when its owner creates an owned Status, 0.107.1 sets that
  Status card's Energy cost to zero until played. The v0.110 "reduce cost by 1"
  behavior must not replace the exact `SetUntilPlayed(0)` semantics.
- **Inky / Blade of Ink:** 0.107.1 Inky still has its additive attack-damage
  hook and its OnPlay Weak application. Prediction intentionally leaves Inky
  unregistered in the custom additive-damage mirror so the pinned native
  `ModifyDamageAdditive` implementation handles the +1 damage, while the
  explicit enchantment OnPlay mirror applies Weak. The v0.111 removal of Inky's
  extra damage must not be backported.
- **Synchronize:** keep the temporary Focus amount driven by the pinned
  CalculationBase/CalculationExtra values times the number of distinct current
  Orb types. Later changes to its Exhaust/upgrade behavior and Focus values are
  card-model metadata, not constants to copy into prediction.

These checked paths now have source-shape guards in
`tools/verify-refactor-boundaries.ps1`. Ordinary keyword/cost/value changes
that are already supplied by the pinned 0.107.1 CardModel remain model-driven
rather than duplicated in compatibility code.

### Pillar of Creation version guard

Pillar of Creation is a checked-match version trap rather than a new mismatch.
In the pinned 0.107.1 model, the card applies `PillarOfCreationPower` using its
Block dynamic var (3 base, 4 upgraded). Its `AfterCardGeneratedForCombat` hook
then grants that Power amount as Unpowered Block whenever the Power owner's
card generation creates a card. There is no once-per-turn prediction state.

The later v0.109 redesign changed this to a larger Block gain only on the first
created card each turn, so the compatibility verifier now scopes the Pillar
hook itself and rejects once-per-turn state if that newer behavior is copied
back into the 0.107.1 target.

### Adjacent generated-card hook correction

Regalite is not a card, but its hook changes the value of every card-generation
route. In the pinned 0.107.1 DLL, each generated card whose creator is the
relic owner grants 2 Unpowered Block. There is no once-per-turn flag or hidden
counter. The solver had imported the v0.110 behavior and stopped after the
first generated card each turn. The mirror now grants Block on every matching
generation, and the nonexistent Regalite per-turn prediction/fingerprint state
has been removed.

History Course is another adjacent relic boundary because it determines which
card is auto-played at the next turn start. In 0.107.1 it records the owner's
last non-dupe **Attack or Skill** from the previous player turn. The solver had
already imported the v0.109 nerf and tracked Attacks only, which silently
dropped Skill routes from cross-turn prediction. Both simulated turn history
and live-root history lookup now accept Attack or Skill while still excluding
dupes.

### Generated-card continuation correction

Generated-card insertion now preserves the native awaited sequence. If an
`AfterCardGeneratedForCombat` hook suspends, the prediction frame retains the
unresolved `CardGenerated` history entry, resolves it after the nested choice,
then continues with the next generated card. Both already-inserted and
not-yet-inserted card wrappers are remapped when the branch forks.

This is reachable in 0.107.1 through `TrashToTreasurePower`: generating a
Status can Channel random Orbs, and the native state machine re-reads the
current Power amount after every awaited Channel. Its continuation therefore
stores the actual Power instance and next loop index, not a frozen trigger
count. Stoke now likewise preserves its initial Hand snapshot and next Exhaust
index before entering the generated-card batch.

Dedicated generation-card OnPlay handlers now also acknowledge that resumable
generated-card batch. This lets a choice opened by an
`AfterCardGeneratedForCombat` hook resume after the already-completed card
prefix instead of forcing an avoidable whole-card replay. Opaque Attack/Damage
commands keep their separate rejection boundary, so this acknowledgement does
not make a partially completed attack resumable.

### Multi-step OnPlay continuation correction

Several dedicated 0.107.1 card mirrors had the right immediate effects but lost
the rest of their native OnPlay sequence if an intermediate Exhaust, attack,
damage, Block, or kill hook opened a prediction choice. The outer card dispatcher
only resumes the later generic CardSpec suffix; it cannot reconstruct the
remaining bespoke steps by itself.

The affected mirrors are Bone Shards, Demonic Shield, Intercept, Fiend Fire,
Leading Strike, Maul, The Scythe, Sacrifice, Second Wind, and Sovereign Blade.
Their native suffixes are now represented by fork-safe execution frames.
Fiend Fire preserves its original Hand snapshot and next Exhaust index; Second
Wind preserves its non-Attack Hand snapshot plus whether the current card still
needs its post-Exhaust Block; Leading Strike creates each Shiv separately and
preserves the next Shiv index. Simple post-command suffixes share one tail
frame. This keeps nested vanilla hooks such as Dark Embrace, Charon's Ashes,
Forgotten Soul, generated-card hooks, and damage hooks from silently skipping
the rest of the played card.

### Orb command and card continuation correction

The 0.107.1 Orb cards use ordered async command chains: attacks or Block can
precede Channel, Channel can evict and Evoke an existing Orb, and cards such as
Chaos, Darkness, Shatter, and Tesla Coil continue loops after each awaited Orb
operation. Prediction previously returned as soon as an intermediate hook
opened a choice, so the remainder of the native command chain could disappear.

Orb command helpers now retain their own suffixes across a suspended choice
(Evoke -> AfterOrbEvoked, repeated Evoke/death cleanup, full-slot Channel ->
enqueue, repeated Channel, and repeated passive triggers). Orb card mirrors
acknowledge the adapted CardModel.OnPlay dispatch and preserve their card-level
suffix independently. Shared tail frames cover attack/Block -> Channel and
Channel -> draw/Power chains, while Chaos, Darkness, Shatter, and Tesla Coil
store explicit loop indices and fork-remapped Orb snapshots. This mirrors the
0.107.1 await order without replaying already completed Orb effects.

### Card-draw sequence continuation correction

A second multi-step continuation gap existed in the dedicated draw-card mirrors.
The simulator's Draw command already resumes its own shuffle/draw/AfterCardDrawn
work and keeps the same mutable drawn-card list, but the surrounding CardModel
OnPlay handler previously returned on a pending choice and forgot what the card
was supposed to do next.

The 0.107.1 sequences for Adrenaline, Offering, Neurosurge, Spoils of Battle,
Compile Driver, Escape Plan, Fetch, FTL, Huddle Up, Pillage, Reboot,
Restlessness, and Scrape now use one fork-safe card-draw execution frame.
The frame preserves the native program counter, any in-flight Draw result list,
the teammate Creature roster/index for Huddle Up, and Pillage's draw/evaluate
loop. Huddle Up re-checks alive/player state only when each native lazy-enumerator
position is reached, rather than freezing the alive set before the first Draw.
Escape Plan and Scrape therefore evaluate the cards that finish drawing after
the nested choice instead of a partial pre-suspension snapshot, while Offering,
Neurosurge, Reboot, and the attack-then-draw cards no longer skip their native
suffixes.

### Card-selection sequence continuation correction

The dedicated selection/autoplay mirrors had the same suspended-suffix gap as
the draw and Orb cards. In the 0.107.1 assembly, Beat Down and Catastrophe keep
their async loop program counters across each awaited AutoPlay, while Cinder,
Drain Power, Thrash, True Grit, and Uproar resume their selection/exhaust/
upgrade/autoplay suffix only after the preceding Attack or Block command
finishes.

These seven mirrors now acknowledge the card execution dispatch and use a
fork-safe selection execution frame. Beat Down preserves its already-shuffled
discard-pile selection plus the next index; Catastrophe preserves only the next
iteration because native code re-reads and re-shuffles the current Draw pile on
each iteration; the remaining cards preserve their post-command stage. This
prevents a nested card choice from silently ending the outer card early without
changing the existing 0.107.1 RNG-selection policy or True Grit's explicitly
unresolved upgraded player choice.

### Flak Cannon exhaust-loop continuation correction

The pinned 0.107.1 Flak Cannon snapshots every Status card outside the Exhaust
pile and calculates its hit count before exhausting anything. It then awaits
each Exhaust in order and only after the whole snapshot is processed performs
the random-target attack with that original hit count.

Prediction previously returned on the first Exhaust-triggered choice, losing
the remaining Status cards and the final attack. The mirror now stores the
Status snapshot, precomputed hit count, and next Exhaust index in a fork-safe
execution frame. Effects triggered by an early Exhaust therefore cannot change
this play's hit count, matching the native async state machine.

### Generated-card prefix and Mad Science continuation correction

Several generated-card mirrors already used resumable generated-card batches,
but their card-level prefixes could still disappear across a nested choice.
In 0.107.1, Jackpot awaits its Attack before selecting and creating zero-cost
cards, and Manifest Authority awaits Block before creating its colorless card.
Those two mirrors now resume at the generation suffix instead of returning from
OnPlay early.

Mad Science has a second nested state machine in the native assembly. Skill
resolves Block before its rider; Power/Expertise resolves Strength then
Dexterity before returning to the rider; and the Sapping rider resolves Weak
then Vulnerable. Prediction now preserves those exact stages in fork-safe
execution frames. The existing Violence rider still uses separate AttackCommands,
and the Chaos rider still uses ordinary pile insertion as required by 0.107.1.

### Opaque Attack / Damage / Kill continuation boundary

AttackCommand, CreatureCmd.Damage, and CreatureCmd.Kill contain multiple native
hook phases but do not yet have local execution frames that can resume from an
arbitrary interior phase. Card-level continuations therefore must not treat a
choice captured inside one of those commands as a completed command prefix.

The simulator now enters those command mirrors through an **unacknowledged**
execution-dispatch scope. If a nested hook opens a choice, the existing
execution-continuation machinery rejects that partial prefix and falls back to
replaying the whole action from its stable snapshot. Begin/End attack-context
helpers use the same boundary so bespoke attacks and monster attacks cannot
leak an unfinished BeforeAttack/AfterAttack command into a card tail. This
matches the existing `AbortAttack` contract: command-scoped bookkeeping is
cleared on suspension and replay restarts from the whole-action snapshot.

This is deliberately a correctness fallback, not a search-budget reduction.
Draw, generated-card, Orb, discard, and other commands that already own
fork-safe execution frames keep their localized continuation path.

## Checked matches

The initial pass also checked several special cases that already match the
0.107.1 assembly and were left unchanged: No Escape, Synchronize, Hang,
The Scythe, Spite, Heavenly Drill, Glacier, Meteor Strike, Refract,
Fight Through, Predator, Bouncing Flask, Gang Up, Lift, Rally, Tag Team,
Pillar of Creation, Summon Forth, Seeking Edge, and Juggling.

A later-patch trap pass also confirmed four model/version boundaries without
changing solver behavior: Nightmare and Transfigure remain eligible for
in-combat generation because the root pool delegates to the pinned native
combat filter; Entropy's Curse transform remains able to select Ascender's Bane
because transformation filtering stays in the pinned native CardFactory; and
Hand Drill keeps the 0.107.1 damage-based block-break listener while the broader
AfterBlockBroken mirror remains excluded under STS2_01071. This distinction is
intentional: ordinary damage that breaks enemy Block can still trigger Hand
Drill, while the v0.109 Expose fix is not backported.

A focused STS2_01071 conditional pass also confirmed the v0.108 multiplayer
additions remain outside the target build: Midnight, Concoct, Constellation,
Underworld, Soulbound, Cacophony, Hibernate, Imitation Learning, and The Ball.
Their registrations/handlers stay behind `#if !STS2_01071`; no target-version
fallback or partial hook registration was found.

A ten-card later-patch pass also found no new route mismatch. Abundance stays
excluded from the 0.107.1 build because the card was introduced after the
target version. Brightest Flame keeps its 0.107.1 Max HP loss through
`DynamicVars.MaxHp`; Rend keeps its 2-Energy / 15(18)-damage model data;
Rampage keeps 9 base damage and 5(9) scaling through its model vars; Alignment's
3-Star cost remains model metadata while its Energy gain reads `DynamicVars`;
Refine Blade keeps Forge 9(13) plus next-turn Energy from model vars; Shroud
keeps Block 2(3); Time's Up keeps Exhaust through its 0.107.1 keywords; Thunder
keeps 6(8) through `ThunderPower`'s card dynamic var; and Biased Cognition
keeps Focus 4(5) through `FocusPower`'s card dynamic var. Later 0.110/0.111
balance values therefore do not leak into the pinned build.

The next v0.108 balance batch also found no route mismatch. Colossus keeps
5(8) Block, Crimson Mantle 8(10) Block, Howl from Beyond 16(21) damage,
Setup Strike 2(3) temporary Strength, Anticipate 2(3) Dexterity, Flick-Flack
6(8) damage, Devastate 30(40) damage, Resonance a 3-Star cost, Haunt 6(8)
HP loss per Soul, and Reave 9(11) damage. Their changed values remain sourced
from the pinned 0.107.1 CardModel/DynamicVars. Haunt's applied Power also keeps
its amount through the Soul-play trigger, while Reave's explicit generated-Soul
suffix reads only its Cards var and does not replace the model-driven attack
damage.

The final twenty known post-0.107.1 single-player patch-note candidates are
also checked with no additional route mismatch. For v0.108 deltas, Soul Storm
keeps its 2(3) additional damage per exhausted Soul by using the Soul count only
as the CalculatedVar multiplier, and Momentum Strike keeps its 10(13) model
damage while its separate set-to-zero-cost effect remains unchanged. For
v0.109, Demon Form keeps 2(3) Strength; Primal Force transforms into the pinned
`CanonicalModels.Card<GiantRock>()`, preserving Giant Rock's 16(20) damage;
Taunt keeps 7(8) Block and Uncommon rarity; Bloodletting remains Common,
Cruelty Rare, Dominate Uncommon, and Accelerant Rare; Collision Course keeps
11(15) model damage; and Sunder keeps 24(32) model damage while its fatal Energy
gain still reads the card's Energy var.

The remaining v0.110/v0.111 deltas are likewise isolated to pinned model data:
Relax keeps 15(17) Block, Whistle costs 3, Mangle deals 15(20), Pact's End
deals 17(23) when its Exhaust-pile condition passes, Echoing Slash remains
Rare, Terraforming grants 6(8) Vigor, and Crush Under deals 7(8). Salvo and
Splash retain their pre-v0.111 rarities (Salvo Rare, Splash Uncommon); their
combat implementations do not depend on rarity. Mangle and Echoing Slash read
`DynamicVars.Damage`, Pact's End uses the normal card attack value after its
condition, Terraforming reads `VigorPower`, and Splash's generated-card choice
logic is unchanged by the rarity swap.

The first solver-authored hard-coded-literal pass reviewed ten fixed-unit Power
applications: Conqueror, Convergence, Shadow Step, Aggression, Dark Embrace,
Calamity, Fan of Knives, Hello World, Infinite Blades, and Unmovable. No new
route mismatch was found. Their literal `1` values are Power stack/mode units,
not later-version card balance values: each play installs one instance/stack of
the corresponding persistent or temporary rule, while quantified card effects
such as Conqueror's Forge amount and Convergence's next-turn resources remain
model-driven. Fan of Knives' `FanOfKnivesPower(1)` likewise represents the
persistent "Shivs hit all enemies" mode; its 4(5) Shiv creation is a separate
card effect and is not encoded by that literal.

A second hard-coded-literal pass reviewed Expect a Fight, Pounce, Predator,
Rebound, Reflect, Synthesis, Tag Team, The Gambit, Unrelenting, and Veilpiercer.
Again, no route mismatch was found. These literals are semantic counters rather
than balance mirrors: Predator queues exactly two next-turn draws; Pounce,
Synthesis, Unrelenting, Rebound, Tag Team, and Veilpiercer install one
consumable use; Reflect installs one duration stack; The Gambit is a non-stacking
state marker; and Expect a Fight installs one no-energy-gain marker after its
calculated Energy gain. The corresponding hook/turn code consumes or decrements
those Power amounts instead of treating them as card damage/block values.

This completes the known post-0.107.1 single-player patch-note candidate list:
78/78 candidate entries have now been reviewed. Further card audit should no
longer mechanically follow patch notes; it should target solver-authored
hard-coded mirrors, version conditionals, mutable live-state reads, and custom
continuation/order logic that can be wrong even when no later patch mentioned
the card.

The multiplayer-card result is tracked separately in
[`MULTIPLAYER_CARD_COVERAGE.md`](MULTIPLAYER_CARD_COVERAGE.md). "Checked
match" there means source semantics were compared and no mismatch was found;
it does not claim a Host/Client native differential.

Choice-driven cards must be compared across the whole solver pipeline rather
than one OnPlay switch. For example, Brand's exhaust choice and post-choice
Strength application live in `CardChoiceSupport`, so they are not missing
just because the direct OnPlay compensation only contains self-damage.

## Next audit boundary

Continue prioritizing cards whose solver mirrors contain hard-coded constants,
version conditionals, or custom execution order. DynamicVar-driven generic
attack/block/draw recipes are lower risk because their values come from the
0.107.1 model instances themselves.
