#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('HostVanilla', 'ClientVanilla', 'ClientRitsuOnly', 'ClientCombatSolver')]
    [string]$Profile,

    [string]$Instance = '',

    [string]$RuntimeRoot = '',

    [string]$Sts2GameRoot = 'D:\SteamLibrary\steamapps\common\Slay the Spire 2',

    [string]$RitsuWorkshopRoot = 'D:\SteamLibrary\steamapps\workshop\content\2868840\3747602295',

    [string]$CombatSolverBuildDir = 'D:\yingye\CombatSolver\artifacts\CombatSolver',

    [switch]$ForceRebuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '..\headless-runtime.ps1')

# Set-HeadlessGameSnapshot has a cancellation hook because the unattended
# runner can be interrupted during a large copy. Preparation is a bounded
# foreground operation, so it supplies the same hook without owning a second
# cancellation protocol.
function Assert-LauncherNotCancelled { }

$repositoryRoot = Get-HeadlessCanonicalPath (Join-Path $PSScriptRoot '..\..\..')
$sourceRoot = Get-HeadlessCanonicalPath (Join-Path $PSScriptRoot '..\..')
if ([string]::IsNullOrWhiteSpace($Instance)) {
    $Instance = switch ($Profile) {
        'HostVanilla' { 'mp-host' }
        'ClientCombatSolver' { 'mp-client-solver' }
        'ClientRitsuOnly' { 'mp-client-ritsu' }
        'ClientVanilla' { 'mp-client-vanilla' }
        default { throw "Unsupported multiplayer profile: $Profile" }
    }
}
if ([string]::IsNullOrWhiteSpace($RuntimeRoot)) {
    $RuntimeRoot = Join-Path $repositoryRoot ".local\multiplayer-lab\runtime-$Instance"
}
$RuntimeRoot = Get-HeadlessCanonicalPath $RuntimeRoot
if ($RuntimeRoot -notlike 'D:\*') {
    throw "Multiplayer Lab runtime must be on D:; received $RuntimeRoot"
}
$Sts2GameRoot = Get-HeadlessCanonicalPath $Sts2GameRoot
$RitsuWorkshopRoot = Get-HeadlessCanonicalPath $RitsuWorkshopRoot
$CombatSolverBuildDir = Get-HeadlessCanonicalPath $CombatSolverBuildDir

