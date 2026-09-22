using MegaCrit.Sts2.Core.Entities.Creatures;

namespace CombatSolver;

internal static partial class MonsterMoveEffects
{
// Exact allow-list from the hash-verified 0.107.1 monster target fanout audit.
    // Only these move-specific effects may reuse the captured player roster. RNG, choice,
    // and unresolved rows are deliberately not widened by this path.
    private static bool IsPinnedSimpleFanOutSafe(string monsterType, string moveId)
        => (monsterType, moveId) is
            ("MagiKnight", "DAMPEN_MOVE") or
            ("TestSubject", "SKULL_BASH_MOVE") or
            ("SludgeSpinner", "OIL_SPRAY_MOVE") or
            ("Flyconid", "VULNERABLE_SPORES_MOVE") or
            ("Flyconid", "FRAIL_SPORES_MOVE") or
            ("FrogKnight", "TONGUE_LASH") or
            ("GlobeHead", "SHOCKING_SLAP") or
            ("BowlbugSilk", "TOXIC_SPIT_MOVE") or
            ("HauntedShip", "HAUNT_MOVE") or
            ("HunterKiller", "TENDERIZING_GOOP_MOVE") or
            ("KinPriest", "ORB_OF_FRAILTY_MOVE") or
            ("KinPriest", "ORB_OF_WEAKNESS_MOVE") or
            ("LeafSlimeM", "STICKY_SHOT") or
            ("LeafSlimeS", "GOOP_MOVE") or
            ("Mawler", "ROAR_MOVE") or
            ("Myte", "TOXIC_MOVE") or
            ("Chomper", "SCREECH_MOVE") or
            ("MechaKnight", "FLAMETHROWER_MOVE") or
            ("PunchConstruct", "FAST_PUNCH_MOVE") or
            ("CorpseSlug", "GOOP_MOVE") or
            ("SoulFysh", "SCREAM_MOVE") or
            ("EyeWithTeeth", "DISTRACT_MOVE") or
            ("Ovicopter", "TENDERIZER_MOVE") or
            ("Stabbot", "STAB_MOVE") or
            ("ShrinkerBeetle", "SHRINKER_MOVE") or
            ("VineShambler", "GRASPING_VINES_MOVE") or
            ("SlitheringStrangler", "CONSTRICT") or
            ("SpectralKnight", "HEX") or
            ("SoulNexus", "DRAIN_LIFE_MOVE") or
            ("SlimedBerserker", "VOMIT_ICHOR_MOVE") or
            ("TerrorEel", "TERROR_MOVE") or
            ("TwigSlimeM", "STICKY_SHOT_MOVE") or
            ("PhrogParasite", "INFECT_MOVE") or
            ("Vantom", "DISMEMBER_MOVE") or
            ("OwlMagistrate", "VERDICT") or
            ("CeremonialBeast", "BEAST_CRY_MOVE") or
            ("Queen", "PUPPET_STRINGS_MOVE") or
            ("Queen", "YOU_ARE_MINE_MOVE") or
            ("LouseProgenitor", "WEB_CANNON_MOVE") or
            ("Crusher", "BUG_STING_MOVE") or
            ("TrackerRubyRaider", "TRACK_MOVE") or
            ("Noisebot", "NOISE_MOVE") or
            ("SoulFysh", "BECKON_MOVE") or
            ("SoulFysh", "GAZE_MOVE") or
            ("Axebot", "HAMMER_UPPERCUT_MOVE") or
            ("FakeMerchantMonster", "THROW_RELIC_MOVE") or
            ("FossilStalker", "TACKLE_MOVE") or
            ("DecimillipedeSegmentBack", "CONSTRICT_MOVE") or
            ("DecimillipedeSegmentFront", "CONSTRICT_MOVE") or
            ("DecimillipedeSegmentMiddle", "CONSTRICT_MOVE") or
            ("LivingFog", "ADVANCED_GAS_MOVE") or
            ("TwoTailedRat", "SCREECH_MOVE");

