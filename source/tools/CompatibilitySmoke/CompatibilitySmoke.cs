using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class CompatibilitySmoke
{
    private const int FirstTurnFrameBudget = 3600;
    private const int TurnSetupFrameBudget = 9000;

    public static async Task RunAsync(NGame host)
    {
        string output = System.Environment.GetEnvironmentVariable("COMBATSOLVER_COMPAT_SMOKE")!;
        string mode = (System.Environment.GetEnvironmentVariable("COMBATSOLVER_COMPAT_SMOKE_MODE")
            ?? "FIRST_TURN").Trim().ToUpperInvariant();
        try
        {
            CombatState state = await StartCombatAsync(host);
            string result = mode switch
            {
                "FIRST_TURN" => await RunFirstTurnAsync(state),
                "COMPAT1071_TURN_SETUP" or "TURN_SETUP" =>
                    await RunTurnSetupAsync(host, state, fullAuto: false),
                "COMPAT1071_FULLAUTO" or "FULLAUTO" =>
                    await RunTurnSetupAsync(host, state, fullAuto: true),
                _ => throw new InvalidOperationException(
                    $"Unknown compatibility smoke mode '{mode}'. " +
                    "Expected FIRST_TURN, COMPAT1071_TURN_SETUP, or COMPAT1071_FULLAUTO."),
            };
            string? directory = System.IO.Path.GetDirectoryName(output);
            if (!string.IsNullOrWhiteSpace(directory))
                System.IO.Directory.CreateDirectory(directory);
            System.IO.File.WriteAllText(output, result);
        }
        catch (Exception error)
        {
            System.IO.File.WriteAllText(output, "FAIL: mode=" + mode + " " + error);
        }
        finally
        {
            host.GetTree().Quit();
        }
    }

    private static async Task<CombatState> StartCombatAsync(NGame host)
    {
        for (int frame = 0; frame < FirstTurnFrameBudget
            && (host.MainMenu is null || !host.MainMenu.IsNodeReady()); frame++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        if (host.MainMenu is null || !host.MainMenu.IsNodeReady())
            throw new InvalidOperationException("Timed out waiting for the main menu.");

        await host.StartNewSingleplayerRun(
            ModelDb.AllCharacters.Single(c => c.Id.Entry == "IRONCLAD"),
            false,
            ActModel.GetDefaultList(),
            [],
            "COMPAT1071",
            GameMode.Standard,
            0);
        var encounter = ModelDb.AllEncounters.First(e => e.Id.Entry.Contains("NIBBIT")).ToMutable();
        encounter.DebugRandomizeRng();
        await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Unassigned, encounter);

        for (int frame = 0; frame < FirstTurnFrameBudget; frame++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            CombatState? state = CombatManager.Instance.DebugOnlyGetState();
            if (state?.CurrentSide == CombatSide.Player
                && LocalContext.GetMe(state)?.PlayerCombatState?.Phase == PlayerTurnPhase.Play)
            {
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                return state;
            }
        }
        throw new InvalidOperationException("Timed out waiting for the first playable turn.");
    }

    private static async Task<string> RunFirstTurnAsync(CombatState state)
    {
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        var root = CombatRootSnapshot.Capture(state);
        var predictedRng = CombatPredictionRngSet.From(state.RunState.Rng);
        int[] liveRngCounters =
        [
            state.RunState.Rng.Shuffle.GetCounter(),
            state.RunState.Rng.CombatCardGeneration.GetCounter(),
            state.RunState.Rng.CombatPotionGeneration.GetCounter(),
            state.RunState.Rng.CombatCardSelection.GetCounter(),
            state.RunState.Rng.CombatEnergyCosts.GetCounter(),
            state.RunState.Rng.CombatTargets.GetCounter(),
            state.RunState.Rng.CombatOrbGeneration.GetCounter(),
            state.RunState.Rng.MonsterAi.GetCounter(),
            state.RunState.Rng.Niche.GetCounter(),
        ];
        int[] predictedRngCounters =
        [
            predictedRng.Shuffle.GetCounter(),
            predictedRng.CombatCardGeneration.GetCounter(),
            predictedRng.CombatPotionGeneration.GetCounter(),
            predictedRng.CombatCardSelection.GetCounter(),
            predictedRng.CombatEnergyCosts.GetCounter(),
            predictedRng.CombatTargets.GetCounter(),
            predictedRng.CombatOrbGeneration.GetCounter(),
            predictedRng.MonsterAi.GetCounter(),
            predictedRng.Niche.GetCounter(),
        ];
        if (!liveRngCounters.SequenceEqual(predictedRngCounters))
        {
            throw new InvalidOperationException(
                $"RunRngSet stream mapping mismatch: live=[{string.Join(',', liveRngCounters)}], " +
                $"predicted=[{string.Join(',', predictedRngCounters)}].");
        }

        var names = SolverDisplayNames.Capture(state);
        var damage = BattleDamageTracker.Observe(state);
        var policy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(),
            state,
            includeTurnSetup: false,
            theftPolicy: null) with
        {
            BudgetOverrideMilliseconds = 5000,
            VerifyIncrementalSearch = true,
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        SolverResult result = await Task.Run(
            () => CombatSearchCoordinator.Solve(root, names, damage, policy, timeout.Token, null));
        if (!result.BestNode.Actions.Any(a => a.Kind == PlanActionKind.PlayCard))
            throw new InvalidOperationException("Search produced no card-play route.");
        return $"PASS: native 0.107.1 first-turn search; actions={result.BestNode.Actions.Count}; " +
            "incremental verification enabled";
    }

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

    private static bool HasChoiceTrace(string owner, int turn, string stage)
        => NativeChoiceRuntime.TraceSnapshotForTesting.Any(trace =>
            trace.Owner == owner && trace.Turn == turn && trace.Stage == stage);
}
