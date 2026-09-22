# Next local task — 0.107.1 monster target fanout audit

Purpose: resolve the remaining multiplayer enemy-turn semantic gap without guessing from later game builds.

## Hard truth source

Use the locally pinned game body only:

- game version: `v0.107.1`
- game commit: `59260271`
- assembly: `game-body/data_sts2_windows_x86_64/sts2.dll`
- expected SHA-256: `a1f9e653f1e28e4076558fee1e60d218619cb7e057b887c6417f62c62c6d7a52`

Abort the audit if the assembly hash differs. Do not use current-beta source or wiki text to fill missing target semantics.

## Already resolved — do not redo

Repository commits `7f3c0634`, `8bf42128`, `6eee0070`, `3f1fb7c2` already establish the generic monster attack path:

- native `MonsterModel.PerformMove` supplies the player-creature roster to a move;
- native `AttackCommand.FromMonster` targets all player creatures;
- predicted monster Attack damage now uses the multi-target Damage pipeline;
- a local-player death no longer truncates later hits while another player survives;
- any fully blocked recipient can trigger Imbalanced;
- simulated player death deactivates that player's later hooks.

Do not change those paths unless the pinned DLL contradicts them.

## Exact audit target

Compare the pinned 0.107.1 implementation of each listed move with
`source/src/Prediction/MonsterMoveEffects.cs`.

The current solver still routes the following effects through one `Creature player`.
For each move, record how the native move uses its `targets` argument.

### Special / stateful first

- `ThievingHopper.THIEVERY_MOVE`
- `KnowledgeDemon.CURSE_OF_KNOWLEDGE_MOVE`
- `MagiKnight.DAMPEN_MOVE`
- `Aeonglass.INCREASING_INTENSITY_MOVE`
- `TheInsatiable.LIQUIFY_GROUND_MOVE`

### Debuff / status / targeted-state candidates

- `TestSubject.SKULL_BASH_MOVE`
- `TestSubject.BURNING_GROWL_MOVE`
- `SludgeSpinner.OIL_SPRAY_MOVE`
- `Flyconid.VULNERABLE_SPORES_MOVE`
- `Flyconid.FRAIL_SPORES_MOVE`
- `FrogKnight.TONGUE_LASH`
- `GlobeHead.SHOCKING_SLAP`
- `BowlbugSilk.TOXIC_SPIT_MOVE`
- `HauntedShip.HAUNT_MOVE`
- `HunterKiller.TENDERIZING_GOOP_MOVE`
- `KinPriest.ORB_OF_FRAILTY_MOVE`
- `KinPriest.ORB_OF_WEAKNESS_MOVE`
- `LagavulinMatriarch.SOUL_SIPHON_MOVE`
- `LeafSlimeM.STICKY_SHOT`
- `LeafSlimeS.GOOP_MOVE`
- `Mawler.ROAR_MOVE`
- `Myte.TOXIC_MOVE`
- `Chomper.SCREECH_MOVE`
- `MechaKnight.FLAMETHROWER_MOVE`
- `PunchConstruct.FAST_PUNCH_MOVE`
- `Wriggler.WRIGGLE_MOVE`
- `CorpseSlug.GOOP_MOVE`
- `SoulFysh.SCREAM_MOVE`
- `TheLost.DEBILITATING_SMOG`
- `EyeWithTeeth.DISTRACT_MOVE`
- `Ovicopter.TENDERIZER_MOVE`
- `Stabbot.STAB_MOVE`
- `ShrinkerBeetle.SHRINKER_MOVE`
- `VineShambler.GRASPING_VINES_MOVE`
- `SlitheringStrangler.CONSTRICT`
- `SpectralKnight.HEX`
- `SoulNexus.DRAIN_LIFE_MOVE`
- `SlimedBerserker.VOMIT_ICHOR_MOVE`
- `SlimedBerserker.LEECHING_HUG_MOVE`
- `TerrorEel.TERROR_MOVE`
- `TwigSlimeM.STICKY_SHOT_MOVE`
- `PhrogParasite.INFECT_MOVE`
- `Vantom.DISMEMBER_MOVE`
- `TheForgotten.MIASMA`
- `OwlMagistrate.VERDICT`
- `CeremonialBeast.BEAST_CRY_MOVE`
- `Queen.PUPPET_STRINGS_MOVE`
- `Queen.YOU_ARE_MINE_MOVE`
- `LouseProgenitor.WEB_CANNON_MOVE`
- `Crusher.BUG_STING_MOVE`
- `TrackerRubyRaider.TRACK_MOVE`
- `Noisebot.NOISE_MOVE`
- `SoulFysh.BECKON_MOVE`
- `SoulFysh.GAZE_MOVE`
- `Axebot.HAMMER_UPPERCUT_MOVE`
- `FakeMerchantMonster.THROW_RELIC_MOVE`
- `FossilStalker.TACKLE_MOVE`
- `WaterfallGiant.STOMP_MOVE`
- `DecimillipedeSegmentBack.CONSTRICT_MOVE`
- `DecimillipedeSegmentFront.CONSTRICT_MOVE`
- `DecimillipedeSegmentMiddle.CONSTRICT_MOVE`
- `GremlinMerc.DOUBLE_SMASH_MOVE`
- `LivingFog.ADVANCED_GAS_MOVE`
- `TwoTailedRat.SCREECH_MOVE`

## Required classification

For each move output exactly one native target class:

- `AllTargets`: command receives the full `targets` collection directly.
- `PerTargetLoop`: native explicitly loops all targets and performs per-player work/RNG.
- `SingleSelected`: native deliberately selects only one target.
- `SelfOnly`: listed solver `player` use is not actually a player-targeted native effect.
- `PerPlayerChoice`: each living target opens/resolves a separate player choice.
- `Unknown`: exact pinned implementation could not be established.

Also record:

- whether dead targets are skipped;
- whether target order matters;
- RNG stream(s) and how many consumes occur per target;
- whether remote Hand/Draw/Discard/Exhaust, Orb, Energy/Stars, relic state, or player choice is required;
- whether the solver can mechanically fan out the existing effect with the currently captured readable root, or must fail closed.

## Output

Create/update:

`source/docs/compat/0.107.1/MONSTER_TARGET_FANOUT_AUDIT.md`

Use a compact table:

| Monster.Move | Native target class | Dead filtering | RNG/choice/private dependency | Solver action |
|---|---|---|---|---|

`Solver action` must be one of:

- `FanOutSafe`
- `NeedsPerTargetRng`
- `NeedsRemoteChoiceFailClosed`
- `NeedsMoreModeling`
- `NoChange`

For each row include the pinned type/method name used as evidence. Keep quotations/snippets minimal.

## Constraints

- Do not modify runtime solver code in this task.
- Do not broaden `RootActionPlayers`.
- Do not invent teammate actions.
- Do not use current Hexpion/current-beta behavior as a substitute for the pinned DLL.
- Do not claim Host/Client runtime PASS.
- If decompilation is unavailable, stop after writing the hash/tooling failure into the audit file.
