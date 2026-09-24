using System.Diagnostics;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private int _searchEfficiencyMemberId;

    private void BeginSearchEfficiencyMember()
    {
        BeamWidthPortfolioTelemetry? telemetry = policy.PortfolioTelemetry;
        if (telemetry == null)
            return;
        string kind = policy.NoveltySearch != null
            ? "novelty"
            : _fixedPrefixActions.Count > 0
                ? "fixed_prefix"
                : _forceAllPotionsDisabled
                    ? "potion_disabled"
                    : _minimumPotionUses > 0
                        ? "potion_required"
                        : "beam";
        _searchEfficiencyMemberId = telemetry.BeginSearchMember(
            kind,
            _profile.BeamWidth,
            _profile.SecondRankBand,
            _profile.BaseScoreOnly,
            policy.NoveltySearch != null);
    }

    private void CompleteSearchEfficiencyMember()
        => policy.PortfolioTelemetry?.CompleteSearchMember(
            _searchEfficiencyMemberId,
            _run.Expanded,
            _run.TransitionCount);

    private CandidateOrigin? EnsureCandidateOrigin(SearchNode node)
    {
        if (node.CandidateOrigin != null)
            return node.CandidateOrigin;
        BeamWidthPortfolioTelemetry? telemetry = policy.PortfolioTelemetry;
        if (telemetry == null || _searchEfficiencyMemberId <= 0)
            return null;
        node.CandidateOrigin = telemetry.CreateCandidateOrigin(
            _searchEfficiencyMemberId,
            _run.Expanded,
            Math.Max(0, node.Turn - _startTurnNumber));
        return node.CandidateOrigin;
    }

    private string SearchEfficiencyEvaluationContextId(
        bool scenarioReevaluation,
        string completion)
        => $"rules=0.107.1;route={policy.RoutePolicy};team={policy.UseMultiplayerTeamObjective};" +
           $"objective={policy.MultiplayerCombatObjectiveStrategy};scenario={scenarioReevaluation};" +
           $"potion={_potionPolicy};theft={_theftPolicy?.ToString() ?? "none"};" +
           $"growth={policy.EffectiveHasGrowthTargets};turn_setup={_includeTurnSetup};completion={completion}";

    private void RecordCandidateEvaluated(SearchNode node, string contextId)
        => policy.PortfolioTelemetry?.RecordCandidateEvaluated(
            EnsureCandidateOrigin(node),
            contextId);

    private void RecordCandidateSelected(SearchNode node, string contextId)
        => policy.PortfolioTelemetry?.RecordCandidateSelected(
            EnsureCandidateOrigin(node),
            contextId);

    private void RecordCandidatePublished(CandidateOrigin? origin, string? contextId)
    {
        if (contextId != null)
            policy.PortfolioTelemetry?.RecordCandidatePublished(origin, contextId);
    }

    private void RecordSearchEfficiencyPhase(
        string phase,
        long startedTimestamp,
        long nestedTicks = 0)
    {
        if (_searchEfficiencyMemberId <= 0 || startedTimestamp <= 0)
            return;
        long elapsed = Math.Max(0, Stopwatch.GetTimestamp() - startedTimestamp);
        policy.PortfolioTelemetry?.RecordExclusivePhase(
            _searchEfficiencyMemberId,
            phase,
            Math.Max(0, elapsed - nestedTicks));
    }

    private long SearchEfficiencyPhaseTicks(string phase)
        => policy.PortfolioTelemetry?.GetExclusivePhaseTicks(
            _searchEfficiencyMemberId,
            phase) ?? 0;
}
