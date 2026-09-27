namespace CombatSolver;

/// <summary>
/// Battle-scoped replay cache storage for rolling horizon reuse.
/// The first phase intentionally stores opaque replay artifacts only; replay semantics
/// are added after key correctness is validated.
/// </summary>
internal sealed class ActionReplayCache
{
    private readonly Dictionary<ReplayCacheKey, object> _entries = [];

    public bool TryGet(ReplayCacheKey key, out object entry)
        => _entries.TryGetValue(key, out entry!);

    public void Store(ReplayCacheKey key, object entry)
        => _entries[key] = entry;

    public void Clear()
        => _entries.Clear();

    public int Count => _entries.Count;
}

internal readonly record struct ReplayCacheKey(
    string StateFingerprint,
    string ActionIdentity,
    int ContractVersion);
