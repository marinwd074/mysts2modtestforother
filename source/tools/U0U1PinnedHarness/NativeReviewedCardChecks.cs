using System.Text.Json;
using CombatSolver;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using OfflineSearchHarness;

namespace U0U1PinnedHarness;

internal static class NativeReviewedCardChecks
{
    internal static string Run(CombatState live, MainLoopContext loop, string output)
    {
        Player player = LocalContext.GetMe(live)!;
        Player teammate = live.Players.Single(candidate => candidate != player);
        var enemy = live.Enemies.Single();
        enemy.SetCurrentHpInternal(50);
        player.PlayerCombatState!.Hand.Clear(silent: true);
        CardModel gangUp = live.CreateCard(ModelDb.Card<GangUp>(), player);
        player.PlayerCombatState.Hand.AddInternal(gangUp, -1);
        // Strike records one native hit without adding Vulnerable to the victim.
        CardModel teammateAttack = teammate.PlayerCombatState!.Hand.Cards.First(card => card.Id.Entry == "STRIKE_IRONCLAD");
        SolverDisplayNames names = SolverDisplayNames.Capture(live);
        SolverSearchProfile profile = new(8, 1_200, 8, 4, 4, 20_000);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(),
            live, includeTurnSetup: false, theftPolicy: null) with
        {
            Profile = profile, UseReviewedLocalMultiplayerCards = true, CurrentTurnOnly = false,
            RoutePolicy = SearchRoutePolicy.MultiplayerSinglePlayerCore,
            UseMultiplayerTeamObjective = false, UseMultiplayerTeammateForecast = false,
            UseMultiplayerScenarioReevaluation = false, UseBeamWidthPortfolio = false, UseNoveltyPortfolio = false,
            FixedBudget = true, MaxDegreeOfParallelism = 1, VerifyIncrementalSearch = true,
            PotionPolicy = SolverPotionPolicy.Disabled, PotionStrategy = new(SolverPotionPolicy.Disabled, []),
        };
        int DamageFromObservedRoot()
        {
            CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
            CombatBeamSolver solver = new(root, names, BattleDamageTracker.Observe(live), policy, searchProfile: profile);
            PlanAction action = new(PlanActionKind.PlayCard, root.StartTurnNumber, gangUp.Id.Entry,
                TargetIndex: 0, TargetCombatId: enemy.CombatId) { AdviceOnly = true };
            SimulationSnapshot preview = solver.ReplayDiagnosticPrefix([action]);
            try
            {
                if (preview.HasRisk || ContinuationStamp.CaptureLive(live) != root.ContinuationStamp)
                    throw new InvalidOperationException("Reviewed card replay was unsupported or changed the live root.");
                return enemy.CurrentHp - preview.EnemyHp;
            }
            finally { preview.ReleaseSimulator(); }
        }

        int baseDamage = DamageFromObservedRoot();
        int enemyHpBeforeTeammate = enemy.CurrentHp;
        NativeActionChecks.Execute(loop, new PlayCardAction(teammateAttack, enemy));
        int observedEnemyHp = enemy.CurrentHp;
        int baseTeammateDamage = enemyHpBeforeTeammate - observedEnemyHp;
        int boostedDamage = DamageFromObservedRoot();
        if (boostedDamage <= baseDamage || enemy.CurrentHp <= boostedDamage)
            throw new InvalidOperationException("Observed teammate attack did not boost the reviewed card as expected.");

        enemy.SetCurrentHpInternal(boostedDamage);
        CombatRootSnapshot lethalRoot = CombatRootSnapshot.Capture(live);
        SolverResult result = CombatSearchCoordinator.Solve(lethalRoot, names, BattleDamageTracker.Observe(live),
            policy, CancellationToken.None, null);
        PlanAction suggestion = result.BestNode.Actions.Single();
        if (suggestion.CardId != gangUp.Id.Entry || !suggestion.AdviceOnly || suggestion.IsExecutable
            || !result.Snapshot.AllEnemiesDead || result.Snapshot.HasRisk
            || ContinuationStamp.CaptureLive(live) != lethalRoot.ContinuationStamp)
            throw new InvalidOperationException("Reviewed card did not produce an isolated, advisory-only lethal.");
        VerifyDeploymentRejects(suggestion);
        // Execute directly as the native oracle. Safe Execute must stop at this suggestion.
        enemy.SetCurrentHpInternal(observedEnemyHp);
        NativeActionChecks.Execute(loop, new PlayCardAction(gangUp, enemy));
        int actualDamage = observedEnemyHp - enemy.CurrentHp;
        if (!CombatManager.Instance.IsInProgress || actualDamage != boostedDamage)
            throw new InvalidOperationException("The native card outcome disagrees with the observed-root prediction.");

