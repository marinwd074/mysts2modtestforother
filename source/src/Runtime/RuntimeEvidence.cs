using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CombatSolver;

internal sealed record RuntimeEvidenceEvent(
    long TimestampUnixMilliseconds,
    string Kind,
    string? Label,
    string? Detail,
    int? Turn,
    int? ActionIndex,
    string? Native,
    string? Predicted,
    string? Difference);

internal sealed class RuntimeEvidenceRingBuffer(int capacity = 2048)
{
    private readonly object _gate = new();
    private readonly RuntimeEvidenceEvent?[] _items = new RuntimeEvidenceEvent?[Math.Max(32, capacity)];
    private int _next;
    private int _count;

    public void Record(
        string kind,
        string? label = null,
        string? detail = null,
        int? turn = null,
        int? actionIndex = null,
        string? native = null,
        string? predicted = null,
        string? difference = null)
    {
        RuntimeEvidenceEvent value = new(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            kind,
            Limit(label),
            Limit(detail),
            turn,
            actionIndex,
            Limit(native),
            Limit(predicted),
            Limit(difference));
        lock (_gate)
        {
            _items[_next] = value;
            _next = (_next + 1) % _items.Length;
            _count = Math.Min(_count + 1, _items.Length);
        }
    }

    public RuntimeEvidenceEvent[] Snapshot()
    {
        lock (_gate)
        {
            RuntimeEvidenceEvent[] result = new RuntimeEvidenceEvent[_count];
            int start = (_next - _count + _items.Length) % _items.Length;
            for (int index = 0; index < _count; index++)
                result[index] = _items[(start + index) % _items.Length]!;
            return result;
        }
    }

    private static string? Limit(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= 768 ? value : value[..768] + "…";
}

internal sealed record RuntimeEvidencePerformance(
    double? SearchMilliseconds,
    long? NodesExpanded,
    double? NodesPerSecond,
    int? ForkCount,
    int? ReplayCount,
    long? AllocatedBytes,
    int? Gen0Collections,
    int? Gen1Collections,
    int? Gen2Collections,
    double? GcPauseMilliseconds,
    double? MaxGcPauseMilliseconds,
    double? P95FrameGapMilliseconds,
    double? P99FrameGapMilliseconds,
    double? MaxFrameGapMilliseconds,
    long? ProcessWorkingSetBytes)
{
    internal static RuntimeEvidencePerformance From(SolverResult? result)
    {
        if (result == null)
            return new(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

        double? nodesPerSecond = result.TotalSearchElapsed.TotalSeconds > 0
            ? result.TotalExpandedNodes / result.TotalSearchElapsed.TotalSeconds
            : null;
        return new(
            result.TotalSearchElapsed.TotalMilliseconds,
            result.TotalExpandedNodes,
            nodesPerSecond,
            result.ForkCount,
            result.ReplayCount,
            result.TotalWorkerAllocatedBytes,
            result.TotalGen0Collections,
            result.TotalGen1Collections,
            result.TotalGen2Collections,
            result.TotalGcPauseDuration.TotalMilliseconds,
            result.TotalMaxObservedGcPause.TotalMilliseconds,
            result.P95MainThreadFrameGapMilliseconds,
            result.P99MainThreadFrameGapMilliseconds,
            result.MaxMainThreadFrameGapMilliseconds,
            Environment.WorkingSet);
    }
}

internal sealed record RuntimeEvidenceRun(
    string SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string EncounterId,
    string EncounterType,
    string Seed,
    string? GameVersion,
    string ModVersion,
    string EndReason,
    string FailureStage,
    bool FullCaptureCandidate,
    CombatReplayOutcomeSnapshot? Outcome,
    CombatBugReportClassificationSnapshot Classification,
    RuntimeEvidencePerformance Performance,
    RuntimeEvidenceEvent[] Events);

internal sealed record RuntimeEvidenceCapture(
    string CaptureId,
    string Fingerprint,
    int Occurrence,
    bool IsNewFingerprint,
    bool FullCaptureRequested,
    string SummaryDirectory,
    string? RawDirectory,
    string? RegressionDirectory);

internal static class RuntimeEvidenceFingerprint
{
    internal static string Compute(RuntimeEvidenceRun run)
    {
        CombatBugReportIssue[] issues = run.Classification.Issues.ToArray();
        string[] parts =
        [
            "runtime-evidence-v1",
            Normalize(run.GameVersion),
            Normalize(run.ModVersion),
            Normalize(run.EncounterId),
            Normalize(run.EncounterType),
            Normalize(run.FailureStage),
            run.Classification.StateMismatchReplans.ToString(),
            run.Classification.DeploymentDriftReplans.ToString(),
            run.Classification.ContinuationMissingReplans.ToString(),
            run.Classification.PlanExhaustedReplans.ToString(),
            run.Classification.ManualDivergenceReplans.ToString(),
            .. issues.Select(issue => $"{issue.Kind}:{Normalize(issue.Detail)}"),
            .. run.Events
                .Where(value => value.Kind is "failure" or "divergence" or "background_failure")
                .Select(value =>
                    $"{value.Kind}:{Normalize(value.Label)}:{Normalize(value.Detail)}:{Normalize(value.Difference)}"),
        ];
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))))
            .ToLowerInvariant();
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        string normalized = string.Join(' ', value.Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 512 ? normalized.ToLowerInvariant() : normalized[..512].ToLowerInvariant();
    }
}

