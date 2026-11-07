using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CombatSolver;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using OfflineSearchHarness;

namespace U0U1PinnedHarness;

// One native battle per process. Both policy runs use the same legal teammate rule.
// The harness owns action dispatch and a deterministic clock for the observation gate.
internal static class NativeRedecisionChecks
{
    internal static string Run(CombatState combat, MainLoopContext loop, SolverDisplayNames names,
        SearchPolicySnapshot captured, bool selective, string outputDirectory)
    {
        Player local = LocalContext.GetMe(combat)!;
        Player teammate = combat.Players.Single(player => player != local);
        Creature enemy = combat.Enemies.Single();
        enemy.SetMaxHpInternal(60);
        enemy.SetCurrentHpInternal(60);
        string initialState = ContinuationStamp.CaptureLive(combat).StateText;
        SolverSearchProfile profile = new(8, 1200, 8, 4, 4, 20_000);
        List<string> deepening = [];
        SearchPolicySnapshot policy = captured with
        {
            Profile = profile, RoutePolicy = SearchRoutePolicy.MultiplayerSinglePlayerCore,
            CurrentTurnOnly = false, IncludeTurnSetup = false, FixedBudget = true, MaxDegreeOfParallelism = 1,
            UseSelectiveDeepening = selective, UseNoveltyPortfolio = false, NoveltySearch = null,
            UseBeamWidthPortfolio = false, UseMultiplayerTeamObjective = false, UseMultiplayerTeammateForecast = false,
            UseMultiplayerScenarioReevaluation = false, StopAtAcceptableBattleHpLoss = false,
            IgnoreLongTermRewards = true, RelicTargets = [], Interaction = null,
            PotionPolicy = SolverPotionPolicy.Disabled, PotionStrategy = new(SolverPotionPolicy.Disabled, []),
            MeasurePhasePerformance = true,
            Diagnostics = new SearchDiagnosticsSink(message =>
            {
                if (message.StartsWith("[CombatSolver/Test] SEARCH_DEEPENING ", StringComparison.Ordinal))
                    deepening.Add(message);
            }, _ => { }),
        };
        MultiplayerWorldObservation observation = new();
        long now = 0;
        List<object> turns = [];
        long staleNodes = 0, recoveredNodes = 0;
        int rejectedVersions = 0, teammatePlays = 0;
        for (int step = 0; step < 12 && CombatManager.Instance.IsInProgress; step++)
        {
            int turn = local.PlayerCombatState!.TurnNumber;
            string before = ContinuationStamp.CaptureLive(combat).StateText;
            observation.ObserveSnapshot(Fingerprint(before), "native_turn_start", now);
            now += MultiplayerWorldTracker.DefaultDebounceMilliseconds;
            if (observation.TryReadStable(now, out long initialVersion)) observation.TryConfirmStable(initialVersion, now);
            var old = Search(before, null);

            CardModel? teammateCard = teammate.PlayerCombatState!.Hand.Cards.FirstOrDefault(card =>
                card.Id.Entry == "STRIKE_IRONCLAD" && card.CanPlayTargeting(enemy));
            if (teammateCard != null)
            {
                Play(teammateCard, enemy);
                teammatePlays++;
            }
            string after = ContinuationStamp.CaptureLive(combat).StateText;
            bool changed = observation.ObserveSnapshot(Fingerprint(after), "native_teammate_action", now);
            if (changed != (teammateCard != null))
                throw new InvalidOperationException("Teammate action observation did not match native state change.");
            if (changed)
            {
                if (observation.IsStable(initialVersion, now) || observation.TryConfirmStable(initialVersion, now))
                    throw new InvalidOperationException("A stale result version survived a native teammate action.");
                rejectedVersions++;
                staleNodes += old.Result.TotalExpandedNodes;
                now += MultiplayerWorldTracker.DefaultDebounceMilliseconds;
                if (!observation.TryTakeStable(now, out _)) throw new InvalidOperationException("Native update did not settle.");
            }
            if (!CombatManager.Instance.IsInProgress)
            {
                turns.Add(new { Turn = turn, TeammateCard = teammateCard?.Id.Entry, Before = old.Metrics, TerminalAfterTeammate = true });
                break;
            }
            long acceptedVersion = observation.WorldVersion;
            var current = changed ? Search(after, MultiplayerPlanRefreshRequest.Capture(old.Result)) : old;
            if (changed) recoveredNodes += current.Result.TotalExpandedNodes;
            if (!observation.IsStable(acceptedVersion, now) || after != ContinuationStamp.CaptureLive(combat).StateText)
                throw new InvalidOperationException("Native redecision result was stale before dispatch.");

            List<string> played = [];
            foreach (PlanAction action in current.Result.BestNode.Actions.TakeWhile(action => action.Turn == turn))
            {
                if (action.Kind == PlanActionKind.EndTurn || !CombatManager.Instance.IsInProgress) break;
                if (action.Kind != PlanActionKind.PlayCard || !action.IsExecutable || action.Choice != null
                    || action.NestedChoices is { Count: > 0 } || action.EndsPlayerTurn)
                    throw new InvalidOperationException("Unsupported action in the native redecision fixture.");
                CardModel card = SolverController.FindCardForDeployment(local.PlayerCombatState.Hand.Cards, action);
                Creature? target = action.TargetCombatId is uint id
                    ? combat.Enemies.Single(creature => creature.CombatId == id && creature.IsAlive) : null;
                Play(card, target);
                played.Add(card.Id.Entry);
            }
            if (CombatManager.Instance.IsInProgress)
            {
                NativeActionChecks.Execute(loop, new EndPlayerTurnAction(teammate, teammate.PlayerCombatState.TurnNumber));
                NativeActionChecks.Execute(loop, new EndPlayerTurnAction(local, turn));
                if (CombatManager.Instance.IsInProgress && OfflineCombat.WaitForPlayableCombat(loop).RoundNumber != turn + 1)
                    throw new InvalidOperationException("Native redecision round did not advance.");
            }
            turns.Add(new { Turn = turn, TeammateCard = teammateCard?.Id.Entry, Before = old.Metrics,
                Accepted = current.Metrics, current.Result.PlanRefresh, Played = played,
                LocalHp = local.Creature.CurrentHp, EnemyHp = enemy.CurrentHp });
            now += 1;
        }
        bool won = !CombatManager.Instance.IsInProgress && enemy.IsDead && !local.Creature.IsDead;
        if (!won || rejectedVersions < 2 || (deepening.Count > 0) != selective)
            throw new InvalidOperationException($"Native redecision incomplete: won={won} updates={rejectedVersions} deepening={deepening.Count}.");
        int loss = BattleDamageTracker.Observe(combat).HpLostSoFar;
        string filename = selective ? "native-redecision-selective.json" : "native-redecision-fixed.json";
        File.WriteAllText(Path.Combine(outputDirectory, filename), JsonSerializer.Serialize(new
        {
            Selective = selective, Root = initialState, Profile = profile,
            DllSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(CombatBeamSolver).Assembly.Location))),
            Won = won, LocalHp = local.Creature.CurrentHp, BattleHpLost = loss, TeammatePlays = teammatePlays,
            RejectedVersions = rejectedVersions, StaleNodes = staleNodes, RecoveryNodes = recoveredNodes,
            Rounds = turns.Count, ShuffleCounter = combat.RunState.Rng.Shuffle.Counter, Turns = turns, Deepening = deepening,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return $"NativeRedecision PASS selective={selective} rounds={turns.Count} loss={loss} updates={rejectedVersions} stale_nodes={staleNodes} refresh_nodes={recoveredNodes}";

        void Play(CardModel card, Creature? target)
        {
            if (!card.CanPlayTargeting(target)) throw new InvalidOperationException($"Native card is not legal: {card.Id.Entry}.");
            int finished = CombatManager.Instance.History.CardPlaysFinished.Count(entry => ReferenceEquals(entry.CardPlay.Card, card));
            NativeActionChecks.Execute(loop, new PlayCardAction(card, target));
            if (CombatManager.Instance.IsInProgress
                && CombatManager.Instance.History.CardPlaysFinished.Count(entry => ReferenceEquals(entry.CardPlay.Card, card)) <= finished)
                throw new InvalidOperationException($"Native executor did not settle {card.Id.Entry}.");
        }

        (SolverResult Result, object Metrics) Search(string expectedRoot, MultiplayerPlanRefreshRequest? refresh)
        {
            Stopwatch watch = Stopwatch.StartNew();
            CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
            double capturedMs = watch.Elapsed.TotalMilliseconds;
            BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
            SearchPolicySnapshot request = policy with { MultiplayerPlanRefresh = refresh };
            double? previewMs = null, approvedMs = null;
            SolverResult result = Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage, request,
                CancellationToken.None, progress =>
                {
                    if (progress.CurrentTurnPreview?.Actions.Count > 0) previewMs ??= watch.Elapsed.TotalMilliseconds;
                    if (progress.OfficialPublishedOrigin != null) approvedMs ??= watch.Elapsed.TotalMilliseconds;
                })).GetAwaiter().GetResult();
            double completedMs = watch.Elapsed.TotalMilliseconds;
            if (result.TotalExpandedNodes > profile.MaxExpandedNodes || result.Snapshot.HasRisk
                || result.BestNode.Actions.Any(action => action.IsForecastOnlyObservation || action.ShadowForecast != null)
                || result.ResultScope != SolverResultScope.SearchCompletion)
                throw new InvalidOperationException("Native redecision exceeded its budget or returned an unapproved scope.");
            CombatBeamSolver verifier = new(root, names, damage, request, searchProfile: profile);
            var replay = verifier.ReplayDiagnosticPrefix(result.BestNode.Actions);
            try
            {
                if (verifier.CaptureDiagnosticContinuation(replay) != result.PredictedFinalState
                    || replay.CumulativePlayerHpLost != result.Snapshot.CumulativePlayerHpLost
                    || expectedRoot != ContinuationStamp.CaptureLive(combat).StateText)
                    throw new InvalidOperationException("Native redecision replay or root isolation failed.");
            }
            finally { replay.ReleaseSimulator(); }
            return (result, new { CapturedMs = capturedMs, PreviewMs = previewMs, ApprovedMs = approvedMs, CompletedMs = completedMs,
                result.TotalExpandedNodes, result.TotalTransitionCount, result.EvaluationWindow, result.ProjectedBattleHpLost,
                FirstAction = result.BestNode.Actions.FirstOrDefault(), result.RequestPhaseMetrics });
        }
    }

    private static StateFingerprint Fingerprint(string text)
    {
        StateFingerprintBuilder builder = new();
        builder.Add(text);
        return builder.Finish();
    }
}
