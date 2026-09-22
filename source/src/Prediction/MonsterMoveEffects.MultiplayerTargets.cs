using MegaCrit.Sts2.Core.Entities.Creatures;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static partial class MonsterMoveEffects
{
// Exact 0.107.1 move classification from the pinned target fanout audit.
// Runtime dispatch is intentionally a single switch: audit groups remain evidence,
// but multiplayer prediction should classify each move only once.
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

        return (monsterType, moveId) switch
        {
            ("MagiKnight", "DAMPEN_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("TestSubject", "SKULL_BASH_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("SludgeSpinner", "OIL_SPRAY_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("Flyconid", "VULNERABLE_SPORES_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("Flyconid", "FRAIL_SPORES_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("FrogKnight", "TONGUE_LASH") => MultiplayerTargetMode.PerPlayer,
            ("GlobeHead", "SHOCKING_SLAP") => MultiplayerTargetMode.PerPlayer,
            ("BowlbugSilk", "TOXIC_SPIT_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("HauntedShip", "HAUNT_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("HunterKiller", "TENDERIZING_GOOP_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("KinPriest", "ORB_OF_FRAILTY_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("KinPriest", "ORB_OF_WEAKNESS_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("LeafSlimeM", "STICKY_SHOT") => MultiplayerTargetMode.PerPlayer,
            ("LeafSlimeS", "GOOP_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("Mawler", "ROAR_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("Myte", "TOXIC_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("Chomper", "SCREECH_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("MechaKnight", "FLAMETHROWER_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("PunchConstruct", "FAST_PUNCH_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("CorpseSlug", "GOOP_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("SoulFysh", "SCREAM_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("EyeWithTeeth", "DISTRACT_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("Ovicopter", "TENDERIZER_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("Stabbot", "STAB_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("ShrinkerBeetle", "SHRINKER_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("VineShambler", "GRASPING_VINES_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("SlitheringStrangler", "CONSTRICT") => MultiplayerTargetMode.PerPlayer,
            ("SpectralKnight", "HEX") => MultiplayerTargetMode.PerPlayer,
            ("SoulNexus", "DRAIN_LIFE_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("SlimedBerserker", "VOMIT_ICHOR_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("TerrorEel", "TERROR_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("TwigSlimeM", "STICKY_SHOT_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("PhrogParasite", "INFECT_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("Vantom", "DISMEMBER_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("OwlMagistrate", "VERDICT") => MultiplayerTargetMode.PerPlayer,
            ("CeremonialBeast", "BEAST_CRY_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("Queen", "PUPPET_STRINGS_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("Queen", "YOU_ARE_MINE_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("LouseProgenitor", "WEB_CANNON_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("Crusher", "BUG_STING_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("TrackerRubyRaider", "TRACK_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("Noisebot", "NOISE_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("SoulFysh", "BECKON_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("SoulFysh", "GAZE_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("Axebot", "HAMMER_UPPERCUT_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("FakeMerchantMonster", "THROW_RELIC_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("FossilStalker", "TACKLE_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("DecimillipedeSegmentBack", "CONSTRICT_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("DecimillipedeSegmentFront", "CONSTRICT_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("DecimillipedeSegmentMiddle", "CONSTRICT_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("LivingFog", "ADVANCED_GAS_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("TwoTailedRat", "SCREECH_MOVE") => MultiplayerTargetMode.PerPlayer,
            ("Aeonglass", "INCREASING_INTENSITY_MOVE") => MultiplayerTargetMode.PerPlayerThenOwnerOnce,
            ("TestSubject", "BURNING_GROWL_MOVE") => MultiplayerTargetMode.PerPlayerThenOwnerOnce,
            ("LagavulinMatriarch", "SOUL_SIPHON_MOVE") => MultiplayerTargetMode.PerPlayerThenOwnerOnce,
            ("Wriggler", "WRIGGLE_MOVE") => MultiplayerTargetMode.PerPlayerThenOwnerOnce,
            ("TheLost", "DEBILITATING_SMOG") => MultiplayerTargetMode.PerPlayerThenOwnerOnce,
            ("SlimedBerserker", "LEECHING_HUG_MOVE") => MultiplayerTargetMode.PerPlayerThenOwnerOnce,
            ("TheForgotten", "MIASMA") => MultiplayerTargetMode.PerPlayerThenOwnerOnce,
            ("WaterfallGiant", "STOMP_MOVE") => MultiplayerTargetMode.PerPlayerThenOwnerOnce,
            ("GremlinMerc", "DOUBLE_SMASH_MOVE") => MultiplayerTargetMode.PerPlayerThenOwnerOnce,
            ("ThievingHopper", "THIEVERY_MOVE") => MultiplayerTargetMode.SpecialRng,
            ("TheInsatiable", "LIQUIFY_GROUND_MOVE") => MultiplayerTargetMode.SpecialRng,
            _ => MultiplayerTargetMode.SingleTarget,
        };
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
