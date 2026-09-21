using System.Security;
using System.Text.Json;
using CombatSolver;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
T? Caught<T>(Exception error) where T : class
{
    string? log = null;
    T? result = AncillaryWork.Try<T>("TEST", () => throw error, text => log = text);
    Check(result is null && log?.Contains("TEST error=", StringComparison.Ordinal) == true,
        "supported ancillary failure was not isolated");
    return result;
}
Caught<object>(new IOException("io"));
Caught<object>(new UnauthorizedAccessException("denied"));
Caught<object>(new SecurityException("security"));
Caught<object>(new JsonException("json"));
Caught<object>(new InvalidDataException("data"));
Caught<object>(new NotSupportedException("unsupported"));

bool ran = false;
AncillaryWork.Run("RUN", () => throw new IOException("run"), _ => ran = true);
Check(ran, "Run did not isolate supported failure.");

try
{
    AncillaryWork.Try<object>("BUG", () => throw new InvalidOperationException("bug"), _ => { });
    throw new InvalidOperationException("programming error was swallowed");
}
catch (InvalidOperationException error) when (error.Message == "bug")
{
    checks++;
}

try
{
    AncillaryWork.Try<object>("CANCEL", () => throw new OperationCanceledException("cancel"), _ => { });
    throw new InvalidOperationException("cancellation was swallowed");
}
catch (OperationCanceledException)
{
    checks++;
}

Console.WriteLine($"ANCILLARY_WORK_CHECKS_PASS checks={checks}");
