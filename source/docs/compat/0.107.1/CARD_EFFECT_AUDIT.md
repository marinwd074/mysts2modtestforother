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

Eighteen route-affecting mismatches have been confirmed in the 0.107.1 source audit:

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
| Flanking | applies an instanced FlankingPower(2) to one enemy; powered attacks from creatures other than the applier deal 2x damage to that target until that target's side turn ends | the card had no deterministic Power application or FlankingPower damage/expiry mirror | apply the instanced debuff, mirror its applier-sensitive multiplier, and remove every instance at the native side-turn boundary |
| Coordinate | applies CoordinatePower equal to Strength (5, upgraded to 8) to one ally; the power is a TemporaryStrengthPower | pure PowerCmd.Apply was not inferable and had no explicit card effect | apply CoordinatePower from the Strength dynamic var through the existing temporary-Strength gain path |
| Tank | applies TankPower(1) to self; Tank takes 2x powered-attack damage while every other living player gets an instanced GuardedPower(1) for 0.5x powered-attack damage, removed if the Tank owner dies | the pure Power card had no explicit application/AfterApplied compensation | apply TankPower, create Guarded instances for living teammates, and remove those instances on applier death |
| Beacon of Hope | applies one BeaconOfHopePower; after the owner gains post-modifier Block on their side, living teammates each receive half that decimal amount through normal Block gain, guarded against recursion | the AfterBlockGained mirror already existed but the card itself never applied BeaconOfHopePower in prediction | register the card's deterministic Power application so the existing fork-safe Block-sharing mirror becomes reachable |
| Hammer Time | applies HammerTimePower(1); after its owner Forges, every other living player Forges the same amount, while HammerTimePower-sourced secondary Forges do not recurse | the card never applied HammerTimePower and simulated Forge stopped before the only gameplay-relevant global AfterForge listener | apply HammerTimePower and run branch-local teammate Forge propagation after the normal Sovereign Blade mutation, passing HammerTimePower as the recursion-suppression source |
| Intercept | gains 9/13 Block, applies instanced CoveredPower to the ally, records each unique covered creature in the interceptor's hidden list, zeros Powered Attack damage to covered allies, and multiplies Powered Attack damage to the interceptor by covered-count + 1 until enemy-side end | no explicit OnPlay chain, Covered/Intercept damage mirrors, hidden-list prediction state, or lifecycle cleanup existed | add native-order OnPlay, exact hidden-state root capture/Fork/fingerprint/stamp, both multipliers, enemy-side expiry, and Covered cleanup when the interceptor dies |
| Demonic Shield | first loses 1 HP as Unblockable/Unpowered/Move card damage, then gives the chosen ally Block equal to the owner's current Block; upgrade only removes Exhaust | the calculated-Block formula existed, but there was no exact compensated OnPlay path for the preceding self-damage and its HP-loss hooks | use a dedicated native-order mirror: resolve card-sourced self-damage first, then calculate and grant the ally Block from the post-damage branch state |
| Sneaky | applies SneakyPower 1 (2 upgraded); after any other player's Attack finishes, the owner gains that much Unpowered Block | the AfterCardPlayed SneakyPower mirror already existed, but the card never applied SneakyPower in prediction | register Sneaky's deterministic owner Power application so the existing cross-player Attack trigger becomes reachable |
| Mimic | reads the selected ally's current Block, then gives that calculated amount of Block to the Mimic owner; upgrade only removes Exhaust | generic AnyAlly block inference used the selected ally as the Block recipient even though the selected ally is only the calculation source | use a dedicated OnPlay mirror that keeps the selected ally as the CalculatedBlock target but grants the resulting Block to the card owner |

These differences materially affect route ranking, so source-shape guards in
`tools/verify-refactor-boundaries.ps1` reject the later-version semantics.
Outbreak's hidden poison counter is also included in the search fingerprint and
live/predicted continuation stamps so branch deduplication and cross-turn reuse
cannot erase or silently mismatch the next trigger.

## Checked matches

The initial pass also checked several special cases that already match the
0.107.1 assembly and were left unchanged: No Escape, Synchronize, Hang,
The Scythe, Spite, Heavenly Drill, Glacier, Meteor Strike, Refract,
Fight Through, Predator, and Bouncing Flask.

Choice-driven cards must be compared across the whole solver pipeline rather
than one OnPlay switch. For example, Brand's exhaust choice and post-choice
Strength application live in `CardChoiceSupport`, so they are not missing
just because the direct OnPlay compensation only contains self-damage.

## Next audit boundary

Continue prioritizing cards whose solver mirrors contain hard-coded constants,
version conditionals, or custom execution order. DynamicVar-driven generic
attack/block/draw recipes are lower risk because their values come from the
0.107.1 model instances themselves.
