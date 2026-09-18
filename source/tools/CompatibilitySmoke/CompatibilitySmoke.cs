using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal static partial class CompatibilitySmoke
{
    private const int FirstTurnFrameBudget = 3600;

    public static async Task RunAsync(NGame host)
    {
        string output = System.Environment.GetEnvironmentVariable("COMBATSOLVER_COMPAT_SMOKE")!;
        string mode = (System.Environment.GetEnvironmentVariable("COMBATSOLVER_COMPAT_SMOKE_MODE")
            ?? "FIRST_TURN").Trim().ToUpperInvariant();
        try
        {
            CombatState state = await StartCombatAsync(host);
            if (mode is "COMPAT1071_FULL_BATTLE" or "FULL_BATTLE")
                await PrepareFullBattleFixtureAsync(state);
            string result = mode switch
            {
                "FIRST_TURN" => await RunFirstTurnAsync(state),
                "COMPAT1071_TURN_SETUP" or "TURN_SETUP" =>
                    await RunTurnSetupAsync(host, state, fullAuto: false),
                "COMPAT1071_FULLAUTO" or "FULLAUTO" =>
                    await RunTurnSetupAsync(host, state, fullAuto: true),
                "COMPAT1071_FULL_BATTLE" or "FULL_BATTLE" =>
                    await RunFullBattleAsync(host, state),
                "COMPAT1071_PERFORMANCE_BASELINE" or "PERFORMANCE_BASELINE" =>
                    await RunPerformanceBaselineAsync(host, state),
                _ => throw new InvalidOperationException(
                    $"Unknown compatibility smoke mode '{mode}'. " +
                    "Expected FIRST_TURN, COMPAT1071_TURN_SETUP, COMPAT1071_FULLAUTO, " +
                    "COMPAT1071_FULL_BATTLE, or COMPAT1071_PERFORMANCE_BASELINE."),
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
}