        CardModel flanking = live.CreateCard(ModelDb.Card<Flanking>(), player);
        player.PlayerCombatState.Hand.AddInternal(flanking, -1);
        CombatRootSnapshot flankingRoot = CombatRootSnapshot.Capture(live);
        CombatBeamSolver flankingVerifier = new(flankingRoot, names, BattleDamageTracker.Observe(live), policy, searchProfile: profile);
        PlanAction flankingAction = new(PlanActionKind.PlayCard, flankingRoot.StartTurnNumber,
            flanking.Id.Entry, TargetIndex: 0, TargetCombatId: enemy.CombatId) { AdviceOnly = true };
        SimulationSnapshot flankingPreview = flankingVerifier.ReplayDiagnosticPrefix([flankingAction]);
        ContinuationStamp predictedFlanking;
        try
        {
            predictedFlanking = flankingVerifier.CaptureDiagnosticContinuation(flankingPreview);
            if (flankingPreview.HasRisk || ContinuationStamp.CaptureLive(live) != flankingRoot.ContinuationStamp)
                throw new InvalidOperationException("Flanking prediction was unsupported or changed the live root.");
        }
        finally { flankingPreview.ReleaseSimulator(); }

        VerifyDeploymentRejects(flankingAction);
        NativeActionChecks.Execute(loop, new PlayCardAction(flanking, enemy));
        ContinuationStamp actualFlanking = ContinuationStamp.CaptureLive(live);
        if (predictedFlanking != actualFlanking)
            throw new InvalidOperationException("Flanking post-action state differs from native: "
                + predictedFlanking.DescribeFirstDifference(actualFlanking));
        CardModel secondAttack = live.CreateCard(ModelDb.Card<StrikeIronclad>(), teammate);
        teammate.PlayerCombatState.Hand.AddInternal(secondAttack, -1);
        int hpBeforeFlankedHit = enemy.CurrentHp;
        NativeActionChecks.Execute(loop, new PlayCardAction(secondAttack, enemy));
        int flankedDamage = hpBeforeFlankedHit - enemy.CurrentHp;
        if (baseTeammateDamage <= 0 || flankedDamage != 2 * baseTeammateDamage)
            throw new InvalidOperationException("The native attack did not receive the expected observed Flanking effect.");

        File.WriteAllText(Path.Combine(output, "native-reviewed-card.json"), JsonSerializer.Serialize(new
        {
            Card = gangUp.Id.Entry, BaseDamage = baseDamage, BoostedDamage = boostedDamage, NativeDamage = actualDamage,
            result.CombatEndedTurn, result.TotalExpandedNodes, Suggestion = suggestion, DeploymentStop = "advice_only_card",
            Flanking = new { PostActionMatches = true, BaseTeammateDamage = baseTeammateDamage, FlankedDamage = flankedDamage },
        }, new JsonSerializerOptions { WriteIndented = true }));
        return $"NativeReviewedCard PASS gang_up={baseDamage}->{boostedDamage} flanking={baseTeammateDamage}->{flankedDamage}";

        void VerifyDeploymentRejects(PlanAction action)
        {
            IReadOnlyList<PlanAction> prefix = MultiplayerSafeLocalActionClassifier.TakeBoundedDeploymentSlice(
                live, [action], out SafeLocalActionDecision stop);
            if (prefix.Count != 0 || stop.IsSafe || stop.Reason != "advice_only_card")
                throw new InvalidOperationException($"{action.CardId} entered the multiplayer deployment slice.");
        }
    }
}
