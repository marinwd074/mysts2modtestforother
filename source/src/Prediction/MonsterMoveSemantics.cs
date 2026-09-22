using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class MonsterMoveSemantics
{
    public static bool ApplyForecastMove(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        ISet<uint> processedEnemyDeaths,
        IReadOnlyList<PlanCardChoice>? plannedChoices = null)
    {
        SimCreatureState simulatedPlayer = simulator.State.GetCreature(player);
        MonsterMoveEffects.ApplyBeforeAttack(simulator, combat, move, player);
        if (simulator.HasPendingChoice)
            return simulatedPlayer.IsDead;
        bool fullyBlockedAttack = false;
        bool playerDied = false;
        AttackCommand? attackContext = move.AttackHits.Count > 0
            ? simulator.BeginAttackContext(
                new AttackCommand(0m)
                    .FromMonster(move.Owner.Monster
                        ?? throw new InvalidOperationException("预测攻击的所有者不是怪物。"))
                    .WithHitCount(0))
            : null;
        bool attackCompleted = attackContext == null;
        try
        {
            if (simulator.HasPendingChoice)
                return simulatedPlayer.IsDead;

            foreach (ForecastAttackHit hit in move.AttackHits)
            {
                int baseDamage = combat.AdjustMonsterMoveDamage(move.Owner, move.Move.Id, hit.BaseDamage);
                IReadOnlyList<DamageResult> results = DamagePlayers(
                    simulator,
                    combat,
                    move.Owner,
                    simulator.State.PlayerCreatures,
                    baseDamage);
                if (simulator.HasPendingChoice)
                    return simulatedPlayer.IsDead;
                simulator.AddAttackContextHit(attackContext!, results);
                foreach (DamageResult result in results)
                {
                    if (ReferenceEquals(result.Receiver, player) && result.WasFullyBlocked)
                        fullyBlockedAttack = true;
                }
                CorePowerSupport.ApplyEnemyDeathPowers(
                    simulator,
                    combat,
                    combat.KnownEnemies,
                    processedEnemyDeaths);
                if (simulator.HasPendingChoice)
                    return simulatedPlayer.IsDead;
                if (simulatedPlayer.IsDead)
                {
                    playerDied = true;
                    break;
                }
                if (simulator.State.GetCreature(move.Owner).IsDead)
                    break;
            }

            attackCompleted = true;
        }
        finally
        {
            if (attackContext != null)
                simulator.EndAttackContext(attackContext, attackCompleted);
        }

        if (simulator.HasPendingChoice)
            return simulatedPlayer.IsDead;
        if (playerDied)
            return true;
        if (fullyBlockedAttack && combat.GetAmount<ImbalancedPower>(move.Owner) > 0)
        {
            if (move.Owner.Monster is BowlbugRock)
                combat.ForceStunnedMove(move.Owner, "HEADBUTT_MOVE");
            combat.StunNextMove(move.Owner);
        }
        MonsterMoveEffects.Apply(
            simulator,
            combat,
            move,
            player,
            out bool killedOwner,
            plannedChoices);
        if (simulator.HasPendingChoice)
            return simulatedPlayer.IsDead;
        if (killedOwner
            && move.Owner.CombatId is uint moveOwnerCombatId
            && !processedEnemyDeaths.Contains(moveOwnerCombatId))
        {
            CorePowerSupport.ApplyEnemyDeathPowers(
                simulator,
                combat,
                combat.KnownEnemies,
                processedEnemyDeaths);
            if (simulator.HasPendingChoice)
                return simulatedPlayer.IsDead;
        }
        simulator.SynchronizePowerAmountPredictionStates();
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
        combat.NormalizeAeonglassWithers(simulator);
        combat.NormalizeCardAfflictions(simulator);
        return simulatedPlayer.IsDead;
    }

    public static IReadOnlyList<DamageResult> DamagePlayer(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature attacker,
        Creature player,
        int baseDamage)
        => DamagePlayers(simulator, combat, attacker, [player], baseDamage);

    /// <summary>
    /// Mirrors monster <see cref="AttackCommand"/> targeting. FromMonster targets the complete
    /// player-creature roster in one CreatureCmd.Damage dispatch; teammate actions remain
    /// unmodeled, but deterministic enemy damage still affects every captured player.
    /// </summary>
    public static IReadOnlyList<DamageResult> DamagePlayers(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature attacker,
        IReadOnlyList<Creature> players,
        int baseDamage)
    {
        List<(Creature Osty, int Amount)>? suppressedDieForYou = null;
        foreach (Creature target in players)
        {
            Creature? osty = target.Player is { } owner ? simulator.State.GetOsty(owner) : null;
            if (osty == null
                || !simulator.State.GetCreature(osty).IsDead
                || combat.GetAmount<DieForYouPower>(osty) is not (> 0) amount)
            {
                continue;
            }

            (suppressedDieForYou ??= []).Add((osty, amount));
            combat.SetAmount<DieForYouPower>(osty, 0);
        }

        try
        {
            using (simulator.PushDamageSource(
                CombatDamageSource.For(CombatDamageSourceKind.MonsterMove, attacker.Monster?.Id.Entry)))
            {
                return simulator.Damage(players, baseDamage, ValueProp.Move, attacker);
            }
        }
        finally
        {
            if (suppressedDieForYou == null)
                return;
            foreach ((Creature osty, int amount) in suppressedDieForYou)
                combat.SetAmount<DieForYouPower>(osty, amount);
        }
    }
}
