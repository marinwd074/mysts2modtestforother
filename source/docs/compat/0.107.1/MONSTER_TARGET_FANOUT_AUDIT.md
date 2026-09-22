# STS2 0.107.1 monster target fanout audit

Status: **PENDING LOCAL PINNED-DLL EVIDENCE**

Truth source:

- game version: `v0.107.1`
- game commit: `59260271`
- assembly: `game-body/data_sts2_windows_x86_64/sts2.dll`
- required SHA-256: `a1f9e653f1e28e4076558fee1e60d218619cb7e057b887c6417f62c62c6d7a52`
- local task: [NEXT_LOCAL_01071_MONSTER_TARGET_AUDIT.md](../../multiplayer/NEXT_LOCAL_01071_MONSTER_TARGET_AUDIT.md)

Do not replace `PENDING_PINNED_IL` with a conclusion derived only from a current-beta
decompilation or wiki. Run `Sts2LocalInspector --monster-move-il-output` against the
hash-verified pinned DLL first.

The generic native monster Attack path is already resolved by
`7f3c0634`, `8bf42128`, `6eee0070`, and `3f1fb7c2`: monster Attack damage
fans out to the captured player roster, multi-hit continues while any player lives,
any fully-blocked recipient can trigger Imbalanced, and dead-player hooks are removed.
The table below is only for the remaining move-specific target fanout.

Allowed native target classes:
`AllTargets`, `PerTargetLoop`, `SingleSelected`, `SelfOnly`,
`PerPlayerChoice`, `Unknown`.

Allowed solver actions:
`FanOutSafe`, `NeedsPerTargetRng`, `NeedsRemoteChoiceFailClosed`,
`NeedsMoreModeling`, `NoChange`.

| Monster.Move | Native target class | Dead filtering | RNG/choice/private dependency | Solver action |
|---|---|---|---|---|
| `ThievingHopper.THIEVERY_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `KnowledgeDemon.CURSE_OF_KNOWLEDGE_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `MagiKnight.DAMPEN_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `Aeonglass.INCREASING_INTENSITY_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `TheInsatiable.LIQUIFY_GROUND_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `TestSubject.SKULL_BASH_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `TestSubject.BURNING_GROWL_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `SludgeSpinner.OIL_SPRAY_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `Flyconid.VULNERABLE_SPORES_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `Flyconid.FRAIL_SPORES_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `FrogKnight.TONGUE_LASH` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `GlobeHead.SHOCKING_SLAP` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `BowlbugSilk.TOXIC_SPIT_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `HauntedShip.HAUNT_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `HunterKiller.TENDERIZING_GOOP_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `KinPriest.ORB_OF_FRAILTY_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `KinPriest.ORB_OF_WEAKNESS_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `LagavulinMatriarch.SOUL_SIPHON_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `LeafSlimeM.STICKY_SHOT` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `LeafSlimeS.GOOP_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `Mawler.ROAR_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `Myte.TOXIC_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `Chomper.SCREECH_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `MechaKnight.FLAMETHROWER_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `PunchConstruct.FAST_PUNCH_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `Wriggler.WRIGGLE_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `CorpseSlug.GOOP_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `SoulFysh.SCREAM_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `TheLost.DEBILITATING_SMOG` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `EyeWithTeeth.DISTRACT_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `Ovicopter.TENDERIZER_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `Stabbot.STAB_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `ShrinkerBeetle.SHRINKER_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `VineShambler.GRASPING_VINES_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `SlitheringStrangler.CONSTRICT` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `SpectralKnight.HEX` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `SoulNexus.DRAIN_LIFE_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `SlimedBerserker.VOMIT_ICHOR_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `SlimedBerserker.LEECHING_HUG_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `TerrorEel.TERROR_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `TwigSlimeM.STICKY_SHOT_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `PhrogParasite.INFECT_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `Vantom.DISMEMBER_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `TheForgotten.MIASMA` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `OwlMagistrate.VERDICT` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `CeremonialBeast.BEAST_CRY_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `Queen.PUPPET_STRINGS_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `Queen.YOU_ARE_MINE_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `LouseProgenitor.WEB_CANNON_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `Crusher.BUG_STING_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `TrackerRubyRaider.TRACK_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `Noisebot.NOISE_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `SoulFysh.BECKON_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `SoulFysh.GAZE_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `Axebot.HAMMER_UPPERCUT_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `FakeMerchantMonster.THROW_RELIC_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `FossilStalker.TACKLE_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `WaterfallGiant.STOMP_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `DecimillipedeSegmentBack.CONSTRICT_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `DecimillipedeSegmentFront.CONSTRICT_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `DecimillipedeSegmentMiddle.CONSTRICT_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `GremlinMerc.DOUBLE_SMASH_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `LivingFog.ADVANCED_GAS_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
| `TwoTailedRat.SCREECH_MOVE` | PENDING_PINNED_IL | PENDING | PENDING | NeedsMoreModeling |
