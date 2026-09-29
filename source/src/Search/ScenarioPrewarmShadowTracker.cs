using System.Runtime.CompilerServices;

namespace CombatSolver;

internal readonly record struct ScenarioPrewarmRootObservation(
    string Status,
    long RootEpoch,
    int PredictionCount,
    ShadowTeammateScenarioKind? MatchedScenarioKind,
    int ExactMatches,
    int Misses,
    int DuplicateStores,
    int StaleStores,
    int DroppedStores);

/// <summary>
/// Phase F0 shadow-only measurement. Experimental scenario reevaluation may record exact
/// predicted future continuation states; the next distinct live root only measures whether
/// one matched exactly. No search node, score, action or deployment authority is reused.
/// </summary>
internal sealed class ScenarioPrewarmShadowTracker
{
    private const int MaximumPredictionsPerRoot = 64;
    private static readonly ConditionalWeakTable<CombatTransitionMemo, ScenarioPrewarmShadowTracker>
        Owners = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, Prediction> _predictions = new(StringComparer.Ordinal);
    private string? _combatIdentity;
    private string? _currentRootStateText;
    private long _rootEpoch;
    private int _exactMatches;
    private int _misses;
    private int _duplicateStores;
    private int _staleStores;
    private int _droppedStores;

    private sealed record Prediction(
        string DecisionKey,
        ShadowTeammateScenarioKind ScenarioKind);

    internal static ScenarioPrewarmShadowTracker For(CombatTransitionMemo owner)
        => Owners.GetValue(owner, static _ => new ScenarioPrewarmShadowTracker());

    internal ScenarioPrewarmRootObservation ObserveRoot(ContinuationStamp root)
    {
        ArgumentNullException.ThrowIfNull(root);
        lock (_gate)
        {
            BindCombat(root.CombatIdentity);
            if (_currentRootStateText == null)
            {
                _currentRootStateText = root.StateText;
                _rootEpoch = 1;
                _predictions.Clear();
                return Snapshot("initial", predictionCount: 0, matched: null);
            }

            if (string.Equals(_currentRootStateText, root.StateText, StringComparison.Ordinal))
                return Snapshot("same_root", _predictions.Count, matched: null);

            int predictionCount = _predictions.Count;
            Prediction? matched = null;
            if (predictionCount == 0)
            {
                // No scenario matrix produced a reusable nonterminal observation from the
                // previous distinct root.
            }
            else if (_predictions.TryGetValue(root.StateText, out matched))
            {
                _exactMatches++;
            }
            else
            {
                _misses++;
            }

            _currentRootStateText = root.StateText;
            _rootEpoch++;
            _predictions.Clear();
            return Snapshot(
                predictionCount == 0 ? "no_predictions" : matched == null ? "miss" : "exact_match",
                predictionCount,
                matched);
        }
    }

    internal void RecordPrediction(
        ContinuationStamp sourceRoot,
        ContinuationStamp predicted,
        string decisionKey,
        ShadowTeammateScenarioKind scenarioKind)
    {
        ArgumentNullException.ThrowIfNull(sourceRoot);
        ArgumentNullException.ThrowIfNull(predicted);
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionKey);
        lock (_gate)
        {
            if (_combatIdentity == null
                || !string.Equals(
                    _combatIdentity,
                    sourceRoot.CombatIdentity,
                    StringComparison.Ordinal)
                || _currentRootStateText == null
                || !string.Equals(
                    _currentRootStateText,
                    sourceRoot.StateText,
                    StringComparison.Ordinal))
            {
                _staleStores++;
                return;
            }

            if (_predictions.ContainsKey(predicted.StateText))
            {
                _duplicateStores++;
                return;
            }

            if (_predictions.Count >= MaximumPredictionsPerRoot)
            {
                _droppedStores++;
                return;
            }

            _predictions.Add(
                predicted.StateText,
                new Prediction(decisionKey, scenarioKind));
        }
    }

    private void BindCombat(string combatIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(combatIdentity);
        if (string.Equals(_combatIdentity, combatIdentity, StringComparison.Ordinal))
            return;

        _combatIdentity = combatIdentity;
        _currentRootStateText = null;
        _rootEpoch = 0;
        _predictions.Clear();
        _exactMatches = 0;
        _misses = 0;
        _duplicateStores = 0;
        _staleStores = 0;
        _droppedStores = 0;
    }

    private ScenarioPrewarmRootObservation Snapshot(
        string status,
        int predictionCount,
        Prediction? matched)
        => new(
            status,
            _rootEpoch,
            predictionCount,
            matched?.ScenarioKind,
            _exactMatches,
            _misses,
            _duplicateStores,
            _staleStores,
            _droppedStores);

    internal static bool VerifyShadowGateForTesting()
    {
        CombatTransitionMemo owner = new();
        ScenarioPrewarmShadowTracker tracker = For(owner);
        ContinuationStamp Root(string state)
            => new(
                "combat_identity=seed=F0;players=1,2;enemies=7:X;" +
                "local_net_id=1;state=" + state);

        ContinuationStamp rootA = Root("A");
        ContinuationStamp rootB = Root("B");
        ContinuationStamp rootC = Root("C");
        ContinuationStamp rootD = Root("D");

        ScenarioPrewarmRootObservation initial = tracker.ObserveRoot(rootA);
        tracker.RecordPrediction(rootA, rootB, "decision-a", ShadowTeammateScenarioKind.Defensive);
        tracker.RecordPrediction(rootA, rootB, "decision-a-duplicate", ShadowTeammateScenarioKind.Aggressive);
        ScenarioPrewarmRootObservation same = tracker.ObserveRoot(rootA);
        ScenarioPrewarmRootObservation hit = tracker.ObserveRoot(rootB);

        tracker.RecordPrediction(rootA, rootC, "stale", ShadowTeammateScenarioKind.Conserve);
        tracker.RecordPrediction(rootB, rootC, "decision-b", ShadowTeammateScenarioKind.Conserve);
        ScenarioPrewarmRootObservation miss = tracker.ObserveRoot(rootD);

        return initial.Status == "initial"
            && initial.RootEpoch == 1
            && same.Status == "same_root"
            && same.PredictionCount == 1
            && hit.Status == "exact_match"
            && hit.PredictionCount == 1
            && hit.MatchedScenarioKind == ShadowTeammateScenarioKind.Defensive
            && hit.ExactMatches == 1
            && hit.DuplicateStores == 1
            && miss.Status == "miss"
            && miss.PredictionCount == 1
            && miss.Misses == 1
            && miss.StaleStores == 1;
    }
}
