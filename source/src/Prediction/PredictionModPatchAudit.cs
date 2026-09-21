using System.Reflection;
using CombatSolver.Engine.Common;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

/// <summary>
/// Root-capture guard against third-party Harmony patches that replace gameplay behavior the engine mirrors.
/// </summary>
/// <remarks>
/// Mirrors read live model data, so third-party patches to canonical data (energy cost, dynamic vars, keywords,
/// rarity) are followed automatically except for explicitly rejected gameplay mods. A replaced <see cref="CardModel.OnPlay"/>
/// is different in kind: <c>CardOnPlayInferrer</c> reads the original, unpatched IL by design, and the
/// bespoke mirrors are keyed on the vanilla card type. The engine therefore keeps executing the vanilla recipe it
/// was written against and silently produces a route for a card the game no longer plays that way, which the
/// project's "unknown semantics must fail explicitly" constraint forbids.
/// </remarks>
internal static class PredictionModPatchAudit
{
    private static readonly string[] IncompatibleModIds = ["WheelchairSpire", "PengoTarot", "BetterCharacterRelics"];

    internal readonly record struct ForeignPatch(string ModId, string ModName, string Description);

    /// <summary>
    /// Throws when any card reachable from the captured root has a third-party patch on its mirrored OnPlay.
    /// </summary>
    /// <remarks>
    /// Reachable card types are audited immediately. Registered generated-card types and the set of patched
    /// OnPlay methods are also frozen at root capture so worker threads never inspect the mutable Harmony table.
    /// </remarks>
    public static void ValidateCardOnPlay(IEnumerable<CardModel> cards)
        => CaptureCardOnPlay(cards);

    internal static AdaptedOnPlaySnapshot? CaptureCardOnPlay(IEnumerable<CardModel> cards)
    {
        ValidateLoadedMods(ModManager.GetLoadedMods());
        bool adapted = AdaptedCardOnPlayMirrors.Seal();
        Dictionary<Type, AdaptedCardOnPlayMirrors.Registration?>? selections = adapted ? [] : null;
        HashSet<Type> checkedTypes = [];

        foreach (CardModel card in cards)
        {
            Type type = card.GetType();
            if (!checkedTypes.Add(type))
                continue;

            AdaptedCardOnPlayMirrors.Registration? selected =
                AuditCardOnPlay(type, adapted, out ForeignPatch? firstForeign);
            if (selected is null && firstForeign is { } unsupported)
            {
                throw new IncompatibleGameplayModException(
                    unsupported.ModId,
                    unsupported.ModName,
                    unsupported.Description,
                    "combat");
            }
            selections?.Add(type, selected);
        }

        if (selections is null)
            return null;

        Dictionary<Type, string> deferredFailures = [];
        foreach (Type type in AdaptedCardOnPlayMirrors.RegisteredTypes())
        {
            if (!checkedTypes.Add(type))
                continue;

            try
            {
                AdaptedCardOnPlayMirrors.Registration? selected =
                    AuditCardOnPlay(type, adapted: true, out ForeignPatch? firstForeign);
                if (selected is null && firstForeign is { } unsupported)
                {
                    deferredFailures.Add(
                        type,
                        $"{unsupported.ModName} ({unsupported.ModId}) patches the OnPlay of " +
                        $"{type.FullName} without a matching adapter: {unsupported.Description}.");
                }
                else
                {
                    selections.Add(type, selected);
                }
            }
            catch (PredictionUnsupportedException error)
            {
                // The registered type is not reachable yet. Preserve the exact root-time rejection
                // and report it only if a later generated card actually uses this type.
                deferredFailures.Add(type, error.Message);
            }
        }

        HashSet<MethodInfo> patchedOnPlayTargets = [];
        string stamp = AdaptedCardOnPlayMirrors.CaptureLiveStamp(patchedOnPlayTargets)!;
        return new(selections, stamp, patchedOnPlayTargets, deferredFailures);
    }

    internal static AdaptedCardOnPlayMirrors.Registration? AuditCardOnPlay(
        Type type,
        bool adapted,
        out ForeignPatch? firstForeign)
    {
        MethodInfo target = AdaptedCardOnPlayMirrors.ResolveOnPlay(type)
            ?? throw new PredictionUnsupportedException($"Missing OnPlay for {type.FullName}.");
        Patches? patches = Harmony.GetPatchInfo(target);
        firstForeign = null;
        if (patches is not null)
        {
            foreach (var group in AdaptedCardOnPlayMirrors.Groups(patches))
            {
                foreach (Patch patch in group.Patches)
                {
                    // Resolve every source even when the full composition has an adapter.
                    ForeignPatch? foreign = TryDescribeForeignPatch(patch, target);
                    firstForeign ??= foreign;
                }
            }
        }

        return adapted ? AdaptedCardOnPlayMirrors.Select(type, target, patches) : null;
    }

    internal static void ValidateLoadedMods(IEnumerable<Mod> mods)
    {
        foreach (Mod mod in mods)
        {
            string? incompatibleId = IncompatibleModIds.FirstOrDefault(id =>
                string.Equals(mod.manifest?.id, id, StringComparison.OrdinalIgnoreCase)
#if STS2_01071
                || mod.path.Contains(id, StringComparison.OrdinalIgnoreCase)
#else
                || mod.assemblies.Any(assembly => string.Equals(
                    assembly.GetName().Name,
                    id,
                    StringComparison.OrdinalIgnoreCase))
#endif
            );
            if (incompatibleId is null)
                continue;
            throw new IncompatibleGameplayModException(
                mod.manifest?.id ?? string.Empty,
                mod.manifest?.name ?? incompatibleId,
                $"{incompatibleId} gameplay changes",
                "combat");
        }
    }

    private static ForeignPatch? TryDescribeForeignPatch(Patch patch, MethodInfo target)
    {
        Type? patchType = patch.PatchMethod.DeclaringType;
        if (patchType == null)
            throw new PredictionUnsupportedException(
                $"Unknown Harmony patch {patch.PatchMethod} (owner={patch.owner}) on {target}.");

#if !STS2_01071
        var mod = AssemblyInfo.ModForType(patchType, out bool isBaseGame);
        if (isBaseGame)
            return null;
        // Same policy as the ModHelper subscriber audit: mods that declare themselves gameplay-neutral are trusted.
        if (mod?.manifest?.affectsGameplay is false)
            return null;
        if (mod?.manifest?.id is not { Length: > 0 } modId)
            throw new PredictionUnsupportedException(
                $"Unknown Harmony patch {patchType.FullName}.{patch.PatchMethod.Name} " +
                $"(owner={patch.owner}) on mirrored {target.DeclaringType?.FullName}.{target.Name}.");
        if (string.Equals(modId, Entry.ModId, StringComparison.OrdinalIgnoreCase))
            return null;

        return new ForeignPatch(
            modId,
            mod.manifest.name ?? string.Empty,
            $"Harmony patch {patchType.FullName}.{patch.PatchMethod.Name} on mirrored "
            + $"{target.DeclaringType?.FullName}.{target.Name}");
#else
        if (patchType.Assembly == typeof(AbstractModel).Assembly
            || patchType.Assembly == typeof(Entry).Assembly
            || string.Equals(patch.owner, Entry.ModId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        throw new PredictionUnsupportedException(
            $"Unknown third-party Harmony patch {patchType.FullName}.{patch.PatchMethod.Name} "
            + $"(owner={patch.owner}) on mirrored {target.DeclaringType?.FullName}.{target.Name}.");
#endif
    }
}
