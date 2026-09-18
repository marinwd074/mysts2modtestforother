using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Runs;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static partial class CompatibilitySmoke
{
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
}