$previousHeadlessRoot = $env:COMBATSOLVER_HEADLESS_ROOT
$previousHostRoot = $env:COMBATSOLVER_HEADLESS_HOST_ROOT
try {
    $env:COMBATSOLVER_HEADLESS_ROOT = $RuntimeRoot
    $context = New-HeadlessRuntimeContext $repositoryRoot $Sts2GameRoot $Instance `
        'parallel' 2048 1 1
    New-Item -ItemType Directory -Path $context.Root -Force | Out-Null
    Initialize-HeadlessRuntimeOwner $context

    $profilePath = Join-Path $context.Root 'multiplayer-profile.json'
    if (Test-Path -LiteralPath $profilePath -PathType Leaf) {
        $existing = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json -AsHashtable
        if (-not $ForceRebuild.IsPresent -and [string]$existing.profile -ne $Profile) {
            throw "Instance already contains profile $($existing.profile); use -ForceRebuild to replace it."
        }
    }

    $targetConfigPath = Join-Path $sourceRoot 'build-target.json'
    $targetConfig = Get-Content -LiteralPath $targetConfigPath -Raw | ConvertFrom-Json
    $targetGameVersion = [string]$targetConfig.game_version
    $targetRitsuLibVersion = [string]$targetConfig.ritsu_lib_target_version
    if ([string]::IsNullOrWhiteSpace($targetGameVersion) -or
        [string]::IsNullOrWhiteSpace($targetRitsuLibVersion) -or
        $targetGameVersion -ne $targetRitsuLibVersion) {
        throw "build-target.json must define matching game and RitsuLib compatibility versions."
    }

    $combatSolverDll = Join-Path $CombatSolverBuildDir 'CombatSolver.dll'
    $combatSolverManifest = Join-Path $CombatSolverBuildDir 'CombatSolver.json'
    $memoryCleaner = Join-Path $CombatSolverBuildDir 'CombatSolver.MemoryCleaner.exe'
    $ritsuManifest = Join-Path $RitsuWorkshopRoot 'mod_manifest.json'
    $snapshotPlan = Get-HeadlessMultiplayerSnapshotPlan $context $Profile `
        $combatSolverDll $combatSolverManifest $memoryCleaner `
        $RitsuWorkshopRoot $ritsuManifest $targetRitsuLibVersion $targetGameVersion
    $context.ArtifactId = $snapshotPlan.id

    $syncResult = Set-HeadlessGameSnapshot $context $snapshotPlan -ForceFullRebuild:$ForceRebuild.IsPresent
    $profileRecord = [ordered]@{
        schemaVersion = 1
        profile = $Profile
        instance = $context.Instance
        runtimeRoot = $context.Root
        gameRoot = $context.GameRoot
        sourceGameRoot = $context.SourceGameRoot
        roamingRoot = Join-Path $context.Root 'Roaming'
        localRoot = Join-Path $context.Root 'Local'
        userDataPolicy = 'persistent-per-instance'
        forceRebuildResetsUserData = $false
        gameExecutable = Join-Path $context.GameRoot 'SlayTheSpire2.exe'
        artifactId = $snapshotPlan.id
        snapshotSchemaVersion = 2
        baseGameId = $snapshotPlan.baseGameId
        ritsuArtifactId = $snapshotPlan.ritsuArtifactId
        combatSolverArtifactId = $snapshotPlan.combatSolverArtifactId
        baseSnapshotId = $snapshotPlan.baseGameId
        overlayId = $snapshotPlan.overlayId
        syncMode = [string]$syncResult.syncMode
        snapshotAction = [string]$syncResult.snapshotAction
        baseGameAction = [string]$syncResult.baseGameAction
        ritsuAction = [string]$syncResult.ritsuAction
        combatSolverAction = [string]$syncResult.combatSolverAction
        copiedFiles = [int]$syncResult.copiedFiles
        targetGameVersion = $targetGameVersion
        targetRitsuLibVersion = $targetRitsuLibVersion
        preparedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        safety = [ordered]@{
            sourceGamePreserved = $true
            formalModsPreserved = $true
            usesCustomNetworkProtocol = $false
            runtimeEvidenceEligible = $false
        }
    }
    Write-HeadlessJson $profilePath $profileRecord

    $result = [ordered]@{
        status = 'PREPARED'
        profile = $Profile
        instance = $context.Instance
        runtimeRoot = $context.Root
        gameRoot = $context.GameRoot
        roamingRoot = Join-Path $context.Root 'Roaming'
        localRoot = Join-Path $context.Root 'Local'
        userDataPolicy = 'persistent-per-instance'
        forceRebuildResetsUserData = $false
        artifactId = $snapshotPlan.id
        snapshotSchemaVersion = 2
        baseGameId = $snapshotPlan.baseGameId
        ritsuArtifactId = $snapshotPlan.ritsuArtifactId
        combatSolverArtifactId = $snapshotPlan.combatSolverArtifactId
        baseSnapshotId = $snapshotPlan.baseGameId
        overlayId = $snapshotPlan.overlayId
        syncMode = [string]$syncResult.syncMode
        snapshotAction = [string]$syncResult.snapshotAction
        baseGameAction = [string]$syncResult.baseGameAction
        ritsuAction = [string]$syncResult.ritsuAction
        combatSolverAction = [string]$syncResult.combatSolverAction
        copiedFiles = [int]$syncResult.copiedFiles
        profilePath = $profilePath
        runtimeEvidenceEligible = $false
    }
    $result | ConvertTo-Json -Depth 8
}
finally {
    if ($null -eq $previousHeadlessRoot) {
        Remove-Item Env:COMBATSOLVER_HEADLESS_ROOT -ErrorAction SilentlyContinue
    }
    else {
        $env:COMBATSOLVER_HEADLESS_ROOT = $previousHeadlessRoot
    }
    if ($null -eq $previousHostRoot) {
        Remove-Item Env:COMBATSOLVER_HEADLESS_HOST_ROOT -ErrorAction SilentlyContinue
    }
    else {
        $env:COMBATSOLVER_HEADLESS_HOST_ROOT = $previousHostRoot
    }
}
