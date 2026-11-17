using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.FoolArrows;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal static partial class CompatibilitySmoke
{
    private static async Task<string> RunFoolMechanicsAsync(CombatState live)
    {
        Player player = LocalContext.GetMe(live) ?? throw new InvalidOperationException("Missing player.");
        Creature enemy = live.Enemies.Single();
        PlayerCombatState playerState = player.PlayerCombatState!;
        int availableBefore = playerState.Arrows.Count(arrow => arrow.IsAvailable);
        if (availableBefore == 0)
            throw new InvalidOperationException("Fool starts the smoke with no available arrows.");

        var sim = await PlayNativeAndPredictedAsync<FoolishShot>(live, player, enemy);
        Equal("Foolish Shot HP", enemy.CurrentHp, sim.State.GetCreature(enemy).CurrentHp);
        Equal("Foolish Shot native impaled", 1, enemy.GetPower<ImpaledPower>()!.Arrows.Count);
        Equal("Foolish Shot predicted impaled", 1, sim.ImpaledBy(enemy, player)!.Arrows.Count);
        CompareArrows(sim);

        var child = sim.Fork();
        child.DislodgeArrow(enemy, player, source: null);
        Equal("Arrow stays impaled in parent", 1, sim.ImpaledBy(enemy, player)!.Arrows.Count);
        Equal("Child removes its impaled arrow", true, child.ImpaledBy(enemy, player) == null);
        Equal("Child arrow returned", ArrowStatus.Retrieved,
            child.State.GetPlayerCombatState(player).Arrows.Last().Status);

        sim = await PlayNativeAndPredictedAsync<Wrest>(live, player, enemy);
        Equal("Wrest draw", playerState.Hand.Cards.Count, sim.State.GetPlayerCombatState(player).Hand.Cards.Count);
        Equal("Wrest removes native Impaled", 0, enemy.GetPowerAmount<ImpaledPower>());
        Equal("Wrest removes predicted Impaled", true, sim.ImpaledBy(enemy, player) == null);
        CompareArrows(sim);

        sim = await PlayNativeAndPredictedAsync<ChoFirm>(live, player);
        Equal("Cho Firm block", player.Creature.Block, sim.State.GetCreature(player.Creature).Block);
        Equal("Cho Firm records melody", true,
            ((SimulatedCombatState)sim.State.CombatState).PlayedMelody(player, CardTag.Cho));
        return "PASS: v0.113.1 Fool native shot, impale, fork, dislodge, draw and melody";

        void CompareArrows(CombatPredictionSimulator predicted) => Equal("Arrow slot order and status",
            string.Join(',', playerState.Arrows.Select(arrow => arrow.Status)),
            string.Join(',', predicted.State.GetPlayerCombatState(player).Arrows.Select(arrow => arrow.Status)));
    }

    private static async Task<string> RunMechanicsAsync(CombatState live)
    {
        Player player = LocalContext.GetMe(live) ?? throw new InvalidOperationException("Missing player.");
        PlayerCombatState livePlayer = player.PlayerCombatState!;
        Creature enemy = live.Enemies.Single();
        var checks = new List<string>();

        await CheckPendulumHandDrawAsync(live, player);
        checks.Add("PendulumHandDraw");

        int captureCount = ModelDb.Monsters.Count(model => MonsterMoveEffects.CaptureStaticIntValues(model).Count > 0);
        checks.Add($"monster_root_values={captureCount}");

        CombatPredictionSimulator sim = await PlayNativeAndPredictedAsync<Fuel>(live, player);
        Equal("Fuel energy", livePlayer.Energy, sim.State.GetPlayerCombatState(player).Energy);
        Equal("Fuel draw pile size", livePlayer.DrawPile.Cards.Count, sim.State.GetPlayerCombatState(player).DrawPile.Cards.Count);
        checks.Add("Fuel");

        sim = await PlayNativeAndPredictedAsync<Haze>(live, player);
        Equal("Haze poison", enemy.GetPowerAmount<PoisonPower>(), PredictedAmount<PoisonPower>(sim, enemy));
        Equal("Haze weak", enemy.GetPowerAmount<WeakPower>(), PredictedAmount<WeakPower>(sim, enemy));
        checks.Add("Haze");

        sim = await PlayNativeAndPredictedAsync<Tracking>(live, player);
        Equal("Tracking stacks", player.Creature.GetPowerAmount<TrackingPower>(), PredictedAmount<TrackingPower>(sim, player.Creature));
        sim = await PlayNativeAndPredictedAsync<StrikeIronclad>(live, player, enemy);
        Equal("Tracking attack HP", enemy.CurrentHp, sim.State.GetCreature(enemy).CurrentHp);
        checks.Add("Tracking");

        sim = await PlayNativeAndPredictedAsync<GuidingStar>(live, player, enemy);
        Equal("Guiding Star damage", enemy.CurrentHp, sim.State.GetCreature(enemy).CurrentHp);
        Equal("Guiding Star delayed draw", player.Creature.GetPowerAmount<DrawCardsNextTurnPower>(),
            PredictedAmount<DrawCardsNextTurnPower>(sim, player.Creature));
        Equal("Guiding Star draw pile size", livePlayer.DrawPile.Cards.Count, sim.State.GetPlayerCombatState(player).DrawPile.Cards.Count);
        checks.Add("GuidingStar");

        var attack = live.CreateCard<StrikeIronclad>(player);
        await CardPileCmd.Add(attack, PileType.Hand);
        sim = await PlayNativeAndPredictedAsync<ExpectAFight>(live, player);
        Equal("Expect a Fight block", player.Creature.Block, sim.State.GetCreature(player.Creature).Block);
        var predictedAttack = sim.State.GetPlayerCombatState(player).Hand.Cards.Single(card => card.References(attack));
        Equal("Expect a Fight attack cost", attack.EnergyCost.GetWithModifiers(CostModifiers.All),
            predictedAttack.Preview.EnergyCost.GetWithModifiers(CostModifiers.All));
        Equal("Expect a Fight energy", livePlayer.Energy, sim.State.GetPlayerCombatState(player).Energy);
        checks.Add("ExpectAFight");

        sim = await PlayNativeAndPredictedAsync<WellLaidPlans>(live, player);
        Equal("Well-Laid Plans amount", player.Creature.GetPowerAmount<WellLaidPlansPower>(),
            PredictedAmount<WellLaidPlansPower>(sim, player.Creature));
        Equal("Well-Laid Plans hand flush", player.Creature.GetPower<WellLaidPlansPower>()!.ShouldFlush(player),
            PersistentRelicSupport.ShouldFlush((SimulatedCombatState)sim.State.CombatState, player));
        checks.Add("WellLaidPlans");

        await CardPileCmd.Add(live.CreateCard<Apparition>(player), PileType.Exhaust);
        await CardPileCmd.Add(live.CreateCard<Dazed>(player), PileType.Exhaust);
        sim = await PlayNativeAndPredictedAsync<Eidolon>(live, player);
        Equal("Eidolon Intangible", player.Creature.GetPowerAmount<IntangiblePower>(),
            PredictedAmount<IntangiblePower>(sim, player.Creature));
        Equal("Eidolon exhaust pile", string.Join(',', livePlayer.ExhaustPile.Cards.Select(card => card.Id.Entry)),
            string.Join(',', sim.State.GetPlayerCombatState(player).ExhaustPile.Cards.Select(card => card.Preview.Id.Entry)));
        checks.Add("Eidolon");

        sim = await PlayNativeAndPredictedAsync<Expertise>(live, player);
        Equal("Expertise hand size", livePlayer.Hand.Cards.Count, sim.State.GetPlayerCombatState(player).Hand.Cards.Count);
        Equal("Expertise retention", player.Creature.GetPowerAmount<RetainHandPower>(),
            PredictedAmount<RetainHandPower>(sim, player.Creature));
        checks.Add("Expertise");

        foreach (CardModel card in livePlayer.Hand.Cards.ToArray())
            await CardPileCmd.Add(card, PileType.Discard);
        var paidAttack = live.CreateCard<StrikeIronclad>(player);
        var discountedSkill = live.CreateCard<DefendIronclad>(player);
        await CardPileCmd.Add(paidAttack, PileType.Draw, CardPilePosition.Top);
        await CardPileCmd.Add(discountedSkill, PileType.Draw, CardPilePosition.Top);
        await PowerCmd.Apply<CorruptionPower>(new ThrowingPlayerChoiceContext(), player.Creature, 1, player.Creature, null);
        sim = await PlayNativeAndPredictedAsync<Scrape>(live, player, enemy);
        Equal("Scrape keeps the globally free Skill", PileType.Hand, discountedSkill.Pile!.Type);
        Equal("Scrape discards the paid Attack", PileType.Discard, paidAttack.Pile!.Type);
        Equal("Scrape predicted Hand", true, sim.State.GetPlayerCombatState(player).Hand.Cards.Any(card => card.References(discountedSkill)));
        Equal("Scrape predicted Discard", true, sim.State.GetPlayerCombatState(player).DiscardPile.Cards.Any(card => card.References(paidAttack)));
        checks.Add("Scrape");

        Shiv borrowedRetain = live.CreateCard<Shiv>(player);
        Shiv localRetain = live.CreateCard<Shiv>(player);
        localRetain.AddKeyword(CardKeyword.Retain);
        await CardPileCmd.Add(borrowedRetain, PileType.Hand);
        await CardPileCmd.Add(localRetain, PileType.Hand);
        sim = CombatRootSnapshot.Capture(live).ForkSimulator();
        var shadow = (SimulatedCombatState)sim.State.CombatState;
        shadow.Apply<PhantomBladesPower>(player.Creature, 8, player.Creature);
        var predictedShiv = sim.State.FindCard(borrowedRetain)!;
        Equal("Phantom Blades retains predicted Shiv", true, predictedShiv.ShouldRetainThisTurn(sim.State));
        var nativePhantom = await PowerCmd.Apply<PhantomBladesPower>(
            new ThrowingPlayerChoiceContext(), player.Creature, 8, player.Creature, null);
        Equal("Phantom Blades retains native Shiv", true, borrowedRetain.ShouldRetainThisTurn);
        shadow.SetAmount<PhantomBladesPower>(player.Creature, 0);
        Equal("Phantom Blades removes predicted Retain", false, predictedShiv.ShouldRetainThisTurn(sim.State));
        await PowerCmd.Remove(nativePhantom!);
        Equal("Phantom Blades removes native Retain", false, borrowedRetain.ShouldRetainThisTurn);
        Equal("Phantom Blades preserves native local Retain", true, localRetain.ShouldRetainThisTurn);
        Equal("Phantom Blades preserves predicted local Retain", true, sim.State.FindCard(localRetain)!.ShouldRetainThisTurn(sim.State));
        checks.Add("PhantomBlades");

        await RelicCmd.Obtain<Regalite>(player);
        sim = CombatRootSnapshot.Capture(live).ForkSimulator();
        int beforeBlock = player.Creature.Block;
        for (int i = 0; i < 2; i++)
        {
            sim.CreateAndAddGeneratedCardsToCombat<Fuel>(player, PileType.Discard, 1, player);
            await CardPileCmd.AddGeneratedCardToCombat(live.CreateCard<Fuel>(player), PileType.Discard, player);
            Equal("Regalite first generation block", 4, player.Creature.Block - beforeBlock);
            Equal("Regalite block", player.Creature.Block, sim.State.GetCreature(player.Creature).Block);
        }
        checks.Add("Regalite");

        sim = await ApplyNativeAndPredictedMoveAsync(live, player, enemy, "PULSATE_MOVE");
        Equal("Prism Pulsate HP", player.Creature.CurrentHp, sim.State.GetCreature(player.Creature).CurrentHp);
        Equal("Prism Pulsate Vulnerable", player.Creature.GetPowerAmount<VulnerablePower>(),
            PredictedAmount<VulnerablePower>(sim, player.Creature));
        Equal("Prism Pulsate Vital Spark", enemy.GetPowerAmount<VitalSparkPower>(), PredictedAmount<VitalSparkPower>(sim, enemy));
        Equal("Prism Pulsate block", enemy.Block, sim.State.GetCreature(enemy).Block);
        checks.Add("InfestedPrism.Pulsate");

        Creature weaker = await CreatureCmd.Add<Nibbit>(live);
        if (weaker.CurrentHp >= enemy.CurrentHp)
            throw new InvalidOperationException("Memento Mori fixture requires a weaker second enemy.");
        int strongerHp = enemy.CurrentHp;
        int weakerHp = weaker.CurrentHp;
        var targetingRng = live.RunState.Rng.CombatTargets.CaptureState();
        sim = await PlayNativeAndPredictedAsync<MementoMori>(live, player);
        Equal("Memento Mori native stronger enemy HP", strongerHp, enemy.CurrentHp);
        Equal("Memento Mori predicted stronger enemy HP", strongerHp, sim.State.GetCreature(enemy).CurrentHp);
        Equal("Memento Mori native target damaged", true, weaker.CurrentHp < weakerHp);
        Equal("Memento Mori predicted weaker enemy HP", weaker.CurrentHp, sim.State.GetCreature(weaker).CurrentHp);
        Equal("Memento Mori native targeting RNG", targetingRng, live.RunState.Rng.CombatTargets.CaptureState());
        Equal("Memento Mori predicted targeting RNG", targetingRng, sim.Rng.CombatTargets.CaptureState());
        checks.Add("MementoMori");

        return "PASS: v0.113.1 native/predicted mechanics " + string.Join(", ", checks);
    }

    private static async Task CheckPendulumHandDrawAsync(CombatState live, Player player)
    {
        Pendulum relic = await RelicCmd.Obtain<Pendulum>(player);
        try
        {
            int cadence = relic.DynamicVars["Turns"].IntValue;
            for (int counter = 0; counter < cadence; counter++)
            {
                relic.TurnsSeen = counter;
                var simulator = CombatRootSnapshot.Capture(live).ForkSimulator();
                var branch = (SimulatedCombatState)simulator.State.CombatState;
                var capturedRelic = branch.RelicsOf(player).OfType<Pendulum>().Single();
                branch.ApplyRelicBeforeHandDraw(simulator, player, new TurnStartChoiceCursor(null), capturedRelic);

                int predicted = PersistentPowerSupport.GetModifiedHandDraw(branch, player, CombatManager.baseHandDrawCount);
                Equal("Pendulum preserves native counter during prediction", counter, relic.TurnsSeen);
                relic.TurnsSeen = (counter + 1) % cadence;
                int native = (int)relic.ModifyHandDraw(player, CombatManager.baseHandDrawCount);
                Equal($"Pendulum draw after counter {counter}", native, predicted);
            }
        }
        finally
        {
            await RelicCmd.Remove(relic);
        }
    }

    private static async Task<string> RunHopperMechanicsAsync(CombatState live)
    {
        Player player = LocalContext.GetMe(live) ?? throw new InvalidOperationException("Missing player.");
        Creature enemy = live.Enemies.Single();
        PlayerCombatState playerState = player.PlayerCombatState!;
        var eligibleCards = playerState.DrawPile.Cards.Concat(playerState.DiscardPile.Cards).ToArray();
        CardModel eligible = eligibleCards.First(card => card.DeckVersion != null);
        foreach (CardModel card in eligibleCards)
            await CardPileCmd.Add(card, PileType.Exhaust);
        var questDeckCard = player.RunState.CreateCard<Dowsing>(player);
        await CardPileCmd.Add(questDeckCard, PileType.Deck);
        Dowsing quest = live.CreateCard<Dowsing>(player);
        quest.DeckVersion = questDeckCard;
        await CardPileCmd.Add(quest, PileType.Draw);

        var selectionWithQuestOnly = live.RunState.Rng.CombatCardSelection.CaptureState();
        var questOnlySim = await ApplyNativeAndPredictedMoveAsync(live, player, enemy, "THIEVERY_MOVE");
        Equal("Quest-only native RNG", selectionWithQuestOnly, live.RunState.Rng.CombatCardSelection.CaptureState());
        Equal("Quest-only predicted RNG", selectionWithQuestOnly, questOnlySim.Rng.CombatCardSelection.CaptureState());
        Equal("Quest-only predicted pile", quest.Id.Entry,
            questOnlySim.State.GetPlayerCombatState(player).DrawPile.Cards.Single().Preview.Id.Entry);

        await CardPileCmd.Add(eligible, PileType.Draw);
        var beforeSelection = live.RunState.Rng.CombatCardSelection.CaptureState();
        var beforeGeneration = live.RunState.Rng.CombatCardGeneration.CaptureState();
        CombatPredictionSimulator sim = await ApplyNativeAndPredictedMoveAsync(live, player, enemy, "THIEVERY_MOVE");
        Equal("Hopper player HP", player.Creature.CurrentHp, sim.State.GetCreature(player.Creature).CurrentHp);
        Equal("Hopper selection RNG", live.RunState.Rng.CombatCardSelection.CaptureState(), sim.Rng.CombatCardSelection.CaptureState());
        Equal("Hopper generation RNG", beforeGeneration, sim.Rng.CombatCardGeneration.CaptureState());
        if (beforeSelection == sim.Rng.CombatCardSelection.CaptureState())
            throw new InvalidOperationException("Hopper failed to consume selection RNG.");
        Equal("Hopper draw pile", string.Join(',', player.PlayerCombatState!.DrawPile.Cards.Select(card => card.Id.Entry)),
            string.Join(',', sim.State.GetPlayerCombatState(player).DrawPile.Cards.Select(card => card.Preview.Id.Entry)));
        Equal("Hopper retains the quest card", quest.Id.Entry, playerState.DrawPile.Cards.Single().Id.Entry);
        return "PASS: v0.113.1 Thieving Hopper native HP, quest exclusion, stolen card and RNG streams";
    }

    private static async Task<string> RunAeonglassMechanicsAsync(CombatState live)
    {
        Player player = LocalContext.GetMe(live) ?? throw new InvalidOperationException("Missing player.");
        Creature enemy = live.Enemies.Single();
        await CardPileCmd.Add(live.CreateCard<StrikeIronclad>(player), PileType.Discard);
        await CardPileCmd.Add(live.CreateCard<DefendIronclad>(player), PileType.Discard);
        var before = live.RunState.Rng.Shuffle.CaptureState();
        var sim = await ApplyNativeAndPredictedMoveAsync(live, player, enemy, "INCREASING_INTENSITY_MOVE");
        Equal("Aeonglass strength", enemy.GetPowerAmount<StrengthPower>(), PredictedAmount<StrengthPower>(sim, enemy));
        Equal("Aeonglass discard pile order", string.Join(',', player.PlayerCombatState!.DiscardPile.Cards.Select(card => card.Id.Entry)),
            string.Join(',', sim.State.GetPlayerCombatState(player).DiscardPile.Cards.Select(card => card.Preview.Id.Entry)));
        Equal("Aeonglass shuffle state", live.RunState.Rng.Shuffle.CaptureState(), sim.Rng.Shuffle.CaptureState());
        if (before == sim.Rng.Shuffle.CaptureState())
            throw new InvalidOperationException("Aeonglass did not consume Shuffle RNG.");
        return "PASS: v0.113.1 Aeonglass native Wither insertion, Shuffle RNG and strength";
    }

    private static async Task<CombatPredictionSimulator> ApplyNativeAndPredictedMoveAsync(
        CombatState live, Player player, Creature enemy, string moveId)
    {
        var move = (MoveState)enemy.Monster!.MoveStateMachine!.States[moveId];
        var hits = move.Intents.OfType<AttackIntent>().SelectMany(intent =>
        {
            int damage = (int)intent.DamageCalc!.Invoke();
            return Enumerable.Repeat(new ForecastAttackHit(damage, damage), intent.Repeats);
        }).ToArray();
        CombatPredictionSimulator sim = CombatRootSnapshot.Capture(live).ForkSimulator();
        MonsterMoveSemantics.ApplyForecastMove(sim, (SimulatedCombatState)sim.State.CombatState,
            new(enemy, move, hits), player.Creature, new HashSet<uint>());
        await move.PerformMove([player.Creature]);
        return sim;
    }

    private static async Task<CombatPredictionSimulator> PlayNativeAndPredictedAsync<T>(
        CombatState live, Player player, Creature? target = null)
        where T : CardModel
    {
        var card = live.CreateCard<T>(player);
        await CardPileCmd.Add(card, PileType.Hand);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions().WaitAsync(TimeSpan.FromSeconds(10));
        var sim = CombatRootSnapshot.Capture(live).ForkSimulator();
        var predicted = sim.State.GetPlayerCombatState(player).Hand.Cards.Single(candidate => candidate.References(card));
        if (!sim.AutoPlay(predicted, target))
            throw new InvalidOperationException($"Could not finish predicted effects for {card.Id.Entry}.");
        await CardCmd.AutoPlay(new ThrowingPlayerChoiceContext(), card, target);
        return sim;
    }

    private static int PredictedAmount<T>(CombatPredictionSimulator sim, Creature creature)
        where T : PowerModel
        => ((SimulatedCombatState)sim.State.CombatState).GetAmount<T>(creature);

    private static void Equal<T>(string check, T native, T predicted)
    {
        if (!EqualityComparer<T>.Default.Equals(native, predicted))
            throw new InvalidOperationException($"{check}: native={native}, predicted={predicted}");
    }
}
