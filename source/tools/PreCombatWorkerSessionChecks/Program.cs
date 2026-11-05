using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Api;

if (args is ["--owned-child"])
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}
int passed = 0;
await Check("fresh session is reusable only for its exact signature", () =>
{
    using Fixture f = new();
    Require(f.Session.CanReuse("signature") && !f.Session.CanReuse("other"));
    f.Process.Exited = true;
    Require(!f.Session.IsReusable);
    return Task.CompletedTask;
});
await Check("submission clears old outputs and atomically writes the matching request", async () =>
{
    using Fixture f = new();
    f.Result("old"); f.Ready("old");
    await f.Submit();
    Require(!File.Exists(f.ResultPath) && !File.Exists(f.ReadyPath) && !f.Session.IsReusable);
    Require(JsonSerializer.Deserialize<UnattendedRuntimeRequest>(File.ReadAllText(f.RequestPath),
        UnattendedRuntimeProtocol.JsonOptions)?.RunId == "request");
    Require(!File.Exists(f.RequestPath + ".tmp"));
});
await Check("result then matching ready and cleanup are all required for reuse", async () =>
{
    using Fixture f = new();
    await f.Submit(); f.Result();
    Require((await f.Session.WaitForResultAsync(CancellationToken.None)).RunId == "request");
    Require(!f.Session.IsReusable);
    f.Ready(); await f.Session.WaitForReadyAsync(CancellationToken.None);
    Require(!f.Session.IsReusable);
    bool cleaned = false;
    f.Session.CompleteRequestCleanup("request", () => cleaned = true);
    Require(cleaned && f.Session.CanReuse("signature"));
    await f.Session.SubmitAsync(new() { RunId = "next" }, CancellationToken.None);
    Require(!f.Session.IsReusable);
});
await Check("stale result ID cannot complete the active request", async () =>
{
    using Fixture f = new(); await f.Submit(); f.Result("old");
    Task<UnattendedRuntimeResult> pending = f.Session.WaitForResultAsync(CancellationToken.None);
    Require(!pending.IsCompleted && !f.Session.IsReusable);
    f.Result(); Require((await pending.WaitAsync(TimeSpan.FromSeconds(3))).RunId == "request");
});
await Check("ready is rejected before a matching result", async () =>
{
    using Fixture f = new(); await f.Submit(); f.Ready();
    await Throws<InvalidOperationException>(() => f.Session.WaitForReadyAsync(CancellationToken.None));
    Require(!f.Session.IsReusable);
});
await Check("stale ready ID cannot make a completed result reusable", async () =>
{
    using Fixture f = new(); await f.Submit(); f.Result();
    await f.Session.WaitForResultAsync(CancellationToken.None); f.Ready("old");
    Task pending = f.Session.WaitForReadyAsync(CancellationToken.None);
    Require(!pending.IsCompleted && !f.Session.IsReusable);
    f.Ready(); await pending.WaitAsync(TimeSpan.FromSeconds(3));
    f.Session.CompleteRequestCleanup("request", () => { }); Require(f.Session.IsReusable);
});
await Check("held ready fails closed and prevents reuse", async () =>
{
    using Fixture f = new(); await f.Submit(); f.Result();
    await f.Session.WaitForResultAsync(CancellationToken.None); f.Ready(held: true);
    await Throws<InvalidOperationException>(() => f.Session.WaitForReadyAsync(CancellationToken.None));
    Require(!f.Session.IsReusable);
});
await Check("cancelled result wait leaves the session unavailable", async () =>
{
    using Fixture f = new(); await f.Submit();
    await Throws<OperationCanceledException>(() => f.Session.WaitForResultAsync(new CancellationToken(true)));
    Require(!f.Session.IsReusable);
});
await Check("cancelled ready wait leaves the session unavailable", async () =>
{
    using Fixture f = new(); await f.Submit(); f.Result();
    await f.Session.WaitForResultAsync(CancellationToken.None);
    await Throws<OperationCanceledException>(() => f.Session.WaitForReadyAsync(new CancellationToken(true)));
    Require(!f.Session.IsReusable);
});
await Check("malformed protocol result fails closed", async () =>
{
    using Fixture f = new(); await f.Submit(); File.WriteAllText(f.ResultPath, "not-json");
    await Throws<JsonException>(() => f.Session.WaitForResultAsync(CancellationToken.None));
    Require(!f.Session.IsReusable);
});
await Check("process exit before result is reported", async () =>
{
    using Fixture f = new(); await f.Submit(); f.Process.Exited = true;
    await Throws<InvalidOperationException>(() => f.Session.WaitForResultAsync(CancellationToken.None));
    Require(!f.Session.IsReusable);
});
await Check("process exit before ready is reported", async () =>
{
    using Fixture f = new(); await f.Submit(); f.Result();
    await f.Session.WaitForResultAsync(CancellationToken.None); f.Process.Exited = true;
    await Throws<InvalidOperationException>(() => f.Session.WaitForReadyAsync(CancellationToken.None));
});
await Check("cleanup cannot run for a different or incomplete request", async () =>
{
    using Fixture f = new(); await f.Submit(); bool cleaned = false;
    await Throws<InvalidOperationException>(() => { f.Session.CompleteRequestCleanup("request", () => cleaned = true); return Task.CompletedTask; });
    f.Result(); await f.Session.WaitForResultAsync(CancellationToken.None);
    f.Ready(); await f.Session.WaitForReadyAsync(CancellationToken.None);
    await Throws<InvalidOperationException>(() => { f.Session.CompleteRequestCleanup("other", () => cleaned = true); return Task.CompletedTask; });
    Require(!cleaned && !f.Session.IsReusable);
});
await Check("cleanup failure keeps the session unavailable", async () =>
{
    using Fixture f = new(); await f.Submit(); f.Result();
    await f.Session.WaitForResultAsync(CancellationToken.None); f.Ready();
    await f.Session.WaitForReadyAsync(CancellationToken.None);
    await Throws<IOException>(() => { f.Session.CompleteRequestCleanup("request", () => throw new IOException("cleanup")); return Task.CompletedTask; });
    Require(!f.Session.IsReusable);
});
await Check("confirmed stop disposes the exact owned process once", () =>
{
    using Fixture f = new(); f.Process.ExitOnKill = false; f.Process.ExitOnWait = true;
    f.Session.EnsureStopped(); f.Session.EnsureStopped();
    Require(f.Process.Kills == 1 && f.Process.Waits == 1 && f.Process.LastWait == 5_000 && f.Process.Disposals == 1);
    Require(!f.Session.CaptureStatus(false, null).IsRunning && !f.Session.IsReusable);
    return Task.CompletedTask;
});
await Check("stop timeout retains ownership and never reports exited or reusable", async () =>
{
    using Fixture f = new(); f.Process.ExitOnKill = false; f.Process.ExitOnWait = false;
    await Throws<PreCombatWorkerExitUnconfirmedException>(() => { f.Session.EnsureStopped(); return Task.CompletedTask; });
    Require(!f.Session.IsReusable && f.Process.Disposals == 0);
    PreCombatWorkerStatus status = f.Session.CaptureStatus(false, 120_000);
    Require(status.IsRunning && status.ProcessId == f.Process.Id);
    f.Process.Exited = true; f.Session.EnsureStopped();
    Require(f.Process.Disposals == 1 && !f.Session.CaptureStatus(false, null).IsRunning);
});
await Check("kill failure retains ownership without pretending the worker stopped", async () =>
{
    using Fixture f = new(); f.Process.KillError = new Win32Exception(5);
    await Throws<PreCombatWorkerExitUnconfirmedException>(() => { f.Session.EnsureStopped(); return Task.CompletedTask; });
    Require(f.Session.CaptureStatus(false, null).IsRunning && !f.Session.IsReusable && f.Process.Disposals == 0);
});
await Check("failed status queries keep tracked ownership visible with unknown memory", () =>
{
    using Fixture f = new(); f.Process.QueryError = true;
    PreCombatWorkerStatus status = f.Session.CaptureStatus(true, null);
    Require(status.IsRunning && status.IsBusy && status.ProcessId == f.Process.Id && status.WorkingSetBytes == null);
    return Task.CompletedTask;
});
await Check("status stays responsive while OS stop confirmation is pending", async () =>
{
    using Fixture f = new();
    using ManualResetEventSlim reached = new(false), resume = new(false);
    f.Process.ExitOnKill = false;
    f.Process.OnWait = () => { reached.Set(); Require(resume.Wait(TimeSpan.FromSeconds(3))); };
    Task stop = Task.Run(f.Session.EnsureStopped);
    try
    {
        Require(reached.Wait(TimeSpan.FromSeconds(2)));
        Task<PreCombatWorkerStatus> status = Task.Run(() => f.Session.CaptureStatus(false, null));
        Require((await status.WaitAsync(TimeSpan.FromSeconds(1))).IsRunning && !f.Session.IsReusable);
    }
    finally { resume.Set(); await stop; }
});
await Check("real process adapter stops only its exact owned child", () =>
{
    using Process first = StartChild(); using Process second = StartChild();
    string root = Path.Combine(Path.GetTempPath(), "combatsolver-session-real-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        PreCombatWorkerSession session = new("real", new OwnedPreCombatWorkerProcess(first),
            Path.Combine(root,"request.json"), Path.Combine(root,"result.json"), Path.Combine(root,"ready.json"), "test.log", true);
        int ownedId = session.ProcessId;
        Require(ownedId != second.Id);
        session.EnsureStopped();
        Require(!session.CaptureStatus(false, null).IsRunning && !second.HasExited);
    }
    finally
    {
        StopTestChild(first); StopTestChild(second);
        Directory.Delete(root, recursive: true);
    }
    return Task.CompletedTask;
});
await Check("outer explicit stop retains the tracked session until exit is confirmed", async () =>
{
    using Fixture f = new();
    await WithTracked(f, async () =>
    {
        f.Process.ExitOnKill = false; f.Process.ExitOnWait = false;
        await Throws<PreCombatWorkerExitUnconfirmedException>(PreCombatForecastWorker.StopSessionAsync);
        Require(ReferenceEquals(Tracked(), f.Session) && PreCombatForecastWorker.GetStatus().IsRunning);
        f.Process.Exited = true; await PreCombatForecastWorker.StopSessionAsync();
        Require(Tracked() == null);
    });
});
await Check("outer restart cannot replace an unconfirmed owned process", async () =>
{
    using Fixture f = new();
    await WithTracked(f, async () =>
    {
        f.Process.ExitOnKill = false; f.Process.ExitOnWait = false;
        int starts = PreCombatForecastWorker.WorkerStartCountForTesting;
        await Throws<PreCombatWorkerExitUnconfirmedException>(() => PreCombatForecastWorker.RestartSessionAsync(
            new() { GameRoot = f.Root }, null, CancellationToken.None));
        Require(ReferenceEquals(Tracked(), f.Session) && PreCombatForecastWorker.WorkerStartCountForTesting == starts);
    });
});
await Check("outer new request cannot replace an unconfirmed owned process", async () =>
{
    using Fixture f = new();
    await WithTracked(f, async () =>
    {
        f.Process.ExitOnKill = false; f.Process.ExitOnWait = false;
        await Throws<PreCombatWorkerExitUnconfirmedException>(() => { f.Session.EnsureStopped(); return Task.CompletedTask; });
        int starts = PreCombatForecastWorker.WorkerStartCountForTesting;
        await Throws<PreCombatWorkerExitUnconfirmedException>(() => PreCombatForecastWorker.RunAsync(
            new() { GameRoot = f.Root }, "test", 1, 0, PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal,
            false, new(), CancellationToken.None));
        Require(ReferenceEquals(Tracked(), f.Session) && PreCombatForecastWorker.WorkerStartCountForTesting == starts
            && !PreCombatForecastWorker.GetStatus().IsBusy);
    });
});
await Check("outer cache-close reports failure while retaining ownership", async () =>
{
    using Fixture f = new();
    await WithTracked(f, async () =>
    {
        f.Process.ExitOnKill = false; f.Process.ExitOnWait = false;
        await Throws<PreCombatWorkerExitUnconfirmedException>(() =>
            PreCombatForecastWorker.ApplyCachedRequestLifetimeAsync(true, null, CancellationToken.None));
        Require(ReferenceEquals(Tracked(), f.Session));
    });
});
await Check("outer idle-stop failure retains the same session", async () =>
{
    using Fixture f = new();
    await WithTracked(f, async () =>
    {
        f.Process.ExitOnKill = false; f.Process.ExitOnWait = false;
        TaskCompletionSource logged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Entry.Logger.ErrorSink = _ => logged.TrySetResult();
        try
        {
            await PreCombatForecastWorker.ConfigureIdleTimeoutAsync(1);
            await logged.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(ReferenceEquals(Tracked(), f.Session) && PreCombatForecastWorker.GetStatus().IsRunning);
        }
        finally { Entry.Logger.ErrorSink = null; }
    });
});
await Check("outer process exit keeps runtime directories after an unconfirmed stop", async () =>
{
    using Fixture f = new();
    await WithTracked(f, () =>
    {
        f.Process.ExitOnKill = false; f.Process.ExitOnWait = false;
        string runtime = Path.Combine(f.Root, "runtime"); Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(runtime, ".combatsolver-precombat-owner"), "CombatSolver PreCombat API v1");
        foreach (string name in new[] { "game", "startup-mods", "requests" }) Directory.CreateDirectory(Path.Combine(runtime, name));
        WorkerField("_ownedRuntimeRoot").SetValue(null, runtime);
        PreCombatForecastWorker.StopSessionAtProcessExit();
        Require(ReferenceEquals(Tracked(), f.Session) && Directory.Exists(Path.Combine(runtime, "game")));
        return Task.CompletedTask;
    });
});
await Check("completed idle cancellation cannot skip explicit or process-exit stop", async () =>
{
    foreach (bool exiting in new[] { false, true })
    {
        using Fixture f = new();
        await WithTracked(f, async () =>
        {
            CancellationTokenSource completedIdle = new(); completedIdle.Dispose();
            WorkerField("_idleShutdown").SetValue(null, completedIdle);
            if (exiting) PreCombatForecastWorker.StopSessionAtProcessExit();
            else await PreCombatForecastWorker.StopSessionAsync();
            Require(Tracked() == null && f.Process.Exited && f.Process.Disposals == 1);
        });
    }
});
Console.WriteLine($"PreCombatWorkerSessionChecks PASS ({passed} linked-production lifecycle contracts; one real owned-process adapter test)");

