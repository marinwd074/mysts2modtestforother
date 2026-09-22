using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
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
    uint? TargetCombatId);

internal sealed record ShadowTeammateRoute(
    CombatPredictionSimulator Simulator,
    IReadOnlyList<ShadowTeammateActionCandidate> Actions,
    bool CompleteVictory,
    bool TeammateAlive,
    bool TurnEndRequested,
    int EnemyDurability,
    int TeamEffectiveHp,
    double WorstPlayerEffectiveHpRatio,
    int TeammateStars)
{
    internal bool IsTerminal => CompleteVictory || !TeammateAlive || TurnEndRequested;
}

internal readonly record struct ShadowTeammatePlanResult(
    IReadOnlyList<ShadowTeammateRoute> Routes,
    int ExpandedBranches,
    int PendingChoiceBranches,
    bool HitActionDepthLimit);

/// <summary>
/// Predicts teammate actions only inside detached simulation forks. Shadow routes never create
/// deployment actions and never widen RootActionPlayers.
/// </summary>
internal static class ShadowTeammatePlanner
{
    internal const int DefaultBeamWidth = 4;
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
                    target?.CombatId));
            }
        }

        return candidates;
    }

    internal static ShadowTeammatePlanResult BuildTopKRoutes(
        CombatPredictionSimulator source,
        Player teammate,
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
            teammate,
            Array.Empty<ShadowTeammateActionCandidate>());
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

            frontier = RetainParetoSpectrum(next, beamWidth);
            if (depth == maxActions - 1 && frontier.Any(route => !route.IsTerminal))
                hitDepthLimit = true;
        }

        completed.AddRange(frontier);
        return new ShadowTeammatePlanResult(
            RetainParetoSpectrum(completed, beamWidth),
            expandedBranches,
            pendingChoiceBranches,
            hitDepthLimit);
    }

    private static bool TryPlayCandidate(
        ShadowTeammateRoute parent,
        Player teammate,
        ShadowTeammateActionCandidate candidate,
        out ShadowTeammateRoute? child)
    {
        CombatPredictionSimulator simulator = parent.Simulator.Fork();
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(teammate);
        if ((uint)candidate.HandIndex >= (uint)playerState.Hand.Cards.Count)
            throw new InvalidOperationException("Shadow teammate hand index changed across a prediction fork.");

        PredictedCard card = playerState.Hand.Cards[candidate.HandIndex];
        if (!string.Equals(card.Preview.Id.Entry, candidate.CardId, StringComparison.Ordinal)
            || card.Preview.CurrentUpgradeLevel != candidate.UpgradeLevel
            || !string.Equals(
                CardChoiceSupport.ChoiceCardKey(card),
                candidate.SemanticKey,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Shadow teammate card identity changed across a prediction fork.");
        }

        Creature? target = combat.GetCreature(candidate.TargetCombatId);
        HashSet<uint> processedEnemyDeaths = [];
        combat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        bool completed;
        try
        {
            using IDisposable executionScope = combat.BeginCardExecutionScope(processedEnemyDeaths);
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
        {
            child = null;
            return false;
        }

        simulator.CheckWinCondition(combat.GetPlayerTurnNumber(teammate));
        ShadowTeammateActionCandidate[] actions = new ShadowTeammateActionCandidate[
            parent.Actions.Count + 1];
        for (int index = 0; index < parent.Actions.Count; index++)
            actions[index] = parent.Actions[index];
        actions[^1] = candidate;
        child = CaptureRoute(simulator, teammate, actions);
        return true;
    }

    private static ShadowTeammateRoute CaptureRoute(
        CombatPredictionSimulator simulator,
        Player teammate,
        IReadOnlyList<ShadowTeammateActionCandidate> actions)
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
        double worstPlayerEffectiveHpRatio = double.PositiveInfinity;
        foreach (Player player in simulator.State.RootCapturedPlayers)
        {
            SimCreatureState state = simulator.State.GetCreature(player.Creature);
            int effectiveHp = Math.Max(0, state.CurrentHp) + Math.Max(0, state.Block);
            teamEffectiveHp = checked(teamEffectiveHp + effectiveHp);
            double ratio = effectiveHp / (double)Math.Max(1, state.MaxHp);
            worstPlayerEffectiveHpRatio = Math.Min(worstPlayerEffectiveHpRatio, ratio);
        }
        if (double.IsPositiveInfinity(worstPlayerEffectiveHpRatio))
            worstPlayerEffectiveHpRatio = 0d;

        SimCreatureState teammateState = simulator.State.GetCreature(teammate.Creature);
        SimPlayerCombatState teammateCombatState = simulator.State.GetPlayerCombatState(teammate);
        return new ShadowTeammateRoute(
            simulator,
            actions,
            simulator.TerminalStamp is { Outcome: CombatTerminalOutcome.Victory },
            teammateState.IsAlive,
            combat.PlayerTurnEndRequested,
            enemyDurability,
            teamEffectiveHp,
            worstPlayerEffectiveHpRatio,
            teammateCombatState.Stars);
    }

    private static List<ShadowTeammateRoute> RetainParetoSpectrum(
        IReadOnlyList<ShadowTeammateRoute> candidates,
        int limit)
    {
        if (candidates.Count <= 1)
            return [.. candidates];

        List<ShadowTeammateRoute> frontier = [];
        for (int index = 0; index < candidates.Count; index++)
        {
            ShadowTeammateRoute candidate = candidates[index];
            bool dominated = false;
            for (int otherIndex = 0; otherIndex < candidates.Count; otherIndex++)
            {
                if (index == otherIndex)
                    continue;
                if (Dominates(candidates[otherIndex], candidate))
                {
                    dominated = true;
                    break;
                }
            }
            if (!dominated)
                frontier.Add(candidate);
        }

        frontier.Sort(CompareRoutesForSpectrum);
        if (frontier.Count <= limit)
            return frontier;

        List<ShadowTeammateRoute> sampled = new(limit);
        if (limit == 1)
        {
            sampled.Add(frontier[0]);
            return sampled;
        }

        int previous = -1;
        for (int slot = 0; slot < limit; slot++)
        {
            int index = (int)Math.Round(
                slot * (frontier.Count - 1d) / (limit - 1d),
                MidpointRounding.AwayFromZero);
            if (index == previous)
                continue;
            sampled.Add(frontier[index]);
            previous = index;
        }
        return sampled;
    }

    private static bool Dominates(ShadowTeammateRoute left, ShadowTeammateRoute right)
    {
        bool noWorse = (left.CompleteVictory || !right.CompleteVictory)
            && (left.TeammateAlive || !right.TeammateAlive)
            && left.EnemyDurability <= right.EnemyDurability
            && left.TeamEffectiveHp >= right.TeamEffectiveHp
            && left.WorstPlayerEffectiveHpRatio >= right.WorstPlayerEffectiveHpRatio
            && left.TeammateStars >= right.TeammateStars
            && left.Actions.Count <= right.Actions.Count;
        if (!noWorse)
            return false;

        return left.CompleteVictory != right.CompleteVictory
            || left.TeammateAlive != right.TeammateAlive
            || left.EnemyDurability != right.EnemyDurability
            || left.TeamEffectiveHp != right.TeamEffectiveHp
            || !left.WorstPlayerEffectiveHpRatio.Equals(right.WorstPlayerEffectiveHpRatio)
            || left.TeammateStars != right.TeammateStars
            || left.Actions.Count != right.Actions.Count;
    }

    private static int CompareRoutesForSpectrum(
        ShadowTeammateRoute left,
        ShadowTeammateRoute right)
    {
        int comparison = right.CompleteVictory.CompareTo(left.CompleteVictory);
        if (comparison != 0)
            return comparison;
        comparison = right.TeammateAlive.CompareTo(left.TeammateAlive);
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
        comparison = right.TeammateStars.CompareTo(left.TeammateStars);
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
