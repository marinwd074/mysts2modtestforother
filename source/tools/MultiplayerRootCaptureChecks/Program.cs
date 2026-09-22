using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver;
using CombatSolver.Engine.Common;
using System.Runtime.CompilerServices;

int checks = 0;

void Check(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException(description);
    Console.WriteLine($"PASS {++checks}: {description}");
}

Check(
    MultiplayerRemotePublicRelicSupport.IsKnownCurrentTurnIrrelevant(typeof(BurningBlood)),
    "BurningBlood is the exact audited remote public relic allow-list entry.");
Check(
    !MultiplayerRemotePublicRelicSupport.IsKnownCurrentTurnIrrelevant(typeof(IceCream)),
    "An unevaluated concrete relic remains unsupported and fail closed.");
Check(
    !MultiplayerRemotePublicRelicSupport.IsKnownCurrentTurnIrrelevant(typeof(RelicModel)),
    "The abstract RelicModel base type is not accepted as a semantic contract.");
Check(
    !MultiplayerRemotePublicRelicSupport.IsKnownCurrentTurnIrrelevant(typeof(AbstractModel)),
    "A non-relic model cannot enter the remote relic allow-list.");

Check(
    MultiplayerRemotePublicRelicSupport.IsKnownCurrentTurnIrrelevant(typeof(BurningBlood)),
    "The audited BurningBlood semantic is supported by the boundary contract.");
Check(
    !MultiplayerRemotePublicRelicSupport.IsKnownCurrentTurnIrrelevant(typeof(IceCream)),
    "An unknown remote relic remains unsupported by the runtime boundary contract.");

PlayerBoundaryContractChecks();

Check(
    !MultiplayerAdvisorBoundaryContracts.ShouldApplyEnemyBlockScaling(false, false, true),
    "A local-player block does not enter enemy multiplayer scaling.");
Check(
    MultiplayerAdvisorBoundaryContracts.ShouldApplyEnemyBlockScaling(true, false, true),
    "A powered primary-enemy block enters multiplayer scaling.");
Check(
    !MultiplayerAdvisorBoundaryContracts.ShouldApplyEnemyBlockScaling(true, false, false),
    "An unpowered enemy block preserves the native early exit.");

Console.WriteLine($"PASS: {checks} multiplayer root/phase boundary checks");

void PlayerBoundaryContractChecks()
{
    Player local = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
    Player remote = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
    IReadOnlyList<Player> captured = [local, remote];

    Check(
        MultiplayerAdvisorBoundaryContracts.IsCapturedPlayer(captured, local),
        "The local player is recognized as captured.");
    Check(
        MultiplayerAdvisorBoundaryContracts.IsCapturedPlayer(captured, remote),
        "A locally readable remote player is recognized as captured.");
    MultiplayerAdvisorBoundaryContracts.RequireCapturedPlayer(captured, remote);
    Check(true, "A side-turn phase may read a captured remote player.");
    Check(
        ReferenceEquals(
            MultiplayerAdvisorBoundaryContracts.SelectEndTurnPlayers([local, remote], captured),
            captured),
        "EndTurn returns the captured readable roster.");
    IReadOnlyList<Player> singleplayerRoster = [local, remote];
    Check(
        ReferenceEquals(
            MultiplayerAdvisorBoundaryContracts.SelectEndTurnPlayers(singleplayerRoster, singleplayerRoster),
            singleplayerRoster),
        "Singleplayer EndTurn retains the complete player roster.");

    bool rejected = false;
    try
    {
        MultiplayerAdvisorBoundaryContracts.SelectEndTurnPlayers([local], [remote]);
    }
    catch (InvalidOperationException)
    {
        rejected = true;
    }
    Check(rejected, "EndTurn rejects a root player outside the public roster.");
}
