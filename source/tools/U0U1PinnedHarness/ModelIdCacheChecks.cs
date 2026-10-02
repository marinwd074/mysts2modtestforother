using CombatSolver;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace U0U1PinnedHarness;

internal static partial class ModelIdCacheChecks
{
    internal static void Run()
    {
        ModelId temporary = ModelDb.GetId(typeof(StrikeIronclad));
        ModelId registered = ModelDb.GetId(typeof(DefendIronclad));
        Type modType = typeof(RegistrationMarker);
        int before = ModelDbGetIdCachePatch.CachedEntryCount;
        ModelDbGetIdCachePatch.Postfix(modType, temporary);
        ModelId result = temporary;
        Require(ModelDbGetIdCachePatch.Prefix(modType, ref result),
            "A pre-registration mod ID must not bypass native resolution.");
        Require(ModelDbGetIdCachePatch.CachedEntryCount == before,
            "A temporary mod ID must not enter the cache.");

        ModelDbGetIdCachePatch.Postfix(typeof(StrikeIronclad), temporary);
        Require(!ModelDbGetIdCachePatch.Prefix(typeof(StrikeIronclad), ref result) && result == temporary,
            "Base-game IDs remain cacheable before mod registration finishes.");

        ModelDbGetIdCachePatch.MarkModelRegistryInitialized();
        ModelDbGetIdCachePatch.Postfix(modType, registered);
        Require(!ModelDbGetIdCachePatch.Prefix(modType, ref result) && result == registered,
            "Only the finalized mod ID may be cached.");
        ModelDbGetIdCachePatch.MarkModelRegistryInitialized();
        Require(!ModelDbGetIdCachePatch.Prefix(modType, ref result) && result == registered,
            "Repeated registration notifications must preserve finalized IDs.");
        Require(ModelDbGetIdCachePatch.Prefix(null!, ref result), "Null keeps the native failure path.");
        ModelDbGetIdCachePatch.Postfix(null!, registered);
        ModelDbGetIdCachePatch.Postfix(typeof(NullResultMarker), null!);
        Require(ModelDbGetIdCachePatch.Prefix(typeof(NullResultMarker), ref result),
            "Null results must not be cached.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RegistrationMarker;
    private sealed class NullResultMarker;
}