async Task Check(string name, Func<Task> body)
{
    await body().WaitAsync(TimeSpan.FromSeconds(8));
    passed++; Console.WriteLine("PASS " + name);
}
static async Task Throws<T>(Func<Task> operation) where T : Exception
{
    try { await operation(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Contract failed"); }
static void StopTestChild(Process process)
{
    try { if (!process.HasExited) { process.Kill(entireProcessTree: true); Require(process.WaitForExit(5_000)); } }
    catch (InvalidOperationException) { /* The confirmed owned session already disposed its exact handle. */ }
}
static FieldInfo WorkerField(string name) => typeof(PreCombatForecastWorker).GetField(name,
    BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException(name);
static PreCombatWorkerSession? Tracked() => (PreCombatWorkerSession?)WorkerField("_session").GetValue(null);
static async Task WithTracked(Fixture fixture, Func<Task> body)
{
    Require(Tracked() == null);
    object? originalRoot = WorkerField("_ownedRuntimeRoot").GetValue(null);
    WorkerField("_session").SetValue(null, fixture.Session);
    try { await body(); }
    finally
    {
        fixture.Process.QueryError = false; fixture.Process.KillError = null; fixture.Process.Exited = true;
        await PreCombatForecastWorker.StopSessionAsync();
        WorkerField("_shuttingDown").SetValue(null, false);
        WorkerField("_ownedRuntimeRoot").SetValue(null, originalRoot);
    }
}
static Process StartChild()
{
    string host = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test process path");
    ProcessStartInfo start = new(host) { UseShellExecute = false, CreateNoWindow = true };
    if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--owned-child");
    return Process.Start(start) ?? throw new InvalidOperationException("Test child did not start");
}
sealed class Fixture : IDisposable
{
    public FakeProcess Process { get; } = new();
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "combatsolver-session-" + Guid.NewGuid().ToString("N"));
    public string RequestPath => Path.Combine(Root, "request.json");
    public string ResultPath => Path.Combine(Root, "result.json");
    public string ReadyPath => Path.Combine(Root, "ready.json");
    public PreCombatWorkerSession Session { get; }
    public Fixture()
    {
        Directory.CreateDirectory(Root);
        Session = new("signature", Process, RequestPath, ResultPath, ReadyPath, "test.log", true);
    }
    public Task Submit() => Session.SubmitAsync(new() { RunId = "request" }, CancellationToken.None);
    public void Result(string id = "request") => WriteAtomically(ResultPath,
        JsonSerializer.Serialize(new UnattendedRuntimeResult { RunId = id, Status = "Passed" }, UnattendedRuntimeProtocol.JsonOptions));
    public void Ready(string id = "request", bool held = false) => WriteAtomically(ReadyPath,
        JsonSerializer.Serialize(new { SchemaVersion = 1, RunId = id, Held = held }, UnattendedRuntimeProtocol.JsonOptions));
    private static void WriteAtomically(string path, string text)
    {
        File.WriteAllText(path + ".tmp", text);
        File.Move(path + ".tmp", path, true); // Same publication rule as the real protocol host.
    }
    public void Dispose()
    {
        Process.QueryError = false; Process.KillError = null; Process.Exited = true;
        Session.EnsureStopped(); Directory.Delete(Root, recursive: true);
    }
}
sealed class FakeProcess : IOwnedPreCombatWorkerProcess
{
    public int Id => 12345;
    public bool Exited;
    public bool QueryError;
    public bool ExitOnKill = true;
    public bool ExitOnWait = true;
    public Exception? KillError;
    public int Kills, Waits, Disposals, LastWait;
    public Action? OnWait;
    public bool HasExited => QueryError ? throw new Win32Exception(5) : Exited;
    public int ExitCode => 7;
    public (long WorkingSet, long PrivateMemory, long PeakWorkingSet) CaptureMemory()
        => QueryError ? throw new Win32Exception(5) : (10, 20, 30);
    public void Kill() { Kills++; if (KillError != null) throw KillError; if (ExitOnKill) Exited = true; }
    public bool WaitForExit(int milliseconds) { Waits++; LastWait = milliseconds; OnWait?.Invoke(); if (ExitOnWait) Exited = true; return Exited; }
    public void Dispose() => Disposals++;
}
