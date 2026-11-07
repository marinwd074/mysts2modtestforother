using System.Text.Json;
using CombatSolver;

// Enumerate causal event schedules, not OS threads: both production contracts
// are main-thread-only. No game, sockets, wall-clock sleeps or mock game models.
int? replay = args.Length == 2 && args[0] == "--replay" ? int.Parse(args[1]) : null;
if (args.Length != 0 && replay == null)
    throw new ArgumentException("Usage: [--replay <schedule-id>]");
int scheduleId = 0, executed = 0;
int staleConfirmations = 0, acceptedConfirmations = 0, canceledSessions = 0;
foreach (int peers in new[] { 1, 2, 3 })
foreach (int stepMs in new[] { 0, 199, 200, 201 })
{
    var chains = Enumerable.Range(1, peers)
        .Select(peer => new[] { $"peer-{peer}-change", $"peer-{peer}-duplicate" })
        .Prepend(new[] { "read", "confirm" }).ToArray();
    foreach (string[] events in Interleave(chains))
        Run($"observation peers={peers} stepMs={stepMs}", events,
            () => CheckObservation(events, stepMs));
}

foreach (string disturbance in new[] { "cancel", "turn-change", "route-change", "remote-change" })
foreach (string[] events in Interleave(new[] {
    new[] { "begin", "native-complete", "revalidate", "accept", "next", "end-turn" },
    new[] { disturbance }
}))
    Run($"execution disturbance={disturbance}", events, () => CheckExecution(events));

Run("independent observers and local authority", new[] { "observe", "reset-one", "remote-card", "remote-potion" }, CheckIsolation);
Run("optional search under route churn", new[] { "isolated", "burst", "adjacent-turn", "quiet" }, () =>
{
    var churn = new MultiplayerSearchChurn();
    churn.RecordInvalidation(1);
    Require(!churn.ShouldDeferOptionalWork(1), "An isolated invalidation keeps optional work eligible.");
    churn.RecordInvalidation(1);
    Require(churn.ShouldDeferOptionalWork(1) && churn.ShouldDeferOptionalWork(2),
        "A burst defers optional work into the following turn.");
    churn.RecordInvalidation(2);
    churn.RecordInvalidation(1);
    Require(churn.ShouldDeferOptionalWork(3) && !churn.ShouldDeferOptionalWork(4),
        "Consecutive invalidated turns defer work, then one quiet turn clears it.");
    churn.RecordInvalidation(4);
    Require(!churn.ShouldDeferOptionalWork(4), "A separated event does not resurrect older churn.");
});
if (executed == 0)
    throw new ArgumentOutOfRangeException(nameof(replay), $"Schedule must be 1..{scheduleId}.");
if (replay == null)
    Require(staleConfirmations > 0 && acceptedConfirmations > 0 && canceledSessions > 0,
        "Schedule exploration must reach both confirmation outcomes and cancellation.");
Console.WriteLine($"MULTIPLAYER_INTERLEAVING_OK schedules={executed} staleConfirmations={staleConfirmations} acceptedConfirmations={acceptedConfirmations} canceledSessions={canceledSessions}");

void Run(string scenario, string[] events, Action body)
{
    int id = ++scheduleId;
    if (replay is int selected && selected != id) return;
    try { body(); executed++; }
    catch (Exception error)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new {
            scheduleId = id, scenario, events, error = error.Message,
            replay = $"dotnet run --project tools/MultiplayerInterleavingChecks -c Release -- --replay {id}"
        }));
        throw;
    }
}

void CheckObservation(string[] events, int stepMs)
{
    var observer = new MultiplayerWorldObservation();
    observer.ObserveSnapshot(new(0, 0), "root", 0);
    long now = 200, lastChangeAt = 0, changes = 1;
    ulong visiblePeers = 0;
    long? ticket = null;
    bool pending = true;
    foreach (string item in events)
    {
        now += stepMs;
        if (item.StartsWith("peer-"))
        {
            int peer = item[5] - '0';
            bool change = item.EndsWith("change");
            visiblePeers |= 1UL << peer;
            Require(observer.ObserveSnapshot(new(visiblePeers, 0), item, now) == change,
                "Repeated visible snapshot must neither advance version nor restart debounce.");
            if (change) { changes++; lastChangeAt = now; pending = true; }
        }
        else if (item == "read")
        {
            bool read = observer.TryReadStable(now, out long version);
            Require(read == (pending && now - lastChangeAt >= 200), "Reader must honor quiet interval.");
            if (read) ticket = version;
        }
        else if (ticket is long readVersion)
        {
            bool accepted = observer.TryConfirmStable(readVersion, now);
            bool expected = readVersion == changes && pending && now - lastChangeAt >= 200;
            Require(accepted == expected, "A reader cannot consume an observation that arrived after its read.");
            if (accepted) { pending = false; acceptedConfirmations++; }
            else staleConfirmations++;
        }
        Require(observer.WorldVersion == changes && observer.IsDirty == pending,
            "Versions count actual changes; failed or read-only operations preserve pending work.");
        Require(!observer.IsStable(changes - 1, now), "An older version never becomes stable again.");
    }
    now = Math.Max(now, lastChangeAt + 199);
    if (now == lastChangeAt + 199 && pending)
        Require(!observer.TryTakeStable(now, out _), "199 ms is still inside debounce.");
    now = Math.Max(now, lastChangeAt + 200);
    Require(observer.TryTakeStable(now, out long finalVersion) == pending && finalVersion == changes,
        "Newest pending observation must remain deliverable after all interference.");
    Require(!observer.TryTakeStable(now, out _) && observer.IsStable(changes, now),
        "Stable observation is consumed once and remains usable by EndTurn.");
}

