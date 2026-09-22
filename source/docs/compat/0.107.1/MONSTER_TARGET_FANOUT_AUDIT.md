# STS2 0.107.1 monster target fanout audit

Status: **PINNED 0.107.1 IL AUDIT COMPLETE**

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
| `ThievingHopper.THIEVERY_MOVE` | PerTargetLoop | Explicit `target.IsDead` skip | Enumeration order; per-target deck/pet-owner resolution, `_stealPriorities`, `CombatCardGeneration` RNG, one `SwipePower` per stolen card | NeedsPerTargetRng |
| `KnowledgeDemon.CURSE_OF_KNOWLEDGE_MOVE` | PerPlayerChoice | Explicit `target.IsDead` skip in `ChooseCurse` | Enumeration order; per-player `BlockingPlayerChoiceContext`, curse set/counter and target player; solver currently owns only one pending choice | NeedsRemoteChoiceFailClosed |
| `MagiKnight.DAMPEN_MOVE` | PerTargetLoop | No explicit target-death skip in move IL | Enumeration order; existing `DampenPower` caster set and per-target upgraded-card state; both are already mirrored/fingerprinted | FanOutSafe |
| `Aeonglass.INCREASING_INTENSITY_MOVE` | PerTargetLoop | No explicit target-death skip in move IL | Enumeration order; per-player Wither mutation, then shared `WitherUpgradeCount`, owner Strength and `AdditionalStrength` advance once | NeedsMoreModeling |
| `TheInsatiable.LIQUIFY_GROUND_MOVE` | PerTargetLoop | No explicit target-death skip in move IL | Enumeration order; per-target `SandpitPower` and generated `FranticEscape` placement, plus owner `HasLiquified` once | NeedsPerTargetRng |
| `TestSubject.SKULL_BASH_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Vulnerable application after generic Attack; no per-target RNG/choice | FanOutSafe |
| `TestSubject.BURNING_GROWL_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Burns use supplied target list; owner Strength gain occurs once after target effect | NeedsMoreModeling |
| `SludgeSpinner.OIL_SPRAY_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Weak application after generic Attack; no per-target RNG/choice | FanOutSafe |
| `Flyconid.VULNERABLE_SPORES_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Vulnerable application; no per-target RNG/choice | FanOutSafe |
| `Flyconid.FRAIL_SPORES_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Frail application; no per-target RNG/choice | FanOutSafe |
| `FrogKnight.TONGUE_LASH` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Frail application after generic Attack; no per-target RNG/choice | FanOutSafe |
| `GlobeHead.SHOCKING_SLAP` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Frail application after generic Attack; no per-target RNG/choice | FanOutSafe |
| `BowlbugSilk.TOXIC_SPIT_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Visual target prepass only; gameplay Weak command consumes supplied target list; no gameplay RNG/choice | FanOutSafe |
| `HauntedShip.HAUNT_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Target-list Weak plus deterministic Dazed discard insertion; no per-target choice/RNG | FanOutSafe |
| `HunterKiller.TENDERIZING_GOOP_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Tender application; no per-target RNG/choice | FanOutSafe |
| `KinPriest.ORB_OF_FRAILTY_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Frail application after generic Attack; no per-target RNG/choice | FanOutSafe |
| `KinPriest.ORB_OF_WEAKNESS_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Weak application after generic Attack; no per-target RNG/choice | FanOutSafe |
| `LagavulinMatriarch.SOUL_SIPHON_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Target-list Strength/Dexterity loss; owner Strength gain occurs once | NeedsMoreModeling |
| `LeafSlimeM.STICKY_SHOT` | AllTargets | No explicit target-death skip; forwards supplied targets | Visual target prepass only; deterministic Slimed discard insertion uses supplied target list | FanOutSafe |
| `LeafSlimeS.GOOP_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Deterministic Slimed discard insertion over supplied target list | FanOutSafe |
| `Mawler.ROAR_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Vulnerable application; no per-target RNG/choice | FanOutSafe |
| `Myte.TOXIC_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Deterministic Toxic hand insertion over supplied target list | FanOutSafe |
| `Chomper.SCREECH_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Deterministic Dazed discard insertion over supplied target list | FanOutSafe |
| `MechaKnight.FLAMETHROWER_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Deterministic Burn hand insertion over supplied target list | FanOutSafe |
| `PunchConstruct.FAST_PUNCH_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Frail application after generic Attack; no per-target RNG/choice | FanOutSafe |
| `Wriggler.WRIGGLE_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Deterministic Infection insertion uses supplied target list; owner Strength gain occurs once | NeedsMoreModeling |
| `CorpseSlug.GOOP_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Frail application with native amount; no per-target RNG/choice | FanOutSafe |
| `SoulFysh.SCREAM_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Owner visibility/VFX work is separate; target-list Vulnerable application is deterministic | FanOutSafe |
| `TheLost.DEBILITATING_SMOG` | AllTargets | No explicit target-death skip; forwards supplied targets | Target-list Strength loss; owner Strength gain occurs once | NeedsMoreModeling |
| `EyeWithTeeth.DISTRACT_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Deterministic Dazed discard insertion over supplied target list | FanOutSafe |
| `Ovicopter.TENDERIZER_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Vulnerable application after generic Attack; no per-target RNG/choice | FanOutSafe |
| `Stabbot.STAB_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Frail application after generic Attack; no per-target RNG/choice | FanOutSafe |
| `ShrinkerBeetle.SHRINKER_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Shrink application; no per-target RNG/choice | FanOutSafe |
| `VineShambler.GRASPING_VINES_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Tangled application after generic Attack; no per-target RNG/choice | FanOutSafe |
| `SlitheringStrangler.CONSTRICT` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Constrict application; no per-target RNG/choice | FanOutSafe |
| `SpectralKnight.HEX` | PerTargetLoop | No explicit target-death skip in move IL | Enumeration order; sequential deterministic Hex application to each target | FanOutSafe |
| `SoulNexus.DRAIN_LIFE_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Target-list Vulnerable and Weak applications after generic Attack; deterministic | FanOutSafe |
| `SlimedBerserker.VOMIT_ICHOR_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Deterministic Slimed discard insertion over supplied target list | FanOutSafe |
| `SlimedBerserker.LEECHING_HUG_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Target-list Weak application; owner Strength gain occurs once | NeedsMoreModeling |
| `TerrorEel.TERROR_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Vulnerable application; no per-target RNG/choice | FanOutSafe |
| `TwigSlimeM.STICKY_SHOT_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Visual target prepass only; deterministic Slimed discard insertion uses supplied target list | FanOutSafe |
| `PhrogParasite.INFECT_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Visual target prepass only; deterministic Infection discard insertion uses supplied target list | FanOutSafe |
| `Vantom.DISMEMBER_MOVE` | AllTargets | No explicit target-death skip; owner-alive guard only | `Chaotic` RNG in move IL is visual screen shake only; gameplay Wound insertion uses supplied target list deterministically | FanOutSafe |
| `TheForgotten.MIASMA` | AllTargets | No explicit target-death skip; forwards supplied targets | Target-list Dexterity loss; owner Block and Dexterity gain occur once | NeedsMoreModeling |
| `OwlMagistrate.VERDICT` | AllTargets | No explicit target-death skip; forwards supplied targets | Target-list Vulnerable then owner Soar removal once; solver's repeated set-to-zero is idempotent | FanOutSafe |
| `CeremonialBeast.BEAST_CRY_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Ringing application; no per-target RNG/choice | FanOutSafe |
| `Queen.PUPPET_STRINGS_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Chains of Binding application; no per-target RNG/choice | FanOutSafe |
| `Queen.YOU_ARE_MINE_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Target-list Frail/Weak/Vulnerable applications; deterministic | FanOutSafe |
| `LouseProgenitor.WEB_CANNON_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Frail application after generic Attack; no per-target RNG/choice | FanOutSafe |
| `Crusher.BUG_STING_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Target-list Weak and Frail applications after generic multi-hit Attack; deterministic | FanOutSafe |
| `TrackerRubyRaider.TRACK_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Target/VFX setup is deterministic; gameplay Frail command consumes supplied target list | FanOutSafe |
| `Noisebot.NOISE_MOVE` | PerTargetLoop | No explicit target-death skip in move IL | Enumeration order; per-target generated Dazed cards and draw-pile insertion modeled by solver with random position | NeedsPerTargetRng |
| `SoulFysh.BECKON_MOVE` | PerTargetLoop | No explicit target-death skip in move IL | Enumeration order; per-target generated Beckon cards and draw-pile insertion modeled by solver with random position | NeedsPerTargetRng |
| `SoulFysh.GAZE_MOVE` | PerTargetLoop | No explicit target-death skip in move IL | Enumeration order; per-target generated Beckon discard insertion after generic Attack; no random position in solver path | FanOutSafe |
| `Axebot.HAMMER_UPPERCUT_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Target-list Weak and Frail applications after generic Attack; deterministic | FanOutSafe |
| `FakeMerchantMonster.THROW_RELIC_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Frail application after generic Attack; no per-target RNG/choice | FanOutSafe |
| `FossilStalker.TACKLE_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Frail application after generic Attack; no per-target RNG/choice | FanOutSafe |
| `WaterfallGiant.STOMP_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Target-list Weak application; owner Steam Eruption gain occurs once | NeedsMoreModeling |
| `DecimillipedeSegmentBack.CONSTRICT_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Shared base-handler target-list Weak application after generic Attack; deterministic | FanOutSafe |
| `DecimillipedeSegmentFront.CONSTRICT_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Shared base-handler target-list Weak application after generic Attack; deterministic | FanOutSafe |
| `DecimillipedeSegmentMiddle.CONSTRICT_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Shared base-handler target-list Weak application after generic Attack; deterministic | FanOutSafe |
| `GremlinMerc.DOUBLE_SMASH_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Owner `ThieveryPower.Steal` side effect runs once before target-list Weak application | NeedsMoreModeling |
| `LivingFog.ADVANCED_GAS_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Single target-list Smoggy application after generic Attack; no per-target RNG/choice | FanOutSafe |
| `TwoTailedRat.SCREECH_MOVE` | AllTargets | No explicit target-death skip; forwards supplied targets | Owner `TurnsUntilSummonable` changes once before deterministic target-list Frail application; current target effect is independently fan-out safe | FanOutSafe |
