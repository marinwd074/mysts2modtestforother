using System.Collections;
using System.Reflection;
using CombatSolver;
using HarmonyLib;

namespace U2DegenerateHarness;

internal static class U2Runtime
{
    private static readonly string[] SearchPatchTypes =
    [
        "CombatSolver.CombatStateTrackerIsolationPatch",
        "CombatSolver.PowerDynamicVarMaterializationGuardPatch",
        "CombatSolver.BaseLibCloneConcurrencyPatch",
        "CombatSolver.BaseLibDynamicVarCloneMetadataPatch",
        "CombatSolver.RitsuDynamicVarCloneMetadataPatch",
        "CombatSolver.SimulationCardPileLookupPatch",
        "CombatSolver.RitsuFreePlayVoidIsolationPatch",
        "CombatSolver.RitsuFreePlayBoolIsolationPatch",
        "CombatSolver.RitsuFreePlayResolveIsolationPatch",
        "CombatSolver.RitsuBaseLibTargetTypeLookupPatch",
        "CombatSolver.RitsuBaseLibTargetTypeResolutionPatch",
        "CombatSolver.RitsuBaseLibTargetTypeEvidencePatch",
        "CombatSolver.PowerAmountComparisonPatch",
    ];

    internal static int Initialize(
        string logDirectory,
        int beamWidth,
        int maxExpandedNodes,
        int budgetMilliseconds)
    {
        Directory.CreateDirectory(logDirectory);
        SetStatic(
            typeof(Entry),
            "Logger",
            Activator.CreateInstance(
                typeof(CombatSolverLog),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [logDirectory],
                culture: null)!);

        SolverSettings.ApplyForTesting(new SolverSettingsData
        {
            PerformanceMigrationVersion = SolverSettings.CurrentPerformanceMigrationVersion,
            PerformancePreset = SolverPerformancePreset.Custom,
            SearchMaxExpandedNodes = maxExpandedNodes,
            SearchBeamWidth = beamWidth,
            SearchTimeLimitSeconds = Math.Max(1, (int)Math.Ceiling(budgetMilliseconds / 1000d)),
            SearchMaxDegreeOfParallelism = 1,
            SearchMaxCardBranchesPerNode = 32,
            SearchMaxPileChoiceBranchesPerAction = 18,
            SearchMaxHandChoiceBranchesPerAction = 24,
            EnableNoGcRegion = false,
            StopAtAcceptableBattleHpLoss = false,
            OnlineStatisticsEnabled = false,
            SearchCompletionNotificationsEnabled = false,
            PotionPolicy = SolverPotionPolicy.Smart,
        });

        int applied = 0;
        foreach (string typeName in SearchPatchTypes)
        {
            Type? patchType = AccessTools.TypeByName(typeName);
            if (patchType == null)
                continue;

            object? targets = AccessTools.Method(patchType, "GetTargets")?.Invoke(null, null);
            if (targets is not IEnumerable list)
                continue;

            HarmonyMethod? prefix = Wrap(patchType, "Prefix");
            HarmonyMethod? postfix = Wrap(patchType, "Postfix");
            HarmonyMethod? transpiler = Wrap(patchType, "Transpiler");
            HarmonyMethod? finalizer = Wrap(patchType, "Finalizer");
            int patched = 0;
            foreach (object target in list)
            {
                MethodBase? method = Resolve(target);
                if (method == null)
                    continue;
                OfflineSearchHarness.GameBootstrap.Harmony.Patch(
                    method,
                    prefix,
                    postfix,
                    transpiler,
                    finalizer);
                patched++;
            }
            if (patched > 0)
                applied++;
        }
        return applied;
    }

    private static HarmonyMethod? Wrap(Type patchType, string name)
    {
        MethodInfo? method = patchType.GetMethod(
            name,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        return method == null ? null : new HarmonyMethod(method);
    }

    private static MethodBase? Resolve(object target)
    {
        Type type = target.GetType();
        Type targetType = (Type)type.GetProperty("TargetType")!.GetValue(target)!;
        string methodName = (string)type.GetProperty("MethodName")!.GetValue(target)!;
        Type[] parameterTypes =
            (Type[]?)type.GetProperty("ParameterTypes")?.GetValue(target) ?? [];
        object? methodType = type.GetProperty("HarmonyMethodType")?.GetValue(target);
        if (methodType is MethodType.Getter)
            return AccessTools.PropertyGetter(targetType, methodName);
        if (methodType is MethodType.Setter)
            return AccessTools.PropertySetter(targetType, methodName);
        if (methodType is MethodType.Constructor)
            return AccessTools.Constructor(targetType, parameterTypes);
        return AccessTools.Method(
                targetType,
                methodName,
                parameterTypes.Length == 0 ? null : parameterTypes)
            ?? AccessTools.Method(targetType, methodName);
    }

    private static void SetStatic(Type type, string name, object value)
    {
        PropertyInfo? property = type.GetProperty(
            name,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo? setter = property?.GetSetMethod(nonPublic: true);
        if (setter != null)
        {
            setter.Invoke(null, [value]);
            return;
        }

        FieldInfo field = type.GetField(
                $"<{name}>k__BackingField",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? type.GetField(
                name,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(type.FullName, name);
        field.SetValue(null, value);
    }
}
