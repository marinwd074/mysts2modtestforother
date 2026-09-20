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
        string publicFingerprint = BuildPublicFingerprint(
            scalingHooks,
            cardConstraint,
            remotePlayers,
            enemies);
        StateFingerprint remotePublicFingerprint =
            MultiplayerClientProbe.CaptureContinuationRemotePublicFingerprint(state);

        return MultiplayerCarryRankingContext.Create(
            enabled: true,
            worldVersion,
            publicFingerprint,
            remotePublicFingerprint,
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

    private static string BuildPublicFingerprint(
        bool? scalingHooks,
        string cardConstraint,
        IReadOnlyList<MultiplayerCarryRemotePlayerPublicState> remotePlayers,
        IReadOnlyList<MultiplayerCarryEnemyPublicState> enemies)
    {
        string remote = string.Join(
            ';',
            remotePlayers.Select(player =>
                $"{player.NetId}:{player.CharacterId}:turn={player.TurnNumber}/phase={player.Phase}:" +
                $"hp={player.CurrentHp}/{player.MaxHp}:block={player.Block}:" +
                $"powers={PowerTokens(player.Powers)}"));
        string enemy = string.Join(
            ';',
            enemies.Select(item =>
                $"{item.CombatId}:{item.MonsterId}:" +
                $"hp={item.CurrentHp}/{item.MaxHp}:block={item.Block}:" +
                $"next={item.NextMoveId}:target={item.ThreatTarget}:" +
                $"powers={PowerTokens(item.Powers)}"));
        return $"scaling_hooks={scalingHooks?.ToString() ?? "-"};" +
               $"card_constraint={cardConstraint};remote={remote};enemies={enemy}";
    }

    private static string PowerTokens(
        IEnumerable<MultiplayerCarryPowerPublicState> powers)
        => string.Join(',', powers.Select(power => $"{power.PowerId}:{power.Amount}"));
}
