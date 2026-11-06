using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    private ForkableDictionary<(Player Player, int Slot), PotionModel?>? _potionSlots;
    private ForkableDictionary<(Player Player, int Slot), PotionOrigin>? _potionOrigins;
    private ForkableList<PredictedPotionUse>? _potionUses;

    internal IReadOnlyList<PredictedPotionUse> PotionUses
        => _potionUses is { } uses ? uses : Array.Empty<PredictedPotionUse>();

    public PotionModel? GetPotionAtSlot(Player player, int slot)
    {
        if (slot < 0 || slot >= PotionSlotCount(player))
            return null;
        (Player, int) key = (player, slot);
        if (_potionSlots?.TryGetValue(key, out PotionModel? potion) == true)
            return potion;
        throw new InvalidOperationException($"Potion slot {slot} is outside the captured inventory.");
    }

    public bool IsPotionAvailable(Player player, int slot)
        => GetPotionAtSlot(player, slot) != null;

    internal PotionOrigin PotionOriginAtSlot(Player player, int slot)
        => GetPotionAtSlot(player, slot) != null
            && _potionOrigins?.TryGetValue((player, slot), out PotionOrigin origin) == true
                ? origin : PotionOrigin.RootInventory;

    public bool HasOpenPotionSlot(Player player)
    {
        for (int slot = 0; slot < PotionSlotCount(player); slot++)
        {
            if (GetPotionAtSlot(player, slot) == null)
                return true;
        }
        return false;
    }

    public void ConsumePotion(Player player, int slot)
        => ConsumePotion(player, slot, automatic: false);

    private void ConsumePotion(Player player, int slot, bool automatic)
    {
        PotionModel potion = GetPotionAtSlot(player, slot)
            ?? throw new InvalidOperationException($"药水槽位 {slot} 已不可用。");
        PotionOrigin origin = PotionOriginAtSlot(player, slot);
        (_potionSlots ??= [])[(player, slot)] = null;
        if (_potionOrigins?.ContainsKey((player, slot)) == true)
            _potionOrigins[(player, slot)] = PotionOrigin.RootInventory;
        bool renewablePotionShapedRock = potion is PotionShapedRock
            && RelicsOf(player).OfType<PetrifiedToad>().Any(static relic => !relic.IsMelted);
        (_potionUses ??= []).Add(new PredictedPotionUse(
            slot,
            potion.Id.Entry,
            origin == PotionOrigin.EntropicBrew ? 0 : PotionUsePolicy.StrategicHpCost(potion, renewablePotionShapedRock),
            automatic, origin));
        InvalidateBaseHookListeners();
    }

    public void ConsumePotion(PotionModel potion)
    {
        Player player = potion.Owner;
        for (int slot = 0; slot < PotionSlotCount(player); slot++)
        {
            if (!ReferenceEquals(GetPotionAtSlot(player, slot), potion))
                continue;
            ConsumePotion(player, slot, automatic: true);
            return;
        }
        throw new InvalidOperationException($"预测药水槽中找不到实例 {potion.Id.Entry}。");
    }

    public void BeforePotionUsed(
        CombatPredictionSimulator simulator,
        PotionModel potion,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature? target)
        => PowerLifecycleSupport.UpdateSurroundedForTarget(
            simulator,
            this,
            potion.Owner,
            target ?? (potion.IsValidTarget(potion.Owner.Creature) ? potion.Owner.Creature : null));

    public void AfterPotionUsed(
        CombatPredictionSimulator simulator,
        PotionModel potion,
        MegaCrit.Sts2.Core.Entities.Creatures.Creature? target)
    {
        TriggerRelicsAfterPotionUsed(simulator, potion);
        TriggerRelicsAfterHandEmptied(simulator, potion.Owner);
    }

    public bool TryProcurePotion(Player player, PotionModel canonical)
        => TryProcurePotion(player, canonical, freeFromEntropic: false);

    internal bool TryProcurePotion(Player player, PotionModel canonical, bool freeFromEntropic)
    {
        PotionModel potion = PredictionUtils.CloneModelForSimulation(canonical);
        potion.Owner = player;
        foreach (AbstractModel listener in GetEffectiveRunHookListeners())
        {
            if (!listener.ShouldProcurePotion(potion, player))
                return false;
        }
        for (int slot = 0; slot < PotionSlotCount(player); slot++)
        {
            if (GetPotionAtSlot(player, slot) != null)
                continue;
            (_potionSlots ??= [])[(player, slot)] = potion;
            (_potionOrigins ??= [])[(player, slot)] = freeFromEntropic
                ? PotionOrigin.EntropicBrew : PotionOrigin.Procured;
            InvalidateBaseHookListeners();
            TriggerRelicsAfterPotionProcured(player);
            return true;
        }
        return false;
    }

    private void AppendPotionFingerprint(ref StateFingerprintBuilder fingerprint)
    {
        fingerprint.Add('p');
        // Fingerprint contains captured/readable players, not the entire live multiplayer roster.
        // A local multiplayer core captures exactly one player, so sorting that one element is redundant.
        if (_rootCapturedPlayers.Count == 1)
        {
            AppendPlayerPotionFingerprint(ref fingerprint, _rootCapturedPlayers[0]);
        }
        else
            foreach (Player player in _rootCapturedPlayers.OrderBy(player => player.NetId))
                AppendPlayerPotionFingerprint(ref fingerprint, player);
        // Root directives can distinguish otherwise identical empty inventories.
        // Local explicit uses are the only authorized actions; automatic peer
        // consumption cannot fulfill a local root directive.
        foreach (PredictedPotionUse use in PotionUses.Where(use => !use.Automatic
                     && use.Origin == PotionOrigin.RootInventory).OrderBy(use => use.Slot))
        {
            fingerprint.Add('u'); fingerprint.Add(use.Slot); fingerprint.Add(use.PotionId);
        }
    }

    private void AppendPlayerPotionFingerprint(ref StateFingerprintBuilder fingerprint, Player player)
    {
        fingerprint.Add((long)player.NetId);
        int slotCount = PotionSlotCount(player);
        fingerprint.Add(slotCount);
        for (int slot = 0; slot < slotCount; slot++)
        {
            fingerprint.Add(GetPotionAtSlot(player, slot)?.Id.Entry ?? "-");
            fingerprint.Add((int)PotionOriginAtSlot(player, slot));
        }
    }
}
