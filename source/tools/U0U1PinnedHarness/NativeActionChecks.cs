using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using CombatSolver;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using OfflineSearchHarness;

namespace U0U1PinnedHarness;

internal static class NativeActionChecks
{
    internal static void InstallOfflineBridges()
    {
        if (typeof(CardModel).Module.ModuleVersionId != new Guid("97f10687-c306-4798-ab75-8b9f23f34dfb")
            || !NonInteractiveMode.IsActive)
            throw new InvalidOperationException("Native action checks require the pinned noninteractive 0.107.1 host.");

        PatchMoveNext(typeof(CardModel), "OnPlayWrapper", nameof(ReplaceCardAnimationClock));
        PatchMoveNext(typeof(CardPileCmd), "Shuffle", nameof(ReplaceShuffleFrameDelta));
        PatchMoveNext(typeof(TaskHelper), "LogTaskExceptions", nameof(KeepNativeErrorsInProcess));
        GameBootstrap.Harmony.Patch(AccessTools.Method(typeof(PlatformUtil), nameof(PlatformUtil.GetLocalPlayerId)),
            prefix: new HarmonyMethod(typeof(NativeActionChecks), nameof(OfflineLocalPlayerId)));
        GameBootstrap.Harmony.Patch(AccessTools.Method(typeof(UserDataPathProvider), "GetPlatformDirectoryName"),
            prefix: new HarmonyMethod(typeof(NativeActionChecks), nameof(OfflineProfileDirectory)));
        GameBootstrap.Harmony.Patch(AccessTools.Method(typeof(NHoverTipSet), nameof(NHoverTipSet.Clear), Type.EmptyTypes),
            prefix: new HarmonyMethod(typeof(NativeActionChecks), nameof(SkipHoverTipCleanup)));
        // The existing bootstrap has already installed MemorySaveStore. Native combat
        // completion still resolves a profile path before the disabled replay writer.
        SaveManager.Instance.InitProfileId(0);
    }

    private static void PatchMoveNext(Type type, string name, string bridge)
    {
        Type? machine = AccessTools.Method(type, name)?.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        MethodInfo? moveNext = machine == null ? null : AccessTools.Method(machine, "MoveNext");
        if (moveNext == null) throw new MissingMethodException(type.FullName, name);
        GameBootstrap.Harmony.Patch(moveNext, transpiler: new HarmonyMethod(typeof(NativeActionChecks), bridge));
    }

