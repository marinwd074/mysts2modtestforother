using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Combat;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class PlayerTurnEndLifecycle
{
    public static bool RunPhaseTwo(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        IReadOnlyList<Creature> participants,
        int etherealExhaustCount = 0)
    {
        if (!CorePowerSupport.TriggerPlayerRegularSideTurnEndEffects(
                simulator, combat, participants, etherealExhaustCount)
            || !TurnStartRelicSupport.TriggerAfterSideTurnEnd(
                simulator, combat, participants, etherealExhaustCount)
            || !HookMirrors.AfterSideTurnEndLate(simulator, CombatSide.Player, participants))
        {
            return false;
        }
        combat.NormalizeCardAfflictions(simulator);
        foreach (Creature participant in participants)
            if (participant.Player is { } player)
                simulator.State.GetPlayerCombatState(player).Phase = PlayerTurnPhase.None;
        return true;
    }

    public static bool RunForecastPhaseOne(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        IReadOnlyList<Player> players)
    {
        if (players.Count == 0)
            throw new ArgumentException("Forecast player-side end requires at least one player.", nameof(players));

        Creature[] participants = players.Select(static player => player.Creature).ToArray();
        foreach (Player player in players)
            simulator.State.GetPlayerCombatState(player).Phase = PlayerTurnPhase.End;

        EndTurnPowerSupport.TriggerVeryEarly(combat, participants);
        if (combat.HasPendingChoice)
            return false;
        TurnStartRelicSupport.TriggerBeforeSideTurnEnd(simulator, combat, participants);
        if (combat.HasPendingChoice)
            return false;

        int playerTurn = combat.GetPlayerTurnNumber(players[0]);
        if (!simulator.SimulateForecastEndPlayerTurnBeforeOrbPassives(playerTurn, players))
            return false;
        if (simulator.IsOverOrEnding)
            return true;

        foreach (Player player in players)
        {
            if (!OrbLifecycleSupport.TriggerBeforeTurnEnd(simulator, combat, player)
                || combat.HasPendingChoice)
            {
                return false;
            }
        }

        if (!simulator.SimulateForecastEndPlayerTurnAfterOrbPassives(playerTurn, players))
            return false;
        CorePowerSupport.CompletePlayerEarlySideTurnEndEffects(combat, participants);
        return !combat.HasPendingChoice;
    }

    public static bool RunPhaseOne(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player,
        IReadOnlyList<Creature> participants)
    {
        simulator.State.GetPlayerCombatState(player).Phase = PlayerTurnPhase.End;
        EndTurnPowerSupport.TriggerVeryEarly(combat, participants);
        if (combat.HasPendingChoice)
            return false;
        TurnStartRelicSupport.TriggerBeforeSideTurnEnd(simulator, combat, participants);
        if (combat.HasPendingChoice)
            return false;
        if (!simulator.SimulateEndPlayerTurnBeforeOrbPassives(combat.GetPlayerTurnNumber(player)))
            return false;
        if (simulator.IsOverOrEnding)
            return true;
        if (!OrbLifecycleSupport.TriggerBeforeTurnEnd(simulator, combat, player)
            || combat.HasPendingChoice
            || !simulator.SimulateEndPlayerTurnAfterOrbPassives(combat.GetPlayerTurnNumber(player)))
        {
            return false;
        }
        CorePowerSupport.CompletePlayerEarlySideTurnEndEffects(combat, participants);
        return !combat.HasPendingChoice;
    }
}
