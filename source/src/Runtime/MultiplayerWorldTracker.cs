namespace CombatSolver;

/// <summary>
/// Main-thread-only observation state for future client-local multiplayer search.
/// It owns no network objects and never reads live state itself; callers provide an
/// immutable observation fingerprint after reading the current state on the main thread.
/// </summary>
internal static class MultiplayerWorldTracker
{
    internal const int DefaultDebounceMilliseconds = 200;

    private static StateFingerprint? _lastFingerprint;
    private static string _lastReason = "reset";
    private static long _worldVersion;
    private static long _stableAfter;
    private static bool _dirty;

    internal static long WorldVersion => _worldVersion;
    internal static bool IsDirty => _dirty;
    internal static string LastReason => _lastReason;

    internal static void Reset()
    {
        _lastFingerprint = null;
        _lastReason = "reset";
        _worldVersion = 0;
        _stableAfter = 0;
        _dirty = false;
    }

    internal static bool ObserveSnapshot(StateFingerprint fingerprint, string reason)
    {
        if (_lastFingerprint is { } previous && previous == fingerprint)
            return false;

        _lastFingerprint = fingerprint;
        _lastReason = reason;
        _worldVersion = checked(_worldVersion + 1);
        _stableAfter = Environment.TickCount64 + DefaultDebounceMilliseconds;
        _dirty = true;
        return true;
    }

    /// <summary>
    /// Reads the current stable dirty version without consuming it.
    /// Callers can inspect their own conditions after this returns true, then
    /// use <see cref="TryConfirmStable"/> with the returned version.
    /// </summary>
    internal static bool TryReadStable(out long worldVersion)
    {
        worldVersion = _worldVersion;
        return _dirty && Environment.TickCount64 >= _stableAfter;
    }

    /// <summary>
    /// Checks whether the current version is settled without consuming a dirty
    /// observation. A Safe EndTurn uses this after the last accepted local action
    /// has already confirmed its observation.
    /// </summary>
    internal static bool IsStable(long worldVersion)
        => worldVersion == _worldVersion
           && (!_dirty || Environment.TickCount64 >= _stableAfter);

    /// <summary>
    /// Consumes a stable dirty version only when it is still the current world
    /// version. A newer observation leaves the newer version dirty.
    /// </summary>
    internal static bool TryConfirmStable(long worldVersion)
    {
        if (!TryReadStable(out long currentWorldVersion)
            || currentWorldVersion != worldVersion)
            return false;

        _dirty = false;
        return true;
    }

    internal static bool TryTakeStable(out long worldVersion)
    {
        if (!TryReadStable(out worldVersion))
            return false;

        return TryConfirmStable(worldVersion);
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
    private static long _stableAfter;
    private static bool _dirty;

    internal static long Version => _version;
    internal static string LastReason => _lastReason;

    internal static void Reset()
    {
        _lastEnemyHpFingerprint = null;
        _lastReason = "reset";
        _version = 0;
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
            Signal(reason);
        return allowInvalidation;
    }

    internal static void SynchronizeEnemyHp(StateFingerprint fingerprint)
        => _lastEnemyHpFingerprint = fingerprint;

    internal static void SignalSchedulingBoundary(string reason)
        => Signal(reason);

    internal static bool TryTakeStable(out long version)
    {
        version = _version;
        if (!_dirty || Environment.TickCount64 < _stableAfter)
            return false;

        _dirty = false;
        return true;
    }

    private static void Signal(string reason)
    {
        _lastReason = reason;
        _version = checked(_version + 1);
        _stableAfter = Environment.TickCount64 + DefaultDebounceMilliseconds;
        _dirty = true;
    }
}
