using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class CompatibilitySmoke
{
    public static async Task RunAsync(NGame host)
    {
        string output = System.Environment.GetEnvironmentVariable("COMBATSOLVER_COMPAT_SMOKE")!;
        try
        {
            for (int frame = 0; frame < 3600 && (host.MainMenu is null || !host.MainMenu.IsNodeReady()); frame++)
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            if (host.MainMenu is null || !host.MainMenu.IsNodeReady())
                throw new InvalidOperationException("Timed out waiting for the main menu.");
            await host.StartNewSingleplayerRun(
                ModelDb.AllCharacters.Single(c => c.Id.Entry == "IRONCLAD"),
                false, ActModel.GetDefaultList(), [], "COMPAT1071", GameMode.Standard, 0);
            var encounter = ModelDb.AllEncounters.First(e => e.Id.Entry.Contains("NIBBIT")).ToMutable();
            encounter.DebugRandomizeRng();
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Unassigned, encounter);
            CombatState? state = null;
            for (int frame = 0; frame < 3600; frame++)
            {
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                state = CombatManager.Instance.DebugOnlyGetState();
                if (state?.CurrentSide == CombatSide.Player &&
                    LocalContext.GetMe(state)?.PlayerCombatState?.Phase == PlayerTurnPhase.Play)
                    break;
            }
            if (state is null || LocalContext.GetMe(state)?.PlayerCombatState?.Phase != PlayerTurnPhase.Play)
                throw new InvalidOperationException("Timed out waiting for the first playable turn.");
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
                throw new InvalidOperationException(
                    $"RunRngSet stream mapping mismatch: live=[{string.Join(',', liveRngCounters)}], predicted=[{string.Join(',', predictedRngCounters)}].");
            var names = SolverDisplayNames.Capture(state);
            var damage = BattleDamageTracker.Observe(state);
            var policy = SolverController.CaptureSearchPolicy(
                SolverSettings.Capture(),
                state,
                includeTurnSetup: false,
                theftPolicy: null) with
            {
                BudgetOverrideMilliseconds = 5000,
                VerifyIncrementalSearch = true
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage, policy, timeout.Token, null));
            if (!result.BestNode.Actions.Any(a => a.Kind == PlanActionKind.PlayCard))
                throw new InvalidOperationException("Search produced no card-play route.");
            System.IO.File.WriteAllText(output, $"PASS: native 0.107.1 first-turn search; actions={result.BestNode.Actions.Count}; incremental verification enabled");
        }
        catch (Exception error)
        {
            System.IO.File.WriteAllText(output, "FAIL: " + error);
        }
        finally
        {
            host.GetTree().Quit();
        }
    }
}
