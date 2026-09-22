using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed class MultiplayerConsoleFixtureDefinition
{
    public int SchemaVersion { get; init; }
    public string Name { get; init; } = string.Empty;
    public string WaitFor { get; init; } = string.Empty;
    public string[] Commands { get; init; } = [];
}

internal static class MultiplayerConsoleFixtureRunner
{
    internal const string FixtureEnvironmentVariable = "COMBATSOLVER_MULTIPLAYER_CONSOLE_FIXTURE";
    internal const string LocalPlayableTurnWaitToken = "local_playable_turn";
    internal const int MaxCommands = 64;
    internal const int MaxCommandLength = 256;

    private static readonly HashSet<string> AllowedVerbs = new(
        ["card", "power", "energy", "block", "potion", "draw", "heal", "damage"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<IReadOnlyDictionary<string, bool>> NetworkedCommands =
        new(DiscoverNetworkedCommands);

    private static bool _scheduled;
    private static bool _terminal;

    internal static void TrySchedule(CombatState state)
    {
        if (_scheduled || _terminal)
            return;
        string? fixturePath = Environment.GetEnvironmentVariable(FixtureEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(fixturePath))
            return;

        if (!TryLoadFixture(fixturePath, out MultiplayerConsoleFixtureDefinition? fixture, out string reason))
        {
            _terminal = true;
            Entry.Logger.Warn($"[CombatSolver/MultiplayerFixture] FIXTURE_REJECT reason={reason}");
            return;
        }

        if (!SolverSessionCapabilities.IsNetworkMultiplayer || state.Players.Count < 2)
        {
            _terminal = true;
            Entry.Logger.Warn("[CombatSolver/MultiplayerFixture] FIXTURE_REJECT reason=not_network_multiplayer");
            return;
        }

        _scheduled = true;
        Entry.Logger.Info(
            $"[CombatSolver/MultiplayerFixture] FIXTURE_ARMED name={fixture.Name} commands={fixture.Commands.Length}");
        Task task = SolverController.StartCombatDeferredOperation(
            token => RunFixtureAsync(state, fixture, token));
        TaskHelper.RunSafely(task);
    }

    private static async Task RunFixtureAsync(
        CombatState state,
        MultiplayerConsoleFixtureDefinition fixture,
        CancellationToken token)
    {
        try
        {
            NGame? host = NGame.Instance;
            if (host == null)
                throw new InvalidOperationException("game_host_missing");

            Player? localPlayer = null;
            for (int frame = 0; frame < 120; frame++)
            {
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                token.ThrowIfCancellationRequested();
                localPlayer = LocalContext.GetMe(state);
                if (frame >= 2
                    && localPlayer?.PlayerCombatState?.Phase == PlayerTurnPhase.Play
                    && state.CurrentSide == CombatSide.Player)
                {
                    break;
                }
            }

            localPlayer = LocalContext.GetMe(state);
            if (localPlayer?.PlayerCombatState?.Phase != PlayerTurnPhase.Play)
                throw new InvalidOperationException("local_playable_turn_timeout");
            if (!CombatManager.Instance.IsInProgress
                || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), state))
            {
                throw new InvalidOperationException("combat_lifecycle_changed");
            }
            if (SolverController.IsDeploying)
                throw new InvalidOperationException("solver_deployment_active");

            await RunManager.Instance.ActionExecutor.FinishedExecutingActions().WaitAsync(token);
            DevConsole console = new(true);

            for (int index = 0; index < fixture.Commands.Length; index++)
            {
                string command = fixture.Commands[index].Trim();
                if (!TryValidateNetworkedCommand(command, out string commandReason))
                    throw new InvalidOperationException($"command_{index}_{commandReason}");

                long beforeWorldVersion = MultiplayerWorldTracker.WorldVersion;
                Entry.Logger.Info(
                    $"[CombatSolver/MultiplayerFixture] FIXTURE_COMMAND_START " +
                    $"name={fixture.Name} index={index} command={QuoteForLog(command)} " +
                    $"world_version={beforeWorldVersion}");

                CmdResult result = console.ProcessCommand(command);
                if (!result.success)
                    throw new InvalidOperationException($"command_{index}_rejected:{result.msg}");

                if (result.task != null)
                    await result.task.WaitAsync(token);

                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                token.ThrowIfCancellationRequested();
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions().WaitAsync(token);
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                token.ThrowIfCancellationRequested();

                Entry.Logger.Info(
                    $"[CombatSolver/MultiplayerFixture] FIXTURE_COMMAND_RESULT " +
                    $"name={fixture.Name} index={index} success=true message={QuoteForLog(result.msg)} " +
                    $"world_version_before={beforeWorldVersion} " +
                    $"world_version_after={MultiplayerWorldTracker.WorldVersion}");
            }

            _terminal = true;
            Entry.Logger.Info(
                $"[CombatSolver/MultiplayerFixture] FIXTURE_COMPLETE name={fixture.Name} " +
                $"commands={fixture.Commands.Length} world_version={MultiplayerWorldTracker.WorldVersion}");
        }
        catch (OperationCanceledException)
        {
            Entry.Logger.Info(
                $"[CombatSolver/MultiplayerFixture] FIXTURE_CANCELLED name={fixture.Name} reason=combat_lifecycle");
        }
        catch (Exception exception)
        {
            _terminal = true;
            Entry.Logger.Warn(
                $"[CombatSolver/MultiplayerFixture] FIXTURE_FAIL name={fixture.Name} " +
                $"exception={exception.GetType().Name} message={QuoteForLog(exception.Message)}");
        }
        finally
        {
            _scheduled = false;
        }
    }

