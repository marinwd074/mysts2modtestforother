using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sts2LocalInspector;

internal static class Program
{
    private static readonly (string Name, Regex Pattern)[] Categories =
    [
        ("devConsole", Rx(@"DevConsole|ConsoleCmd")),
        ("multiplayer", Rx(@"Multiplayer|ActionQueueSynchronizer|Net[A-Za-z0-9_]*Action|FastMp|Peer|Lobby")),
        ("rng", Rx(@"\bRng\b|Random|Shuffle|Seed|RandomizeRng")),
        ("combatActions", Rx(@"PlayCard|EndPlayerTurn|CombatAction|GameAction|ActionQueue|RequestEnqueue")),
        ("choices", Rx(@"Choice|Selection|Reward|Grid")),
        ("potions", Rx(@"Potion")),
        ("cards", Rx(@"\bCard\b|CardModel|CardPile|DrawPile|DiscardPile|ExhaustPile")),
        ("monsterIntent", Rx(@"MonsterMove|Intent|Targeting|NextMove"))
    ];

    public static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
                return SelfTest();

            var (gameArg, outputArg, monsterMoveIlOutputArg) = ParseArgs(args);
            var gameDir = gameArg ?? ResolveGameDir();
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            {
                Console.Error.WriteLine("STS2 game directory not found. Pass --game-dir <path> or set STS2_DIR.");
                return 2;
            }

            gameDir = Path.GetFullPath(gameDir);
            var dataDir = ResolveDataDir(gameDir);
            var assemblyPath = Path.Combine(dataDir, "sts2.dll");
            if (!File.Exists(assemblyPath))
            {
                Console.Error.WriteLine("sts2.dll not found under " + dataDir);
                return 3;
            }

            var output = Path.GetFullPath(outputArg
                ?? Path.Combine(Directory.GetCurrentDirectory(), ".local", "game-inspection", "sts2-local-index.json"));
            var result = Inspect(gameDir, dataDir, assemblyPath);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

