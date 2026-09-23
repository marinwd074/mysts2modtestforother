using System.Diagnostics.CodeAnalysis;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal readonly record struct ShadowTeammateActionCandidate(
    string PlayerNetId,
    int HandIndex,
    string CardId,
    int UpgradeLevel,
    string SemanticKey,
    int EnergyCost,
    int StarCost,
    uint? TargetCombatId,
    bool IsPowerCard = false);

internal sealed record ShadowTeammateRoute(
    CombatPredictionSimulator Simulator,
    IReadOnlyList<ShadowTeammateActionCandidate> Actions,
    IReadOnlySet<uint> ProcessedEnemyDeaths,
    bool CompleteVictory,
    bool AllPlayersAlive,
    bool TurnEndRequested,
    int EnemyDurability,
    int TeamEffectiveHp,
    double WorstPlayerEffectiveHpRatio,
    int TeamEnergy,
    int TeamStars)
{
    internal bool IsTerminal => CompleteVictory || !AllPlayersAlive || TurnEndRequested;

    internal IReadOnlySet<string> TurnEndedPlayerNetIds { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    internal double BehaviorLogProbability { get; init; }

    /// <summary>
    /// Log probability mass represented by this exact future state. This equals
    /// BehaviorLogProbability until exact-equivalent action histories are merged.
    /// </summary>
    internal double BehaviorLogMass { get; init; }

    internal int BehaviorDecisionCount { get; init; }

    internal double ScenarioProbabilityMass { get; init; } = 1d;

    internal double ScenarioConditionalProbability { get; init; } = 1d;

    internal double RetainedScenarioProbabilityMass { get; init; } = 1d;

    internal bool ScenarioProbabilityTrusted { get; init; } = true;

    /// <summary>
    /// True only when the Shadow scenario search itself was not truncated by unsupported Choice
    /// handling or the action-depth ceiling. P3 robust reranking uses this completeness bit and
    /// does not depend on the behavior prior being probabilistically calibrated.
    /// </summary>
    internal bool ScenarioSetComplete { get; init; } = true;

    internal StateFingerprint ScenarioFingerprint { get; init; }

    internal ShadowTeammateScenarioKind ScenarioKind { get; init; }

    internal double BehaviorMeanLogProbability =>
        ShadowTeammateBehaviorModel.MeanLogProbability(
            BehaviorLogProbability,
            BehaviorDecisionCount);
}

internal readonly record struct ShadowTeammatePlanResult(
    IReadOnlyList<ShadowTeammateRoute> Routes,
    int ExpandedBranches,
    int PendingChoiceBranches,
    bool HitActionDepthLimit,
    double RetainedProbabilityMass = 1d,
    bool ProbabilityModelTrusted = true);

/// <summary>
/// Predicts teammate actions only inside detached simulation forks. Shadow routes never create
/// deployment actions and never widen RootActionPlayers.
/// </summary>
internal static class ShadowTeammatePlanner
{
    internal const int DefaultBeamWidth =
        ShadowTeammateScenarioPolicy.DefaultScenarioCount;
    internal const int DefaultMaxActions = 12;

    internal static IReadOnlyList<ShadowTeammateActionCandidate> EnumerateLegalActions(
        CombatPredictionSimulator simulator,
        Player teammate)
    {
        AssertShadowPlayer(simulator, teammate);

        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(teammate);
        List<ShadowTeammateActionCandidate> candidates = [];
        for (int handIndex = 0; handIndex < playerState.Hand.Cards.Count; handIndex++)
        {
            PredictedCard card = playerState.Hand.Cards[handIndex];
            if (card.Preview.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly)
                continue;
            if (!combat.CanPlayCard(simulator, card, out int energyCost, out int starCost))
                continue;

            string semanticKey = CardChoiceSupport.ChoiceCardKey(card);
            foreach (Creature? target in EnumerateTargets(simulator, card))
            {
                candidates.Add(new ShadowTeammateActionCandidate(
                    teammate.NetId.ToString(),
                    handIndex,
                    card.Preview.Id.Entry,
                    card.Preview.CurrentUpgradeLevel,
                    semanticKey,
                    energyCost,
                    starCost,
                    target?.CombatId,
                    card.Preview.Type == CardType.Power));
            }
        }

        return candidates;
    }

    internal static ShadowTeammatePlanResult BuildTeamTopKRoutes(
        CombatPredictionSimulator source,
        Player localPlayer,
        IReadOnlySet<uint>? processedEnemyDeaths = null,
        int beamWidth = DefaultBeamWidth,
        int maxActionsPerPlayer = DefaultMaxActions)
    {
        if (beamWidth < 1)
            throw new ArgumentOutOfRangeException(nameof(beamWidth));
        if (maxActionsPerPlayer < 1)
            throw new ArgumentOutOfRangeException(nameof(maxActionsPerPlayer));
        if (!source.State.RootActionPlayers.Any(player => ReferenceEquals(player, localPlayer)))
            throw new InvalidOperationException("Joint shadow forecast local player is outside RootActionPlayers.");

        Player[] teammates = source.State.RootCapturedPlayers
            .Where(player => !ReferenceEquals(player, localPlayer))
            .OrderBy(player => player.NetId)
            .ToArray();
        ShadowTeammateRoute seed = CaptureRoute(
            source.Fork(),
            Array.Empty<ShadowTeammateActionCandidate>(),
            CaptureProcessedEnemyDeaths(source, processedEnemyDeaths));
        if (teammates.Length == 0)
        {
            IReadOnlyList<ShadowTeammateRoute> noTeammateRoutes =
                FinalizeBehaviorScenarioProbabilities([seed], scenarioSetComplete: true);
            return new ShadowTeammatePlanResult(
                noTeammateRoutes,
                ExpandedBranches: 0,
                PendingChoiceBranches: 0,
                HitActionDepthLimit: false,
                RetainedProbabilityMass: 1d,
                ProbabilityModelTrusted: true);
        }

        // Team search is action-interleaved: each search layer plays exactly one card from
        // any teammate that can still act. NetId is used only for deterministic enumeration,
        // never to grant one teammate an entire route before another teammate is considered.
        List<ShadowTeammateRoute> frontier = [seed];
        List<ShadowTeammateRoute> completed = [];
        int expandedBranches = 0;
        int pendingChoiceBranches = 0;
        bool hitActionDepthLimit = false;
        int maxTeamActions = checked(maxActionsPerPlayer * teammates.Length);

        for (int depth = 0; depth < maxTeamActions && frontier.Count > 0; depth++)
        {
            List<ShadowTeammateRoute> next = [];
            foreach (ShadowTeammateRoute route in frontier)
            {
                if (route.CompleteVictory || !route.AllPlayersAlive)
                {
                    completed.Add(route);
                    continue;
                }

                List<(ShadowTeammateRoute Route, ShadowBehaviorActionObservation Observation)>
                    behaviorChoices = [];
                foreach (Player teammate in teammates)
                {
                    string playerNetId = teammate.NetId.ToString();
                    if (route.TurnEndedPlayerNetIds.Contains(playerNetId)
                        || !route.Simulator.State.GetCreature(teammate.Creature).IsAlive)
                    {
                        continue;
                    }

                    int playerActionCount = 0;
                    for (int actionIndex = 0; actionIndex < route.Actions.Count; actionIndex++)
                    {
                        if (string.Equals(
                                route.Actions[actionIndex].PlayerNetId,
                                playerNetId,
                                StringComparison.Ordinal))
                        {
                            playerActionCount++;
                        }
                    }
                    if (playerActionCount >= maxActionsPerPlayer)
                        continue;

                    IReadOnlyList<ShadowTeammateActionCandidate> candidates =
                        EnumerateLegalActions(route.Simulator, teammate);
                    foreach (ShadowTeammateActionCandidate candidate in candidates)
                    {
                        expandedBranches++;
                        if (!TryPlayCandidate(
                                route,
                                teammate,
                                candidate,
                                out ShadowTeammateRoute? child))
                        {
                            pendingChoiceBranches++;
                            continue;
                        }

                        HashSet<string> turnEndedPlayers =
                            new(route.TurnEndedPlayerNetIds, StringComparer.Ordinal);
                        if (child.TurnEndRequested)
                        {
                            // A force-end card ends only the acting teammate's shadow turn.
                            // Consume the prediction-only request immediately so other teammates
                            // can still interleave actions in subsequent layers.
                            SimulatedCombatState childCombat =
                                (SimulatedCombatState)child.Simulator.State.CombatState;
                            _ = childCombat.ConsumePlayerTurnEndRequest();
                            turnEndedPlayers.Add(playerNetId);
                            child = CaptureRoute(
                                child.Simulator,
                                child.Actions,
                                child.ProcessedEnemyDeaths);
                        }

                        child = child with
                        {
                            TurnEndedPlayerNetIds = turnEndedPlayers,
                        };
                        behaviorChoices.Add((
                            child,
                            new ShadowBehaviorActionObservation(
                                child.CompleteVictory,
                                Math.Max(0, route.EnemyDurability - child.EnemyDurability),
                                Math.Max(0, child.TeamEffectiveHp - route.TeamEffectiveHp),
                                candidate.EnergyCost,
                                candidate.StarCost,
                                candidate.IsPowerCard)));
                    }
                }

                if (behaviorChoices.Count == 0)
                {
                    completed.Add(route);
                    continue;
                }

                ShadowBehaviorActionObservation[] observations =
                    behaviorChoices.Select(choice => choice.Observation).ToArray();
                double[] decisionLogProbabilities =
                    ShadowTeammateBehaviorModel.DecisionLogProbabilities(observations);

                // Stopping is modeled as an explicit alternative whenever at least one legal
                // remote action exists. It is a behavior decision, not a quality judgment.
                completed.Add(route with
                {
                    BehaviorLogProbability =
                        route.BehaviorLogProbability + decisionLogProbabilities[^1],
                    BehaviorLogMass =
                        route.BehaviorLogMass + decisionLogProbabilities[^1],
                    BehaviorDecisionCount = route.BehaviorDecisionCount + 1,
                });

                for (int choiceIndex = 0; choiceIndex < behaviorChoices.Count; choiceIndex++)
                {
                    ShadowTeammateRoute child = behaviorChoices[choiceIndex].Route with
                    {
                        BehaviorLogProbability =
                            route.BehaviorLogProbability + decisionLogProbabilities[choiceIndex],
                        BehaviorLogMass =
                            route.BehaviorLogMass + decisionLogProbabilities[choiceIndex],
                        BehaviorDecisionCount = route.BehaviorDecisionCount + 1,
                    };
                    next.Add(child);
                }
            }

            frontier = RetainBehaviorAwareSpectrum(next, beamWidth, labelScenarios: false);
            if (depth == maxTeamActions - 1 && frontier.Count > 0)
                hitActionDepthLimit = true;
        }

        completed.AddRange(frontier);
        List<ShadowTeammateRoute> retained =
            RetainBehaviorAwareSpectrum(completed, beamWidth, labelScenarios: true);
        bool scenarioSetComplete = pendingChoiceBranches == 0 && !hitActionDepthLimit;
        IReadOnlyList<ShadowTeammateRoute> finalized =
            FinalizeBehaviorScenarioProbabilities(retained, scenarioSetComplete);
        double retainedProbabilityMass = finalized.Count == 0
            ? 0d
            : finalized[0].RetainedScenarioProbabilityMass;
        return new ShadowTeammatePlanResult(
            finalized,
            expandedBranches,
            pendingChoiceBranches,
            hitActionDepthLimit,
            retainedProbabilityMass,
            ProbabilityModelTrusted: false);
    }

    internal static ShadowTeammatePlanResult BuildTopKRoutes(
        CombatPredictionSimulator source,
        Player teammate,
        IReadOnlySet<uint>? processedEnemyDeaths = null,
        int beamWidth = DefaultBeamWidth,
        int maxActions = DefaultMaxActions)
    {
        if (beamWidth < 1)
            throw new ArgumentOutOfRangeException(nameof(beamWidth));
        if (maxActions < 1)
            throw new ArgumentOutOfRangeException(nameof(maxActions));
        AssertShadowPlayer(source, teammate);

        ShadowTeammateRoute seed = CaptureRoute(
            source.Fork(),
            Array.Empty<ShadowTeammateActionCandidate>(),
            CaptureProcessedEnemyDeaths(source, processedEnemyDeaths));
        List<ShadowTeammateRoute> frontier = [seed];
        List<ShadowTeammateRoute> completed = [];
        int expandedBranches = 0;
        int pendingChoiceBranches = 0;
        bool hitDepthLimit = false;

        for (int depth = 0; depth < maxActions && frontier.Count > 0; depth++)
        {
            List<ShadowTeammateRoute> next = [];
            foreach (ShadowTeammateRoute route in frontier)
            {
                // A teammate can always choose to stop playing cards here.
                completed.Add(route);
                if (route.IsTerminal)
                    continue;

                IReadOnlyList<ShadowTeammateActionCandidate> candidates =
                    EnumerateLegalActions(route.Simulator, teammate);
                foreach (ShadowTeammateActionCandidate candidate in candidates)
                {
                    expandedBranches++;
                    if (TryPlayCandidate(route, teammate, candidate, out ShadowTeammateRoute? child))
                    {
                        next.Add(child);
                    }
                    else
                    {
                        pendingChoiceBranches++;
                    }
                }
            }

            frontier = RetainQualitySpectrum(next, beamWidth);
            if (depth == maxActions - 1 && frontier.Any(route => !route.IsTerminal))
                hitDepthLimit = true;
        }

        completed.AddRange(frontier);
        return new ShadowTeammatePlanResult(
            RetainQualitySpectrum(completed, beamWidth),
            expandedBranches,
            pendingChoiceBranches,
            hitDepthLimit);
    }

    internal static bool ReplayForecastActions(
        CombatPredictionSimulator simulator,
        IReadOnlyList<ShadowTeammateActionCandidate> actions,
        ISet<uint> processedEnemyDeaths)
    {
        HashSet<string> turnEndedPlayers = new(StringComparer.Ordinal);
        for (int index = 0; index < actions.Count; index++)
        {
            ShadowTeammateActionCandidate action = actions[index];
            if (turnEndedPlayers.Contains(action.PlayerNetId))
                return false;

            Player teammate = FindCapturedPlayer(simulator, action.PlayerNetId);
            if (!TryPlayCandidateInPlace(
                    simulator,
                    teammate,
                    action,
                    processedEnemyDeaths))
            {
                return false;
            }

            SimulatedCombatState combat =
                (SimulatedCombatState)simulator.State.CombatState;
            if (combat.PlayerTurnEndRequested)
            {
                _ = combat.ConsumePlayerTurnEndRequest();
                turnEndedPlayers.Add(action.PlayerNetId);
            }
        }
        return true;
    }

    private static Player FindCapturedPlayer(
        CombatPredictionSimulator simulator,
        string playerNetId)
    {
        foreach (Player player in simulator.State.RootCapturedPlayers)
        {
            if (string.Equals(
                    player.NetId.ToString(),
                    playerNetId,
                    StringComparison.Ordinal))
            {
                AssertShadowPlayer(simulator, player);
                return player;
            }
        }

        throw new InvalidOperationException(
            $"Shadow forecast replay cannot find captured player {playerNetId}.");
    }

    private static bool TryPlayCandidateInPlace(
        CombatPredictionSimulator simulator,
        Player teammate,
        ShadowTeammateActionCandidate candidate,
        ISet<uint> processedEnemyDeaths)
    {
        SimulatedCombatState combat =
            (SimulatedCombatState)simulator.State.CombatState;
        SimPlayerCombatState playerState =
            simulator.State.GetPlayerCombatState(teammate);
        if ((uint)candidate.HandIndex >= (uint)playerState.Hand.Cards.Count)
        {
            throw new InvalidOperationException(
                "Shadow teammate hand index changed during forecast replay.");
        }

        PredictedCard card = playerState.Hand.Cards[candidate.HandIndex];
        if (!string.Equals(card.Preview.Id.Entry, candidate.CardId, StringComparison.Ordinal)
            || card.Preview.CurrentUpgradeLevel != candidate.UpgradeLevel
            || !string.Equals(
                CardChoiceSupport.ChoiceCardKey(card),
                candidate.SemanticKey,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Shadow teammate card identity changed during forecast replay.");
        }
        if (!combat.CanPlayCard(simulator, card))
        {
            throw new InvalidOperationException(
                $"Shadow forecast card {candidate.CardId} is no longer playable during exact replay.");
        }

        Creature? target = combat.GetCreature(candidate.TargetCombatId);
        combat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        bool completed;
        try
        {
            using IDisposable executionScope =
                combat.BeginCardExecutionScope(processedEnemyDeaths);
            completed = simulator.ManualPlay(card, target, out _);
            if (completed)
            {
                using (simulator.BeginExecutionDispatch())
                {
                    completed = CorePowerSupport.ApplyEnemyDeathPowers(
                        simulator,
                        combat,
                        combat.KnownEnemies,
                        processedEnemyDeaths);
                }
            }
            if (completed)
                completed = CombatBeamSolver.SettleReplayActionBoundary(simulator, combat);
        }
        finally
        {
            combat.EndActionChoices();
        }

        if (!completed || simulator.HasPendingChoice)
            return false;

        simulator.CheckWinCondition(combat.GetPlayerTurnNumber(teammate));
        return true;
    }

    private static bool TryPlayCandidate(
        ShadowTeammateRoute parent,
        Player teammate,
        ShadowTeammateActionCandidate candidate,
        [NotNullWhen(true)] out ShadowTeammateRoute? child)
    {
        CombatPredictionSimulator simulator = parent.Simulator.Fork();
        HashSet<uint> processedEnemyDeaths = [.. parent.ProcessedEnemyDeaths];
        if (!TryPlayCandidateInPlace(
                simulator,
                teammate,
                candidate,
                processedEnemyDeaths))
        {
            child = null;
            return false;
        }
        ShadowTeammateActionCandidate[] actions = new ShadowTeammateActionCandidate[
            parent.Actions.Count + 1];
        for (int index = 0; index < parent.Actions.Count; index++)
            actions[index] = parent.Actions[index];
        actions[^1] = candidate;
        child = CaptureRoute(simulator, actions, processedEnemyDeaths);
        return true;
    }

    private static ShadowTeammateRoute CaptureRoute(
        CombatPredictionSimulator simulator,
        IReadOnlyList<ShadowTeammateActionCandidate> actions,
        IReadOnlySet<uint> processedEnemyDeaths)
    {
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        int enemyDurability = 0;
        foreach (Creature enemy in combat.KnownEnemies)
        {
            SimCreatureState state = simulator.State.GetCreature(enemy);
            enemyDurability = checked(
                enemyDurability
                + Math.Max(0, combat.EffectiveEnemyHp(enemy, state))
                + Math.Max(0, state.Block));
        }

        int teamEffectiveHp = 0;
        int teamEnergy = 0;
        int teamStars = 0;
        bool allPlayersAlive = true;
        double worstPlayerEffectiveHpRatio = double.PositiveInfinity;
        foreach (Player player in simulator.State.RootCapturedPlayers)
        {
            SimCreatureState state = simulator.State.GetCreature(player.Creature);
            SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
            int effectiveHp = Math.Max(0, state.CurrentHp) + Math.Max(0, state.Block);
            teamEffectiveHp = checked(teamEffectiveHp + effectiveHp);
            teamEnergy = checked(teamEnergy + Math.Max(0, playerState.Energy));
            teamStars = checked(teamStars + Math.Max(0, playerState.Stars));
            allPlayersAlive &= state.IsAlive;
            double ratio = effectiveHp / (double)Math.Max(1, state.MaxHp);
            worstPlayerEffectiveHpRatio = Math.Min(worstPlayerEffectiveHpRatio, ratio);
        }
        if (double.IsPositiveInfinity(worstPlayerEffectiveHpRatio))
            worstPlayerEffectiveHpRatio = 0d;

        return new ShadowTeammateRoute(
            simulator,
            actions,
            new HashSet<uint>(processedEnemyDeaths),
            simulator.TerminalStamp is { Outcome: CombatTerminalOutcome.Victory },
            allPlayersAlive,
            combat.PlayerTurnEndRequested,
            enemyDurability,
            teamEffectiveHp,
            worstPlayerEffectiveHpRatio,
            teamEnergy,
            teamStars);
    }

    private static List<ShadowTeammateRoute> RetainBehaviorAwareSpectrum(
        IReadOnlyList<ShadowTeammateRoute> candidates,
        int limit,
        bool labelScenarios)
    {
        List<ShadowTeammateRoute> exactSurvivors = ApplyExactDominance(candidates);
        if (exactSurvivors.Count == 0)
            return [];

        ShadowTeammateScenarioObservation[] observations =
            exactSurvivors.Select(route => new ShadowTeammateScenarioObservation(
                route.Actions.Count,
                route.CompleteVictory,
                route.AllPlayersAlive,
                route.EnemyDurability,
                route.TeamEffectiveHp,
                route.WorstPlayerEffectiveHpRatio,
                route.TeamEnergy,
                route.TeamStars,
                route.BehaviorLogMass,
                BuildActionOrderKey(route.Actions))).ToArray();
        IReadOnlyList<ShadowTeammateScenarioChoice> scenarioChoices =
            ShadowTeammateScenarioPolicy.SelectProtected(
                observations,
                Math.Min(limit, exactSurvivors.Count));

        List<ShadowTeammateRoute> selected = new(Math.Min(limit, exactSurvivors.Count));
        HashSet<ShadowTeammateRoute> selectedSet =
            new(ReferenceEqualityComparer.Instance);
        foreach (ShadowTeammateScenarioChoice choice in scenarioChoices)
        {
            ShadowTeammateRoute route = exactSurvivors[choice.Index];
            if (labelScenarios && choice.Kind != ShadowTeammateScenarioKind.Unspecified)
                route = route with { ScenarioKind = choice.Kind };
            selected.Add(route);
            selectedSet.Add(exactSurvivors[choice.Index]);
        }

        if (selected.Count < limit
            && ShadowRoutePruningPolicy.MayUseApproximateBeamPruning(
                exactSurvivors.Count,
                limit))
        {
            foreach (ShadowTeammateRoute route in
                     SelectApproximateQualityBeam(exactSurvivors, limit))
            {
                if (!selectedSet.Add(route))
                    continue;
                selected.Add(route);
                if (selected.Count == limit)
                    break;
            }
        }

        if (selected.Count < limit)
        {
            List<ShadowTeammateRoute> behaviorRanked = [.. exactSurvivors];
            behaviorRanked.Sort(CompareRoutesForBehavior);
            foreach (ShadowTeammateRoute route in behaviorRanked)
            {
                if (!selectedSet.Add(route))
                    continue;
                selected.Add(route);
                if (selected.Count == limit)
                    break;
            }
        }

        selected.Sort(CompareRoutesForBehavior);
        return selected;
    }

    private static string BuildActionOrderKey(
        IReadOnlyList<ShadowTeammateActionCandidate> actions)
    {
        if (actions.Count == 0)
            return string.Empty;
        return string.Join(
            ">",
            actions.Select(action =>
                $"{action.PlayerNetId}:{action.SemanticKey}:{action.TargetCombatId?.ToString() ?? "-"}"));
    }

    private static IReadOnlyList<ShadowTeammateRoute>
        FinalizeBehaviorScenarioProbabilities(
            IReadOnlyList<ShadowTeammateRoute> routes,
            bool scenarioSetComplete)
    {
        if (routes.Count == 0)
            return Array.Empty<ShadowTeammateRoute>();

        double[] logMasses = routes.Select(route => route.BehaviorLogMass).ToArray();
        ShadowScenarioProbabilitySet probabilitySet =
            ShadowScenarioChanceMath.NormalizeRetainedLogMasses(logMasses);
        ShadowTeammateRoute[] finalized = new ShadowTeammateRoute[routes.Count];
        for (int index = 0; index < routes.Count; index++)
        {
            ShadowTeammateRoute route = routes[index];
            StateFingerprint scenarioFingerprint = ShadowFutureStateFingerprint.Capture(
                route.Simulator,
                route.ProcessedEnemyDeaths,
                route.TurnEndedPlayerNetIds,
                route.Actions);
            double rawMass = Math.Clamp(Math.Exp(Math.Min(0d, route.BehaviorLogMass)), 0d, 1d);
            finalized[index] = route with
            {
                ScenarioProbabilityMass = rawMass,
                ScenarioConditionalProbability =
                    probabilitySet.ConditionalProbabilities[index],
                RetainedScenarioProbabilityMass =
                    probabilitySet.RetainedProbabilityMass,
                // The generic behavior prior is intentionally not calibrated to a real player.
                // Probability-weighted decisions therefore remain fail-closed even when the
                // stress-scenario search itself completed normally.
                ScenarioProbabilityTrusted = false,
                ScenarioSetComplete = scenarioSetComplete,
                ScenarioFingerprint = scenarioFingerprint,
            };
        }
        return finalized;
    }

    private static List<ShadowTeammateRoute> RetainQualitySpectrum(
        IReadOnlyList<ShadowTeammateRoute> candidates,
        int limit)
    {
        List<ShadowTeammateRoute> exactSurvivors = ApplyExactDominance(candidates);
        if (!ShadowRoutePruningPolicy.MayUseApproximateBeamPruning(
                exactSurvivors.Count,
                limit))
        {
            exactSurvivors.Sort(CompareRoutesForSpectrum);
            return exactSurvivors;
        }
        return SelectApproximateQualityBeam(exactSurvivors, limit);
    }

    private static List<ShadowTeammateRoute> ApplyExactDominance(
        IReadOnlyList<ShadowTeammateRoute> candidates)
    {
        if (candidates.Count <= 1)
            return [.. candidates];

        Dictionary<StateFingerprint, ShadowTeammateRoute> winners = [];
        for (int index = 0; index < candidates.Count; index++)
        {
            ShadowTeammateRoute candidate = candidates[index];
            StateFingerprint futureState = ShadowFutureStateFingerprint.Capture(
                candidate.Simulator,
                candidate.ProcessedEnemyDeaths,
                candidate.TurnEndedPlayerNetIds,
                candidate.Actions);

            if (!winners.TryGetValue(futureState, out ShadowTeammateRoute? current))
            {
                winners.Add(futureState, candidate);
                continue;
            }

            // Equal future-state fingerprints include all captured player/enemy mutable state,
            // ordered piles, RNG streams, modeled combat state, action-budget usage and ended
            // teammates. Preserve one representative action history, but probability mass from
            // every exact-equivalent history belongs to the same future scenario and must add.
            double mergedLogMass = ShadowScenarioChanceMath.LogAddExp(
                current.BehaviorLogMass,
                candidate.BehaviorLogMass);
            ShadowTeammateRoute representative =
                CompareRoutesForBehavior(candidate, current) < 0
                    ? candidate
                    : current;
            winners[futureState] = representative with
            {
                BehaviorLogMass = mergedLogMass,
            };
        }

        return [.. winners.Values];
    }

    private static int CompareRoutesForBehavior(
        ShadowTeammateRoute left,
        ShadowTeammateRoute right)
    {
        int comparison = right.BehaviorLogMass.CompareTo(left.BehaviorLogMass);
        if (comparison != 0)
            return comparison;
        comparison = right.BehaviorMeanLogProbability.CompareTo(
            left.BehaviorMeanLogProbability);
        if (comparison != 0)
            return comparison;
        comparison = right.BehaviorLogProbability.CompareTo(left.BehaviorLogProbability);
        if (comparison != 0)
            return comparison;
        return CompareRoutesForSpectrum(left, right);
    }

    private static List<ShadowTeammateRoute> SelectApproximateQualityBeam(
        IReadOnlyList<ShadowTeammateRoute> candidates,
        int limit)
    {
        if (candidates.Count <= limit)
        {
            List<ShadowTeammateRoute> all = [.. candidates];
            all.Sort(CompareRoutesForSpectrum);
            return all;
        }

        List<ShadowTeammateRoute> heuristicFrontier = [];
        for (int index = 0; index < candidates.Count; index++)
        {
            ShadowTeammateRoute candidate = candidates[index];
            bool heuristicallyDominated = false;
            for (int otherIndex = 0; otherIndex < candidates.Count; otherIndex++)
            {
                if (index == otherIndex)
                    continue;
                if (HeuristicQualityDominates(candidates[otherIndex], candidate))
                {
                    heuristicallyDominated = true;
                    break;
                }
            }
            if (!heuristicallyDominated)
                heuristicFrontier.Add(candidate);
        }

        heuristicFrontier.Sort(CompareRoutesForSpectrum);
        if (heuristicFrontier.Count <= limit)
            return heuristicFrontier;

        List<ShadowTeammateRoute> sampled = new(limit);
        if (limit == 1)
        {
            sampled.Add(heuristicFrontier[0]);
            return sampled;
        }

        int previous = -1;
        for (int slot = 0; slot < limit; slot++)
        {
            int index = (int)Math.Round(
                slot * (heuristicFrontier.Count - 1d) / (limit - 1d),
                MidpointRounding.AwayFromZero);
            if (index == previous)
                continue;
            sampled.Add(heuristicFrontier[index]);
            previous = index;
        }
        return sampled;
    }

    private static bool HeuristicQualityDominates(
        ShadowTeammateRoute left,
        ShadowTeammateRoute right)
        => ShadowRoutePruningPolicy.HeuristicQualityDominates(
            DescribeApproximateQuality(left),
            DescribeApproximateQuality(right));

    private static ShadowApproximateQuality DescribeApproximateQuality(
        ShadowTeammateRoute route)
        => new(
            route.CompleteVictory,
            route.AllPlayersAlive,
            route.EnemyDurability,
            route.TeamEffectiveHp,
            route.WorstPlayerEffectiveHpRatio,
            route.TeamEnergy,
            route.TeamStars,
            route.Actions.Count);

    private static int CompareRoutesForSpectrum(
        ShadowTeammateRoute left,
        ShadowTeammateRoute right)
    {
        int comparison = right.CompleteVictory.CompareTo(left.CompleteVictory);
        if (comparison != 0)
            return comparison;
        comparison = right.AllPlayersAlive.CompareTo(left.AllPlayersAlive);
        if (comparison != 0)
            return comparison;
        comparison = left.EnemyDurability.CompareTo(right.EnemyDurability);
        if (comparison != 0)
            return comparison;
        comparison = right.TeamEffectiveHp.CompareTo(left.TeamEffectiveHp);
        if (comparison != 0)
            return comparison;
        comparison = right.WorstPlayerEffectiveHpRatio.CompareTo(left.WorstPlayerEffectiveHpRatio);
        if (comparison != 0)
            return comparison;
        comparison = right.TeamEnergy.CompareTo(left.TeamEnergy);
        if (comparison != 0)
            return comparison;
        comparison = right.TeamStars.CompareTo(left.TeamStars);
        if (comparison != 0)
            return comparison;
        comparison = left.Actions.Count.CompareTo(right.Actions.Count);
        if (comparison != 0)
            return comparison;

        int count = Math.Min(left.Actions.Count, right.Actions.Count);
        for (int index = 0; index < count; index++)
        {
            comparison = string.CompareOrdinal(left.Actions[index].SemanticKey, right.Actions[index].SemanticKey);
            if (comparison != 0)
                return comparison;
            comparison = Nullable.Compare(
                left.Actions[index].TargetCombatId,
                right.Actions[index].TargetCombatId);
            if (comparison != 0)
                return comparison;
        }
        return 0;
    }

    private static HashSet<uint> CaptureProcessedEnemyDeaths(
        CombatPredictionSimulator simulator,
        IReadOnlySet<uint>? processedEnemyDeaths)
    {
        if (processedEnemyDeaths != null)
            return [.. processedEnemyDeaths];

        HashSet<uint> captured = [];
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        foreach (Creature enemy in combat.KnownEnemies)
        {
            if (enemy.CombatId is uint combatId
                && simulator.State.GetCreature(enemy).IsDead)
            {
                captured.Add(combatId);
            }
        }
        return captured;
    }

    private static void AssertShadowPlayer(
        CombatPredictionSimulator simulator,
        Player teammate)
    {
        if (!simulator.State.RootCapturedPlayers.Any(player => ReferenceEquals(player, teammate)))
            throw new InvalidOperationException("Shadow teammate is outside the captured prediction root.");
        if (simulator.State.RootActionPlayers.Any(player => ReferenceEquals(player, teammate)))
            throw new InvalidOperationException("Shadow teammate unexpectedly belongs to the deployment action scope.");
    }

    private static IEnumerable<Creature?> EnumerateTargets(
        CombatPredictionSimulator simulator,
        PredictedCard card)
    {
        TargetType targetType = simulator.GetTargetType(card);
        if (targetType == TargetType.AnyEnemy)
        {
            foreach (Creature enemy in simulator.State.Enemies)
            {
                if (simulator.State.IsHittable(enemy))
                    yield return enemy;
            }
            yield break;
        }

        if (targetType is TargetType.AnyPlayer or TargetType.AnyAlly)
        {
            foreach (Creature target in simulator.State.GetValidManualTargets(
                         card.Preview.Owner.Creature,
                         targetType))
            {
                yield return target;
            }
            yield break;
        }

        yield return null;
    }
}
