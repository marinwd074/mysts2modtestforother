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

/// <summary>
/// The session contract consumed by Runtime control surfaces. Multiplayer profiles are
/// intentionally explicit so a future advisor or safe-execute rollout cannot silently
/// inherit single-player choices, potions, turn setup, or end-turn behavior.
/// </summary>
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
    public static bool IsNetworkMultiplayer
        => RunManager.Instance.IsInProgress
           && RunManager.Instance.NetService.Type != NetGameType.Singleplayer;

    /// <summary>
    /// Multiplayer remains in read-only Probe until a real Host/Client evidence gate is
    /// recorded. The Advisor and SafeExecute profiles are defined here but are not
    /// activated by inference from player count or NetService type.
    /// </summary>
    public static SolverSessionCapabilitySet Capture(CombatState? state)
    {
        if (IsNetworkMultiplayer || state != null && state.Players.Count != 1)
            return MultiplayerProbe;
        return Singleplayer;
    }

    /// <summary>
    /// Captures the same boundary for run-level APIs that execute outside combat.
    /// A run with multiple players or a non-singleplayer transport must not inherit
    /// singleplayer-only forecast, replay, showcase, or telemetry capabilities.
    /// </summary>
    public static SolverSessionCapabilitySet CaptureRun(RunState? run)
    {
        if (run is null)
            return IsNetworkMultiplayer ? MultiplayerProbe : Singleplayer;
        return CaptureRun(run.Players.Count);
    }

    /// <summary>
    /// Applies the run-level boundary to serialized/history shapes that expose only
    /// their player count. The transport check still comes from the active session.
    /// </summary>
    public static SolverSessionCapabilitySet CaptureRun(int playerCount)
    {
        if (IsNetworkMultiplayer || playerCount != 1)
            return MultiplayerProbe;
        return Singleplayer;
    }

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
