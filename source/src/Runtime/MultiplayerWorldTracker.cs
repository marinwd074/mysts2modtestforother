namespace CombatSolver;

/// <summary>
/// Main-thread-only observation state for future client-local multiplayer search.
/// It owns no network objects and never reads live state itself; callers provide an
/// immutable observation fingerprint after reading the current state on the main thread.
/// </summary>
internal static class MultiplayerWorldTracker
{
    internal const int DefaultDebounceMilliseconds = 200;

    private static string? _lastFingerprint;
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

    internal static bool ObserveSnapshot(string fingerprint, string reason)
    {
        if (string.Equals(_lastFingerprint, fingerprint, StringComparison.Ordinal))
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
