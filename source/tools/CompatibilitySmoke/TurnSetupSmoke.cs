using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal static partial class CompatibilitySmoke
{
    private const int TurnSetupFrameBudget = 9000;

    private static async Task<string> RunTurnSetupAsync(
        NGame host,
        CombatState state,
        bool fullAuto)
    {
        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("The local player is missing from the smoke combat.");
        int previousTurn = player.PlayerCombatState?.TurnNumber
            ?? throw new InvalidOperationException("The first player turn is missing.");

        HookPlayerChoiceContext choiceContext = new(player, player.NetId, GameActionType.Combat);
        PowerModel? power = await PowerCmd.Apply<ToolsOfTheTradePower>(
            choiceContext,
            player.Creature,
            1,
            player.Creature,
            null);
        if (power is null)
            throw new InvalidOperationException("Could not install the Tools of the Trade turn-setup fixture.");

        CombatManager.Instance.OnEndedTurnLocally();
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
            new EndPlayerTurnAction(player, previousTurn));
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();

        int setupTurn = previousTurn + 1;
        bool sawManagedSetup = false;
        bool sawNativeSurface = false;
        bool takeoverAccepted = false;
        for (int frame = 0; frame < TurnSetupFrameBudget; frame++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            CombatState? current = CombatManager.Instance.DebugOnlyGetState();
            if (current is null || !CombatManager.Instance.IsInProgress)
                throw new InvalidOperationException("Combat ended before the second-turn setup was driven.");
            Player? currentPlayer = LocalContext.GetMe(current);
            if (currentPlayer?.PlayerCombatState?.TurnNumber != setupTurn)
                continue;

            sawManagedSetup |= PlayerTurnSetupCoordinator.IsManaging(current);
            bool choiceVisible = PlayerTurnSetupCoordinator.CanTakeOverTurnSetup(current);
            sawNativeSurface |= choiceVisible
                && (NPlayerHand.Instance?.IsInCardSelection == true
                    || HasChoiceTrace($"turn_setup:{setupTurn}", setupTurn, "Visible"));
            if (!choiceVisible
                || !PlayerTurnSetupCoordinator.HasPendingPlannedChoice(current)
                || PlayerTurnSetupCoordinator.IsSearching)
            {
                continue;
            }

            if (fullAuto)
            {
                SolverController.SetStopFullAutoOnCombatEnd(false, persist: false);
                SolverController.SetFullAuto(host, current, enabled: true);
                if (!SolverController.FullAutoEnabled)
                    throw new InvalidOperationException("Full-auto takeover was not retained.");
            }
            else if (!PlayerTurnSetupCoordinator.TryContinuePlannedChoice(
                host,
                current,
                deployAfterSetup: false))
            {
                throw new InvalidOperationException("The pending native turn-setup choice could not continue.");
            }
            takeoverAccepted = true;
            break;
        }

        if (!takeoverAccepted)
        {
            throw new TimeoutException(
                $"Turn setup was not ready for takeover: managed={sawManagedSetup} " +
                $"native_surface={sawNativeSurface} controls=" +
                PlayerTurnSetupCoordinator.DescribeControlsForTesting());
        }

        bool sawSelected = false;
        bool sawDeployment = false;
        bool sawNextTurn = false;
        bool sawReuse = false;
        int maximumTurn = setupTurn;
        for (int frame = 0; frame < TurnSetupFrameBudget * (fullAuto ? 2 : 1); frame++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            sawSelected |= HasChoiceTrace($"turn_setup:{setupTurn}", setupTurn, "Selected");
            sawDeployment |= SolverController.LastDeployedActionStartedAtMillisecondsForTesting > 0;
            sawReuse |= SolverController.LastReusedTurnForTesting is >= 0;

            CombatState? current = CombatManager.Instance.DebugOnlyGetState();
            Player? currentPlayer = current is null ? null : LocalContext.GetMe(current);
            maximumTurn = Math.Max(maximumTurn, currentPlayer?.PlayerCombatState?.TurnNumber ?? setupTurn);
            sawNextTurn |= maximumTurn > setupTurn;

            if (!fullAuto
                && sawSelected
                && currentPlayer?.PlayerCombatState?.Phase == PlayerTurnPhase.Play
                && (current is null || !PlayerTurnSetupCoordinator.IsManaging(current))
                && !SolverController.IsSearching)
            {
                return $"PASS: native 0.107.1 turn setup; turn={setupTurn}; " +
                    $"surface={sawNativeSurface}; selected={sawSelected}; continuation=true";
            }

            if (fullAuto
                && sawSelected
                && sawDeployment
                && sawNextTurn
                && (sawReuse || !CombatManager.Instance.IsInProgress))
            {
                return $"PASS: native 0.107.1 full-auto turn setup; setup_turn={setupTurn}; " +
                    $"selected={sawSelected}; deployed={sawDeployment}; next_turn={maximumTurn}; " +
                    $"route_reuse={sawReuse}; combat_in_progress={CombatManager.Instance.IsInProgress}";
            }

            if (!CombatManager.Instance.IsInProgress)
                break;
        }

        throw new TimeoutException(
            $"Turn setup continuation did not satisfy the smoke mode: full_auto={fullAuto} " +
            $"selected={sawSelected} deployed={sawDeployment} next_turn={sawNextTurn} " +
            $"route_reuse={sawReuse} maximum_turn={maximumTurn}.");
    }
}
