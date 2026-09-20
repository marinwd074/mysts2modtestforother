using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal enum SolverSessionKind
{
    Singleplayer,
    MultiplayerProbe,
    MultiplayerAdvisor,
    MultiplayerSafeExecute,
}

internal readonly record struct SolverSessionCapabilitySet(
    SolverSessionKind Kind,
    bool CanSearch,
    bool CanDeploySimpleLocalActions,
    bool CanEndTurnAutomatically,
    bool CanDriveChoices,
    bool CanInterceptTurnSetup,
    bool CanCrossTurnSearch,
    bool CanCrossTurnReuse,
    bool CanFullAuto,
    bool CanUsePotionsAutomatically,
    bool CanUseFastDeployment,
    bool CanUseInstantDeployment,
    bool CanShowcase,
    bool CanPreCombatForecast,
    bool CanUploadRunStatistics)
{
    public bool IsMultiplayer => Kind != SolverSessionKind.Singleplayer;

    public string SearchRejection => Kind switch
    {
        SolverSessionKind.MultiplayerProbe => "多人精简模式尚未完成客户端能力探针，当前只记录只读状态。",
        SolverSessionKind.MultiplayerAdvisor => "多人精简模式当前不允许该搜索入口。",
        SolverSessionKind.MultiplayerSafeExecute => "多人安全执行模式当前不允许该搜索入口。",
        _ => "当前会话不允许搜索。",
    };

    public string DeploymentRejection => Kind switch
    {
        SolverSessionKind.MultiplayerProbe => "多人精简模式尚未完成客户端能力探针，当前不执行动作。",
        SolverSessionKind.MultiplayerAdvisor => "多人 Advisor 只显示路线，不自动执行动作。",
        SolverSessionKind.MultiplayerSafeExecute => "当前多人动作未通过安全本地动作分类。",
        _ => "当前会话不允许部署。",
    };
}

internal static class SolverSessionCapabilities
{
    internal const string MultiplayerModeEnvironmentVariable = "COMBATSOLVER_MULTIPLAYER_MODE";
    private const string ProbeEvidenceEnvironmentVariable = "COMBATSOLVER_MULTIPLAYER_PROBE_EVIDENCE";
    private const string MultiplayerInstanceEnvironmentVariable = "COMBATSOLVER_MULTIPLAYER_INSTANCE";
    private static readonly Lazy<bool> SafeExecuteLabAuthorized = new(EvaluateSafeExecuteLabAuthorization);

    public static bool IsNetworkMultiplayer
        => RunManager.Instance.IsInProgress
           && RunManager.Instance.NetService.Type != NetGameType.Singleplayer;

    internal static bool IsMultiplayerAdvisorOptedIn
        => string.Equals(
            Environment.GetEnvironmentVariable(MultiplayerModeEnvironmentVariable),
            "advisor",
            StringComparison.OrdinalIgnoreCase);

    internal static bool IsMultiplayerSafeExecuteLabOptedIn
        => SafeExecuteLabAuthorized.Value;

    public static SolverSessionCapabilitySet Capture(CombatState? state)
    {
        if (IsNetworkMultiplayer || state != null && state.Players.Count != 1)
        {
            if (IsMultiplayerSafeExecuteLabOptedIn)
                return MultiplayerSafeExecute;
            return IsMultiplayerAdvisorOptedIn ? MultiplayerAdvisor : MultiplayerProbe;
        }
        return Singleplayer;
    }

    public static SolverSessionCapabilitySet CaptureRun(RunState? run)
    {
        if (run is null)
            return IsNetworkMultiplayer ? MultiplayerProbe : Singleplayer;
        return CaptureRun(run.Players.Count);
    }

    public static SolverSessionCapabilitySet CaptureRun(int playerCount)
    {
        if (IsNetworkMultiplayer || playerCount != 1)
            return MultiplayerProbe;
        return Singleplayer;
    }

    private static bool EvaluateSafeExecuteLabAuthorization()
    {
        string? mode = Environment.GetEnvironmentVariable(MultiplayerModeEnvironmentVariable);
        bool probeEvidence = IsTruthy(Environment.GetEnvironmentVariable(ProbeEvidenceEnvironmentVariable));
        string? instanceRoot = Environment.GetEnvironmentVariable(MultiplayerInstanceEnvironmentVariable);
        bool ownedClientInstance = IsOwnedCombatSolverClientInstance(instanceRoot);
        return MultiplayerSafeExecutePolicy.CanGrantLabCapability(
            new(mode, probeEvidence, ownedClientInstance));
    }

