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
        SearchPolicySnapshot captured, bool selective, string outputDirectory, bool holdout = false,
        int? adaptiveTurnLimit = null)
    {
        Player local = LocalContext.GetMe(combat)!;
        Player teammate = combat.Players.Single(player => player != local);
        Creature[] enemies = combat.Enemies.OrderBy(creature => creature.CombatId).ToArray();
        if (enemies.Length != (holdout ? 3 : 1))
            throw new InvalidOperationException("Unexpected native redecision encounter size.");
        for (int index = 0; index < enemies.Length; index++)
        {
            int hp = holdout ? index == 0 ? 6 : 32 : 60;
            enemies[index].SetMaxHpInternal(hp);
            enemies[index].SetCurrentHpInternal(hp);
        }
        if (holdout)
        {
            // Freeze two legal openings in the test player's hand. Later decisions use
            // the cards actually drawn and the targets still alive in each run.
            var hand = teammate.PlayerCombatState!.Hand;
            hand.Clear(silent: true);
            CardModel strike = ModelDb.AllCards.Single(card => card.Id.Entry == "STRIKE_IRONCLAD");
            hand.AddInternal(combat.CreateCard(strike, teammate), -1);
            hand.AddInternal(combat.CreateCard(strike, teammate), -1);
        }
        string initialState = ContinuationStamp.CaptureLive(combat).StateText;
        SolverSearchProfile profile = new(8, 1200, 8, 4, 4, 20_000);
        List<string> deepening = [];
        SearchPolicySnapshot policy = captured with
        {
            Profile = profile, RoutePolicy = SearchRoutePolicy.MultiplayerSinglePlayerCore,
            LocalCoreAdaptiveTurnLimit = adaptiveTurnLimit,
            CurrentTurnOnly = false, IncludeTurnSetup = false, FixedBudget = true, MaxDegreeOfParallelism = 1,
            UseSelectiveDeepening = selective, UseNoveltyPortfolio = false, NoveltySearch = null,
            UseBeamWidthPortfolio = false, UseMultiplayerTeamObjective = false, UseMultiplayerTeammateForecast = false,
            UseMultiplayerScenarioReevaluation = false, StopAtAcceptableBattleHpLoss = false,
            IgnoreLongTermRewards = true, RelicTargets = [], Interaction = null,
            PotionPolicy = SolverPotionPolicy.Disabled, PotionStrategy = new(SolverPotionPolicy.Disabled, []),
            MeasurePhasePerformance = true,
        };
        MultiplayerWorldObservation observation = new();
        MultiplayerSearchChurn churn = new();
        long now = 0;
        List<object> turns = [];
        long staleNodes = 0, recoveredNodes = 0;
        int rejectedVersions = 0, teammatePlays = 0, killedTargets = 0, staleTargetRoutes = 0, coalescedUpdates = 0;
        int deepRoutes = 0, verifiedCheckpoints = 0;
        int deferredRequests = 0, deferredRefreshProbes = 0, rejectedWithoutMaterialization = 0;
        List<string> recoveryDiagnostics = [];
        using CombatReplayOutcome nativeOutcome = new(combat);
        object? cancelled = holdout ? CheckCancellation() : null;
        for (int step = 0; step < 12 && CombatManager.Instance.IsInProgress; step++)
        {
            int turn = local.PlayerCombatState!.TurnNumber;
            string before = ContinuationStamp.CaptureLive(combat).StateText;
            observation.ObserveSnapshot(Fingerprint(before), "native_turn_start", now);
            now += MultiplayerWorldTracker.DefaultDebounceMilliseconds;
            if (observation.TryReadStable(now, out long initialVersion)) observation.TryConfirmStable(initialVersion, now);
            var old = Search(before, null);

            List<uint?> teammateTargets = [];
            string after = before;
            int teammateActions = holdout && step == 0 ? 2 : 1;
            for (int actionIndex = 0; actionIndex < teammateActions && CombatManager.Instance.IsInProgress; actionIndex++)
            {
                Creature? target = enemies.Where(enemy => enemy.IsAlive).OrderBy(enemy => enemy.CurrentHp)
                    .ThenBy(enemy => enemy.CombatId).FirstOrDefault();
                if (target == null) break;
                CardModel? teammateCard = teammate.PlayerCombatState!.Hand.Cards.FirstOrDefault(card =>
                    card.Id.Entry == "STRIKE_IRONCLAD" && card.CanPlayTargeting(target));
                if (teammateCard == null) break;
                long previousVersion = observation.WorldVersion;
                Play(teammateCard, target);
                teammateTargets.Add(target.CombatId);
                teammatePlays++;
                if (target.IsDead)
                {
                    killedTargets++;
                    if (old.Result.BestNode.Actions.Any(action => action.TargetCombatId == target.CombatId)) staleTargetRoutes++;
                }
                after = ContinuationStamp.CaptureLive(combat).StateText;
                if (!observation.ObserveSnapshot(Fingerprint(after), "native_teammate_action", now)
                    || observation.IsStable(previousVersion, now) || observation.TryConfirmStable(previousVersion, now)
                    || observation.TryReadStable(now, out _))
                    throw new InvalidOperationException("Native teammate action did not invalidate the earlier observation.");
                churn.RecordInvalidation(turn);
                now += 1;
            }
            bool changed = teammateTargets.Count > 0;
            if (changed)
            {
                if (observation.IsStable(initialVersion, now) || observation.TryConfirmStable(initialVersion, now))
                    throw new InvalidOperationException("A stale result version survived a native teammate action.");
                rejectedVersions++;
                coalescedUpdates += teammateTargets.Count - 1;
                staleNodes += old.Result.TotalExpandedNodes;
                now += MultiplayerWorldTracker.DefaultDebounceMilliseconds;
                if (!observation.TryTakeStable(now, out _)) throw new InvalidOperationException("Native update did not settle.");
            }
            if (!CombatManager.Instance.IsInProgress)
            {
                turns.Add(new { Turn = turn, TeammateTargets = teammateTargets, Before = old.Metrics, TerminalAfterTeammate = true });
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
                NativeActionChecks.Execute(loop, new EndPlayerTurnAction(teammate, teammate.PlayerCombatState!.TurnNumber));
                NativeActionChecks.Execute(loop, new EndPlayerTurnAction(local, turn));
                if (CombatManager.Instance.IsInProgress && OfflineCombat.WaitForPlayableCombat(loop).RoundNumber != turn + 1)
                    throw new InvalidOperationException("Native redecision round did not advance.");
            }
            turns.Add(new { Turn = turn, TeammateTargets = teammateTargets, Before = old.Metrics,
                Accepted = current.Metrics, current.Result.PlanRefresh, Played = played,
                LocalHp = local.Creature.CurrentHp, EnemyHp = enemies.Sum(enemy => enemy.CurrentHp) });
            now += 1;
        }
        bool allPlayersAlive = combat.Players.All(player => !player.Creature.IsDead);
        bool won = !CombatManager.Instance.IsInProgress && enemies.All(enemy => enemy.IsDead) && allPlayersAlive;
        if (!won || rejectedVersions < 2 || (deepening.Count > 0) != selective)
            throw new InvalidOperationException($"Native redecision incomplete: won={won} updates={rejectedVersions} deepening={deepening.Count}.");
        if (holdout && (killedTargets == 0 || staleTargetRoutes == 0 || coalescedUpdates == 0))
            throw new InvalidOperationException($"Holdout did not cover a stale target and coalesced update: killed={killedTargets} stale={staleTargetRoutes} coalesced={coalescedUpdates}.");
        if (holdout && (deferredRequests == 0 || deferredRefreshProbes == 0 || rejectedWithoutMaterialization == 0))
            throw new InvalidOperationException("Holdout did not exercise deferred work and replay rejection.");
        CombatReplayOutcomeSnapshot actual = nativeOutcome.Capture(combat, ended: won);
        int loss = actual.HpLost;
        string filename = selective ? "native-redecision-selective.json" : "native-redecision-fixed.json";
        if (holdout) filename = "holdout-" + filename;
        if (adaptiveTurnLimit != null) filename = $"adaptive-{adaptiveTurnLimit}-" + filename;
        File.WriteAllText(Path.Combine(outputDirectory, filename), JsonSerializer.Serialize(new
        {
            Selective = selective, Holdout = holdout, Root = initialState, Profile = profile,
            DllSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(CombatBeamSolver).Assembly.Location))),
            Won = won, AllPlayersAlive = allPlayersAlive, LocalHp = local.Creature.CurrentHp,
            TeammateHp = teammate.Creature.CurrentHp, BattleHpLost = loss, NativeOutcome = actual, TeammatePlays = teammatePlays,
            RejectedVersions = rejectedVersions, KilledTargets = killedTargets, StaleTargetRoutes = staleTargetRoutes,
            CoalescedUpdates = coalescedUpdates, StaleNodes = staleNodes, RecoveryNodes = recoveredNodes,
            Rounds = turns.Count, ShuffleCounter = combat.RunState.Rng.Shuffle.Counter, Turns = turns,
            Deepening = deepening, Cancellation = cancelled,
            AdaptiveTurnLimit = adaptiveTurnLimit, DeepRoutes = deepRoutes, VerifiedCheckpoints = verifiedCheckpoints,
            DeferredRequests = deferredRequests, DeferredRefreshProbes = deferredRefreshProbes,
            RejectedWithoutMaterialization = rejectedWithoutMaterialization, RecoveryDiagnostics = recoveryDiagnostics,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return $"NativeRedecision PASS selective={selective} rounds={turns.Count} loss={loss} updates={rejectedVersions} stale_nodes={staleNodes} refresh_nodes={recoveredNodes}";

        object CheckCancellation()
        {
            CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
            using CancellationTokenSource cancellation = new();
            long cancelAt = 0;
            int callbackCount = 0;
            SearchRequestWorkSnapshot work = default;
            SearchPolicySnapshot request = policy with
            {
                RequestWorkObserver = snapshot => { work = snapshot; callbackCount++; },
            };
            BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
            try
            {
                Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage, request, cancellation.Token,
                    progress =>
                    {
                        if (progress.ExpandedNodes <= 0 || cancelAt != 0) return;
                        cancelAt = Stopwatch.GetTimestamp();
                        cancellation.Cancel();
                    })).GetAwaiter().GetResult();
                throw new InvalidOperationException("Cancelled holdout request returned a result.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested && cancelAt != 0)
            {
                double drainMs = Stopwatch.GetElapsedTime(cancelAt).TotalMilliseconds;
                if (callbackCount != 1 || work.ExpandedNodes <= 0 || work.ExpandedNodes > profile.MaxExpandedNodes
                    || root.ContinuationStamp.StateText != ContinuationStamp.CaptureLive(combat).StateText)
                    throw new InvalidOperationException("Cancelled holdout request lost work accounting or changed the native root.");
                return new { DrainMs = drainMs, Work = work };
            }
        }

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
            bool defer = churn.ShouldDeferOptionalWork(root.StartTurnNumber);
            if (defer) deferredRequests++;
            SearchPolicySnapshot request = policy with
            {
                MultiplayerPlanRefresh = refresh,
                DeferLocalMultiplayerSpeculation = defer,
                Diagnostics = new SearchDiagnosticsSink(message =>
                {
                    if (message.StartsWith("[CombatSolver/Test] SEARCH_DEEPENING ", StringComparison.Ordinal))
                        deepening.Add(message);
                    if (message.Contains("stopped=observed_churn", StringComparison.Ordinal))
                        recoveryDiagnostics.Add(message);
                    if (message.Contains("status=quality_rejected reason=quality_rejected materialized=false", StringComparison.Ordinal))
                        rejectedWithoutMaterialization++;
                }, _ => { }),
            };
            double? previewMs = null, approvedMs = null;
            SolverResult result = Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage, request,
                CancellationToken.None, progress =>
                {
                    if (progress.CurrentTurnPreview?.Actions.Count > 0) previewMs ??= watch.Elapsed.TotalMilliseconds;
                    if (progress.OfficialPublishedOrigin != null) approvedMs ??= watch.Elapsed.TotalMilliseconds;
                })).GetAwaiter().GetResult();
            double completedMs = watch.Elapsed.TotalMilliseconds;
            if (result.PlanRefresh?.Reason == "prefix_probe_deferred_after_churn")
                deferredRefreshProbes++;
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
            if (result.EvaluationWindow?.TargetTurn > root.StartTurnNumber + SearchEvaluationPolicy.RollingHorizonTurnLayers)
            {
                deepRoutes++;
                foreach (CachedContinuation checkpoint in result.Continuations)
                {
                    var prefix = verifier.ReplayDiagnosticPrefix(result.BestNode.Actions
                        .TakeWhile(action => action.Turn < checkpoint.StartTurnNumber).ToArray());
                    try
                    {
                        if (verifier.CaptureDiagnosticContinuation(prefix) != checkpoint.ExpectedState)
                            throw new InvalidOperationException("Deep route retained a mismatched continuation checkpoint.");
                        verifiedCheckpoints++;
                    }
                    finally { prefix.ReleaseSimulator(); }
                }
            }
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
