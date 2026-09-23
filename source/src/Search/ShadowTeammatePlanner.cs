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
    uint? TargetCombatId);

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
                    target?.CombatId));
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
        List<ShadowTeammateRoute> worlds =
            [CaptureRoute(
                source.Fork(),
                Array.Empty<ShadowTeammateActionCandidate>(),
                CaptureProcessedEnemyDeaths(source, processedEnemyDeaths))];
        int expandedBranches = 0;
        int pendingChoiceBranches = 0;
        bool hitActionDepthLimit = false;

        for (int teammateIndex = 0; teammateIndex < teammates.Length; teammateIndex++)
        {
            Player teammate = teammates[teammateIndex];
            List<ShadowTeammateRoute> nextWorlds = [];
            foreach (ShadowTeammateRoute world in worlds)
            {
                if (world.CompleteVictory
                    || !world.Simulator.State.GetCreature(teammate.Creature).IsAlive)
                {
                    nextWorlds.Add(world);
                    continue;
                }

                ShadowTeammatePlanResult forecast = BuildTopKRoutes(
                    world.Simulator,
                    teammate,
                    world.ProcessedEnemyDeaths,
                    beamWidth,
                    maxActionsPerPlayer);
                expandedBranches = checked(expandedBranches + forecast.ExpandedBranches);
                pendingChoiceBranches = checked(
                    pendingChoiceBranches + forecast.PendingChoiceBranches);
                hitActionDepthLimit |= forecast.HitActionDepthLimit;

                foreach (ShadowTeammateRoute teammateRoute in forecast.Routes)
                {
                    SimulatedCombatState combat =
                        (SimulatedCombatState)teammateRoute.Simulator.State.CombatState;
                    _ = combat.ConsumePlayerTurnEndRequest();

                    ShadowTeammateActionCandidate[] combined =
                        new ShadowTeammateActionCandidate[
                            world.Actions.Count + teammateRoute.Actions.Count];
                    for (int index = 0; index < world.Actions.Count; index++)
                        combined[index] = world.Actions[index];
                    for (int index = 0; index < teammateRoute.Actions.Count; index++)
                        combined[world.Actions.Count + index] = teammateRoute.Actions[index];

                    nextWorlds.Add(CaptureRoute(
                        teammateRoute.Simulator,
                        combined,
                        teammateRoute.ProcessedEnemyDeaths));
                }
            }

            worlds = RetainParetoSpectrum(nextWorlds, beamWidth);
        }

        return new ShadowTeammatePlanResult(
            worlds,
            expandedBranches,
            pendingChoiceBranches,
            hitActionDepthLimit);
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

    internal static bool ReplayForecastActions(
        CombatPredictionSimulator simulator,
        IReadOnlyList<ShadowTeammateActionCandidate> actions,
        ISet<uint> processedEnemyDeaths)
    {
        string? activePlayerNetId = null;
        for (int index = 0; index < actions.Count; index++)
        {
            ShadowTeammateActionCandidate action = actions[index];
            if (activePlayerNetId != null
                && !string.Equals(activePlayerNetId, action.PlayerNetId, StringComparison.Ordinal))
            {
                SimulatedCombatState betweenPlayers =
                    (SimulatedCombatState)simulator.State.CombatState;
                _ = betweenPlayers.ConsumePlayerTurnEndRequest();
            }

            Player teammate = FindCapturedPlayer(simulator, action.PlayerNetId);
            if (!TryPlayCandidateInPlace(
                    simulator,
                    teammate,
                    action,
                    processedEnemyDeaths))
            {
                return false;
            }
            activePlayerNetId = action.PlayerNetId;
        }

        if (activePlayerNetId != null)
        {
            SimulatedCombatState combat =
                (SimulatedCombatState)simulator.State.CombatState;
            _ = combat.ConsumePlayerTurnEndRequest();
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
            && (left.AllPlayersAlive || !right.AllPlayersAlive)
            && left.EnemyDurability <= right.EnemyDurability
            && left.TeamEffectiveHp >= right.TeamEffectiveHp
            && left.WorstPlayerEffectiveHpRatio >= right.WorstPlayerEffectiveHpRatio
            && left.TeamEnergy >= right.TeamEnergy
            && left.TeamStars >= right.TeamStars
            && left.Actions.Count <= right.Actions.Count;
        if (!noWorse)
            return false;

        return left.CompleteVictory != right.CompleteVictory
            || left.AllPlayersAlive != right.AllPlayersAlive
            || left.EnemyDurability != right.EnemyDurability
            || left.TeamEffectiveHp != right.TeamEffectiveHp
            || !left.WorstPlayerEffectiveHpRatio.Equals(right.WorstPlayerEffectiveHpRatio)
            || left.TeamEnergy != right.TeamEnergy
            || left.TeamStars != right.TeamStars
            || left.Actions.Count != right.Actions.Count;
    }

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
