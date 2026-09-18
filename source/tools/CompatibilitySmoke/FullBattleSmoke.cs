using System.Runtime;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal static partial class CompatibilitySmoke
{
    private const int FullBattleCleanupFrameBudget = 1200;

    private static async Task PrepareFullBattleFixtureAsync(CombatState state)
    {
        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("The local player is missing from the full-battle smoke combat.");
        await CreatureCmd.SetCurrentHp(player.Creature, player.Creature.MaxHp);
        foreach (var enemy in state.Enemies.Where(enemy => enemy.IsAlive))
            await CreatureCmd.SetCurrentHp(enemy, 1);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private static async Task<string> RunFullBattleAsync(NGame host, CombatState state)
    {
        int lifecycleBeforeEnd = SolverController.CombatLifecycleGeneration;
        string battleResult = await RunTurnSetupAsync(
            host,
            state,
            fullAuto: true,
            awaitCombatEnd: true);

        await WaitForCombatCleanupAsync(host, state, lifecycleBeforeEnd);

        if (CombatManager.Instance.IsInProgress)
            throw new InvalidOperationException("Full-battle smoke did not observe CombatEnded.");
        if (SolverController.IsSearching || SolverController.IsDeploying)
        {
            throw new InvalidOperationException(
                $"CombatEnded retained solver activity: searching={SolverController.IsSearching} " +
                $"deploying={SolverController.IsDeploying}.");
        }
        if (SolverController.FullAutoEnabled || SolverController.AutomaticSearchPaused)
        {
            throw new InvalidOperationException(
                $"CombatEnded retained automation state: full_auto={SolverController.FullAutoEnabled} " +
                $"paused={SolverController.AutomaticSearchPaused}.");
        }
        if (PlayerTurnSetupCoordinator.IsManaging(state)
            || PlayerTurnSetupCoordinator.IsSearching)
        {
            throw new InvalidOperationException(
                $"CombatEnded retained turn-setup state: managing={PlayerTurnSetupCoordinator.IsManaging(state)} " +
                $"searching={PlayerTurnSetupCoordinator.IsSearching}.");
        }

        var backgroundCollection = SearchGcPolicy.InSearchBackgroundCollectionForTesting;
        if (SearchGcPolicy.IsBackgroundReclaiming
            || backgroundCollection.ConfirmationPending
            || SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting != 0
            || SearchGcPolicy.AutomaticGcLifecycleUsed
            || GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
        {
            throw new InvalidOperationException(
                $"CombatEnded retained GC state: background={SearchGcPolicy.IsBackgroundReclaiming} " +
                $"confirmation={backgroundCollection.ConfirmationPending} " +
                $"budget={SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting} " +
                $"automatic={SearchGcPolicy.AutomaticGcLifecycleUsed} " +
                $"latency={GCSettings.LatencyMode}.");
        }

        SearchGcLifecycleSnapshot lifecycle = SearchGcPolicy.CaptureLifecycle();
        return battleResult + "; cleanup=search,deployment,turn_setup,gc; " +
            $"lifecycle={SolverController.CombatLifecycleGeneration}>{lifecycleBeforeEnd}; " +
            $"gc_ends={lifecycle.NoGcEnds}; gc_losses={lifecycle.NoGcLosses}";
    }

    private static async Task WaitForCombatCleanupAsync(
        NGame host,
        CombatState state,
        int lifecycleBeforeEnd)
    {
        for (int frame = 0; frame < FullBattleCleanupFrameBudget; frame++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            if (CombatManager.Instance.IsInProgress
                || SolverController.CombatLifecycleGeneration <= lifecycleBeforeEnd)
            {
                continue;
            }

            await SolverController.LastCombatReferenceReleaseForTesting
                .WaitAsync(TimeSpan.FromSeconds(20));
            var backgroundCollection = SearchGcPolicy.InSearchBackgroundCollectionForTesting;
            if (!SolverController.IsSearching
                && !SolverController.IsDeploying
                && !PlayerTurnSetupCoordinator.IsManaging(state)
                && !PlayerTurnSetupCoordinator.IsSearching
                && !SearchGcPolicy.IsBackgroundReclaiming
                && !backgroundCollection.ConfirmationPending
                && SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting == 0
                && !SearchGcPolicy.AutomaticGcLifecycleUsed
                && GCSettings.LatencyMode != GCLatencyMode.NoGCRegion)
            {
                return;
            }
        }

        throw new TimeoutException(
            $"CombatEnded cleanup did not quiesce: in_progress={CombatManager.Instance.IsInProgress} " +
            $"lifecycle={SolverController.CombatLifecycleGeneration}>{lifecycleBeforeEnd} " +
            $"searching={SolverController.IsSearching} deploying={SolverController.IsDeploying} " +
            $"turn_setup={PlayerTurnSetupCoordinator.DescribeControlsForTesting()} " +
            $"gc_budget={SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting} " +
            $"gc_background={SearchGcPolicy.IsBackgroundReclaiming}.");
    }
}
