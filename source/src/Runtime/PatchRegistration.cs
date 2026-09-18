using STS2RitsuLib;
using STS2RitsuLib.Patching.Core;

namespace CombatSolver;

// Owns the complete required-patch list so Entry remains responsible for startup
// lifecycle wiring rather than the patch registry's version-specific inventory.
internal static class PatchRegistration
{
    public static void ApplyRequiredPatches(string modId, Action onFailure)
    {
        var patcher = RitsuLibFramework.CreatePatcher(modId, "combat-solver", "战斗路线求解器");
        patcher.RegisterPatch<PlayerTurnSetupPatch>();
        patcher.RegisterPatch<PlayerTurnAutoPrePlayPatch>();
        patcher.RegisterPatch<PlayerTurnSetupSceneExitPatch>();
        patcher.RegisterPatch<ChooseCardObservationPatch>();
        patcher.RegisterPatch<SimpleGridObservationPatch>();
        patcher.RegisterPatch<RewardGridObservationPatch>();
        patcher.RegisterPatch<CombatPileObservationPatch>();
        patcher.RegisterPatch<HandObservationPatch>();
        patcher.RegisterPatch<HandUpgradeObservationPatch>();
        patcher.RegisterPatch<CombatStateTrackerIsolationPatch>();
        patcher.RegisterPatch<RitsuFreePlayVoidIsolationPatch>();
        patcher.RegisterPatch<RitsuFreePlayBoolIsolationPatch>();
        patcher.RegisterPatch<RitsuFreePlayResolveIsolationPatch>();
        patcher.RegisterPatch<RitsuDefaultCapabilityRegistrationPatch>();
        patcher.RegisterPatch<RitsuEmptyCardTypeFastPathPatch>();
        patcher.RegisterPatch<RitsuEmptyCardRarityFastPathPatch>();
        patcher.RegisterPatch<RitsuEmptyCardTagsFastPathPatch>();
        patcher.RegisterPatch<RitsuEmptyEnergyContributorFastPathPatch>();
        patcher.RegisterPatch<RitsuEmptyEnergyCostFastPathPatch>();
        patcher.RegisterPatch<RitsuEmptyStarContributorFastPathPatch>();
        patcher.RegisterPatch<RitsuEmptyStarCostFastPathPatch>();
        patcher.RegisterPatch<RitsuEmptyCanPlayFastPathPatch>();
        patcher.RegisterPatch<SimulationCardPileLookupPatch>();
        patcher.RegisterPatch<BaseLibCloneConcurrencyPatch>();
        patcher.RegisterPatch<BaseLibDynamicVarCloneMetadataPatch>();
        patcher.RegisterPatch<RitsuDynamicVarCloneMetadataPatch>();
        patcher.RegisterPatch<PowerDynamicVarMaterializationGuardPatch>();
        patcher.RegisterPatch<PowerAmountComparisonPatch>();
        patcher.RegisterPatch<RichTextEnvironmentLifetimePatch>();
        patcher.RegisterPatch<NodePoolSignalLifetimePatch>();
        patcher.RegisterPatch<CombatInstantModePatch>();
#if !STS2_01071
        patcher.RegisterPatch<UnattendedTestIsolationPatch>();
        patcher.RegisterPatch<UnattendedHeadlessFtuePatch>();
#endif
        patcher.RegisterPatch<CombatReplayRecordingPatch>();
        patcher.RegisterPatch<RunStatisticsNewRunPatch>();
        patcher.RegisterPatch<RunStatisticsLaunchPatch>();
        patcher.RegisterPatch<RunStatisticsEndPatch>();
#if !STS2_01071
        patcher.RegisterPatch<UnattendedCombatStartReplayPatch>();
#endif
#if !STS2_01071
        // 0.107.1 没有该 SaveManager 重载；注册会让 RitsuLib 回滚整个必需补丁集。
        patcher.RegisterPatch<CombatShowcaseSaveIsolationPatch>();
#endif
        patcher.RegisterPatch<CombatShowcaseCleanupPatch>();
        RitsuLibFramework.ApplyRequiredPatcher(patcher, onFailure);
    }
}
