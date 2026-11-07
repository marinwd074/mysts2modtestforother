using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

/// <summary>
/// Main-thread-only capture for the public input used by Multiplayer Carry Ranking.
/// Keep this separate from the search evaluator so live game objects never cross the
/// background-search boundary.
/// </summary>
internal static class MultiplayerCarryRankingContextCapture
{
    internal static MultiplayerCarryRankingContext CaptureContinuationBoundary(
        CombatState state,
        long worldVersion)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException(
                "Multiplayer continuation boundary must be captured on the main thread.");

        return MultiplayerCarryRankingContext.Create(
            enabled: false,
            worldVersion,
            remotePlayers: [],
            enemies: [],
            state.MultiplayerScalingModel?.ShouldReceiveCombatHooks,
            state.RunState.CardMultiplayerConstraint.ToString());
    }

    internal static MultiplayerCarryRankingContext Capture(
        CombatState state,
        long worldVersion,
        bool useAdvertisedThreat = false)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException(
                "Multiplayer carry ranking context must be captured on the main thread.");

        Player? localPlayer = LocalContext.GetMe(state);
        Player[] remotes = state.Players
            .Where(player => localPlayer == null || player.NetId != localPlayer.NetId)
            .OrderBy(player => player.NetId)
            .ToArray();
        MultiplayerCarryRemotePlayerPublicState[] remotePlayers = remotes.Select(CaptureRemotePlayer).ToArray();
        MultiplayerCarryEnemyPublicState[] enemies = state.Enemies
            .Select(enemy => CaptureEnemy(enemy, useAdvertisedThreat ? remotes : []))
            .ToArray();
        bool? scalingHooks = state.MultiplayerScalingModel?.ShouldReceiveCombatHooks;
        string cardConstraint = state.RunState.CardMultiplayerConstraint.ToString();

        return MultiplayerCarryRankingContext.Create(
            enabled: true,
            worldVersion,
            remotePlayers,
            enemies,
            scalingHooks,
            cardConstraint,
            useAdvertisedThreat);
    }

    private static MultiplayerCarryRemotePlayerPublicState CaptureRemotePlayer(Player player)
    {
        PlayerCombatState? combat = player.PlayerCombatState;
        return new MultiplayerCarryRemotePlayerPublicState(
            player.NetId.ToString(),
            player.Character.Id.Entry,
            player.Creature.CurrentHp,
            player.Creature.MaxHp,
            player.Creature.Block,
            combat?.TurnNumber ?? -1,
            combat?.Phase.ToString() ?? string.Empty,
            PublicPowers(player.Creature.Powers));
    }

    private static MultiplayerCarryEnemyPublicState CaptureEnemy(Creature enemy, IReadOnlyList<Player> remotes)
    {
        MonsterModel? monster = enemy.Monster;
        bool isBaseGameMonster = monster != null
            && monster.GetType().Assembly == typeof(MonsterModel).Assembly;
        bool hasAttackIntent = monster?.NextMove?.Intents.Any(intent => intent is AttackIntent) == true;
        MultiplayerCarryThreatTarget threatTarget =
            MultiplayerCarryThreatTargetContracts.ClassifyBaseGameMove(
                isBaseGameMonster,
                hasAttackIntent);

        return new MultiplayerCarryEnemyPublicState(
            enemy.CombatId ?? uint.MaxValue,
            monster?.Id.Entry ?? string.Empty,
            enemy.CurrentHp,
            enemy.MaxHp,
            enemy.Block,
            monster?.NextMove?.Id.ToString() ?? string.Empty,
            PublicPowers(enemy.Powers),
            threatTarget,
            monster != null && threatTarget == MultiplayerCarryThreatTarget.AllPlayers && remotes.Count > 0
                ? CaptureAdvertisedDamage(monster, remotes) : null);
    }

    private static IReadOnlyList<MultiplayerAdvertisedDamage> CaptureAdvertisedDamage(MonsterModel monster,
        IReadOnlyList<Player> remotes)
    {
        if (monster.NextMove is not { } move)
            return [];
        AttackIntent[] attacks = move.Intents.OfType<AttackIntent>().ToArray();
        if (attacks.Any(attack => attack.GetType().Assembly != typeof(AttackIntent).Assembly
            || (attack.DamageCalc?.Target != null && !IntentForecaster.IsKnownStableAttack(monster, move.Id))))
            return [];
        List<MultiplayerAdvertisedDamage> result = [];
        foreach (Player remote in remotes)
        {
            if (remote.Creature.IsDead)
                continue;
            long total = attacks.Sum(attack =>
                (long)Math.Max(0, attack.GetSingleDamage([remote.Creature], monster.Creature)) * Math.Max(1, attack.Repeats));
            if (total > 0)
                result.Add(new(remote.NetId.ToString(), (int)Math.Min(int.MaxValue, total)));
        }
        return result;
    }

    private static MultiplayerCarryPowerPublicState[] PublicPowers(
        IEnumerable<PowerModel> powers)
        => powers
            .Where(power => power.Amount != 0)
            .OrderBy(power => power.Id.Entry, StringComparer.Ordinal)
            .Select(power => new MultiplayerCarryPowerPublicState(power.Id.Entry, power.Amount))
            .ToArray();

}
