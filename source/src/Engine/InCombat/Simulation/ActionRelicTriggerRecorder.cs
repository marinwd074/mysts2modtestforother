using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.InCombat.Simulation;

internal readonly record struct RecordedRelicTrigger(string RelicId, string Summary);
internal readonly record struct RecordedKill(
    int Sequence,
    uint CombatId,
    string TargetId,
    CombatDamageSource Source);
internal readonly record struct RecordedAutoPlay(
    int Sequence,
    string SourceId,
    string CardId,
    int UpgradeLevel,
    uint? TargetCombatId,
    int ReplayCount);
internal readonly record struct RecordedHealthChange(int ActionIndex, string Kind, uint? Target,
    CombatDamageSource Source, decimal Requested, decimal Modified, int Before, int After);

/// <summary>
/// Enabled only for the single final-route replay. Normal Beam expansion keeps this null, so
/// displaying relic and kill provenance does not add a list or string allocation to every transition.
/// </summary>
internal sealed class ActionRelicTriggerRecorder
{
    private readonly Dictionary<int, List<RecordedRelicTrigger>> _triggers = [];
    private readonly Dictionary<int, List<RecordedKill>> _kills = [];
    private readonly Dictionary<int, List<RecordedAutoPlay>> _autoPlays = [];
    private int _actionIndex = -1;
    private int _eventSequence;
    private List<RecordedHealthChange>? _healthChanges;
    public IReadOnlyList<RecordedHealthChange> HealthChanges => _healthChanges ?? [];
    public void RecordHealth(string kind, uint? target, CombatDamageSource source,
        decimal requested, decimal modified, int before, int after)
        => (_healthChanges ??= []).Add(new(_actionIndex, kind, target, source, requested, modified, before, after));

    public void BeginAction(int actionIndex) => _actionIndex = actionIndex;

    public void Record(RelicModel relic, string summary)
    {
        if (_actionIndex < 0)
            throw new InvalidOperationException("Relic trigger was recorded outside a planned action.");
        RecordedRelicTrigger trigger = new(relic.Id.Entry, summary);
        if (!_triggers.TryGetValue(_actionIndex, out List<RecordedRelicTrigger>? entries))
        {
            entries = [];
            _triggers.Add(_actionIndex, entries);
        }
        if (!entries.Contains(trigger))
            entries.Add(trigger);
    }

    public IReadOnlyList<RecordedRelicTrigger> ForAction(int actionIndex)
        => _triggers.GetValueOrDefault(actionIndex) ?? [];

    public void RecordKill(uint combatId, string targetId, CombatDamageSource source)
    {
        if (_actionIndex < 0)
            throw new InvalidOperationException("击杀来源记录发生在计划动作之外。");
        if (string.IsNullOrEmpty(targetId))
            throw new InvalidOperationException("击杀来源记录缺少目标模型 ID。");
        RecordedKill kill = new(++_eventSequence, combatId, targetId, source);
        if (!_kills.TryGetValue(_actionIndex, out List<RecordedKill>? entries))
        {
            entries = [];
            _kills.Add(_actionIndex, entries);
        }
        if (!entries.Contains(kill))
            entries.Add(kill);
    }

    public IReadOnlyList<RecordedKill> KillsForAction(int actionIndex)
        => _kills.GetValueOrDefault(actionIndex) ?? [];

    public void RecordAutoPlay(
        string sourceId,
        string cardId,
        int upgradeLevel,
        uint? targetCombatId,
        int replayCount)
    {
        if (_actionIndex < 0)
            throw new InvalidOperationException("自动出牌记录发生在计划动作之外。");
        if (string.IsNullOrEmpty(cardId))
            throw new InvalidOperationException("自动出牌记录缺少卡牌模型 ID。");
        RecordedAutoPlay autoPlay = new(
            ++_eventSequence,
            sourceId,
            cardId,
            upgradeLevel,
            targetCombatId,
            Math.Max(0, replayCount));
        if (!_autoPlays.TryGetValue(_actionIndex, out List<RecordedAutoPlay>? entries))
        {
            entries = [];
            _autoPlays.Add(_actionIndex, entries);
        }
        entries.Add(autoPlay);
    }

    public IReadOnlyList<RecordedAutoPlay> AutoPlaysForAction(int actionIndex)
        => _autoPlays.GetValueOrDefault(actionIndex) ?? [];

    public IReadOnlyList<RecordedKill> KillsForAutoPlay(
        int actionIndex,
        RecordedAutoPlay autoPlay)
    {
        IReadOnlyList<RecordedAutoPlay> autoPlays = AutoPlaysForAction(actionIndex);
        int index = -1;
        for (int i = 0; i < autoPlays.Count; i++)
        {
            if (autoPlays[i].Sequence == autoPlay.Sequence)
            {
                index = i;
                break;
            }
        }
        if (index < 0)
            return [];
        int nextSequence = index + 1 < autoPlays.Count
            ? autoPlays[index + 1].Sequence
            : int.MaxValue;
        return KillsForAction(actionIndex)
            .Where(kill =>
                kill.Sequence > autoPlay.Sequence
                && kill.Sequence < nextSequence
                && kill.Source.Kind == CombatDamageSourceKind.Card
                && string.Equals(kill.Source.Id, autoPlay.CardId, StringComparison.Ordinal)
                && (autoPlay.TargetCombatId == null
                    || kill.CombatId == autoPlay.TargetCombatId.Value))
            .ToArray();
    }

    public bool IsKillAttributedToAutoPlay(int actionIndex, RecordedKill kill)
        => AutoPlaysForAction(actionIndex).Any(autoPlay =>
            KillsForAutoPlay(actionIndex, autoPlay).Contains(kill));
}
