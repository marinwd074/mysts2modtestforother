namespace CombatSolver;

/// <summary>
/// Lightweight counters for Rolling Horizon reuse classification.
/// This is intentionally independent from search quality logic; callers can record
/// reuse decisions without changing continuation behaviour.
/// </summary>
internal sealed class RollingHorizonReuseTelemetry
{
    public int ReuseAttempt { get; private set; }
    public int ReuseAccepted { get; private set; }
    public int ReuseRejected { get; private set; }
    public int FullSearchTriggered { get; private set; }

    public Dictionary<string, int> RejectReasons { get; } = new(StringComparer.Ordinal);

    public void RecordAttempt()
        => ReuseAttempt++;

    public void RecordAccepted()
        => ReuseAccepted++;

    public void RecordRejected(string reason)
    {
        ReuseRejected++;
        RejectReasons[reason] = RejectReasons.GetValueOrDefault(reason) + 1;
    }

    public void RecordFullSearch(string reason)
    {
        FullSearchTriggered++;
        RejectReasons[$"full_search:{reason}"] =
            RejectReasons.GetValueOrDefault($"full_search:{reason}") + 1;
    }

    public string Describe()
        => $"attempt={ReuseAttempt} accepted={ReuseAccepted} rejected={ReuseRejected} full_search={FullSearchTriggered}";
}