    private static bool IsOwnedCombatSolverClientInstance(string? instanceRoot)
    {
        if (string.IsNullOrWhiteSpace(instanceRoot))
            return false;
        try
        {
            string root = Path.GetFullPath(instanceRoot).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string ownerPath = Path.Combine(root, "instance.json");
            string profilePath = Path.Combine(root, "multiplayer-profile.json");
            if (!File.Exists(ownerPath) || !File.Exists(profilePath))
                return false;

            using JsonDocument owner = JsonDocument.Parse(File.ReadAllText(ownerPath));
            using JsonDocument profile = JsonDocument.Parse(File.ReadAllText(profilePath));
            JsonElement ownerRoot = owner.RootElement;
            JsonElement profileRoot = profile.RootElement;
            return ownerRoot.TryGetProperty("schemaVersion", out JsonElement ownerSchema)
                   && ownerSchema.GetInt32() == 1
                   && ownerRoot.TryGetProperty("runtimeRoot", out JsonElement ownerRuntime)
                   && PathEquals(ownerRuntime.GetString(), root)
                   && profileRoot.TryGetProperty("schemaVersion", out JsonElement profileSchema)
                   && profileSchema.GetInt32() == 1
                   && profileRoot.TryGetProperty("runtimeRoot", out JsonElement profileRuntime)
                   && PathEquals(profileRuntime.GetString(), root)
                   && profileRoot.TryGetProperty("profile", out JsonElement profileName)
                   && string.Equals(
                       profileName.GetString(),
                       "ClientCombatSolver",
                       StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException
            or NotSupportedException
            or ArgumentException)
        {
            return false;
        }
    }

    private static bool PathEquals(string? value, string expected)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        string actual = Path.GetFullPath(value).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTruthy(string? value)
        => string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    public static SolverSessionCapabilitySet Singleplayer { get; } = new(
        SolverSessionKind.Singleplayer,
        CanSearch: true,
        CanDeploySimpleLocalActions: true,
        CanEndTurnAutomatically: true,
        CanDriveChoices: true,
        CanInterceptTurnSetup: true,
        CanCrossTurnSearch: true,
        CanCrossTurnReuse: true,
        CanFullAuto: true,
        CanUsePotionsAutomatically: true,
        CanUseFastDeployment: true,
        CanUseInstantDeployment: true,
        CanShowcase: true,
        CanPreCombatForecast: true,
        CanUploadRunStatistics: true);

    public static SolverSessionCapabilitySet MultiplayerProbe { get; } = new(
        SolverSessionKind.MultiplayerProbe,
        CanSearch: false,
        CanDeploySimpleLocalActions: false,
        CanEndTurnAutomatically: false,
        CanDriveChoices: false,
        CanInterceptTurnSetup: false,
        CanCrossTurnSearch: false,
        CanCrossTurnReuse: false,
        CanFullAuto: false,
        CanUsePotionsAutomatically: false,
        CanUseFastDeployment: false,
        CanUseInstantDeployment: false,
        CanShowcase: false,
        CanPreCombatForecast: false,
        CanUploadRunStatistics: false);

    public static SolverSessionCapabilitySet MultiplayerAdvisor { get; } = new(
        SolverSessionKind.MultiplayerAdvisor,
        CanSearch: true,
        CanDeploySimpleLocalActions: false,
        CanEndTurnAutomatically: false,
        CanDriveChoices: false,
        CanInterceptTurnSetup: false,
        CanCrossTurnSearch: false,
        CanCrossTurnReuse: false,
        CanFullAuto: false,
        CanUsePotionsAutomatically: false,
        CanUseFastDeployment: false,
        CanUseInstantDeployment: false,
        CanShowcase: false,
        CanPreCombatForecast: false,
        CanUploadRunStatistics: false);

    public static SolverSessionCapabilitySet MultiplayerSafeExecute { get; } = new(
        SolverSessionKind.MultiplayerSafeExecute,
        CanSearch: true,
        CanDeploySimpleLocalActions: true,
        CanEndTurnAutomatically: false,
        CanDriveChoices: false,
        CanInterceptTurnSetup: false,
        CanCrossTurnSearch: false,
        CanCrossTurnReuse: false,
        CanFullAuto: false,
        CanUsePotionsAutomatically: false,
        CanUseFastDeployment: false,
        CanUseInstantDeployment: false,
        CanShowcase: false,
        CanPreCombatForecast: false,
        CanUploadRunStatistics: false);
}
