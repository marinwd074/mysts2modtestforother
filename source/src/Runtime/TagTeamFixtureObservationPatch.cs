using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

internal sealed class TagTeamFixtureObservationPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_tag_team_fixture_observation";
    public static string Description => "记录多人实验中的原生 Tag Team Replay 次数";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(
            typeof(TagTeamPower),
            nameof(TagTeamPower.ModifyCardPlayCount),
            [typeof(CardModel), typeof(Creature), typeof(int)]),
    ];

    public static void Postfix(
        TagTeamPower __instance,
        CardModel card,
        Creature? target,
        int playCount,
        int __result)
    {
        string? fixtureName = MultiplayerConsoleFixtureRunner.ActiveFixtureName;
        if (string.IsNullOrWhiteSpace(fixtureName)
            || !fixtureName.StartsWith("tag-team-", StringComparison.Ordinal)
            || __result <= playCount)
        {
            return;
        }

        Entry.Logger.Info(
            $"[CombatSolver/MultiplayerFixture] TAG_TEAM_REPLAY_OBSERVED " +
            $"name={fixtureName} card={card.Id.Entry} " +
            $"owner={card.Owner.NetId} target={(target?.CombatId?.ToString() ?? "-")} " +
            $"applier={__instance.Applier?.Player?.NetId.ToString() ?? "-"} " +
            $"power_owner={__instance.Owner?.Player?.NetId.ToString() ?? "-"} " +
            $"play_count_before={playCount} play_count_after={__result} " +
            $"extra_plays={__result - playCount}");
    }
}
