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

Thirty-five route-affecting mismatches have been confirmed in the 0.107.1 source audit:

| Card | 0.107.1 native behavior | Incorrect solver behavior | Correction |
|---|---|---|---|
| Tracking | first play applies TrackingPower 2, later plays +1; against Weak targets the power amount is the damage multiplier | applied 50 and interpreted it as percentage bonus | use 2 then +1 and multiply by the branch power amount |
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

These differences materially affect route ranking, so source-shape guards in
`tools/verify-refactor-boundaries.ps1` reject the later-version semantics.
Outbreak's hidden poison counter is also included in the search fingerprint and
live/predicted continuation stamps so branch deduplication and cross-turn reuse
cannot erase or silently mismatch the next trigger.

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
Fight Through, Predator, Bouncing Flask, Gang Up, Lift, Rally, and Tag Team.

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
