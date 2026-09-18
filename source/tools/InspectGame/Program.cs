using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Potions;

Type type = typeof(CardFactory);
foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
    .Where(static method => method.Name is "GetDistinctForCombat" or "GetForCombat" or "GetRandomCard")
    .OrderBy(static method => method.ToString(), StringComparer.Ordinal))
{
    Console.WriteLine($"--- {method} ---");
    try
    {
        foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(method))
            Console.WriteLine($"{instruction.opcode,-12} {FormatOperand(instruction.operand)}");
    }
    catch (Exception exception)
    {
        Console.WriteLine($"ERROR {exception}");
    }
}

foreach (Type modelType in new[] { typeof(CreativeAi), typeof(CreativeAiPower), typeof(AttackPotion), typeof(SkillPotion), typeof(PowerPotion), typeof(ColorlessPotion) })
{
    Console.WriteLine($"=== {modelType.FullName} ===");
    foreach (MethodInfo method in modelType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        .Where(static method => method.GetMethodBody() is not null)
        .OrderBy(static method => method.Name, StringComparer.Ordinal))
    {
        Console.WriteLine($"--- {method} ---");
        PrintInstructions(method);
        Type? machine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        if (machine?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is { } moveNext)
        {
            Console.WriteLine($"--- {moveNext} ---");
            PrintInstructions(moveNext);
        }
    }
}

foreach (Type inspectType in new[] { typeof(Player), typeof(CardPoolModel) })
{
    Console.WriteLine($"=== {inspectType.FullName} unlock methods ===");
    foreach (MethodInfo method in inspectType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(static method => method.Name.Contains("Unlocked", StringComparison.Ordinal)
            && method.GetMethodBody() is not null)
        .OrderBy(static method => method.ToString(), StringComparer.Ordinal))
    {
        Console.WriteLine($"--- {method} ---");
        PrintInstructions(method);
    }
}

foreach (Type extensionType in typeof(CardFactory).Assembly.GetTypes()
    .Where(static type => type.IsAbstract && type.IsSealed && type.Namespace?.StartsWith("MegaCrit.Sts2.Core", StringComparison.Ordinal) == true)
    .OrderBy(static type => type.FullName, StringComparer.Ordinal))
{
    foreach (MethodInfo method in extensionType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
        .Where(static method => method.Name is "TakeRandom" or "NextItem" or "FilterForPlayerCount" or "FilterForCombat")
        .Where(static method => method.GetMethodBody() is not null))
    {
        Console.WriteLine($"=== {method.DeclaringType?.FullName}.{method} ===");
        try
        {
            PrintInstructions(method.ContainsGenericParameters ? method.MakeGenericMethod(typeof(CardModel)) : method);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"ERROR {exception.GetType().Name}: {exception.Message}");
        }
    }
}

static string FormatOperand(object? operand)
    => operand switch
    {
        null => "",
        MethodBase method => method.ToString() ?? method.Name,
        Type type => type.FullName ?? type.Name,
        _ => operand.ToString() ?? ""
    };

static void PrintInstructions(MethodBase method)
{
    foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(method))
        Console.WriteLine($"{instruction.opcode,-12} {FormatOperand(instruction.operand)}");
}
