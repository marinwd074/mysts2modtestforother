namespace CombatSolver;

/// <summary>
/// Main-thread-only observation state for future client-local multiplayer search.
/// It owns no network objects and never reads live state itself; callers provide an
/// immutable observation fingerprint after reading the current state on the main thread.
/// </summary>
internal static class MultiplayerWorldTracker
{
    internal const int DefaultDebounceMilliseconds = 200;

    private static readonly MultiplayerWorldObservation Observation = new();

    internal static long WorldVersion => Observation.WorldVersion;
    internal static bool IsDirty => Observation.IsDirty;
    internal static string LastReason => Observation.LastReason;
    internal static void Reset() => Observation.Reset();
    internal static bool ObserveSnapshot(StateFingerprint fingerprint, string reason)
        => Observation.ObserveSnapshot(fingerprint, reason, Environment.TickCount64);
    internal static bool TryReadStable(out long worldVersion)
        => Observation.TryReadStable(Environment.TickCount64, out worldVersion);
    internal static bool IsStable(long worldVersion)
        => Observation.IsStable(worldVersion, Environment.TickCount64);
    internal static bool TryConfirmStable(long worldVersion)
        => Observation.TryConfirmStable(worldVersion, Environment.TickCount64);
    internal static bool TryTakeStable(out long worldVersion)
        => Observation.TryTakeStable(Environment.TickCount64, out worldVersion);
}

/// <summary>
/// One main-thread observer's state. The caller supplies monotonic milliseconds so
/// production and deterministic event-schedule tests use the same transition logic.
/// Instances neither synchronize peers nor grant execution authority.
/// </summary>
internal sealed class MultiplayerWorldObservation
{

    private StateFingerprint? _lastFingerprint;
    private string _lastReason = "reset";
    private long _worldVersion;
    private long _stableAfter;
    private bool _dirty;

    internal long WorldVersion => _worldVersion;
    internal bool IsDirty => _dirty;
    internal string LastReason => _lastReason;

    internal void Reset()
    {
        _lastFingerprint = null;
        _lastReason = "reset";
        _worldVersion = 0;
        _stableAfter = 0;
        _dirty = false;
    }

    internal bool ObserveSnapshot(StateFingerprint fingerprint, string reason, long now)
    {
        if (_lastFingerprint is { } previous && previous == fingerprint)
            return false;

        _lastFingerprint = fingerprint;
        _lastReason = reason;
        _worldVersion = checked(_worldVersion + 1);
        _stableAfter = now + MultiplayerWorldTracker.DefaultDebounceMilliseconds;
        _dirty = true;
        return true;
    }

    /// <summary>
    /// Reads the current stable dirty version without consuming it.
    /// Callers can inspect their own conditions after this returns true, then
    /// use <see cref="TryConfirmStable"/> with the returned version.
    /// </summary>
    internal bool TryReadStable(long now, out long worldVersion)
    {
        worldVersion = _worldVersion;
        return _dirty && now >= _stableAfter;
    }

    /// <summary>
    /// Checks whether the current version is settled without consuming a dirty
    /// observation. A Safe EndTurn uses this after the last accepted local action
    /// has already confirmed its observation.
    /// </summary>
    internal bool IsStable(long worldVersion, long now)
        => worldVersion == _worldVersion
           && (!_dirty || now >= _stableAfter);

    /// <summary>
    /// Consumes a stable dirty version only when it is still the current world
    /// version. A newer observation leaves the newer version dirty.
    /// </summary>
    internal bool TryConfirmStable(long worldVersion, long now)
    {
        if (!TryReadStable(now, out long currentWorldVersion)
            || currentWorldVersion != worldVersion)
            return false;

        _dirty = false;
        return true;
    }

    internal bool TryTakeStable(long now, out long worldVersion)
    {
        if (!TryReadStable(now, out worldVersion))
            return false;

        return TryConfirmStable(worldVersion, now);
    }
}


/// <summary>
/// Search invalidation signal for multiplayer. Unlike <see cref="MultiplayerWorldTracker"/>,
/// this deliberately ignores every live-state mutation except enemy current HP. Turn boundaries
/// may signal scheduling without invalidating an existing route.
/// </summary>
internal static class MultiplayerRouteChangeTracker
{
    internal const int DefaultDebounceMilliseconds =
        MultiplayerWorldTracker.DefaultDebounceMilliseconds;

    private static StateFingerprint? _lastEnemyHpFingerprint;
    private static string _lastReason = "reset";
    private static long _version;
    private static long _invalidationVersion;
    private static long _stableAfter;
    private static bool _dirty;

    internal static long Version => _version;
    internal static long InvalidationVersion => _invalidationVersion;
    internal static string LastReason => _lastReason;

    internal static void Reset()
    {
        _lastEnemyHpFingerprint = null;
        _lastReason = "reset";
        _version = 0;
        _invalidationVersion = 0;
        _stableAfter = 0;
        _dirty = false;
    }

    internal static bool ObserveEnemyHp(
        StateFingerprint fingerprint,
        bool allowInvalidation,
        string reason)
    {
        if (_lastEnemyHpFingerprint is { } previous && previous == fingerprint)
            return false;

        _lastEnemyHpFingerprint = fingerprint;
        if (allowInvalidation)
            SignalInvalidation(reason);
        return allowInvalidation;
    }

    internal static void SynchronizeEnemyHp(StateFingerprint fingerprint)
        => _lastEnemyHpFingerprint = fingerprint;

    internal static void SignalSchedulingBoundary(string reason)
        => SignalScheduling(reason);

    internal static bool TryTakeStable(out long version)
    {
        version = _version;
        if (!_dirty || Environment.TickCount64 < _stableAfter)
            return false;

        _dirty = false;
        return true;
    }

    private static void SignalInvalidation(string reason)
    {
        _invalidationVersion = checked(_invalidationVersion + 1);
        SignalScheduling(reason);
    }

    private static void SignalScheduling(string reason)
    {
        _lastReason = reason;
        _version = checked(_version + 1);
        _stableAfter = Environment.TickCount64 + DefaultDebounceMilliseconds;
        _dirty = true;
    }
}
