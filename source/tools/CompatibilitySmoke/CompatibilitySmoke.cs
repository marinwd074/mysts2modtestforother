using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
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
            CombatState state = await StartCombatAsync(host, mode);
            if (mode is "COMPAT1071_FULL_BATTLE" or "FULL_BATTLE")
                await PrepareFullBattleFixtureAsync(state);
            string result = mode switch
            {
                "AEONGLASS_MECHANICS" => await RunAeonglassMechanicsAsync(state),
                "HOPPER_MECHANICS" => await RunHopperMechanicsAsync(state),
                "MECHANICS" => await RunMechanicsAsync(state),
                "FIRST_TURN" => await RunFirstTurnAsync(state),
                "STANDARD_FULLAUTO" => await RunStandardFullAutoAsync(host, state),
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
                    "Expected MECHANICS, AEONGLASS_MECHANICS, HOPPER_MECHANICS, FIRST_TURN, " +
                    "STANDARD_FULLAUTO, TURN_SETUP, FULLAUTO, FULL_BATTLE, or PERFORMANCE_BASELINE."),
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

    private static async Task<CombatState> StartCombatAsync(NGame host, string mode)
    {
        for (int frame = 0; frame < FirstTurnFrameBudget
            && (host.MainMenu is null || !host.MainMenu.IsNodeReady()); frame++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        if (host.MainMenu is null || !host.MainMenu.IsNodeReady())
            throw new InvalidOperationException("Timed out waiting for the main menu.");

        var character = ModelDb.AllCharacters.Single(c => c.Id.Entry == "IRONCLAD");
        Player player = Player.CreateForNewRun(character, UnlockState.all, 1UL);
        await host.StartNewSingleplayerRun(
            player,
            false,
            ActModel.GetDefaultList(),
            [],
            "COMPAT1131",
            GameMode.Standard,
            false,
            0,
            null);
        var encounter = mode switch
        {
            "MECHANICS" => ModelDb.Encounter<InfestedPrismsElite>().ToMutable(),
            "AEONGLASS_MECHANICS" => ModelDb.Encounter<AeonglassBoss>().ToMutable(),
            "HOPPER_MECHANICS" => ModelDb.Encounter<ThievingHopperWeak>().ToMutable(),
            _ => ModelDb.AllEncounters.First(e => e.Id.Entry.Contains("NIBBIT")).ToMutable(),
        };
        encounter.DebugRandomizeRng();
        GD.Print("[CombatSolver/CompatibilitySmoke] Run created, entering " + encounter.Id.Entry);
        Task roomEntry = RunManager.Instance.EnterRoomDebug(encounter.RoomType, MapPointType.Unassigned, encounter);
        var deadline = DateTime.UtcNow.AddSeconds(35);
        while (!roomEntry.IsCompleted && DateTime.UtcNow < deadline)
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!roomEntry.IsCompleted)
        {
            var pending = CombatManager.Instance.DebugOnlyGetState();
            throw new TimeoutException($"Room entry stalled: side={pending?.CurrentSide} " +
                $"phase={pending?.Players.FirstOrDefault()?.PlayerCombatState?.Phase} " +
                $"in_progress={CombatManager.Instance.IsInProgress}");
        }
        await roomEntry;
        GD.Print("[CombatSolver/CompatibilitySmoke] Combat room entered.");

        for (int frame = 0; frame < FirstTurnFrameBudget; frame++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            CombatState? state = CombatManager.Instance.DebugOnlyGetState();
            if (state?.CurrentSide == CombatSide.Player
                && LocalContext.GetMe(state)?.PlayerCombatState?.Phase == PlayerTurnPhase.Play)
            {
                GD.Print("[CombatSolver/CompatibilitySmoke] First player turn reached Play.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions()
                    .WaitAsync(TimeSpan.FromSeconds(10));
                return state;
            }
        }
        var pendingCombat = CombatManager.Instance.DebugOnlyGetState();
        throw new InvalidOperationException($"Timed out waiting for the first playable turn: " +
            $"side={pendingCombat?.CurrentSide} phase={pendingCombat?.Players.FirstOrDefault()?.PlayerCombatState?.Phase} " +
            $"in_progress={CombatManager.Instance.IsInProgress}.");
    }
}
