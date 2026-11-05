using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Rooms;

namespace CombatSolver.Api;

internal static class PreCombatForecastWorker
{
    private const string RuntimeDirectoryName = ".combatsolver-precombat";
    private const string RuntimeMarkerName = ".combatsolver-precombat-owner";
    private const string RuntimeMarkerContents = "CombatSolver PreCombat API v1";
    private const int KeepAliveIndefinitely = -1;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object RequestCancellationSync = new();
    private static CancellationTokenSource? _activeRequestCancellation;
    private static bool _shuttingDown;
    private static string RuntimeRoot(string gameRoot) =>
        Path.Combine(gameRoot, RuntimeDirectoryName, $"process-{Environment.ProcessId}");
    private static readonly object PinnedModSourcesSync = new();
    private static readonly Dictionary<string, PinnedModSource> PinnedModSources =
        new(StringComparer.Ordinal);
    private static PreCombatWorkerSession? _session;
    private static string? _ownedRuntimeRoot;
    private static CancellationTokenSource? _idleShutdown;
    private static int _idleWorkerLifetimeMilliseconds = PreCombatForecastApi.DefaultWorkerIdleTimeoutMilliseconds;
    private static int _workerStartCount;
    private static int _workerReuseCount;
    private static int _workerBusy;

    static PreCombatForecastWorker()
    {
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => StopSessionAtProcessExit();
    }

    internal static int WorkerStartCountForTesting => Volatile.Read(ref _workerStartCount);

    internal static int WorkerReuseCountForTesting => Volatile.Read(ref _workerReuseCount);

    internal static void PinMainProcessModSources()
    {
        if (!OperatingSystem.IsWindows() || Entry.IsPreCombatWorker)
        {
            return;
        }

        lock (PinnedModSourcesSync)
        {
            if (PinnedModSources.Count > 0)
                return;

            string executablePath = Godot.OS.GetExecutablePath();
            string gameRoot = Path.GetDirectoryName(executablePath)
                ?? throw new InvalidOperationException("The game executable has no parent directory.");
            string runtimeRoot = RuntimeRoot(gameRoot);
            string pinnedRoot = Path.Combine(runtimeRoot, "startup-mods");
            EnsureRuntimeRoot(runtimeRoot, gameRoot);
            ResetOwnedDirectory(runtimeRoot, pinnedRoot);

            Mod[] candidates = ModManager.Mods
                .Where(static mod => mod.manifest?.id != null
                                     && mod.state is ModLoadState.None or ModLoadState.Loaded)
                .ToArray();
            int sourceIndex = 0;
            foreach (IGrouping<string, Mod> sourceGroup in candidates.GroupBy(
                         static mod => Path.GetFullPath(mod.path),
                         StringComparer.OrdinalIgnoreCase))
            {
                Mod[] sourceMods = sourceGroup.ToArray();
                string label = string.Join("_", sourceMods.Select(static mod => Sanitize(mod.manifest!.id!)));
                string target = Path.Combine(pinnedRoot, $"{sourceIndex++:D2}_{label}");
                try
                {
                    CopyDirectory(sourceGroup.Key, target);
                    foreach (Mod mod in sourceMods)
                    {
                        string id = mod.manifest!.id!;
                        PinnedModSources[id] = new PinnedModSource(
                            mod.manifest.version ?? "unknown",
                            sourceGroup.Key,
                            target);
                    }
                }
                catch (Exception ex)
                {
                    Entry.Logger.Warn(
                        $"[CombatSolver/PreCombatApi] MOD_SOURCE_PIN_FAILED " +
                        $"source={sourceGroup.Key} error={ex.Message}");
                }
            }

            Entry.Logger.Info(
                $"[CombatSolver/PreCombatApi] MOD_SOURCES_PINNED " +
                $"mods={PinnedModSources.Count} sources={sourceIndex} root={pinnedRoot}");
        }
    }