void CheckExecution(string[] events)
{
    var session = new MultiplayerSafeExecutionSession(5, 7, 1, 2);
    int turn = 5, generation = 7;
    long world = 1;
    bool canceled = false, begun = false;
    foreach (string item in events)
    {
        switch (item)
        {
            case "cancel": session.Abort("scheduled_cancel"); canceled = true; canceledSessions++; break;
            case "turn-change": turn++; break;
            case "route-change": generation++; break;
            case "remote-change": world++; break;
            case "begin":
                begun = session.TryBeginAction(0, "local-card", turn, generation, world, out _);
                Require(begun == (!canceled && turn == 5 && generation == 7 && world == 1),
                    "Initial authorization belongs to exactly one turn, route and observed world.");
                break;
            case "native-complete":
                session.MarkAwaitingWorldUpdate();
                world++;
                break;
            case "revalidate": session.BeginRevalidation(); break;
            case "accept":
                // Runtime checks lifecycle before accepting the completion. Model its
                // explicit cancellation, not a second implementation of its state machine.
                if (turn != 5 || generation != 7) session.Abort("identity_changed");
                bool accepted = session.AcceptAction(world, true);
                Require(accepted == (begun && !canceled && turn == 5 && generation == 7),
                    "Only the authorized native completion can advance this session.");
                Require(!session.AcceptAction(world, true), "Duplicate completion cannot accept an action twice.");
                break;
            case "next":
                bool next = session.TryBeginAction(1, "next-card", turn, generation, world, out _);
                if (next)
                {
                    Require(!canceled && turn == 5 && generation == 7 && session.CompletedActions == 1,
                        "Continuation requires surviving original authority.");
                    Require(!session.TryBeginAction(1, "next-card", turn, generation, world, out _),
                        "One action cannot begin twice.");
                    session.MarkAwaitingWorldUpdate(); session.BeginRevalidation();
                    session.AcceptAction(++world, false);
                }
                break;
            case "end-turn":
                bool end = session.TryBeginEndTurn(turn, generation, world, out _);
                if (end)
                {
                    Require(!canceled && turn == 5 && generation == 7 && session.CompletedActions == 2,
                        "EndTurn cannot reuse expired route authority.");
                    session.CompleteEndTurn();
                    Require(!session.TryBeginEndTurn(turn, generation, world, out _),
                        "EndTurn authorization is consumed once.");
                }
                break;
        }
        if (canceled)
            Require(session.State == MultiplayerSafeExecutionState.Aborted && session.ExpectedActionToken == null,
                "Late native callbacks must never revive a canceled request.");
        Require(session.CompletedActions <= 2, "No schedule can exceed the authorized action count.");
    }
}

void CheckIsolation()
{
    var observers = Enumerable.Range(0, 4).Select(_ => new MultiplayerWorldObservation()).ToArray();
    for (int i = 0; i < observers.Length; i++) observers[i].ObserveSnapshot(new((ulong)i, 0), "root", 0);
    observers[0].Reset();
    Require(observers[0].WorldVersion == 0 && !observers[0].IsDirty, "Reset clears only the owning observer.");
    foreach (var observer in observers.Skip(1))
        Require(observer.TryTakeStable(200, out long version) && version == 1, "Peer state must be instance-owned.");
    Require(!MultiplayerSafeExecutePolicy.ClassifyResolved(new(true, false, false, false, false, false, false)).IsSafe,
        "Readable remote cards do not acquire local execution authority.");
    Require(!MultiplayerSafeExecutePolicy.ClassifyResolved(new(true, false, false, false, false, false, false,
        IsUsePotion: true, HasLocalPotion: false, PotionIdentityMatches: true, PotionTargetValid: true)).IsSafe,
        "Readable remote potions do not acquire local execution authority.");
}

static void Require(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
}

static IEnumerable<string[]> Interleave(string[][] chains)
{
    int[] offsets = new int[chains.Length];
    var path = new List<string>();
    return Visit();
    IEnumerable<string[]> Visit()
    {
        if (path.Count == chains.Sum(chain => chain.Length)) { yield return path.ToArray(); yield break; }
        for (int i = 0; i < chains.Length; i++)
        {
            if (offsets[i] == chains[i].Length) continue;
            path.Add(chains[i][offsets[i]++]);
            foreach (var result in Visit()) yield return result;
            offsets[i]--; path.RemoveAt(path.Count - 1);
        }
    }
}