internal static class RuntimeEvidenceStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private static Dictionary<string, int>? _knownFingerprints;

    internal static bool TryReserve(
        RuntimeEvidenceRun run,
        out RuntimeEvidenceCapture capture,
        out string? error)
    {
        try
        {
            capture = Reserve(run);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException
            or ArgumentException)
        {
            capture = null!;
            error = exception.ToString();
            return false;
        }
    }

    internal static async Task FinalizeFullCaptureAsync(
        RuntimeEvidenceCapture capture,
        RuntimeEvidenceRun run,
        Task<string> exportTask)
    {
        string? bundlePath = null;
        string? captureError = null;
        try
        {
            bundlePath = await exportTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException
            or InvalidOperationException
            or NotSupportedException)
        {
            captureError = exception.ToString();
        }

        try
        {
            if (capture.RawDirectory != null)
            {
                WriteJson(Path.Combine(capture.RawDirectory, "metadata.json"), BuildMetadata(
                    run,
                    capture,
                    "full_capture",
                    bundlePath,
                    captureError));
                WriteEvents(Path.Combine(capture.RawDirectory, "events.jsonl"), run.Events);
            }
            if (capture.RegressionDirectory != null)
            {
                WriteJson(Path.Combine(capture.RegressionDirectory, "metadata.json"), BuildMetadata(
                    run,
                    capture,
                    "regression_corpus",
                    bundlePath,
                    captureError));
                WriteEvents(Path.Combine(capture.RegressionDirectory, "events.jsonl"), run.Events);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            captureError ??= exception.ToString();
        }

        if (captureError != null)
            WriteCaptureError(capture, captureError);
    }

    private static RuntimeEvidenceCapture Reserve(RuntimeEvidenceRun run)
    {
        lock (Gate)
        {
            string root = RootDirectory();
            Directory.CreateDirectory(root);
            Dictionary<string, int> known = LoadKnownFingerprints(root);
            string fingerprint = RuntimeEvidenceFingerprint.Compute(run);
            int occurrence = known.GetValueOrDefault(fingerprint) + 1;
            bool isNew = !known.ContainsKey(fingerprint);
            known[fingerprint] = occurrence;

            string date = run.EndedAt.ToString("yyyy-MM-dd");
            string captureId = $"{run.EndedAt:yyyyMMdd-HHmmss}-{run.SessionId[..Math.Min(12, run.SessionId.Length)]}";
            string summaryDirectory = Path.Combine(root, "summaries", date, captureId);
            Directory.CreateDirectory(summaryDirectory);
            RuntimeEvidenceCapture capture = new(
                captureId,
                fingerprint,
                occurrence,
                isNew,
                run.FullCaptureCandidate && isNew,
                summaryDirectory,
                null,
                null);
            if (capture.FullCaptureRequested)
            {
                string rawDirectory = Path.Combine(root, "raw", date, captureId);
                string regressionDirectory = Path.Combine(root, "regression-corpus", fingerprint);
                Directory.CreateDirectory(rawDirectory);
                Directory.CreateDirectory(regressionDirectory);
                capture = capture with
                {
                    RawDirectory = rawDirectory,
                    RegressionDirectory = regressionDirectory,
                };
            }

            WriteJson(Path.Combine(summaryDirectory, "metadata.json"), BuildMetadata(
                run,
                capture,
                "summary",
                null,
                null));
            WriteJson(Path.Combine(summaryDirectory, "performance.json"), run.Performance);
            AppendIndex(root, run, capture);
            return capture;
        }
    }

    private static object BuildMetadata(
        RuntimeEvidenceRun run,
        RuntimeEvidenceCapture capture,
        string storageKind,
        string? bundlePath,
        string? captureError)
        => new
        {
            schemaVersion = 1,
            storageKind,
            captureId = capture.CaptureId,
            fingerprint = capture.Fingerprint,
            fingerprintOccurrence = capture.Occurrence,
            isNewFingerprint = capture.IsNewFingerprint,
            capturedAt = run.EndedAt,
            startedAt = run.StartedAt,
            sessionId = run.SessionId,
            gameVersion = run.GameVersion,
            modVersion = run.ModVersion,
            sourceCommit = Environment.GetEnvironmentVariable("COMBATSOLVER_GIT_COMMIT"),
            encounter = new { id = run.EncounterId, type = run.EncounterType },
            seed = run.Seed,
            endReason = run.EndReason,
            result = run.FullCaptureCandidate ? "failure_or_mismatch" : "success_or_observation",
            failureStage = run.FailureStage,
            fullCaptureRequested = capture.FullCaptureRequested,
            rawDirectory = capture.RawDirectory,
            regressionDirectory = capture.RegressionDirectory,
            bundlePath,
            captureError,
            outcome = run.Outcome,
            classification = run.Classification,
            performance = run.Performance,
            eventCount = run.Events.Length,
            regression = storageKind == "regression_corpus"
                ? new
                {
                    rootSnapshot = bundlePath == null
                        ? null
                        : bundlePath + "::replay/current/checkpoints/",
                    rng = new { seed = run.Seed, source = "replay/checkpoint.json and run-state" },
                    actionPrefix = run.Events
                        .Where(value => value.Kind == "checkpoint" && value.ActionIndex.HasValue)
                        .Select(value => new
                        {
                            value.TimestampUnixMilliseconds,
                            value.Label,
                            value.Turn,
                            value.ActionIndex,
                        })
                        .ToArray(),
                    expected = new
                    {
                        fingerprint = capture.Fingerprint,
                        result = run.FullCaptureCandidate ? "failure_or_mismatch" : "success_or_observation",
                        failureStage = run.FailureStage,
                    },
                    failureReason = run.EndReason,
                }
                : null,
        };

    private static void AppendIndex(string root, RuntimeEvidenceRun run, RuntimeEvidenceCapture capture)
    {
        object value = new
        {
            schemaVersion = 1,
            capturedAt = run.EndedAt,
            captureId = capture.CaptureId,
            fingerprint = capture.Fingerprint,
            occurrence = capture.Occurrence,
            isNewFingerprint = capture.IsNewFingerprint,
            storageKind = capture.FullCaptureRequested ? "raw" : "summary",
            rawDirectory = capture.RawDirectory,
            regressionDirectory = capture.RegressionDirectory,
            summaryDirectory = capture.SummaryDirectory,
            encounterId = run.EncounterId,
            gameVersion = run.GameVersion,
            modVersion = run.ModVersion,
            failureStage = run.FailureStage,
            endReason = run.EndReason,
        };
        string path = Path.Combine(root, "index.jsonl");
        File.AppendAllText(path, JsonSerializer.Serialize(value, Json) + Environment.NewLine, Encoding.UTF8);
    }

    private static Dictionary<string, int> LoadKnownFingerprints(string root)
    {
        if (_knownFingerprints != null)
            return _knownFingerprints;
        _knownFingerprints = new(StringComparer.Ordinal);
        string path = Path.Combine(root, "index.jsonl");
        if (!File.Exists(path))
            return _knownFingerprints;
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement rootElement = document.RootElement;
                string? fingerprint = rootElement.TryGetProperty("fingerprint", out JsonElement value)
                    ? value.GetString()
                    : null;
                int occurrence = rootElement.TryGetProperty("occurrence", out JsonElement count)
                    ? count.GetInt32()
                    : 0;
                if (!string.IsNullOrWhiteSpace(fingerprint))
                    _knownFingerprints[fingerprint] = Math.Max(
                        _knownFingerprints.GetValueOrDefault(fingerprint), occurrence);
            }
            catch (JsonException)
            {
                // Keep valid historical entries when an interrupted append leaves a bad tail line.
            }
        }
        return _knownFingerprints;
    }

    private static void WriteJson(string path, object value)
        => File.WriteAllText(path, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));

    private static void WriteEvents(string path, IReadOnlyList<RuntimeEvidenceEvent> events)
    {
        StringBuilder output = new();
        foreach (RuntimeEvidenceEvent value in events)
        {
            output.Append(JsonSerializer.Serialize(value, Json));
            output.AppendLine();
        }
        File.WriteAllText(path, output.ToString(), new UTF8Encoding(false));
    }

    private static void WriteCaptureError(RuntimeEvidenceCapture capture, string error)
    {
        if (capture.RawDirectory == null)
            return;
        try
        {
            File.WriteAllText(
                Path.Combine(capture.RawDirectory, "capture-error.txt"),
                error,
                new UTF8Encoding(false));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string RootDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("COMBATSOLVER_LAB_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);
        if (Directory.Exists("D:\\"))
            return @"D:\CombatSolverLab";
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return string.IsNullOrWhiteSpace(desktop)
            ? Path.Combine(Path.GetTempPath(), "CombatSolverLab")
            : Path.Combine(desktop, "CombatSolverLab");
    }
}
