using System.Collections.Concurrent;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

/// <summary>
/// ModelDb.GetId(Type) is a pure Type-to-ModelId mapping in the pinned game API.
/// Cache only the immutable value; model instances and ModelDb content remain untouched.
/// </summary>
internal sealed class ModelDbGetIdCachePatch : IPatchMethod
{
    private static readonly ConcurrentDictionary<Type, ModelId> Cache = new();

    public static string PatchId => "combat_solver_model_db_get_id_cache";
    public static string Description => "缓存 ModelDb.GetId(Type) 的纯类型→ModelId 映射";
    public static bool IsCritical => false;

    public static ModPatchTarget[] GetTargets()
        => [new(typeof(ModelDb), "GetId", [typeof(Type)])];

    internal static int CachedEntryCount => Cache.Count;

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
        if (type is not null && __result is not null)
            Cache.TryAdd(type, __result);
    }
}
