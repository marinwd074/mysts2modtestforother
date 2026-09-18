using System.Diagnostics;
using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal static partial class CompatibilitySmoke
{
    private const int PerformanceBaselineBudgetMilliseconds = 5000;
    private const int PerformanceBaselineMaxDegreeOfParallelism = 1;
    private const int PerformanceBaselineMaxWaitSeconds = 45;

    private static readonly JsonSerializerOptions PerformanceBaselineJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly record struct MainThreadFrameMetrics(
        int FrameCount,
        double MaxFrameGapMilliseconds,
        int FramesOver50Milliseconds,
        int FramesOver100Milliseconds);

    private static async Task<string> RunPerformanceBaselineAsync(NGame host, CombatState state)
    {
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        CombatRootSnapshot root = CombatRootSnapshot.Capture(state);
        SolverDisplayNames names = SolverDisplayNames.Capture(state);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(state);
        SolverPolicySettings settings = CaptureBaselineSettings(state);

        using CancellationTokenSource timeout = new(
            TimeSpan.FromSeconds(PerformanceBaselineMaxWaitSeconds));
        Task<SolverResult> solve = Task.Run(
            () => CombatSearchCoordinator.Solve(
                root,
                names,
                damage,
                settings.Policy,
                timeout.Token,
                progressCallback: null),
            timeout.Token);
        (SolverResult result, MainThreadFrameMetrics frames) =
            await PumpSearchAndCaptureFramesAsync(host, solve);

        result.MainThreadFrameCount = frames.FrameCount;
        result.MaxMainThreadFrameGapMilliseconds = frames.MaxFrameGapMilliseconds;
        result.MainThreadFramesOver50Milliseconds = frames.FramesOver50Milliseconds;
        result.MainThreadFramesOver100Milliseconds = frames.FramesOver100Milliseconds;

        long transitions = result.TotalTransitionCount;
        object report = new
        {
            schemaVersion = 1,
            mode = "COMPAT1071_PERFORMANCE_BASELINE",
            sampleLabel = System.Environment.GetEnvironmentVariable("COMBATSOLVER_COMPAT_SMOKE_LABEL")
                ?? "unlabeled",
            targetGameVersion = "0.107.1",
            fixture = new
            {
                runSeed = "COMPAT1071",
                characterId = "IRONCLAD",
                encounterId = state.Encounter?.Id.Entry ?? "unknown",
                actIndex = state.RunState.CurrentActIndex,
                startTurn = result.StartTurnNumber,
            },
            search = new
            {
                profile = new
                {
                    beamWidth = settings.Policy.Profile.BeamWidth,
                    maxExpandedNodes = settings.Policy.Profile.MaxExpandedNodes,
                    maxCardBranchesPerNode = settings.Policy.Profile.MaxCardBranchesPerNode,
                    maxPileChoiceBranchesPerAction = settings.Policy.Profile.MaxPileChoiceBranchesPerAction,
                    maxHandChoiceBranchesPerAction = settings.Policy.Profile.MaxHandChoiceBranchesPerAction,
                },
                parallelism = settings.Policy.MaxDegreeOfParallelism,
                budgetMilliseconds = settings.Policy.BudgetOverrideMilliseconds,
                potionPolicy = settings.Policy.PotionPolicy.ToString(),
                useBeamWidthPortfolio = settings.Policy.UseBeamWidthPortfolio,
                useNoveltyPortfolio = settings.Policy.UseNoveltyPortfolio,
                fixedBudget = settings.Policy.FixedBudget,
                verifyIncrementalSearch = settings.Policy.VerifyIncrementalSearch,
            },
            metrics = new
            {
                expanded = result.TotalExpandedNodes,
                transitions,
                elapsedMs = result.TotalSearchElapsed.TotalMilliseconds,
                allocatedBytes = result.TotalWorkerAllocatedBytes,
                bytesPerTransition = transitions == 0
                    ? (double?)null
                    : (double)result.TotalWorkerAllocatedBytes / transitions,
                gen0 = result.TotalGen0Collections,
                gen1 = result.TotalGen1Collections,
                gen2 = result.TotalGen2Collections,
                gcPauseMs = result.TotalGcPauseDuration.TotalMilliseconds,
                maxGcPauseMs = result.TotalMaxObservedGcPause.TotalMilliseconds,
                maxFrameGapMs = result.MaxMainThreadFrameGapMilliseconds,
                framesOver50Ms = result.MainThreadFramesOver50Milliseconds,
                framesOver100Ms = result.MainThreadFramesOver100Milliseconds,
                frameCount = result.MainThreadFrameCount,
                boundary = result.BoundaryReason.ToString(),
                routeIdentity = DescribeRouteIdentity(result),
                route = result.BestNode.Actions.Select((action, index) => new
                {
                    index,
                    turn = action.Turn,
                    kind = action.Kind.ToString(),
                    cardId = string.IsNullOrEmpty(action.CardId) ? null : action.CardId,
                    potionId = string.IsNullOrEmpty(action.PotionId) ? null : action.PotionId,
                    targetCombatId = action.TargetCombatId,
                    cardStateKey = action.CardStateKey,
                    replayCount = action.ReplayCount,
                }).ToArray(),
                resultIdentity = new
                {
                    score = result.BestNode.Score,
                    projectedBattleHpLost = result.ProjectedBattleHpLost,
                    potionCount = result.PotionCount,
                    searchedTurns = result.SearchedTurns,
                    shufflesCrossed = result.Snapshot.ShufflesCrossed,
                    finalHp = result.Snapshot.PlayerHp,
                    finalEnemyHp = result.Snapshot.EnemyHp,
                    allEnemiesDead = result.Snapshot.AllEnemiesDead,
                    playerDead = result.Snapshot.PlayerDead,
                    onlyDeathRoutes = result.OnlyDeathRoutesFound,
                    combatEndedTurn = result.CombatEndedTurn,
                },
            },
        };
        return JsonSerializer.Serialize(report, PerformanceBaselineJsonOptions);
    }

    private static SolverPolicySettings CaptureBaselineSettings(CombatState state)
    {
        SolverSettingsSnapshot captured = SolverSettings.Capture();
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            captured,
            state,
            includeTurnSetup: false,
            theftPolicy: null) with
        {
            FixedBudget = true,
            BudgetOverrideMilliseconds = PerformanceBaselineBudgetMilliseconds,
            MaxDegreeOfParallelism = PerformanceBaselineMaxDegreeOfParallelism,
            VerifyIncrementalSearch = false,
            MeasurePhasePerformance = false,
        };
        return new SolverPolicySettings(policy);
    }

    private static async Task<(SolverResult Result, MainThreadFrameMetrics Frames)>
        PumpSearchAndCaptureFramesAsync(NGame host, Task<SolverResult> solve)
    {
        long previousTimestamp = Stopwatch.GetTimestamp();
        bool havePreviousFrame = false;
        int frameCount = 0;
        int over50 = 0;
        int over100 = 0;
        double maximumGap = 0d;

        while (!solve.IsCompleted)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            long now = Stopwatch.GetTimestamp();
            if (havePreviousFrame)
            {
                double gap = Stopwatch.GetElapsedTime(previousTimestamp, now).TotalMilliseconds;
                frameCount++;
                maximumGap = Math.Max(maximumGap, gap);
                if (gap >= 50d)
                    over50++;
                if (gap >= 100d)
                    over100++;
            }
            previousTimestamp = now;
            havePreviousFrame = true;
        }

        SolverResult result = await solve;
        return (result, new MainThreadFrameMetrics(frameCount, maximumGap, over50, over100));
    }

    private static string DescribeRouteIdentity(SolverResult result)
        => string.Join(
            "|",
            result.BestNode.Actions.Select((action, index) =>
                $"{index}:{action.Turn}:{action.Kind}:{action.CardId}:{action.PotionId}:" +
                $"{action.TargetCombatId?.ToString() ?? "-"}:{action.CardStateKey}:{action.ReplayCount}"));

    private sealed record SolverPolicySettings(SearchPolicySnapshot Policy);
}
