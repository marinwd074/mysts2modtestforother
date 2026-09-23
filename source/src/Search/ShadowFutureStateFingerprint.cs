using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Random;
using CombatSolver.Engine.InCombat.Mirrors.Orbs;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

/// <summary>
/// Conservative equivalence key for Shadow continuations. Equal keys mean the modeled future
/// state, RNG position, per-player action budget and prediction-only ended-player set are equal.
/// It is used only for exact duplicate dominance; quality heuristics never depend on this key.
/// </summary>
internal static class ShadowFutureStateFingerprint
{
    internal static StateFingerprint Capture(
        CombatPredictionSimulator simulator,
        IReadOnlySet<uint> processedEnemyDeaths,
        IReadOnlySet<string> turnEndedPlayerNetIds,
        IReadOnlyList<ShadowTeammateActionCandidate> actions)
    {
        SimulatedCombatState combat =
            (SimulatedCombatState)simulator.State.CombatState;
        StateFingerprintBuilder key = new();

        key.Add("shadow_future_v1");
        key.Add(combat.RoundNumber);
        key.Add((int)combat.CurrentSide);
        key.Add(combat.PlayerTurnEndRequested);
        key.Add(simulator.IsInProgress);
        key.Add(simulator.TerminalStamp?.Outcome.ToString());
        key.Add(simulator.ShuffleEventCount);
        key.Add(simulator.History.Entries.Count);
        key.Add(simulator.HasRisk);

        foreach (Player player in simulator.State.RootCapturedPlayers.OrderBy(player => player.NetId))
        {
            SimCreatureState creature = simulator.State.GetCreature(player.Creature);
            SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);

            key.Add(player.NetId.ToString());
            key.Add(player.Character.Id.Entry);
            key.Add(creature.CurrentHp);
            key.Add(creature.MaxHp);
            key.Add(creature.Block);
            key.Add(combat.GetPlayerTurnNumber(player));
            key.Add((int)playerState.Phase);
            key.Add(playerState.Energy);
            key.Add(playerState.Stars);
            key.Add(combat.GetPlayerGold(player));
            key.Add(combat.GetCumulativeHpLost(player.Creature));
            key.Add(combat.GetRecoveredHp(player.Creature));
            if (combat.GetOsty(player) is { } osty)
            {
                key.Add(true);
                key.Add(osty.CombatId ?? uint.MaxValue);
                key.Add(simulator.State.GetCreature(osty).CurrentHp);
                key.Add(combat.GetOstyMaxHp(simulator, player));
                key.Add(combat.IsOstyHittable(simulator, player));
            }
            else
            {
                key.Add(false);
            }
            AppendPile(ref key, playerState.Hand, 'H');
            AppendPile(ref key, playerState.DrawPile, 'D');
            AppendPile(ref key, playerState.DiscardPile, 'C');
            AppendPile(ref key, playerState.ExhaustPile, 'X');
            AppendPile(ref key, playerState.PlayPile, 'P');
            AppendOrbs(ref key, simulator, playerState.OrbQueue);

            int actionCount = 0;
            string netId = player.NetId.ToString();
            for (int actionIndex = 0; actionIndex < actions.Count; actionIndex++)
            {
                if (string.Equals(
                        actions[actionIndex].PlayerNetId,
                        netId,
                        StringComparison.Ordinal))
                {
                    actionCount++;
                }
            }
            key.Add(actionCount);
            key.Add(turnEndedPlayerNetIds.Contains(netId));
        }

        key.Add("present_creatures");
        foreach (var creature in combat.Creatures.OrderBy(creature => creature.CombatId ?? uint.MaxValue))
        {
            SimCreatureState state = simulator.State.GetCreature(creature);
            key.Add(creature.CombatId ?? uint.MaxValue);
            key.Add((int)creature.Side);
            key.Add(state.CurrentHp);
            key.Add(state.MaxHp);
            key.Add(state.Block);
        }

        // Keep known enemies separately: a removed enemy can still matter while death/revive
        // lifecycle work is pending even though it is no longer in combat.Creatures.
        key.Add("known_enemies");
        foreach (var enemy in combat.KnownEnemies.OrderBy(enemy => enemy.CombatId ?? uint.MaxValue))
        {
            SimCreatureState state = simulator.State.GetCreature(enemy);
            key.Add(enemy.Monster?.Id.Entry);
            key.Add(enemy.CombatId ?? uint.MaxValue);
            key.Add(combat.ContainsCreature(enemy));
            key.Add(state.CurrentHp);
            key.Add(state.MaxHp);
            key.Add(state.Block);
        }

        AppendRngState(ref key, simulator.Rng.ShuffleState);
        AppendRngState(ref key, simulator.Rng.CombatCardGenerationState);
        AppendRngState(ref key, simulator.Rng.CombatPotionGenerationState);
        AppendRngState(ref key, simulator.Rng.CombatCardSelectionState);
        AppendRngState(ref key, simulator.Rng.CombatEnergyCostsState);
        AppendRngState(ref key, simulator.Rng.CombatTargetsState);
        AppendRngState(ref key, simulator.Rng.CombatOrbGenerationState);
        AppendRngState(ref key, simulator.Rng.MonsterAiState);
        AppendRngState(ref key, simulator.Rng.NicheState);

        ulong deathsFirst = 0;
        ulong deathsSecond = 0;
        foreach (uint combatId in processedEnemyDeaths)
        {
            deathsFirst += StateFingerprintBuilder.MixFirst(combatId);
            deathsSecond += StateFingerprintBuilder.MixSecond(combatId);
        }
        key.Add(processedEnemyDeaths.Count);
        key.Add(deathsFirst);
        key.Add(deathsSecond);

        // Powers, relic/potion state, monster AI/private modeled state, history counters and
        // other branch-owned combat semantics are centralized here.
        combat.AppendFingerprint(ref key, simulator);
        return key.Finish();
    }

    private static void AppendPile(
        ref StateFingerprintBuilder key,
        SimCardPile pile,
        char marker)
    {
        key.Add(marker);
        key.Add(pile.Cards.Count);
        foreach (PredictedCard card in pile.Cards)
        {
            StateFingerprint cardKey =
                CombatBeamSolver.CaptureCardStateFingerprintForTesting(card);
            key.Add(cardKey.First);
            key.Add(cardKey.Second);
        }
    }

    private static void AppendOrbs(
        ref StateFingerprintBuilder key,
        CombatPredictionSimulator simulator,
        SimOrbQueue queue)
    {
        key.Add('O');
        key.Add(queue.Capacity);
        key.Add(queue.Orbs.Count);
        foreach (OrbModel orb in queue.Orbs)
        {
            key.Add(orb.Id.Entry);
            key.Add(OrbMirrors.GetPassiveValue(simulator, orb));
            key.Add(OrbMirrors.GetEvokeValue(simulator, orb));
        }
    }

    private static void AppendRngState(
        ref StateFingerprintBuilder key,
        PredictionRngState state)
    {
        key.Add(state.Counter);
        key.Add(state.State0);
        key.Add(state.State1);
        key.Add(state.State2);
        key.Add(state.State3);
    }
}
