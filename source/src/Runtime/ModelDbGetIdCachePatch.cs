using System.Collections.Concurrent;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

/// <summary>
/// Cache immutable Type-to-ModelId values. Content mods can change their ID prefix
/// during registration, so their values are cacheable only after the registry freezes.
/// </summary>
internal sealed class ModelDbGetIdCachePatch : IPatchMethod
{
    private static readonly ConcurrentDictionary<Type, ModelId> Cache = new();
    private static volatile bool _modelRegistryInitialized;

    public static string PatchId => "combat_solver_model_db_get_id_cache";
    public static string Description => "缓存 ModelDb.GetId(Type)，模组类型等待注册完成";
    public static bool IsCritical => false;

    public static ModPatchTarget[] GetTargets()
        => [new(typeof(ModelDb), "GetId", [typeof(Type)])];

    internal static int CachedEntryCount => Cache.Count;

    internal static void MarkModelRegistryInitialized() => _modelRegistryInitialized = true;

    private static bool IsCacheable(Type type)
        => type.Assembly == typeof(ModelDb).Assembly || _modelRegistryInitialized;

    [HarmonyPriority(Priority.First)]
    public static bool Prefix(Type type, ref ModelId __result)
    {
        if (type is null)
            return true;
        if (!Cache.TryGetValue(type, out ModelId? cached))
            return true;
        __result = cached;
        return false;
    }

    public static void Postfix(Type type, ModelId __result)
    {
        if (type is not null && __result is not null && IsCacheable(type))
            Cache.TryAdd(type, __result);
    }
}