    private static bool TryLoadFixture(
        string fixturePath,
        out MultiplayerConsoleFixtureDefinition? fixture,
        out string reason)
    {
        fixture = null;
        reason = string.Empty;
        string? instanceRoot = Environment.GetEnvironmentVariable(
            SolverSessionCapabilities.MultiplayerInstanceEnvironmentVariable);
        if (!SolverSessionCapabilities.IsTruthy(Environment.GetEnvironmentVariable(
                SolverSessionCapabilities.ProbeEvidenceEnvironmentVariable)))
        {
            reason = "probe_evidence_disabled";
            return false;
        }
        if (!SolverSessionCapabilities.IsOwnedCombatSolverClientInstance(instanceRoot))
        {
            reason = "instance_not_owned_client_solver";
            return false;
        }

        try
        {
            string root = Path.GetFullPath(instanceRoot!).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string path = Path.GetFullPath(fixturePath);
            string rootPrefix = root + Path.DirectorySeparatorChar;
            if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                reason = "fixture_outside_instance";
                return false;
            }
            if (!File.Exists(path))
            {
                reason = "fixture_missing";
                return false;
            }

            fixture = JsonSerializer.Deserialize<MultiplayerConsoleFixtureDefinition>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (fixture == null)
            {
                reason = "fixture_empty";
                return false;
            }
            return ValidateDefinition(fixture, out reason);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException
            or ArgumentException)
        {
            reason = $"fixture_read_{exception.GetType().Name}";
            fixture = null;
            return false;
        }
    }

    private static bool ValidateDefinition(
        MultiplayerConsoleFixtureDefinition fixture,
        out string reason)
    {
        if (fixture.SchemaVersion != 1)
        {
            reason = "schema_version";
            return false;
        }
        if (string.IsNullOrWhiteSpace(fixture.Name))
        {
            reason = "name_missing";
            return false;
        }
        if (!string.Equals(
                fixture.WaitFor,
                LocalPlayableTurnWaitToken,
                StringComparison.OrdinalIgnoreCase))
        {
            reason = "wait_for_unsupported";
            return false;
        }
        if (fixture.Commands.Length is < 1 or > MaxCommands)
        {
            reason = "command_count";
            return false;
        }
        for (int index = 0; index < fixture.Commands.Length; index++)
        {
            if (!TryValidateNetworkedCommand(fixture.Commands[index], out reason))
            {
                reason = $"command_{index}_{reason}";
                return false;
            }
        }

        reason = "ok";
        return true;
    }

    private static bool TryValidateNetworkedCommand(string command, out string reason)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            reason = "empty";
            return false;
        }
        if (command.Length > MaxCommandLength || command.Contains('\n') || command.Contains('\r'))
        {
            reason = "format";
            return false;
        }

        string verb = command.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        if (!AllowedVerbs.Contains(verb))
        {
            reason = $"verb_not_allowed_{verb}";
            return false;
        }
        if (!NetworkedCommands.Value.TryGetValue(verb, out bool isNetworked))
        {
            reason = $"verb_missing_{verb}";
            return false;
        }
        if (!isNetworked)
        {
            reason = $"verb_not_networked_{verb}";
            return false;
        }

        reason = "ok";
        return true;
    }

    private static IReadOnlyDictionary<string, bool> DiscoverNetworkedCommands()
    {
        Dictionary<string, bool> commands = new(StringComparer.OrdinalIgnoreCase);
        Type baseType = typeof(AbstractConsoleCmd);
        foreach (Type type in baseType.Assembly.GetTypes())
        {
            if (type.IsAbstract
                || !baseType.IsAssignableFrom(type)
                || type.GetConstructor(Type.EmptyTypes) == null)
            {
                continue;
            }

            try
            {
                if (Activator.CreateInstance(type) is AbstractConsoleCmd command)
                    commands[command.CmdName] = command.IsNetworked;
            }
            catch (Exception)
            {
                // A missing/broken built-in command fails closed at validation time.
            }
        }
        return commands;
    }

    private static string QuoteForLog(string value)
        => JsonSerializer.Serialize(value);
}