    private static bool IsPinnedSplitFanOutSafe(string monsterType, string moveId)
        => (monsterType, moveId) is
            ("Aeonglass", "INCREASING_INTENSITY_MOVE") or
            ("TestSubject", "BURNING_GROWL_MOVE") or
            ("LagavulinMatriarch", "SOUL_SIPHON_MOVE") or
            ("Wriggler", "WRIGGLE_MOVE") or
            ("TheLost", "DEBILITATING_SMOG") or
            ("SlimedBerserker", "LEECHING_HUG_MOVE") or
            ("TheForgotten", "MIASMA") or
            ("WaterfallGiant", "STOMP_MOVE") or
            ("GremlinMerc", "DOUBLE_SMASH_MOVE");

    private static bool IsPinnedSpecialRngFanOutSafe(string monsterType, string moveId)
        => (monsterType, moveId) is
            ("ThievingHopper", "THIEVERY_MOVE") or
            ("TheInsatiable", "LIQUIFY_GROUND_MOVE");



    private enum MultiplayerTargetMode
    {
        SingleTarget,
        PerPlayer,
        PerPlayerThenOwnerOnce,
        SpecialRng,
    }

    private static MultiplayerTargetMode ResolveMultiplayerTargetMode(
        string monsterType,
        string moveId,
        int playerCount)
    {
        if (playerCount <= 1)
            return MultiplayerTargetMode.SingleTarget;
        if (IsPinnedSplitFanOutSafe(monsterType, moveId))
            return MultiplayerTargetMode.PerPlayerThenOwnerOnce;
        if (IsPinnedSpecialRngFanOutSafe(monsterType, moveId))
            return MultiplayerTargetMode.SpecialRng;
        if (IsPinnedSimpleFanOutSafe(monsterType, moveId))
            return MultiplayerTargetMode.PerPlayer;
        return MultiplayerTargetMode.SingleTarget;
    }

    public static bool Apply(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        out bool killedOwner,
        IReadOnlyList<PlanCardChoice>? plannedChoices = null)
    {
        string type = move.Owner.Monster!.GetType().Name;
        string id = move.Move.Id;
        MultiplayerTargetMode mode = ResolveMultiplayerTargetMode(
            type,
            id,
            simulator.State.PlayerCreatures.Count);

        return mode switch
        {
            MultiplayerTargetMode.PerPlayer => ApplyPerPlayerTargets(
                simulator, combat, move, out killedOwner, plannedChoices),
            MultiplayerTargetMode.PerPlayerThenOwnerOnce => ApplyPerPlayerThenOwnerOnce(
                simulator, combat, move, out killedOwner),
            MultiplayerTargetMode.SpecialRng => ApplySpecialMultiplayerMove(
                simulator, combat, move, player, out killedOwner, plannedChoices),
            _ => ApplySingleTarget(
                simulator,
                combat,
                move,
                player,
                out killedOwner,
                plannedChoices,
                applySharedPreamble: true),
        };
    }

    private static bool ApplyPerPlayerTargets(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        out bool killedOwner,
        IReadOnlyList<PlanCardChoice>? plannedChoices)
    {
        bool handled = false;
        killedOwner = false;
        bool applySharedPreamble = true;
        foreach (Creature target in simulator.State.PlayerCreatures)
        {
            handled |= ApplySingleTarget(
                simulator,
                combat,
                move,
                target,
                out bool targetKilledOwner,
                plannedChoices,
                applySharedPreamble);
            killedOwner |= targetKilledOwner;
            applySharedPreamble = false;
            if (simulator.HasPendingChoice)
                break;
        }
        return handled;
    }

    private static bool ApplySpecialMultiplayerMove(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature fallbackPlayer,
        out bool killedOwner,
        IReadOnlyList<PlanCardChoice>? plannedChoices)
    {
        string type = move.Owner.Monster!.GetType().Name;
        string id = move.Move.Id;
        if (type == "TheInsatiable" && id == "LIQUIFY_GROUND_MOVE")
            return ApplyLiquifyGroundFanOut(simulator, combat, move, out killedOwner);

        // THIEVERY_MOVE is fully handled in ApplyBeforeAttack.
        return ApplySingleTarget(
            simulator,
            combat,
            move,
            fallbackPlayer,
            out killedOwner,
            plannedChoices,
            applySharedPreamble: true);
    }
}
