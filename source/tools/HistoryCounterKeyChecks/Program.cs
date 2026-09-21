using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Orbs;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

string[] readers = ["GOLD_AXE","VOLTAIC","TEAR_ASUNDER","PULL_FROM_BELOW","MURDER","SUPERMASSIVE"];
foreach (string id in readers)
    Check(CombatHistoryCounterKey.AppliesTo(new HashSet<string>([id], StringComparer.Ordinal)), "reader id not gated: " + id);
Check(!CombatHistoryCounterKey.AppliesTo(new HashSet<string>(["STRIKE"], StringComparer.Ordinal)), "unrelated card enabled history key");

Player owner = new();
Player other = new();
CombatHistoryCounters counters = default;
List<CombatPredictionHistoryEntry> events =
[
    new CombatPredictionCardPlayFinishedEntry { Card = new CardSnapshot { Owner = other }, CardPlay = new CardPlay { Player = other }, WasEthereal = true },
    new CombatPredictionCardPlayFinishedEntry { Card = new CardSnapshot { Owner = owner }, CardPlay = new CardPlay { Player = owner }, WasEthereal = true },
    new CombatPredictionOrbChanneledEntry { Orb = new LightningOrb { Owner = owner } },
    new CombatPredictionDamageReceivedEntry { Receiver = owner.Creature, Result = new DamageResult { UnblockedDamage = 3 } },
    new CombatPredictionCardDrawnEntry { Card = new CardSnapshot { Owner = owner } },
    new CombatPredictionCardGeneratedEntry { Creator = owner },
];
foreach (CombatPredictionHistoryEntry entry in events)
    counters = counters.After(entry, owner);
Check(counters == new CombatHistoryCounters(2, 1, 1, 1, 1, 1), "incremental six-counter semantics drifted");

CombatPredictionHistory history = [];
history.AddRange(events);
Check(CombatHistoryCounters.Scan(history, owner) == counters, "incremental counters differ from independent scan");

StateFingerprintBuilder key = new();
CombatHistoryCounterKey.AppendCounters(ref key, counters);
Check(key.Snapshot() == "h|2|1|1|1|1|1", "history key marker/order changed");

CombatPredictionSimulator simulator = new();
simulator.History.Counters = counters;
StateFingerprintBuilder viaSimulator = new();
CombatHistoryCounterKey.Append(ref viaSimulator, simulator, owner);
Check(viaSimulator.Snapshot() == key.Snapshot(), "history key did not consume maintained totals");

Console.WriteLine($"HISTORY_COUNTER_KEY_CHECKS_PASS checks={checks}");
