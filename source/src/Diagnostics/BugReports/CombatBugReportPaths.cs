namespace CombatSolver;

// One user-visible home for exported reports and the diagnostic files that
// explain them. Native Godot logging still owns the game's user-data log file;
// SyncGameLogs mirrors those files here without changing the game's save root.
internal static class CombatBugReportPaths
{
    internal const string RootFolderName = "CombatSolver-BugReports";

    private static readonly object Gate = new();
    private static string? _rootDirectory;

    internal static string RootDirectory
    {
        get
        {
            lock (Gate)
                return _rootDirectory ??= ResolveRootDirectory();
        }
    }

    internal static string LogsDirectory => Path.Combine(RootDirectory, "logs");
    internal static string ModLogsDirectory => Path.Combine(LogsDirectory, "CombatSolver");
    internal static string GameLogsDirectory => Path.Combine(LogsDirectory, "Game");
    internal static string PreCombatLogsDirectory => Path.Combine(LogsDirectory, "PreCombat");

    internal static void EnsureOutputDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ModLogsDirectory);
        Directory.CreateDirectory(GameLogsDirectory);
        Directory.CreateDirectory(PreCombatLogsDirectory);
    }

    internal static void SyncGameLogs(string sourceDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
            return;

        string targetDirectory = GameLogsDirectory;
        Directory.CreateDirectory(targetDirectory);
        foreach (string sourcePath in Directory.EnumerateFiles(sourceDirectory, "*.log", SearchOption.TopDirectoryOnly))
        {
            string targetPath = Path.Combine(targetDirectory, Path.GetFileName(sourcePath));
            try
            {
                // The active godot.log is open by the game. File.Copy can still
                // take a consistent readable snapshot through the shared handle.
                File.Copy(sourcePath, targetPath, overwrite: true);
            }
            catch (IOException)
            {
                // A log may rotate between enumeration and copy; the next sync
                // will retry it and must not affect the running game.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep diagnostics best-effort when a native log is locked.
            }
        }
    }

    private static string ResolveRootDirectory()
    {
        string? multiplayerInstance = Environment.GetEnvironmentVariable("COMBATSOLVER_MULTIPLAYER_INSTANCE");
        if (!string.IsNullOrWhiteSpace(multiplayerInstance))
        {
            string instanceRoot = Path.GetFullPath(multiplayerInstance);
            return Path.Combine(instanceRoot, "diagnostics", RootFolderName);
        }

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
            throw new DirectoryNotFoundException("无法定位桌面目录。");
        return Path.Combine(desktop, RootFolderName);
    }
}
