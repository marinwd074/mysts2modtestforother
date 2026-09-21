# 0.107.1 card-effect source audit

This audit pins card simulation semantics to the repository's own STS2
`v0.107.1` game-body snapshot instead of interpreting later upstream
CombatSolver behavior as old-version truth.

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

Three route-affecting mismatches were confirmed in the first pass:

| Card | 0.107.1 native behavior | Incorrect solver behavior | Correction |
|---|---|---|---|
| Tracking | first play applies TrackingPower 2, later plays +1; against Weak targets the power amount is the damage multiplier | applied 50 and interpreted it as percentage bonus | use 2 then +1 and multiply by the branch power amount |
| Sacrifice | kills the living Osty and gains block equal to 2x Osty's max HP | used 3x max HP | restore 2x |
| Haze | applies Poison to all hittable enemies; upgrade increases Poison | also applied Weak | remove the Weak application |

These differences materially affect route ranking, so source-shape guards in
`tools/verify-refactor-boundaries.ps1` reject the later-version semantics.

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
