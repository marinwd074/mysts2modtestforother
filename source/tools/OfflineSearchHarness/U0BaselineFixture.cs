using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace OfflineSearchHarness;

/// <summary>
/// U0 diagnostic fixtures. These helpers never invent card effects: fixed teammate scripts are
/// replayed through the same ShadowTeammatePlanner path used by production joint prediction.
/// </summary>
internal static class U0BaselineFixture
{
    internal static readonly IReadOnlyList<ShadowTeammateActionCandidate> NoTeammateEvents =
        Array.Empty<ShadowTeammateActionCandidate>();

    internal static StateFingerprint ReplayNoTeammateEvents(
        CombatPredictionSimulator simulator,
        ISet<uint> processedEnemyDeaths)
    {
        HashSet<string> endedPlayers = new(StringComparer.Ordinal);
        StateFingerprint before = ShadowFutureStateFingerprint.Capture(
            simulator,
            processedEnemyDeaths,
            endedPlayers,
            NoTeammateEvents);

        if (!ShadowTeammatePlanner.ReplayForecastActions(
                simulator,
                NoTeammateEvents,
                processedEnemyDeaths))
        {
            throw new InvalidOperationException(
                "U0 no-teammate-event replay unexpectedly failed.");
        }

        StateFingerprint after = ShadowFutureStateFingerprint.Capture(
            simulator,
            processedEnemyDeaths,
            endedPlayers,
            NoTeammateEvents);
        if (after != before)
        {
            throw new InvalidOperationException(
                $"U0 no-teammate-event replay changed modeled state: " +
                $"before={before.First:X16}:{before.Second:X16} " +
                $"after={after.First:X16}:{after.Second:X16}.");
        }

        return after;
    }

    internal static IReadOnlyList<ShadowTeammateActionCandidate> ReplayFixedTeammateScript(
        CombatPredictionSimulator simulator,
        IReadOnlyList<ShadowTeammateActionCandidate> script,
        ISet<uint> processedEnemyDeaths)
    {
        ArgumentNullException.ThrowIfNull(script);

        ShadowTeammateActionCandidate[] frozen = script
            .Select(action => action with { })
            .ToArray();
        for (int index = 0; index < frozen.Length; index++)
        {
            ShadowTeammateActionCandidate action = frozen[index];
            if (string.IsNullOrWhiteSpace(action.PlayerNetId)
                || string.IsNullOrWhiteSpace(action.CardId)
                || string.IsNullOrWhiteSpace(action.SemanticKey)
                || action.HandIndex < 0)
            {
                throw new InvalidOperationException(
                    $"U0 teammate script contains an invalid action at index {index}.");
            }
        }

        if (!ShadowTeammatePlanner.ReplayForecastActions(
                simulator,
                frozen,
                processedEnemyDeaths))
        {
            throw new InvalidOperationException(
                "U0 fixed teammate script is not replayable from this detached root.");
        }

        return Array.AsReadOnly(frozen);
    }

    internal static string DescribeFixedTeammateScript(
        IReadOnlyList<ShadowTeammateActionCandidate> script)
        => string.Join(
            ";",
            script.Select((action, index) =>
                $"{index}:{action.PlayerNetId}:{action.HandIndex}:{action.CardId}+" +
                $"{action.UpgradeLevel}:{action.SemanticKey}:target=" +
                $"{action.TargetCombatId?.ToString() ?? "-"}"));
}