    private static bool Calls(CodeInstruction instruction, string declaringType, string method)
        => (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
            && instruction.operand is MethodInfo info && info.DeclaringType?.FullName == declaringType && info.Name == method;

    private static IEnumerable<CodeInstruction> ReplaceCardAnimationClock(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        CodeInstruction[] reads = code.Where(instruction => Calls(instruction, "Godot.Time", "GetTicksMsec")).ToArray();
        if (reads.Length != 2) throw new InvalidOperationException("Unexpected card animation clock shape.");
        foreach (CodeInstruction read in reads)
            read.operand = AccessTools.Method(typeof(NativeActionChecks), nameof(MonotonicMilliseconds));
        return code;
    }

    private static ulong MonotonicMilliseconds() => (ulong)Stopwatch.GetElapsedTime(ClockStart).TotalMilliseconds;
    private static readonly long ClockStart = Stopwatch.GetTimestamp();

    private static ExceptionDispatchInfo? _nativeError;

    private static bool OfflineLocalPlayerId(ref ulong __result)
    {
        __result = 1;
        return false;
    }

    private static bool OfflineProfileDirectory(PlatformType __0, ref string __result)
    {
        if (__0 != PlatformType.None) throw new InvalidOperationException("Offline profile must use the local platform.");
        __result = "default";
        return false;
    }

    private static bool SkipHoverTipCleanup() => false;

    private static IEnumerable<CodeInstruction> KeepNativeErrorsInProcess(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        CodeInstruction[] reports = code.Where(instruction =>
            Calls(instruction, "MegaCrit.Sts2.Core.Debug.SentryService", "CaptureException")).ToArray();
        if (reports.Length != 1 || reports[0].operand is not MethodInfo report
            || report.ReturnType != typeof(void) || report.GetParameters().Length != 1
            || report.GetParameters()[0].ParameterType != typeof(Exception))
            throw new InvalidOperationException("Unexpected native asynchronous error reporting shape.");
        reports[0].operand = AccessTools.Method(typeof(NativeActionChecks), nameof(RecordNativeError));
        return code;
    }

    private static void RecordNativeError(Exception error)
        => Interlocked.CompareExchange(ref _nativeError, ExceptionDispatchInfo.Capture(error), null);

    private static IEnumerable<CodeInstruction> ReplaceShuffleFrameDelta(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        int index = code.FindIndex(instruction => Calls(instruction, "Godot.Engine", "GetMainLoop"));
        if (index < 0 || index + 3 >= code.Count
            || code.Count(instruction => Calls(instruction, "Godot.Engine", "GetMainLoop")) != 1
            || code[index + 1].opcode != OpCodes.Castclass || code[index + 1].operand is not Type tree || tree.FullName != "Godot.SceneTree"
            || !Calls(code[index + 2], "Godot.SceneTree", "get_Root")
            || !Calls(code[index + 3], "Godot.Node", "GetProcessDeltaTime")
            || code.Skip(index + 1).Take(3).Any(instruction => instruction.labels.Count != 0 || instruction.blocks.Count != 0))
            throw new InvalidOperationException("Unexpected shuffle animation frame query shape.");

        // This query only batches presentation waits. NonInteractiveMode already skips
        // Cmd.Wait. All original card movement, hooks and RNG instructions still run.
        code[index].opcode = OpCodes.Ldc_R8;
        code[index].operand = 0d;
        for (int offset = 1; offset <= 3; offset++)
        {
            code[index + offset].opcode = OpCodes.Nop;
            code[index + offset].operand = null;
        }
        return code;
    }

    internal static string Run(CombatState combat, MainLoopContext loop, string outputDirectory)
    {
        Player local = LocalContext.GetMe(combat)!;
        Player teammate = combat.Players.Single(player => player != local);
        Creature target = combat.Enemies.Single();
        int hpBefore = target.CurrentHp;
        int roundBefore = combat.RoundNumber;
        int localTurn = local.PlayerCombatState!.TurnNumber;
        // Force an early shuffle in the fixture. Keep the cards in circulation and
        // let the original shuffle consume its normal RNG and move them back.
        foreach (CardModel card in local.PlayerCombatState.DrawPile.Cards.ToArray())
        {
            local.PlayerCombatState.DrawPile.RemoveInternal(card, true);
            local.PlayerCombatState.DiscardPile.AddInternal(card, -1);
        }
        int shuffleBefore = combat.RunState.Rng.Shuffle.Counter;
        CardModel strike = teammate.PlayerCombatState!.Hand.Cards.First(card => card.Id.Entry == "STRIKE_IRONCLAD");
        if (!strike.CanPlayTargeting(target)) throw new InvalidOperationException("Scripted teammate Strike is not legal.");
        Execute(loop, new PlayCardAction(strike, target));
        int hpAfter = target.CurrentHp;
        if (hpAfter >= hpBefore || !CombatManager.Instance.History.CardPlaysFinished.Any(entry => entry.CardPlay.Card == strike))
            throw new InvalidOperationException("The native executor did not settle the teammate Strike.");

        Execute(loop, new EndPlayerTurnAction(teammate, teammate.PlayerCombatState.TurnNumber));
        Execute(loop, new EndPlayerTurnAction(local, localTurn));
        CombatState next = OfflineCombat.WaitForPlayableCombat(loop);
        int shuffleAfter = next.RunState.Rng.Shuffle.Counter;
        if (next.RoundNumber <= roundBefore || local.PlayerCombatState.TurnNumber != localTurn + 1 || shuffleAfter <= shuffleBefore
            || local.PlayerCombatState.Hand.Cards.Count == 0)
            throw new InvalidOperationException("The native end turns did not settle through a shuffle to the next playable round.");

        File.WriteAllText(Path.Combine(outputDirectory, "native-actions.json"), JsonSerializer.Serialize(new
        {
            GameMvid = typeof(CardModel).Module.ModuleVersionId, Teammate = teammate.NetId,
            EnemyHp = new[] { hpBefore, hpAfter }, Rounds = new[] { roundBefore, next.RoundNumber },
            ShuffleCounters = new[] { shuffleBefore, shuffleAfter }, Hand = local.PlayerCombatState.Hand.Cards.Select(card => card.Id.Entry),
        }, new JsonSerializerOptions { WriteIndented = true }));
        return $"NativeActions PASS enemy_hp={hpBefore}->{hpAfter} round={roundBefore}->{next.RoundNumber} shuffle={shuffleBefore}->{shuffleAfter}";
    }

    internal static void Execute(MainLoopContext loop, GameAction action)
    {
        RunManager run = RunManager.Instance;
        if (run.CombatReplayWriter.IsEnabled) throw new InvalidOperationException("Offline replay writing is enabled.");
        _nativeError?.Throw();
        run.ActionQueueSet.EnqueueWithoutSynchronizing(action);
        loop.RunUntilCompleted(run.ActionExecutor.FinishedExecutingActions(), TimeSpan.FromSeconds(10), "Native action drain");
        _nativeError?.Throw();
    }
}