            if (!string.IsNullOrWhiteSpace(monsterMoveIlOutputArg))
            {
                var monsterMoveOutput = Path.GetFullPath(monsterMoveIlOutputArg);
                var monsterMoveEvidence = MonsterMoveIlInspector.InspectPinned01071(assemblyPath);
                Directory.CreateDirectory(Path.GetDirectoryName(monsterMoveOutput)!);
                File.WriteAllText(
                    monsterMoveOutput,
                    JsonSerializer.Serialize(monsterMoveEvidence, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(
                    "STS2_MONSTER_MOVE_IL_PASS output=" + monsterMoveOutput
                    + " methods=" + monsterMoveEvidence.Methods.Count);
            }

            Console.WriteLine("STS2_LOCAL_INSPECTOR_PASS output=" + output);
            Console.WriteLine("types=" + result.Metadata.TotalTypes
                + " console=" + result.Capabilities.ConsoleCommands.Count
                + " multiplayer=" + Count(result.Metadata, "multiplayer")
                + " rng=" + Count(result.Metadata, "rng"));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("STS2_LOCAL_INSPECTOR_FAIL " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    private static InspectionResult Inspect(string gameDir, string dataDir, string assemblyPath)
    {
        var metadata = ReadMetadata(assemblyPath);
        var keyPaths = new[]
        {
            assemblyPath,
            Path.Combine(dataDir, "GodotSharp.dll"),
            Path.Combine(dataDir, "0Harmony.dll"),
            Path.Combine(dataDir, "steam_api64.dll"),
            Path.Combine(dataDir, "sts2.xml")
        };

        var consoleCommands = metadata.Categories.GetValueOrDefault("devConsole")?
            .Where(t => t.FullName.EndsWith("ConsoleCmd", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray() ?? [];

        var uses = new List<string>();
        if (consoleCommands.Length > 0) uses.Add("DevConsole commands can build deterministic runtime fixtures.");
        if (Count(metadata, "multiplayer") > 0) uses.Add("Native multiplayer actions and synchronization can be checked against the installed game.");
        if (Count(metadata, "rng") > 0) uses.Add("RNG/seed/shuffle APIs can support exact cross-turn validation.");
        if (Count(metadata, "choices") > 0) uses.Add("Choice/reward APIs can be indexed before choice automation.");
        if (Count(metadata, "potions") > 0) uses.Add("Potion APIs can be indexed before multiplayer potion support.");

        return new InspectionResult(
            1,
            DateTimeOffset.UtcNow,
            gameDir,
            dataDir,
            Names(Directory.EnumerateFiles(gameDir, "*", SearchOption.TopDirectoryOnly)),
            Names(Directory.EnumerateDirectories(gameDir, "*", SearchOption.TopDirectoryOnly)),
            Directory.EnumerateFiles(gameDir, "*.exe", SearchOption.TopDirectoryOnly).Select(FileRecordFor).ToArray(),
            Directory.EnumerateFiles(gameDir, "*.pck", SearchOption.TopDirectoryOnly).Select(FileRecordFor).ToArray(),
            keyPaths.Where(File.Exists).Select(FileRecordFor).ToArray(),
            metadata,
            new CapabilitySummary(consoleCommands, uses));
    }

    private static MetadataIndex ReadMetadata(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!pe.HasMetadata) throw new InvalidDataException(path + " has no .NET metadata.");
        var reader = pe.GetMetadataReader();
        var buckets = Categories.ToDictionary(c => c.Name, _ => new List<TypeSummary>(), StringComparer.Ordinal);
        var total = 0;

        foreach (var handle in reader.TypeDefinitions)
        {
            total++;
            var type = reader.GetTypeDefinition(handle);
            var name = reader.GetString(type.Name);
            if (name == "<Module>") continue;
            var ns = reader.GetString(type.Namespace);
            var full = string.IsNullOrWhiteSpace(ns) ? name : ns + "." + name;
            var methods = type.GetMethods().Select(h => reader.GetString(reader.GetMethodDefinition(h).Name))
                .Where(m => m is not ".ctor" and not ".cctor").Distinct().Order().Take(48).ToArray();
            var fields = type.GetFields().Select(h => reader.GetString(reader.GetFieldDefinition(h).Name))
                .Distinct().Order().Take(48).ToArray();
            var properties = type.GetProperties().Select(h => reader.GetString(reader.GetPropertyDefinition(h).Name))
                .Distinct().Order().Take(48).ToArray();
            var events = type.GetEvents().Select(h => reader.GetString(reader.GetEventDefinition(h).Name))
                .Distinct().Order().Take(48).ToArray();
            var searchable = full + " " + string.Join(' ', methods) + " " + string.Join(' ', fields)
                + " " + string.Join(' ', properties) + " " + string.Join(' ', events);

            foreach (var (category, pattern) in Categories)
                if (pattern.IsMatch(searchable))
                    buckets[category].Add(new TypeSummary(full, methods, fields, properties, events));
        }

        foreach (var bucket in buckets.Values)
        {
            bucket.Sort((a, b) => StringComparer.Ordinal.Compare(a.FullName, b.FullName));
            if (bucket.Count > 160) bucket.RemoveRange(160, bucket.Count - 160);
        }

        return new MetadataIndex(total, buckets.ToDictionary(
            p => p.Key, p => (IReadOnlyList<TypeSummary>)p.Value, StringComparer.Ordinal));
    }

    private static FileRecord FileRecordFor(string path)
    {
        var info = new FileInfo(path);
        string? assemblyName = null;
        string? assemblyVersion = null;
        string? productVersion = null;
        try
        {
            var name = AssemblyName.GetAssemblyName(path);
            assemblyName = name.Name;
            assemblyVersion = name.Version?.ToString();
        }
        catch { }
        try { productVersion = FileVersionInfo.GetVersionInfo(path).ProductVersion; } catch { }
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new FileRecord(info.Name, info.FullName, info.Length, hash, assemblyName, assemblyVersion, productVersion);
    }

    private static string ResolveDataDir(string gameDir)
    {
        foreach (var name in new[] { "data_sts2_windows_x86_64", "data_sts2_linuxbsd_x86_64" })
        {
            var path = Path.Combine(gameDir, name);
            if (Directory.Exists(path)) return path;
        }

        return Directory.EnumerateDirectories(gameDir, "data_sts2_*", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            ?? throw new DirectoryNotFoundException("No data_sts2_* directory under " + gameDir);
    }

    private static string? ResolveGameDir()
    {
        var env = Environment.GetEnvironmentVariable("STS2_DIR");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;

        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(@"D:\SteamLibrary\steamapps\common\Slay the Spire 2");
            candidates.Add(@"D:\Steam\steamapps\common\Slay the Spire 2");
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(pf86))
                candidates.Add(Path.Combine(pf86, "Steam", "steamapps", "common", "Slay the Spire 2"));
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(home, ".local", "share", "Steam", "steamapps", "common", "Slay the Spire 2"));
            candidates.Add(Path.Combine(home, ".steam", "steam", "steamapps", "common", "Slay the Spire 2"));
        }
        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static (string? GameDir, string? Output, string? MonsterMoveIlOutput) ParseArgs(string[] args)
    {
        string? game = null;
        string? output = null;
        string? monsterMoveIlOutput = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--game-dir" && i + 1 < args.Length) game = args[++i];
            else if (args[i] == "--output" && i + 1 < args.Length) output = args[++i];
            else if (args[i] == "--monster-move-il-output" && i + 1 < args.Length)
                monsterMoveIlOutput = args[++i];
            else throw new ArgumentException("Unknown or incomplete argument: " + args[i]);
        }
        return (game, output, monsterMoveIlOutput);
    }

    private static int SelfTest()
    {
        string assemblyPath = typeof(Program).Assembly.Location;
        var metadata = ReadMetadata(assemblyPath);
        if (metadata.TotalTypes <= 0 || string.IsNullOrWhiteSpace(JsonSerializer.Serialize(metadata)))
            return 1;

        int decodedInstructions = MonsterMoveIlInspector.SelfTestDecoder(assemblyPath);
        if (decodedInstructions <= 0)
            return 1;

        Console.WriteLine(
            "STS2_LOCAL_INSPECTOR_SELF_TEST_PASS types=" + metadata.TotalTypes
            + " il_instructions=" + decodedInstructions);
        return 0;
    }

    private static int Count(MetadataIndex index, string key)
        => index.Categories.GetValueOrDefault(key)?.Count ?? 0;

    private static string[] Names(IEnumerable<string> paths)
        => paths.Select(Path.GetFileName).Where(n => !string.IsNullOrWhiteSpace(n))
            .Cast<string>().Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private static Regex Rx(string pattern)
        => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}

internal sealed record InspectionResult(
    int SchemaVersion, DateTimeOffset InspectedUtc, string GameDirectory, string DataDirectory,
    IReadOnlyList<string> RootFiles, IReadOnlyList<string> RootDirectories,
    IReadOnlyList<FileRecord> Executables, IReadOnlyList<FileRecord> ResourcePacks,
    IReadOnlyList<FileRecord> KeyFiles, MetadataIndex Metadata, CapabilitySummary Capabilities);
internal sealed record FileRecord(
    string Name, string Path, long SizeBytes, string Sha256,
    string? AssemblyName, string? AssemblyVersion, string? ProductVersion);
internal sealed record MetadataIndex(
    int TotalTypes, IReadOnlyDictionary<string, IReadOnlyList<TypeSummary>> Categories);
internal sealed record TypeSummary(
    string FullName, IReadOnlyList<string> Methods, IReadOnlyList<string> Fields,
    IReadOnlyList<string> Properties, IReadOnlyList<string> Events);
internal sealed record CapabilitySummary(
    IReadOnlyList<string> ConsoleCommands, IReadOnlyList<string> PotentialProjectUses);
