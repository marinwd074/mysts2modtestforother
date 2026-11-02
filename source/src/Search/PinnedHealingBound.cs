using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Badges;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Models.Singleton;

namespace CombatSolver;

// Adapted from upstream StrategicHpRecoveryBound. The newer game's certificate is
// deliberately not reused: this closed vocabulary belongs to the pinned native DLL.
internal static class PinnedHealingBound
{
    internal static readonly Guid AuditedNativeMvid = new("97f10687-c306-4798-ab75-8b9f23f34dfb");

    internal static bool CanCertify(CombatPredictionSimulator simulator, Player player)
    {
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        return typeof(CardModel).Module.ModuleVersionId == AuditedNativeMvid
            && combat.Players.Count == 1 && combat.Modifiers.Count == 0
            && combat.RootRunModSubscriberCount == 0 && combat.RootCombatModSubscriberCount == 0
            && !combat.RootHasBaseLibCardModifiers && combat.AdaptedOnPlay == null
            && combat.KnownEnemies.Count > 0
            && combat.KnownEnemies.All(enemy => enemy.Monster?.GetType() is { } type
                && (type == typeof(FuzzyWurmCrawler) || type == typeof(Nibbit)))
            && combat.RootSourcesSatisfy(SafeRootSource)
            && SupportsBranch(simulator, player) && !HasForeignNativePatch();
    }

    private static bool SafeRootSource(AbstractModel source)
        => source switch
        {
            CardModel card => SafeCard(card.GetType()) && card.Enchantment == null && card.Affliction == null,
            PowerModel power => SafePower(power.GetType()),
            PotionModel potion => SafePotion(potion.GetType()),
            RelicModel relic => relic.GetType() == typeof(BurningBlood) || relic.GetType() == typeof(BlackBlood)
                || relic.GetType() == typeof(RingOfTheSnake) || relic.GetType() == typeof(BoundPhylactery),
            MonsterModel monster => monster.GetType() == typeof(FuzzyWurmCrawler) || monster.GetType() == typeof(Nibbit),
            // These native listeners only update badge statistics.
            _ => source.GetType() == typeof(MultiplayerScalingModel)
                || source.GetType() == typeof(CccComboModel) || source.GetType() == typeof(DebufferModel),
        };

    private static bool HasForeignNativePatch()
    {
        foreach (var method in Harmony.GetAllPatchedMethods())
        {
            // Even an unrelated command can introduce healing through a patched
            // call graph. Capture this conservative guard once on the main thread.
            if (method.Module.Assembly != typeof(CardModel).Assembly) continue;
            var patches = Harmony.GetPatchInfo(method);
            if (patches != null && patches.Prefixes.Concat(patches.Postfixes).Concat(patches.Transpilers)
                .Concat(patches.Finalizers).Any(patch => patch.PatchMethod.Module.Assembly != typeof(PinnedHealingBound).Assembly))
                return true;
        }
        return false;
    }

    private static bool SupportsBranch(CombatPredictionSimulator simulator, Player player)
    {
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        if (simulator.State.GetPlayerCombatState(player).AllCards.Any(card =>
                card.Preview.Enchantment != null || card.Preview.Affliction != null
                || !SafeCard(card.Preview.GetType()))
            || combat.RelicsOf(player).Any(relic => !relic.IsMelted
                && relic.GetType() != typeof(BurningBlood) && relic.GetType() != typeof(BlackBlood)
                && relic.GetType() != typeof(RingOfTheSnake) && relic.GetType() != typeof(BoundPhylactery))
            || combat.EffectivePowers().Any(power => !SafePower(power.GetType())))
            return false;
        for (int slot = 0; slot < ((ICombatPredictionPlayerLimits)combat).GetPotionSlotCount(player); slot++)
            if (combat.GetPotionAtSlot(player, slot) is { } potion && !SafePotion(potion.GetType()))
                return false;
        return true;
    }

    private static bool SafeCard(Type type)
        => type == typeof(StrikeIronclad) || type == typeof(DefendIronclad) || type == typeof(Bash)
            || type == typeof(StrikeSilent) || type == typeof(DefendSilent) || type == typeof(Neutralize)
            || type == typeof(Survivor) || type == typeof(StrikeNecrobinder) || type == typeof(DefendNecrobinder)
            || type == typeof(Bodyguard) || type == typeof(Unleash) || type == typeof(StrikeRegent)
            || type == typeof(DefendRegent) || type == typeof(Bloodletting)
            || type == typeof(Wound) || type == typeof(Dazed) || type == typeof(Slimed);

    private static bool SafePower(Type type)
        => type == typeof(StrengthPower) || type == typeof(DexterityPower) || type == typeof(WeakPower)
            || type == typeof(VulnerablePower) || type == typeof(FrailPower) || type == typeof(ThornsPower);

    private static bool SafePotion(Type type)
        => type == typeof(FirePotion) || type == typeof(BlockPotion) || type == typeof(EnergyPotion);

    internal static int RemainingHealing(CombatRootSnapshot root, SimulationSnapshot snapshot)
    {
        if (!root.HasPinnedHealingCertificate || snapshot.HasRisk || !snapshot.HasSimulator)
            return int.MaxValue;
        var simulator = (CombatPredictionSimulator)snapshot.Simulator;
        if (!SupportsBranch(simulator, root.PlayerIdentity))
            return int.MaxValue;
        // No admitted source can heal in combat or acquire a new healing source.
        // Overcount conditional post-combat healing: a lower bound must stay optimistic.
        return (int)Math.Min(int.MaxValue,
            (long)Math.Max(0, root.PostCombatRelicHeal.UnconditionalHeal)
                + Math.Max(0, root.PostCombatRelicHeal.WoundedHeal));
    }
}
