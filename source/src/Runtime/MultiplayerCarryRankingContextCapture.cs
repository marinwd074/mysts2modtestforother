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
    internal static MultiplayerCarryRankingContext Capture(
        CombatState state,
        long worldVersion)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException(
                "Multiplayer carry ranking context must be captured on the main thread.");

        Player? localPlayer = LocalContext.GetMe(state);
        MultiplayerCarryRemotePlayerPublicState[] remotePlayers = state.Players
            .Where(player => localPlayer == null || player.NetId != localPlayer.NetId)
            .OrderBy(player => player.NetId)
            .Select(CaptureRemotePlayer)
            .ToArray();
        MultiplayerCarryEnemyPublicState[] enemies = state.Enemies
            .Select(CaptureEnemy)
            .ToArray();
        bool? scalingHooks = state.MultiplayerScalingModel?.ShouldReceiveCombatHooks;
        string cardConstraint = state.RunState.CardMultiplayerConstraint.ToString();

        return MultiplayerCarryRankingContext.Create(
            enabled: true,
            worldVersion,
            remotePlayers,
            enemies,
            scalingHooks,
            cardConstraint);
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

    private static MultiplayerCarryEnemyPublicState CaptureEnemy(Creature enemy)
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
            threatTarget);
    }

    private static MultiplayerCarryPowerPublicState[] PublicPowers(
        IEnumerable<PowerModel> powers)
        => powers
            .Where(power => power.Amount != 0)
            .OrderBy(power => power.Id.Entry, StringComparer.Ordinal)
            .Select(power => new MultiplayerCarryPowerPublicState(power.Id.Entry, power.Amount))
            .ToArray();

}
