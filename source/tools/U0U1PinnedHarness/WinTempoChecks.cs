using System.Security.Cryptography;
using System.Text.Json;
using CombatSolver;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
using OfflineSearchHarness;

namespace U0U1PinnedHarness;

internal static class WinTempoChecks
{
    internal static string Run(CombatState live, MainLoopContext loop, SolverDisplayNames names,
        SearchPolicySnapshot captured, bool enabled, bool overCap, string outputDirectory,
        bool holdout = false, bool heldPotion = false)
    {
        Player local = LocalContext.GetMe(live)!;
        Player teammate = live.Players.Single(player => player != local);
        var player = local.PlayerCombatState!;
        var enemy = live.Enemies.Single();
        if (holdout)
            foreach (var relic in local.Relics.ToArray()) local.RemoveRelicInternal(relic, silent: true);
        enemy.SetMaxHpInternal(12);
        enemy.SetCurrentHpInternal(12);
        foreach (var pile in new[] { player.Hand, player.DrawPile, player.DiscardPile, player.ExhaustPile })
            pile.Clear(silent: true);
        CardModel Create(string id) => live.CreateCard(ModelDb.AllCards.Single(card => card.Id.Entry == id), local);
        CardModel fastCard = Create("HEMOKINESIS");
        if (overCap) fastCard.DynamicVars.HpLoss.BaseValue = 3;
        int fastLoss = fastCard.DynamicVars.HpLoss.IntValue;
        if (fastLoss != (overCap ? 3 : 2))
            throw new InvalidOperationException($"Unexpected pinned Hemokinesis cost: {fastLoss}.");
        player.Hand.AddInternal(fastCard, -1);
        player.Hand.AddInternal(Create("DEFEND_IRONCLAD"), -1);
        foreach (string id in new[] { "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD" })
            player.DrawPile.AddInternal(Create(id), -1);
        foreach (PotionModel potion in local.PotionSlots.OfType<PotionModel>().ToArray())
            local.RemovePotionInternal(potion);
        PotionModel? protectedPotion = heldPotion ? ModelDb.Potion<FirePotion>().ToMutable() : null;
        if (protectedPotion != null && !local.AddPotionInternal(protectedPotion, 0, silent: true).success)
            throw new InvalidOperationException("Could not equip the potion for the bounded tempo fixture.");

        SolverSearchProfile profile = new(8, 1200, 8, 4, 4, 20_000);
        List<string> trades = [];
        SearchPolicySnapshot policy = captured with
        {
            Profile = profile, RoutePolicy = SearchRoutePolicy.MultiplayerSinglePlayerCore,
            CurrentTurnOnly = false, IncludeTurnSetup = false, FixedBudget = true, MaxDegreeOfParallelism = 1,
            UseBoundedWinTempo = enabled, UseSelectiveDeepening = false, VerifyIncrementalSearch = true,
            UseBeamWidthPortfolio = false, UseNoveltyPortfolio = false, NoveltySearch = null,
            UseMultiplayerTeamObjective = false, UseMultiplayerTeammateForecast = false,
            UseMultiplayerScenarioReevaluation = false, StopAtAcceptableBattleHpLoss = false,
            IgnoreLongTermRewards = true, RelicTargets = [], Interaction = null,
            PotionPolicy = SolverPotionPolicy.Disabled, PotionStrategy = new(SolverPotionPolicy.Disabled, []),
            Diagnostics = new SearchDiagnosticsSink(message =>
            {
                if (message.StartsWith("[CombatSolver/Test] SEARCH_WIN_TEMPO ", StringComparison.Ordinal))
                    trades.Add(message);
            }, _ => { }),
        };
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        if (heldPotion && root.SearchablePotionCount == 0)
            throw new InvalidOperationException("The fixture requires a searchable potion in the disabled inventory.");
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(live);
        CombatBeamSolver verifier = new(root, names, damage, policy, searchProfile: profile);
        var witness = verifier.ReplayDiagnosticPrefix([new(PlanActionKind.PlayCard, root.StartTurnNumber,
            "HEMOKINESIS", TargetIndex: 0, TargetCombatId: enemy.CombatId)]);
        try
        {
            if (witness.CombatEndedTurn != root.StartTurnNumber || witness.CumulativePlayerHpLost != fastLoss
                || witness.EvaluationWindow is not { CompleteVictory: true, CoversTarget: true })
                throw new InvalidOperationException("Fixture did not preserve the faster victory witness.");
        }
        finally { witness.ReleaseSimulator(); }

        int startTurn = root.StartTurnNumber;
        var slowWitness = verifier.ReplayDiagnosticPrefix([
            new(PlanActionKind.PlayCard, startTurn, "DEFEND_IRONCLAD"), new(PlanActionKind.EndTurn, startTurn),
            new(PlanActionKind.PlayCard, startTurn + 1, "STRIKE_IRONCLAD", TargetIndex: 0, TargetCombatId: enemy.CombatId),
            new(PlanActionKind.PlayCard, startTurn + 1, "STRIKE_IRONCLAD", TargetIndex: 0, TargetCombatId: enemy.CombatId),
        ]);
        try
        {
            if (slowWitness.CombatEndedTurn != startTurn + 1 || slowWitness.CumulativePlayerHpLost != 0)
                throw new InvalidOperationException($"Slow witness: ended={slowWitness.CombatEndedTurn}, loss={slowWitness.CumulativePlayerHpLost}, enemy={slowWitness.EnemyHp}.");
        }
        finally { slowWitness.ReleaseSimulator(); }

        SolverResult result = CombatSearchCoordinator.Solve(root, names, damage, policy, CancellationToken.None, null);
        bool expectTrade = enabled && !overCap;
        int expectedLoss = expectTrade ? fastLoss : 0;
        int expectedTurn = root.StartTurnNumber + (expectTrade ? 0 : 1);
        var replay = verifier.ReplayDiagnosticPrefix(result.BestNode.Actions);
        try
        {
            if (trades.Count != (expectTrade ? 1 : 0) || result.TotalExpandedNodes > profile.MaxExpandedNodes
                || result.BoundaryReason == SearchBoundaryReason.TimeLimit || replay.CombatEndedTurn != expectedTurn
                || replay.CumulativePlayerHpLost != expectedLoss || replay.PotionUseCount != 0
                || replay.ProjectedDeathSaveUseCount != 0 || replay.PlayerMaxHp != root.InitialPlayerMaxHp
                || verifier.CaptureDiagnosticContinuation(replay) != result.PredictedFinalState
                || ContinuationStamp.CaptureLive(live).StateText != root.ContinuationStamp.StateText)
                throw new InvalidOperationException($"Win-tempo comparison disagreed: ended={replay.CombatEndedTurn}, " +
                    $"loss={replay.CumulativePlayerHpLost}, trades={trades.Count}, boundary={result.BoundaryReason}, " +
                    $"expanded={result.ExpandedNodes}, actions={string.Join(',', result.BestNode.Actions.Select(action => action.CardId))}.");
        }
        finally { replay.ReleaseSimulator(); }

        using CombatReplayOutcome nativeOutcome = new(live);
        foreach (PlanAction action in result.BestNode.Actions)
        {
            if (!CombatManager.Instance.IsInProgress || action.Turn != player.TurnNumber)
                throw new InvalidOperationException("Native action was outside the planned turn.");
            if (action.Kind == PlanActionKind.EndTurn)
            {
                NativeActionChecks.Execute(loop, new EndPlayerTurnAction(teammate, teammate.PlayerCombatState!.TurnNumber));
                NativeActionChecks.Execute(loop, new EndPlayerTurnAction(local, action.Turn));
                OfflineCombat.WaitForPlayableCombat(loop);
            }
            else if (action.Kind == PlanActionKind.PlayCard && action.IsExecutable && !action.EndsPlayerTurn
                && action.Choice == null && action.NestedChoices is not { Count: > 0 })
            {
                CardModel card = SolverController.FindCardForDeployment(player.Hand.Cards, action);
                var target = action.TargetCombatId == enemy.CombatId ? enemy : null;
                if (!card.CanPlayTargeting(target)) throw new InvalidOperationException("Native tempo card was not legal.");
                NativeActionChecks.Execute(loop, new PlayCardAction(card, target));
            }
            else throw new InvalidOperationException("Unsupported action in the win-tempo fixture.");
        }
        CombatReplayOutcomeSnapshot actual = nativeOutcome.Capture(live, ended: !CombatManager.Instance.IsInProgress);
        if (CombatManager.Instance.IsInProgress || !enemy.IsDead || local.Creature.IsDead
            || actual.HpLost != expectedLoss || live.RoundNumber != expectedTurn || local.Creature.MaxHp != root.InitialPlayerMaxHp
            || protectedPotion != null && !ReferenceEquals(local.PotionSlots[0], protectedPotion)
            || holdout && (actual.HpHealed != 0 || actual.InitialHp - actual.FinalHp != expectedLoss))
            throw new InvalidOperationException($"Native win disagreed: round={live.RoundNumber}, loss={actual.HpLost}.");

        string mode = overCap ? "over-cap" : enabled ? "on" : "off";
        if (holdout) mode = "holdout-" + mode;
        if (heldPotion) mode += "-held-potion";
        File.WriteAllText(Path.Combine(outputDirectory, $"win-tempo-{mode}.json"), JsonSerializer.Serialize(new
        {
            Mode = mode, Root = root.ContinuationStamp.StateText, Profile = profile,
            DllSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(CombatBeamSolver).Assembly.Location))),
            FastWitnessLoss = fastLoss, ActualVictoryTurn = live.RoundNumber, ActualHpLost = actual.HpLost, NativeOutcome = actual,
            result.TotalExpandedNodes, result.TotalTransitionCount, result.EvaluationWindow, Actions = result.BestNode.Actions, Trades = trades,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return $"WinTempo PASS mode={mode} turn={live.RoundNumber} loss={actual.HpLost} healed={actual.HpHealed} expanded={result.TotalExpandedNodes}";
    }
}