    internal static string ResolvePinnedModSource(string id, string version, string sourcePath)
    {
        string fullSourcePath = Path.GetFullPath(sourcePath);
        lock (PinnedModSourcesSync)
        {
            if (PinnedModSources.TryGetValue(id, out PinnedModSource? pinned)
                && pinned.Version.Equals(version, StringComparison.Ordinal)
                && pinned.OriginalSourcePath.Equals(fullSourcePath, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(pinned.PinnedSourcePath))
            {
                return pinned.PinnedSourcePath;
            }
        }
        throw new InvalidOperationException(
            $"No startup snapshot is available for loaded mod {id}@{version}: {fullSourcePath}");
    }

    internal static void MirrorDirectoryForTesting(string sourceRoot, string targetRoot) =>
        CopyDirectory(sourceRoot, targetRoot);

    internal static PreCombatWorkerStatus GetStatus()
    {
        bool busy = Volatile.Read(ref _workerBusy) != 0;
        PreCombatWorkerSession? session = Volatile.Read(ref _session);
        if (session is null)
            return StoppedStatus(busy);
        return session.CaptureStatus(busy, GetIdleTimeoutMilliseconds());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        nint securityAttributes);

    public static async Task<PreCombatForecastResult> RunAsync(
        PreCombatLiveStateSnapshot snapshot,
        string encounterId,
        int targetActFloor,
        int targetMapColumn,
        PreCombatRoomKind roomKind,
        PreCombatMapPointKind mapPointKind,
        bool isSecondBoss,
        PreCombatForecastOptions options,
        CancellationToken cancellationToken)
    {
        string requestId = Guid.NewGuid().ToString("N");
        string? diagnosticLogPath = null;
        bool gateEntered = false;
        CancellationToken callerToken = cancellationToken;
        using CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation.Token);
        deadline.CancelAfter(options.OverallTimeoutMilliseconds);
        cancellationToken = deadline.Token;
        try
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            lock (RequestCancellationSync)
                _activeRequestCancellation = requestCancellation;
            Interlocked.Exchange(ref _workerBusy, 1);
            CancelIdleShutdown();
            SetIdleWorkerLifetime(options.WorkerIdleTimeoutMilliseconds);

            string runtimeRoot = RuntimeRoot(snapshot.GameRoot);
            string requestRoot = Path.Combine(runtimeRoot, "requests", requestId);
            string snapshotPath = Path.Combine(requestRoot, "run.save.json");
            EnsureRuntimeRoot(runtimeRoot, snapshot.GameRoot);
            Directory.CreateDirectory(requestRoot);
            await File.WriteAllBytesAsync(
                    snapshotPath,
                    snapshot.SerializedPlanningRun ?? snapshot.SerializedRun,
                    cancellationToken)
                .ConfigureAwait(false);

            string sessionSignature = BuildSessionSignature(snapshot);
            PreCombatWorkerSession? session = TryGetReusableSession(sessionSignature);
            bool reusedWorker = session is not null;
            if (session is null)
            {
                StopCurrentSession();
                session = CreateSession(snapshot, runtimeRoot, sessionSignature, cancellationToken);
            }
            else
            {
                Interlocked.Increment(ref _workerReuseCount);
            }
            diagnosticLogPath = session.LogPath;

            string[] expectedMods = snapshot.Mods
                .Select(static mod => $"{mod.Id}@{mod.Version}")
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();
            UnattendedRuntimeRequest request = new()
            {
                RunId = requestId,
                ScenarioId = options.SimulationSeed.HasValue
                    ? "PRECOMBAT-SIMULATION-V1"
                    : "PRECOMBAT-API-V1",
                CharacterId = snapshot.CharacterId,
                EncounterId = encounterId,
                Seed = snapshot.Seed,
                RunSnapshotPath = snapshotPath,
                ActIndexForTest = snapshot.ActIndex,
                MarkEncounterAsSecondBossForTest = isSecondBoss,
                LoadRunSnapshotDirectly = true,
                TargetActFloor = targetActFloor,
                TargetMapColumn = targetMapColumn,
                TargetRoomType = ToRoomType(roomKind),
                TargetMapPointType = ToMapPointType(mapPointKind),
                ExpectedLoadedMods = expectedMods,
                EnemyCurrentHp = int.MaxValue,
                Cards = [],
                FixedSearchBudget = true,
                SearchBudgetOverrideMilliseconds = options.SearchBudgetMilliseconds,
                SearchMaxDegreeOfParallelismForTest = options.MaxDegreeOfParallelism,
                PreCombatPlayerCurrentHpOverride = options.PlayerCurrentHpOverride,
                PreCombatSimulationSeed = options.SimulationSeed,
                PreCombatInterveningMapPoints = options.InterveningMapPoints
                    .Select(static step => new UnattendedRuntimePreCombatMapStep
                    {
                        Coordinate = step.Coordinate,
                        RoomType = step.RoomType,
                        MapPointType = step.MapPointType,
                    })
                    .ToArray(),
                EnableNoGcRegionForTest = false,
                HeadlessFastModeForTest = SolverDeploymentFastMode.Instant,
                StopAfterInitialSolverResultAssertion = true,
                ExitOnComplete = false,
                TimeoutSeconds = Math.Max(
                    15,
                    (options.OverallTimeoutMilliseconds - 5_000) / 1_000d),
            };
            await session.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
            Entry.Logger.Info(
                $"[CombatSolver/PreCombatApi] WORKER_REQUEST request_id={requestId} " +
                $"pid={session.ProcessId} reused={reusedWorker} audio_muted={session.AudioMuted}");
            UnattendedRuntimeResult workerResult;
            try
            {
                workerResult = await session.WaitForResultAsync(deadline.Token).ConfigureAwait(false);
                if (!workerResult.Status.Equals("Passed", StringComparison.OrdinalIgnoreCase)
                    || workerResult.SolverMetrics == null)
                {
                    InvalidateSession(session);
                    return PreCombatForecastApi.Failure(
                        PreCombatForecastStatus.Failed,
                        requestId,
                        workerResult.Error ?? $"Worker stopped at stage {workerResult.Stage}.",
                        session.LogPath);
                }
                if (options.SimulationSeed is { } simulationSeed
                    && !workerResult.CompletedChecks.Contains(
                        $"PreCombatSimulationRng:{simulationSeed}",
                        StringComparer.Ordinal))
                {
                    InvalidateSession(session);
                    return PreCombatForecastApi.Failure(
                        PreCombatForecastStatus.Failed,
                        requestId,
                        "The isolated worker did not confirm the requested hypothetical combat RNG sample.",
                        session.LogPath);
                }
                await session.WaitForReadyAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (PreCombatWorkerExitUnconfirmedException) { throw; }
            catch (OperationCanceledException) when (!requestCancellation.IsCancellationRequested)
            {
                InvalidateSession(session);
                return PreCombatForecastApi.Failure(
                    PreCombatForecastStatus.TimedOut,
                    requestId,
                    $"The isolated worker exceeded {options.OverallTimeoutMilliseconds} ms.",
                    session.LogPath);
            }
            catch (OperationCanceledException)
            {
                InvalidateSession(session);
                return PreCombatForecastApi.Failure(
                    PreCombatForecastStatus.Cancelled,
                    requestId,
                    "The isolated pre-combat request was cancelled.",
                    session.LogPath);
            }
            catch
            {
                InvalidateSession(session);
                throw;
            }

            UnattendedRuntimeSolverMetrics metrics = workerResult.SolverMetrics;
            PreCombatForecastConfidence confidence = metrics.OnlyDeathRoutes
                ? PreCombatForecastConfidence.DeathOnly
                : metrics.CombatEndedTurn.HasValue
                    ? PreCombatForecastConfidence.Complete
                    : PreCombatForecastConfidence.Bounded;
            PreCombatForecastResult result = new()
            {
                Status = PreCombatForecastStatus.Succeeded,
                RequestId = requestId,
                ProjectedHpLoss = metrics.ProjectedBattleHpLost,
                PotionUses = metrics.PotionUses
                    .Select(static use => new PreCombatPotionUse(
                        use.Id,
                        use.Title,
                        use.Turn,
                        use.Slot))
                    .ToArray(),
                SearchBoundary = metrics.Boundary.ToString(),
                Confidence = confidence,
                FinalHp = metrics.FinalHp,
                CombatEndedTurn = metrics.CombatEndedTurn,
                SearchElapsedMilliseconds = metrics.TotalElapsedMilliseconds,
                TotalElapsedMilliseconds = workerResult.ElapsedMilliseconds,
            };
            session.CompleteRequestCleanup(requestId,
                () => TryDeleteOwnedRequestDirectory(runtimeRoot, requestRoot));
            if (options.CloseWorkerAfterRequest)
                StopCurrentSession();
            else
                ScheduleIdleShutdown(session);
            return result;
        }
        catch (PreCombatWorkerExitUnconfirmedException) { throw; }
        catch (OperationCanceledException)
        {
            if (gateEntered)
                StopCurrentSession();
            return PreCombatForecastApi.Failure(
                requestCancellation.IsCancellationRequested ? PreCombatForecastStatus.Cancelled : PreCombatForecastStatus.TimedOut,
                requestId,
                "The isolated pre-combat request was cancelled.");
        }
        catch (Exception ex)
        {
            if (gateEntered)
                StopCurrentSession();
            Entry.Logger.Error(
                $"[CombatSolver/PreCombatApi] WORKER_FAILED request_id={requestId} exception={ex}");
            return PreCombatForecastApi.Failure(
                PreCombatForecastStatus.Failed,
                requestId,
                ex.GetBaseException().Message,
                diagnosticLogPath);
        }
        finally
        {
            if (gateEntered)
            {
                lock (RequestCancellationSync)
                    _activeRequestCancellation = null;
                Interlocked.Exchange(ref _workerBusy, 0);
                Gate.Release();
            }
        }
    }

