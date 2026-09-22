using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace Sts2LocalInspector;

internal static class MonsterStaticMemberInspector
{
    private const string MonsterNamespace = "MegaCrit.Sts2.Core.Models.Monsters";

    internal static (int Types, int Members) Validate(string assemblyPath, string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Monster static member source not found.", sourcePath);

        Dictionary<string, string[]> expected = ParseSource(File.ReadAllText(sourcePath));
        using FileStream stream = File.OpenRead(assemblyPath);
        using PEReader pe = new(stream, PEStreamOptions.LeaveOpen);
        if (!pe.HasMetadata)
            throw new InvalidDataException(assemblyPath + " has no .NET metadata.");
        MetadataReader reader = pe.GetMetadataReader();

        Dictionary<string, TypeDefinitionHandle> monsterTypes = new(StringComparer.Ordinal);
        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            string ns = reader.GetString(type.Namespace);
            if (!string.Equals(ns, MonsterNamespace, StringComparison.Ordinal))
                continue;

            string name = reader.GetString(type.Name);
            if (name == "<Module>")
                continue;
            if (!monsterTypes.TryAdd(name, handle))
                throw new InvalidDataException("Duplicate pinned monster type " + name + ".");
        }

        int memberCount = 0;
        foreach ((string typeName, string[] members) in expected.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!monsterTypes.TryGetValue(typeName, out TypeDefinitionHandle handle))
            {
                throw new MissingMemberException(
                    $"Pinned sts2.dll has no monster type {MonsterNamespace}.{typeName}.");
            }

            TypeDefinition type = reader.GetTypeDefinition(handle);
            HashSet<string> fields = type.GetFields()
                .Select(field => reader.GetString(reader.GetFieldDefinition(field).Name))
                .ToHashSet(StringComparer.Ordinal);
            HashSet<string> properties = type.GetProperties()
                .Select(property => reader.GetString(reader.GetPropertyDefinition(property).Name))
                .ToHashSet(StringComparer.Ordinal);

            foreach (string member in members)
            {
                memberCount++;
                if (!fields.Contains(member) && !properties.Contains(member))
                {
                    throw new MissingMemberException(
                        $"Pinned sts2.dll monster {typeName} has no direct field/property {member}.");
                }
            }
        }

        return (expected.Count, memberCount);
    }

    internal static bool SelfTest()
    {
        Dictionary<string, string[]> parsed = ParseSource(
            """
            ["Axebot"] = ["BootUpBlock", "BootUpStrGain", "StockAmount"],
            ["LouseProgenitor"] = ["CurlBlock", "_growStrength"],
            """);
        return parsed.Count == 2
            && parsed["Axebot"].SequenceEqual(
                ["BootUpBlock", "BootUpStrGain", "StockAmount"], StringComparer.Ordinal)
            && parsed["LouseProgenitor"].SequenceEqual(
                ["CurlBlock", "_growStrength"], StringComparer.Ordinal);
    }

    private static Dictionary<string, string[]> ParseSource(string source)
    {
        Dictionary<string, string[]> result = new(StringComparer.Ordinal);
        foreach (Match row in Regex.Matches(
                     source,
                     @"\[""(?<type>[^""]+)""\]\s*=\s*\[(?<members>[^\]]+)\]",
                     RegexOptions.CultureInvariant))
        {
            string typeName = row.Groups["type"].Value;
            string[] members = Regex.Matches(
                    row.Groups["members"].Value,
                    @"""(?<member>[^""]+)""",
                    RegexOptions.CultureInvariant)
                .Cast<Match>()
                .Select(match => match.Groups["member"].Value)
                .ToArray();

            if (members.Length == 0)
                throw new InvalidDataException($"Monster static member row {typeName} is empty.");
            if (!result.TryAdd(typeName, members))
                throw new InvalidDataException($"Duplicate monster static member row {typeName}.");
            if (members.Distinct(StringComparer.Ordinal).Count() != members.Length)
                throw new InvalidDataException($"Duplicate member in monster static row {typeName}.");
        }

        if (result.Count == 0)
            throw new InvalidDataException("No MonsterMoveEffects.StaticIntMembers rows were parsed.");
        return result;
    }
}
