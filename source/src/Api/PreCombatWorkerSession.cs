using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace CombatSolver.Api;

// The adapter wraps only the exact Process returned by StartWorker. No PID lookup,
// process discovery or independent process ownership is allowed at this seam.
internal interface IOwnedPreCombatWorkerProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    (long WorkingSet, long PrivateMemory, long PeakWorkingSet) CaptureMemory();
    void Kill();
    bool WaitForExit(int milliseconds);
}

internal sealed class OwnedPreCombatWorkerProcess(Process process) : IOwnedPreCombatWorkerProcess
{
    public int Id => process.Id;
    public bool HasExited => process.HasExited;
    public int ExitCode => process.ExitCode;
    public (long WorkingSet, long PrivateMemory, long PeakWorkingSet) CaptureMemory()
    {
        process.Refresh();
        return (process.WorkingSet64, process.PrivateMemorySize64, process.PeakWorkingSet64);
    }
    public void Kill() => process.Kill(entireProcessTree: true);
    public bool WaitForExit(int milliseconds) => process.WaitForExit(milliseconds);
    public void Dispose() => process.Dispose();
}

// Owns the protocol and process lifecycle. The outer worker serializes callers,
// validates forecast semantics and supplies ownership-checked archive cleanup.
internal sealed class PreCombatWorkerSession(
    string signature,
    IOwnedPreCombatWorkerProcess process,
    string requestPath,
    string resultPath,
    string readyPath,
    string logPath,
    bool audioMuted)
{
    private enum Phase { Ready, AwaitingResult, AwaitingReady, AwaitingCleanup, StopRequired, Stopped }
    private readonly object _sync = new();
    private readonly object _stopSync = new();
    private Phase _phase = Phase.Ready;
    private string? _requestId;
    public int ProcessId { get; } = process.Id;
    public string LogPath { get; } = logPath;
    public bool AudioMuted { get; } = audioMuted;

    public bool IsReusable
    {
        get { lock (_sync) return _phase == Phase.Ready && !process.HasExited; }
    }

    public bool CanReuse(string requestedSignature)
    {
        lock (_sync)
            return _phase == Phase.Ready
                && signature.Equals(requestedSignature, StringComparison.Ordinal)
                && !process.HasExited;
    }

    public PreCombatWorkerStatus CaptureStatus(bool busy, int? idleTimeoutMilliseconds)
    {
        lock (_sync)
        {
            bool exited = _phase == Phase.Stopped;
            try { exited |= process.HasExited; }
            catch (Exception error) when (IsProcessQueryFailure(error)) { }
            if (exited)
                return new() { IsRunning = false, IsBusy = busy, IdleTimeoutMilliseconds = idleTimeoutMilliseconds };
            // A failed status query is not exit confirmation. Keep the tracked owner
            // visible with unknown memory rather than falsely reporting it stopped.
            (long WorkingSet, long PrivateMemory, long PeakWorkingSet)? memory = null;
            try { memory = process.CaptureMemory(); }
            catch (Exception error) when (IsProcessQueryFailure(error)) { }
            return new()
            {
                IsRunning = true, IsBusy = busy, ProcessId = ProcessId,
                WorkingSetBytes = memory?.WorkingSet, PrivateMemoryBytes = memory?.PrivateMemory,
                PeakWorkingSetBytes = memory?.PeakWorkingSet, AudioMuted = AudioMuted,
                IdleTimeoutMilliseconds = idleTimeoutMilliseconds,
            };
        }
    }

    public async Task SubmitAsync(UnattendedRuntimeRequest request, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_phase != Phase.Ready || process.HasExited)
                throw new InvalidOperationException("The owned worker is not ready for another request.");
            _phase = Phase.AwaitingResult;
            _requestId = request.RunId;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteIfExists(resultPath);
            DeleteIfExists(readyPath);
            Directory.CreateDirectory(Path.GetDirectoryName(requestPath)!);
            string temporary = requestPath + ".tmp";
            string json = JsonSerializer.Serialize(request, UnattendedRuntimeProtocol.JsonOptions);
            await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, requestPath, true);
        }
        catch { MarkStopRequired(); throw; }
    }

    public async Task<UnattendedRuntimeResult> WaitForResultAsync(CancellationToken cancellationToken)
    {
        string requestId = RequirePhase(Phase.AwaitingResult);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(resultPath))
                {
                    string json = await File.ReadAllTextAsync(resultPath, cancellationToken).ConfigureAwait(false);
                    UnattendedRuntimeResult? result = JsonSerializer.Deserialize<UnattendedRuntimeResult>(
                        json, UnattendedRuntimeProtocol.JsonOptions);
                    if (result?.RunId == requestId)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Transition(Phase.AwaitingResult, Phase.AwaitingReady, requestId);
                        return result;
                    }
                }
                ThrowIfExited("returning a result");
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        catch { MarkStopRequired(); throw; }
    }

    public async Task WaitForReadyAsync(CancellationToken cancellationToken)
    {
        string requestId = RequirePhase(Phase.AwaitingReady);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(readyPath))
                {
                    string json = await File.ReadAllTextAsync(readyPath, cancellationToken).ConfigureAwait(false);
                    WorkerReady? ready = JsonSerializer.Deserialize<WorkerReady>(json, UnattendedRuntimeProtocol.JsonOptions);
                    if (ready?.RunId == requestId)
                    {
                        if (ready.Held)
                            throw new InvalidOperationException("The pre-combat worker unexpectedly retained a live search.");
                        cancellationToken.ThrowIfCancellationRequested();
                        Transition(Phase.AwaitingReady, Phase.AwaitingCleanup, requestId);
                        return;
                    }
                }
                ThrowIfExited("becoming reusable");
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        catch { MarkStopRequired(); throw; }
    }

    public void CompleteRequestCleanup(string requestId, Action cleanup)
    {
        if (RequirePhase(Phase.AwaitingCleanup) != requestId)
            throw new InvalidOperationException("Request cleanup does not belong to this worker result.");
        try
        {
            cleanup();
            Transition(Phase.AwaitingCleanup, Phase.Ready, requestId);
        }
        catch { MarkStopRequired(); throw; }
    }

    public void EnsureStopped()
    {
        // Only stop callers serialize on this lock. Status/protocol readers must
        // not wait behind a five-second OS exit confirmation on the game thread.
        lock (_stopSync)
        {
            lock (_sync)
            {
                if (_phase == Phase.Stopped) return;
                _phase = Phase.StopRequired;
            }
            try
            {
                if (!process.HasExited)
                {
                    try { process.Kill(); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    if (!process.HasExited && !process.WaitForExit(5_000) && !process.HasExited)
                        throw new TimeoutException("The owned worker did not exit within 5000 ms.");
                    if (!process.HasExited)
                        throw new TimeoutException("The owned worker exit could not be confirmed.");
                }
            }
            catch (Exception error)
            {
                // The outer worker retains this exact owner until confirmation.
                throw new PreCombatWorkerExitUnconfirmedException(
                    $"Owned worker {ProcessId} exit remains unconfirmed; its session is retained and unavailable.", error);
            }
            lock (_sync)
            {
                _phase = Phase.Stopped;
                _requestId = null;
            }
            process.Dispose(); // Exit is already confirmed even if handle disposal fails.
        }
    }

    private string RequirePhase(Phase expected)
    {
        lock (_sync)
        {
            if (_phase != expected || _requestId == null)
                throw new InvalidOperationException("The worker protocol operation is out of order.");
            return _requestId;
        }
    }

    private void Transition(Phase expected, Phase next, string requestId)
    {
        lock (_sync)
        {
            if (_phase != expected || _requestId != requestId)
                throw new InvalidOperationException("The worker request is no longer owned by this operation.");
            _phase = next;
            if (next == Phase.Ready) _requestId = null;
        }
    }

    private void MarkStopRequired()
    {
        lock (_sync)
            if (_phase != Phase.Stopped) _phase = Phase.StopRequired;
    }

    private void ThrowIfExited(string stage)
    {
        if (process.HasExited)
            throw new InvalidOperationException($"The isolated game process exited with code {process.ExitCode} before {stage}.");
    }

    private static bool IsProcessQueryFailure(Exception error)
        => error is InvalidOperationException or Win32Exception or NotSupportedException;
    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
    private sealed class WorkerReady
    {
        public int SchemaVersion { get; init; }
        public string RunId { get; init; } = string.Empty;
        public bool Held { get; init; }
    }
}