    public static async Task<PreCombatWorkerStatus> RestartSessionAsync(
        PreCombatLiveStateSnapshot snapshot,
        int? idleTimeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _workerBusy, 1);
            CancelIdleShutdown();
            SetIdleWorkerLifetime(idleTimeoutMilliseconds);
            StopCurrentSession();
            string runtimeRoot = RuntimeRoot(snapshot.GameRoot);
            EnsureRuntimeRoot(runtimeRoot, snapshot.GameRoot);
            PreCombatWorkerSession session = CreateSession(
                snapshot,
                runtimeRoot,
                BuildSessionSignature(snapshot),
                cancellationToken);
            ScheduleIdleShutdown(session);
        }
        finally
        {
            Interlocked.Exchange(ref _workerBusy, 0);
            Gate.Release();
        }
        return GetStatus();
    }

    public static Task ConfigureIdleTimeoutAsync(int? idleTimeoutMilliseconds) =>
        ApplyCachedRequestLifetimeAsync(false, idleTimeoutMilliseconds, CancellationToken.None);

    // A cache hit owns no running request: wait for the reusable barrier rather than
    // using StopSessionAsync, which would cancel another caller's active search.
    internal static async Task ApplyCachedRequestLifetimeAsync(
        bool closeWorker, int? idleTimeoutMilliseconds, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetIdleWorkerLifetime(idleTimeoutMilliseconds);
            if (closeWorker)
            {
                CancelIdleShutdown();
                StopCurrentSession();
                return;
            }
            PreCombatWorkerSession? session = Volatile.Read(ref _session);
            if (session is not null && session.IsReusable)
                ScheduleIdleShutdown(session);
            else
                CancelIdleShutdown();
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task StopSessionAsync()
    {
        lock (RequestCancellationSync)
            _activeRequestCancellation?.Cancel();
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CancelIdleShutdown();
            StopCurrentSession();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static PreCombatWorkerSession? TryGetReusableSession(string signature)
    {
        PreCombatWorkerSession? session = Volatile.Read(ref _session);
        if (session is null)
            return null;
        if (!session.CanReuse(signature))
        {
            InvalidateSession(session);
            return null;
        }
        return session;
    }

    private static PreCombatWorkerSession CreateSession(
        PreCombatLiveStateSnapshot snapshot,
        string runtimeRoot,
        string sessionSignature,
        CancellationToken cancellationToken)
    {
        string workerGameRoot = Path.Combine(runtimeRoot, "game");
        string sessionRoot = Path.Combine(runtimeRoot, "session");
        string roamingRoot = Path.Combine(sessionRoot, "Roaming");
        string localRoot = Path.Combine(sessionRoot, "Local");
        string workerDataRoot = Path.Combine(roamingRoot, "SlayTheSpire2");
        CombatBugReportPaths.EnsureOutputDirectories();
        string logPath = Path.Combine(
            CombatBugReportPaths.PreCombatLogsDirectory,
            $"worker-{Environment.ProcessId}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.log");
        ResetOwnedDirectory(runtimeRoot, sessionRoot);
        EnsureGameMirror(runtimeRoot, snapshot.GameRoot, workerGameRoot);
        MirrorLoadedMods(runtimeRoot, workerGameRoot, snapshot.Mods);
        bool audioMuted = PrepareWorkerUserData(snapshot.UserDataRoot, snapshot.AccountDataRoot, workerDataRoot);
        File.WriteAllBytes(Path.Combine(workerDataRoot, "combat_solver_settings.json"), snapshot.SolverSettings);
        Directory.CreateDirectory(localRoot);

        string workerExe = Path.Combine(workerGameRoot, "SlayTheSpire2.exe");
        if (!File.Exists(workerExe))
            throw new FileNotFoundException("The mirrored game executable is missing.", workerExe);
        lock (RequestCancellationSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_shuttingDown)
                throw new OperationCanceledException("The main game is exiting.");
            PreCombatWorkerSession session = new(
                sessionSignature,
                new OwnedPreCombatWorkerProcess(
                    StartWorker(workerExe, workerGameRoot, roamingRoot, localRoot, logPath)),
                Path.Combine(workerDataRoot, "combat_solver_test_request.json"),
                Path.Combine(workerDataRoot, "combat_solver_test_result.json"),
                Path.Combine(workerDataRoot, "combat_solver_test_ready.json"),
                logPath,
                audioMuted);
            Volatile.Write(ref _session, session);
            Interlocked.Increment(ref _workerStartCount);
            return session;
        }
    }

    private static string BuildSessionSignature(PreCombatLiveStateSnapshot snapshot) => string.Join(
        '|',
        Path.GetFullPath(snapshot.GameRoot),
        Path.GetFullPath(snapshot.UserDataRoot),
        Path.GetFullPath(snapshot.AccountDataRoot),
        Convert.ToHexString(SHA256.HashData(snapshot.SolverSettings)),
        string.Join(';', snapshot.Mods.Select(static mod =>
            $"{mod.Id}@{mod.Version}@{Path.GetFullPath(mod.SourcePath)}")));

    private static PreCombatWorkerStatus StoppedStatus(bool busy) => new()
    {
        IsRunning = false,
        IsBusy = busy,
        AudioMuted = false,
        IdleTimeoutMilliseconds = GetIdleTimeoutMilliseconds(),
    };

    private static void ScheduleIdleShutdown(PreCombatWorkerSession session)
    {
        CancelIdleShutdown();
        int idleTimeoutMilliseconds = Volatile.Read(ref _idleWorkerLifetimeMilliseconds);
        if (idleTimeoutMilliseconds == KeepAliveIndefinitely)
            return;
        CancellationTokenSource cancellation = new();
        _idleShutdown = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(idleTimeoutMilliseconds, cancellation.Token).ConfigureAwait(false);
                await Gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(_session, session))
                    {
                        Entry.Logger.Info(
                            $"[CombatSolver/PreCombatApi] WORKER_IDLE_STOP pid={session.ProcessId} " +
                            $"idle_ms={idleTimeoutMilliseconds}");
                        StopCurrentSession();
                    }
                }
                finally
                {
                    Gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // A new request or explicit panel close owns the next session transition.
            }
            catch (Exception ex)
            {
                Entry.Logger.Error($"[CombatSolver/PreCombatApi] WORKER_IDLE_STOP_FAILED exception={ex}");
            }
            finally
            {
                Interlocked.CompareExchange(ref _idleShutdown, null, cancellation);
                cancellation.Dispose();
            }
        });
    }

    private static int? GetIdleTimeoutMilliseconds()
    {
        int value = Volatile.Read(ref _idleWorkerLifetimeMilliseconds);
        return value == KeepAliveIndefinitely ? null : value;
    }

    private static void SetIdleWorkerLifetime(int? idleTimeoutMilliseconds) =>
        Interlocked.Exchange(
            ref _idleWorkerLifetimeMilliseconds,
            idleTimeoutMilliseconds ?? KeepAliveIndefinitely);

    private static void CancelIdleShutdown()
    {
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _idleShutdown, null);
        if (cancellation is null)
            return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException)
        {
            // The idle task already finished and disposed its token after our exchange.
            // Cancellation is no longer needed; never let this race skip owned-process stop.
        }
    }

    private static void InvalidateSession(PreCombatWorkerSession session)
    {
        session.EnsureStopped();
        Interlocked.CompareExchange(ref _session, null, session);
    }

    private static void StopCurrentSession()
    {
        PreCombatWorkerSession? session = Volatile.Read(ref _session);
        if (session is not null)
            InvalidateSession(session);
    }

    internal static void StopSessionAtProcessExit()
    {
        try
        {
            lock (RequestCancellationSync)
            {
                _shuttingDown = true;
                _activeRequestCancellation?.Cancel();
            }
            CancelIdleShutdown();
            StopCurrentSession(); // Throws on unconfirmed exit; do not delete its runtime.
            if (_ownedRuntimeRoot is { } root)
            {
                foreach (string name in new[] { "game", "startup-mods", "requests" })
                {
                    string directory = Path.Combine(root, name);
                    AssertOwnedPath(root, directory);
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, recursive: true);
                }
            }
        }
        catch
        {
            // Process shutdown is already in progress; ownership is not reported released.
        }
    }

    private static RoomType ToRoomType(PreCombatRoomKind kind) => kind switch
    {
        PreCombatRoomKind.Normal => RoomType.Monster,
        PreCombatRoomKind.Elite => RoomType.Elite,
        PreCombatRoomKind.Boss => RoomType.Boss,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static MegaCrit.Sts2.Core.Map.MapPointType ToMapPointType(PreCombatMapPointKind kind) => kind switch
    {
        PreCombatMapPointKind.Normal => MegaCrit.Sts2.Core.Map.MapPointType.Monster,
        PreCombatMapPointKind.Elite => MegaCrit.Sts2.Core.Map.MapPointType.Elite,
        PreCombatMapPointKind.Boss => MegaCrit.Sts2.Core.Map.MapPointType.Boss,
        PreCombatMapPointKind.Event => MegaCrit.Sts2.Core.Map.MapPointType.Ancient,
        PreCombatMapPointKind.Unknown => MegaCrit.Sts2.Core.Map.MapPointType.Unknown,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static void EnsureRuntimeRoot(string runtimeRoot, string sourceGameRoot)
    {
        string runtimeParent = Path.GetDirectoryName(Path.GetFullPath(runtimeRoot))
            ?? throw new InvalidOperationException($"The pre-combat runtime has no parent: {runtimeRoot}");
        Directory.CreateDirectory(runtimeParent);
        CleanupStaleProcessRoots(runtimeParent, runtimeRoot, sourceGameRoot);
        Directory.CreateDirectory(runtimeRoot);
        string markerPath = Path.Combine(runtimeRoot, RuntimeMarkerName);
        string expected = $"{RuntimeMarkerContents}{Environment.NewLine}{Path.GetFullPath(sourceGameRoot)}";
        if (File.Exists(markerPath))
        {
            string actual = File.ReadAllText(markerPath);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The pre-combat runtime marker is invalid: {markerPath}");
            _ownedRuntimeRoot = runtimeRoot;
            return;
        }

        if (Directory.EnumerateFileSystemEntries(runtimeRoot).Any())
            throw new InvalidDataException($"Refusing to adopt a non-empty unowned runtime directory: {runtimeRoot}");
        File.WriteAllText(markerPath, expected);
        _ownedRuntimeRoot = runtimeRoot;
    }

    private static void CleanupStaleProcessRoots(
        string runtimeParent,
        string currentRuntimeRoot,
        string sourceGameRoot)
    {
        string parent = Path.GetFullPath(runtimeParent).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string current = Path.GetFullPath(currentRuntimeRoot);
        string expectedMarker = $"{RuntimeMarkerContents}{Environment.NewLine}{Path.GetFullPath(sourceGameRoot)}";
        foreach (string candidate in Directory.EnumerateDirectories(runtimeParent, "process-*", SearchOption.TopDirectoryOnly))
        {
            string fullCandidate = Path.GetFullPath(candidate);
            if (fullCandidate.Equals(current, StringComparison.OrdinalIgnoreCase)
                || !fullCandidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
                continue;

            string markerPath = Path.Combine(fullCandidate, RuntimeMarkerName);
            if (!File.Exists(markerPath)
                || !File.ReadAllText(markerPath).Equals(expectedMarker, StringComparison.OrdinalIgnoreCase))
                continue;

            string name = Path.GetFileName(fullCandidate);
            if (!int.TryParse(name["process-".Length..], out int processId) || processId <= 0)
                continue;
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (!process.HasExited)
                    continue;
            }
            catch (ArgumentException)
            {
                // The owner PID is gone; this is the safe stale-runtime case.
            }
            catch (InvalidOperationException)
            {
                // A process that disappeared while being inspected is also stale.
            }

            Directory.Delete(fullCandidate, recursive: true);
            Entry.Logger.Info(
                $"[CombatSolver/PreCombatApi] STALE_RUNTIME_REMOVED path={fullCandidate} " +
                $"owner_pid={processId}");
        }
    }

    private static void EnsureGameMirror(string runtimeRoot, string sourceRoot, string targetRoot)
    {
        ResetOwnedDirectory(runtimeRoot, targetRoot);
        foreach (string sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.TopDirectoryOnly))
            LinkFile(sourceFile, Path.Combine(targetRoot, Path.GetFileName(sourceFile)));

        foreach (string directoryName in new[] { "data_sts2_windows_x86_64", "controller_config" })
        {
            string sourceDirectory = Path.Combine(sourceRoot, directoryName);
            if (Directory.Exists(sourceDirectory))
                MirrorDirectory(sourceDirectory, Path.Combine(targetRoot, directoryName));
        }
    }

    private static void MirrorLoadedMods(
        string runtimeRoot,
        string workerGameRoot,
        IReadOnlyList<PreCombatModSnapshot> mods)
    {
        string modsRoot = Path.Combine(workerGameRoot, "mods");
        ResetOwnedDirectory(runtimeRoot, modsRoot);
        int index = 0;
        foreach (IGrouping<string, PreCombatModSnapshot> sourceGroup in mods.GroupBy(
                     static mod => mod.SourcePath,
                     StringComparer.OrdinalIgnoreCase))
        {
            string label = string.Join("_", sourceGroup.Select(static mod => Sanitize(mod.Id)));
            string target = Path.Combine(modsRoot, $"{index++:D2}_{label}");
            CopyDirectory(sourceGroup.Key, target);
        }
    }

    private static bool PrepareWorkerUserData(string sourceRoot, string sourceAccountRoot, string workerRoot)
    {
        Directory.CreateDirectory(workerRoot);
        CopyDirectory(sourceAccountRoot, Path.Combine(workerRoot, "default", "1"));
        foreach (string directoryName in new[] { "ModConfig", "mod_configs" })
        {
            string source = Path.Combine(sourceRoot, directoryName);
            if (Directory.Exists(source))
                CopyDirectory(source, Path.Combine(workerRoot, directoryName));
        }

        string sourceNestedConfig = Path.Combine(sourceRoot, "mods", "config");
        if (Directory.Exists(sourceNestedConfig))
            CopyDirectory(sourceNestedConfig, Path.Combine(workerRoot, "mods", "config"));
        string sourceSolverSettings = Path.Combine(sourceRoot, "combat_solver_settings.json");
        if (File.Exists(sourceSolverSettings))
            File.Copy(sourceSolverSettings, Path.Combine(workerRoot, "combat_solver_settings.json"), true);

        string settingsPath = Path.Combine(workerRoot, "default", "1", "settings.save");
        if (!File.Exists(settingsPath))
            throw new FileNotFoundException("The worker profile settings template is missing.", settingsPath);
        JsonObject settings = JsonNode.Parse(File.ReadAllText(settingsPath))?.AsObject()
            ?? throw new InvalidDataException("The worker settings template is empty.");
        settings["mod_settings"] = new JsonObject
        {
            ["mods_enabled"] = true,
            ["mod_list"] = new JsonArray(),
        };
        string[] volumeKeys =
        [
            "volume_master",
            "volume_bgm",
            "volume_sfx",
            "volume_ambience",
        ];
        foreach (string key in volumeKeys)
            settings[key] = 0f;
        File.WriteAllText(settingsPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        JsonObject savedSettings = JsonNode.Parse(File.ReadAllText(settingsPath))?.AsObject()
            ?? throw new InvalidDataException("The muted worker settings file is empty.");
        if (!volumeKeys.All(key => savedSettings[key]?.GetValue<float>() == 0f))
            throw new InvalidDataException("The isolated worker audio settings could not be muted.");
        return true;
    }

    private static Process StartWorker(
        string executable,
        string workingDirectory,
        string roamingRoot,
        string localRoot,
        string logPath)
    {
        ProcessStartInfo start = new(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--headless");
        start.ArgumentList.Add("--disable-vsync");
        start.ArgumentList.Add("--max-fps");
        start.ArgumentList.Add("0");
        start.ArgumentList.Add("--force-steam=off");
        start.ArgumentList.Add("--log-file");
        start.ArgumentList.Add(logPath);
        start.Environment["APPDATA"] = roamingRoot;
        start.Environment["LOCALAPPDATA"] = localRoot;
        start.Environment["COMBATSOLVER_HEADLESS"] = "1";
        start.Environment["COMBATSOLVER_PRECOMBAT_WORKER"] = "1";
        return Process.Start(start)
            ?? throw new InvalidOperationException("The isolated game process did not start.");
    }

    private static void MirrorDirectory(string sourceRoot, string targetRoot)
    {
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException(sourceRoot);
        Directory.CreateDirectory(targetRoot);
        foreach (string sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceRoot, sourceFile);
            LinkFile(sourceFile, Path.Combine(targetRoot, relative));
        }
    }

    private static void LinkFile(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        FileInfo sourceInfo = new(source);
        if (File.Exists(target))
        {
            FileInfo targetInfo = new(target);
            if (sourceInfo.Length == targetInfo.Length
                && sourceInfo.LastWriteTimeUtc == targetInfo.LastWriteTimeUtc)
            {
                return;
            }
            File.Delete(target);
        }
        if (!CreateHardLinkW(target, source, 0))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not hard-link '{source}' to '{target}'.");
        }
    }

    private static void CopyDirectory(string sourceRoot, string targetRoot)
    {
        foreach (string sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceRoot, sourceFile);
            string target = Path.Combine(targetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(sourceFile, target, true);
        }
    }

    private static void ResetOwnedDirectory(string runtimeRoot, string directory)
    {
        AssertOwnedPath(runtimeRoot, directory);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
    }

    private static void TryDeleteOwnedRequestDirectory(string runtimeRoot, string requestRoot)
    {
        try
        {
            AssertOwnedPath(runtimeRoot, requestRoot);
            if (Directory.Exists(requestRoot))
                Directory.Delete(requestRoot, recursive: true);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn(
                $"[CombatSolver/PreCombatApi] REQUEST_CLEANUP_FAILED path={requestRoot} error={ex.Message}");
        }
    }

    private static void AssertOwnedPath(string runtimeRoot, string path)
    {
        string root = Path.GetFullPath(runtimeRoot).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(path);
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to modify a path outside the owned runtime: {candidate}");
        string marker = Path.Combine(runtimeRoot, RuntimeMarkerName);
        if (!File.Exists(marker)
            || !File.ReadAllText(marker).StartsWith(RuntimeMarkerContents, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The pre-combat runtime is not owned by Combat Solver: {runtimeRoot}");
        }
    }

    private static string Sanitize(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }

    private sealed record PinnedModSource(
        string Version,
        string OriginalSourcePath,
        string PinnedSourcePath);

}
