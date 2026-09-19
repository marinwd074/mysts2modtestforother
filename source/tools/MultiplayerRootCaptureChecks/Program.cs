using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver;

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

Console.WriteLine($"PASS: {checks} multiplayer remote relic capture checks");
