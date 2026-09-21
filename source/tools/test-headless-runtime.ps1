#requires -Version 7.4
# No game is launched. Host capacity and the game-name census are controlled
# fixtures; launcher identity validation still uses the real pwsh process.
param([switch]$ProfileOnly, [switch]$MultiplayerSnapshot)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'headless-runtime.ps1')

function Assert-LauncherNotCancelled { }
function Get-HeadlessHostCapacity { return @{ cpu = 8; totalMiB = 32768; availableMiB = 24576 } }
function Get-Process {
    [CmdletBinding()]
    param([string]$Name, [int]$Id)
    if ($PSBoundParameters.ContainsKey('Name')) {
        if ($script:UnknownGame) { return [pscustomobject]@{ Id = 987654 } }
        return
    }
    return Microsoft.PowerShell.Management\Get-Process -Id $Id -ErrorAction SilentlyContinue
}
function Assert-HostFixture([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Headless infrastructure assertion failed: $Message" }
}
function Assert-AdmissionRejected([hashtable]$Context) {
    $rejected = $false
    try { Enter-HeadlessHostLease $Context $null } catch {
        if ($_.Exception.Message -notlike 'Headless host admission timed out*') { throw }
        $rejected = $true
    } finally { Exit-HeadlessHostLease $Context }
    Assert-HostFixture $rejected 'expected bounded admission timeout'
    Assert-HostFixture (-not (Test-Path -LiteralPath $Context.LeasePath)) 'failed request left a queued lease'
}

if ($ProfileOnly) {
    $profileFixture = Join-Path ([IO.Path]::GetTempPath()) ('combatsolver-profile-test-' + [Guid]::NewGuid().ToString('N'))
    $sourceProfile = Join-Path $profileFixture 'source'
    $copiedProfile = Join-Path $profileFixture 'copied'
    New-Item -ItemType Directory -Path $sourceProfile, $copiedProfile -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $sourceProfile 'settings.save') -Value 'fixture-original'
    Copy-HeadlessProfileTree $sourceProfile $copiedProfile
    Set-Content -LiteralPath (Join-Path $copiedProfile 'source/settings.save') -Value 'fixture-private'
    Assert-HostFixture ((Get-Content -LiteralPath (Join-Path $sourceProfile 'settings.save') -Raw).Trim() -eq 'fixture-original') 'copied profile mutated source'
    New-Item -ItemType SymbolicLink -Path (Join-Path $sourceProfile 'linked.save') -Target (Join-Path $sourceProfile 'settings.save') | Out-Null
    $rejected = $false
    try { Copy-HeadlessProfileTree $sourceProfile $copiedProfile } catch {
        if ($_.Exception.Message -notlike 'Cannot isolate a profile containing a reparse point:*') { throw }
        $rejected = $true
    }
    Assert-HostFixture $rejected 'profile symlink was not rejected'
    Write-Output "HEADLESS_PROFILE_SELFTEST_PASS copy/source-preservation/reparse-rejection evidence=$profileFixture"
    return
}

if ($MultiplayerSnapshot) {
    $snapshotFixture = Join-Path ([IO.Path]::GetTempPath()) ('combatsolver-multiplayer-snapshot-test-' + [Guid]::NewGuid().ToString('N'))
    $sourceGame = Join-Path $snapshotFixture 'source-game'
    $ritsuRoot = Join-Path $snapshotFixture 'ritsu'
    $buildRoot = Join-Path $snapshotFixture 'build'
    $context = @{
        Root = Join-Path $snapshotFixture 'instance'
        GameRoot = Join-Path $snapshotFixture 'instance\game'
        SourceGameRoot = $sourceGame
    }
    try {
        New-Item -ItemType Directory -Path $sourceGame, $ritsuRoot, $buildRoot -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $env:WINDIR 'System32\cmd.exe') -Destination (Join-Path $sourceGame 'SlayTheSpire2.exe') -Force
        Set-Content -LiteralPath (Join-Path $sourceGame 'data.bin') -Value 'base-data-v1' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $sourceGame 'build.version') -Value '0.107.1' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $ritsuRoot 'mod_manifest.json') -Value '{"id":"RitsuLib"}' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $ritsuRoot 'ritsulib-variants.manifest') -Value '{"variant":"test"}' -Encoding UTF8
        New-Item -ItemType Directory -Path (Join-Path $ritsuRoot 'lib\0.107.1') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $ritsuRoot 'lib\0.107.1\STS2-RitsuLib.dll') -Value 'ritsu-v1' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $ritsuRoot 'lib\0.107.1\RitsuExtra.dat') -Value 'ritsu-extra-v1' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $buildRoot 'CombatSolver.dll') -Value 'solver-v1' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $buildRoot 'CombatSolver.json') -Value '{"version":"solver-v1"}' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $buildRoot 'CombatSolver.MemoryCleaner.exe') -Value 'cleaner-v1' -Encoding UTF8

        $ritsuManifest = Join-Path $ritsuRoot 'mod_manifest.json'
        $solverPlanArgs = @(
            $context, 'HostVanilla',
            (Join-Path $buildRoot 'CombatSolver.dll'),
            (Join-Path $buildRoot 'CombatSolver.json'),
            (Join-Path $buildRoot 'CombatSolver.MemoryCleaner.exe'),
            $ritsuRoot, $ritsuManifest, '0.107.1', '0.107.1'
        )

        $hostPlan = Get-HeadlessMultiplayerSnapshotPlan @solverPlanArgs
        $hostSync = Set-HeadlessGameSnapshot $context $hostPlan
        Assert-HostFixture ($hostSync.syncMode -eq 'full-rebuild') 'initial HostVanilla snapshot was not a full build'
        Assert-HostFixture ($hostSync.snapshotAction -eq 'FULL_REBUILD' -and $hostSync.baseGameAction -eq 'REBUILT') 'initial snapshot action was not FULL_REBUILD'
        Assert-HostFixture ($hostSync.copiedBaseFiles -eq 3) 'initial base snapshot file count was incorrect'

        $persistentSettings = Join-Path $context.Root 'Roaming\SlayTheSpire2\steam\fixture\settings.save'
        $persistentLocalConfig = Join-Path $context.Root 'Local\SlayTheSpire2\fixture.cfg'
        New-Item -ItemType Directory -Path (Split-Path -Parent $persistentSettings), (Split-Path -Parent $persistentLocalConfig) -Force | Out-Null
        Set-Content -LiteralPath $persistentSettings -Value '{"display_mode":"windowed"}' -Encoding UTF8
        Set-Content -LiteralPath $persistentLocalConfig -Value 'windowed=true' -Encoding UTF8

        Set-Content -LiteralPath (Join-Path $buildRoot 'CombatSolver.dll') -Value 'solver-v2' -Encoding UTF8
        $hostPlanAfterSolverChange = Get-HeadlessMultiplayerSnapshotPlan @solverPlanArgs
        $hostSyncAfterSolverChange = Set-HeadlessGameSnapshot $context $hostPlanAfterSolverChange
        Assert-HostFixture ($hostSyncAfterSolverChange.syncMode -eq 'unchanged') 'HostVanilla rebuilt for a CombatSolver-only change'
        Assert-HostFixture ($hostSyncAfterSolverChange.snapshotAction -eq 'REUSED' -and $hostSyncAfterSolverChange.copiedFiles -eq 0) 'HostVanilla did not report complete reuse'
        Assert-HostFixture ($hostSyncAfterSolverChange.copiedBaseFiles -eq 0) 'HostVanilla copied base files after a CombatSolver-only change'
        Assert-HostFixture ($hostSyncAfterSolverChange.copiedOverlayFiles -eq 0) 'HostVanilla copied overlay files after a CombatSolver-only change'

        Set-Content -LiteralPath (Join-Path $context.GameRoot 'data.bin') -Value 'tampered-base' -Encoding UTF8
        $hostPlanAfterBaseTamper = Get-HeadlessMultiplayerSnapshotPlan @solverPlanArgs
        $hostSyncAfterBaseTamper = Set-HeadlessGameSnapshot $context $hostPlanAfterBaseTamper
        Assert-HostFixture ($hostSyncAfterBaseTamper.syncMode -eq 'full-rebuild') 'tampered base snapshot was not rebuilt'
        Assert-HostFixture ((Get-Content -LiteralPath (Join-Path $context.GameRoot 'data.bin') -Raw).Trim() -eq 'base-data-v1') 'base snapshot tamper was not repaired'
        Assert-HostFixture ((Get-Content -LiteralPath $persistentSettings -Raw).Trim() -eq '{"display_mode":"windowed"}') 'full snapshot rebuild removed Roaming settings'
        Assert-HostFixture ((Get-Content -LiteralPath $persistentLocalConfig -Raw).Trim() -eq 'windowed=true') 'full snapshot rebuild removed Local settings'

        $clientPlan = Get-HeadlessMultiplayerSnapshotPlan -Context $context -Profile 'ClientCombatSolver' `
            -CombatSolverDll $solverPlanArgs[2] -CombatSolverManifest $solverPlanArgs[3] -MemoryCleaner $solverPlanArgs[4] `
            -RitsuRoot $ritsuRoot -RitsuManifest $ritsuManifest -RitsuLibTargetVersion '0.107.1' -BaseGameVersion '0.107.1'
        $clientSync = Set-HeadlessGameSnapshot $context $clientPlan
        Assert-HostFixture ($clientSync.syncMode -eq 'overlay-incremental') 'ClientCombatSolver did not add its profile overlay incrementally'
        Assert-HostFixture ($clientSync.snapshotAction -eq 'OVERLAY_UPDATED' -and $clientSync.baseGameAction -eq 'REUSED') 'Client overlay install action was not reported'
        Assert-HostFixture ($clientSync.ritsuAction -eq 'INSTALLED' -and $clientSync.combatSolverAction -eq 'INSTALLED') 'Client overlay install actions were incomplete'
        Assert-HostFixture ($clientSync.copiedBaseFiles -eq 0) 'adding a profile overlay recopied the base snapshot'
        Assert-HostFixture ($clientSync.copiedFiles -eq 7) 'initial client overlay copied an unexpected file count'
        $clientRepeatPlan = Get-HeadlessMultiplayerSnapshotPlan -Context $context -Profile 'ClientCombatSolver' `
            -CombatSolverDll $solverPlanArgs[2] -CombatSolverManifest $solverPlanArgs[3] -MemoryCleaner $solverPlanArgs[4] `
            -RitsuRoot $ritsuRoot -RitsuManifest $ritsuManifest -RitsuLibTargetVersion '0.107.1' -BaseGameVersion '0.107.1'
        $clientRepeatSync = Set-HeadlessGameSnapshot $context $clientRepeatPlan
        Assert-HostFixture ($clientRepeatSync.snapshotAction -eq 'REUSED' -and $clientRepeatSync.copiedFiles -eq 0) 'second client prepare did not fully reuse the snapshot'
        $baseHashBeforeOverlayUpdate = (Get-FileHash -LiteralPath (Join-Path $context.GameRoot 'data.bin') -Algorithm SHA256).Hash
        $baseWriteTimeBeforeOverlayUpdate = (Get-Item -LiteralPath (Join-Path $context.GameRoot 'data.bin')).LastWriteTimeUtc

        Set-Content -LiteralPath (Join-Path $buildRoot 'CombatSolver.dll') -Value 'solver-v3' -Encoding UTF8
        $clientPlanAfterSolverChange = Get-HeadlessMultiplayerSnapshotPlan -Context $context -Profile 'ClientCombatSolver' `
            -CombatSolverDll $solverPlanArgs[2] -CombatSolverManifest $solverPlanArgs[3] -MemoryCleaner $solverPlanArgs[4] `
            -RitsuRoot $ritsuRoot -RitsuManifest $ritsuManifest -RitsuLibTargetVersion '0.107.1' -BaseGameVersion '0.107.1'
        $clientSyncAfterSolverChange = Set-HeadlessGameSnapshot $context $clientPlanAfterSolverChange
        Assert-HostFixture ($clientSyncAfterSolverChange.syncMode -eq 'overlay-incremental') 'CombatSolver change did not use overlay sync'
        Assert-HostFixture ($clientSyncAfterSolverChange.snapshotAction -eq 'OVERLAY_UPDATED' -and $clientSyncAfterSolverChange.baseGameAction -eq 'REUSED') 'CombatSolver overlay action was not reported'
        Assert-HostFixture ($clientSyncAfterSolverChange.ritsuAction -eq 'REUSED' -and $clientSyncAfterSolverChange.combatSolverAction -eq 'UPDATED') 'CombatSolver-only action classification was incorrect'
        Assert-HostFixture ($clientSyncAfterSolverChange.copiedBaseFiles -eq 0) 'CombatSolver change recopied the base snapshot'
        Assert-HostFixture ($clientSyncAfterSolverChange.copiedOverlayFiles -eq 3 -and $clientSyncAfterSolverChange.copiedFiles -eq 3) 'CombatSolver change did not replace exactly its managed payload'
        Assert-HostFixture ((Get-Content -LiteralPath (Join-Path $context.GameRoot 'mods\CombatSolver\CombatSolver.dll') -Raw).Trim() -eq 'solver-v3') 'CombatSolver payload was not updated'
        Assert-HostFixture ((Get-FileHash -LiteralPath (Join-Path $context.GameRoot 'data.bin') -Algorithm SHA256).Hash -eq $baseHashBeforeOverlayUpdate) 'base snapshot changed during CombatSolver overlay sync'
        Assert-HostFixture ((Get-Item -LiteralPath (Join-Path $context.GameRoot 'data.bin')).LastWriteTimeUtc -eq $baseWriteTimeBeforeOverlayUpdate) 'base snapshot file was rewritten during CombatSolver overlay sync'

        Set-Content -LiteralPath (Join-Path $ritsuRoot 'lib\0.107.1\STS2-RitsuLib.dll') -Value 'ritsu-v2' -Encoding UTF8
        Remove-Item -LiteralPath (Join-Path $ritsuRoot 'lib\0.107.1\RitsuExtra.dat') -Force
        $clientPlanAfterRitsuChange = Get-HeadlessMultiplayerSnapshotPlan -Context $context -Profile 'ClientCombatSolver' `
            -CombatSolverDll $solverPlanArgs[2] -CombatSolverManifest $solverPlanArgs[3] -MemoryCleaner $solverPlanArgs[4] `
            -RitsuRoot $ritsuRoot -RitsuManifest $ritsuManifest -RitsuLibTargetVersion '0.107.1' -BaseGameVersion '0.107.1'
        $clientSyncAfterRitsuChange = Set-HeadlessGameSnapshot $context $clientPlanAfterRitsuChange
        Assert-HostFixture ($clientSyncAfterRitsuChange.syncMode -eq 'overlay-incremental') 'RitsuLib change did not use overlay sync'
        Assert-HostFixture ($clientSyncAfterRitsuChange.snapshotAction -eq 'OVERLAY_UPDATED' -and $clientSyncAfterRitsuChange.baseGameAction -eq 'REUSED') 'Ritsu overlay action was not reported'
        Assert-HostFixture ($clientSyncAfterRitsuChange.ritsuAction -eq 'UPDATED' -and $clientSyncAfterRitsuChange.combatSolverAction -eq 'REUSED') 'Ritsu-only action classification was incorrect'
        Assert-HostFixture ($clientSyncAfterRitsuChange.copiedBaseFiles -eq 0) 'RitsuLib change recopied the base snapshot'
        Assert-HostFixture ($clientSyncAfterRitsuChange.copiedOverlayFiles -eq 3 -and $clientSyncAfterRitsuChange.copiedFiles -eq 3) 'Ritsu change did not replace exactly its managed payload'
        Assert-HostFixture ((Get-Content -LiteralPath (Join-Path $context.GameRoot 'mods\.combatsolver-headless-ritsulib\lib\0.107.1\STS2-RitsuLib.dll') -Raw).Trim() -eq 'ritsu-v2') 'RitsuLib payload was not updated'
        Assert-HostFixture (-not (Test-Path -LiteralPath (Join-Path $context.GameRoot 'mods\.combatsolver-headless-ritsulib\lib\0.107.1\RitsuExtra.dat')) -and
            -not (Test-Path -LiteralPath (Join-Path $context.GameRoot 'mods\.combatsolver-headless-ritsulib\RitsuExtra.dat'))) 'stale Ritsu managed file was not removed'
        Assert-HostFixture ((Get-Content -LiteralPath (Join-Path $context.GameRoot 'mods\CombatSolver\CombatSolver.dll') -Raw).Trim() -eq 'solver-v3') 'RitsuLib update disturbed the CombatSolver payload'

        $hostPlanAfterClient = Get-HeadlessMultiplayerSnapshotPlan @solverPlanArgs
        $hostSyncAfterClient = Set-HeadlessGameSnapshot $context $hostPlanAfterClient
        Assert-HostFixture ($hostSyncAfterClient.snapshotAction -eq 'OVERLAY_UPDATED' -and
            $hostSyncAfterClient.ritsuAction -eq 'UPDATED' -and $hostSyncAfterClient.combatSolverAction -eq 'UPDATED') 'switching to HostVanilla did not remove managed overlays'
        Assert-HostFixture (-not (Test-Path -LiteralPath (Join-Path $context.GameRoot 'mods\.combatsolver-headless-ritsulib'))) 'Ritsu managed overlay remained after HostVanilla switch'
        Assert-HostFixture (-not (Test-Path -LiteralPath (Join-Path $context.GameRoot 'mods\CombatSolver'))) 'CombatSolver managed overlay remained after HostVanilla switch'
        $clientRestoreSync = Set-HeadlessGameSnapshot $context $clientPlanAfterRitsuChange
        Assert-HostFixture ($clientRestoreSync.ritsuAction -eq 'INSTALLED' -and $clientRestoreSync.combatSolverAction -eq 'INSTALLED' -and
            $clientRestoreSync.copiedFiles -eq 6) 'client overlays were not restored after HostVanilla switch'

        Set-Content -LiteralPath (Join-Path $sourceGame 'data.bin') -Value 'base-data-v2' -Encoding UTF8
        $planAfterBaseChange = Get-HeadlessMultiplayerSnapshotPlan -Context $context -Profile 'ClientCombatSolver' `
            -CombatSolverDll $solverPlanArgs[2] -CombatSolverManifest $solverPlanArgs[3] -MemoryCleaner $solverPlanArgs[4] `
            -RitsuRoot $ritsuRoot -RitsuManifest $ritsuManifest -RitsuLibTargetVersion '0.107.1' -BaseGameVersion '0.107.1'
        $syncAfterBaseChange = Set-HeadlessGameSnapshot $context $planAfterBaseChange
        Assert-HostFixture ($syncAfterBaseChange.syncMode -eq 'full-rebuild') 'base-game change did not trigger a full rebuild'
        Assert-HostFixture ($syncAfterBaseChange.snapshotAction -eq 'FULL_REBUILD' -and $syncAfterBaseChange.baseGameAction -eq 'REBUILT') 'base-game change did not report FULL_REBUILD'
        Assert-HostFixture ((Get-Content -LiteralPath (Join-Path $context.GameRoot 'data.bin') -Raw).Trim() -eq 'base-data-v2') 'full base rebuild did not update the base payload'

        $forcedSync = Set-HeadlessGameSnapshot $context $planAfterBaseChange -ForceFullRebuild
        Assert-HostFixture ($forcedSync.syncMode -eq 'full-rebuild' -and $forcedSync.snapshotAction -eq 'FULL_REBUILD') '-ForceRebuild did not force a full rebuild'
        Assert-HostFixture ($forcedSync.copiedFiles -eq 9) '-ForceRebuild copied an unexpected file count'
        Assert-HostFixture ((Get-Content -LiteralPath $persistentSettings -Raw).Trim() -eq '{"display_mode":"windowed"}') '-ForceRebuild removed Roaming settings'
        Assert-HostFixture ((Get-Content -LiteralPath $persistentLocalConfig -Raw).Trim() -eq 'windowed=true') '-ForceRebuild removed Local settings'

        Set-Content -LiteralPath (Join-Path $buildRoot 'CombatSolver.dll') -Value 'solver-v4' -Encoding UTF8
        $liveProcess = $null
        try {
            $liveProcess = Start-Process -FilePath (Join-Path $context.GameRoot 'SlayTheSpire2.exe') `
                -ArgumentList @('/c', 'ping', '127.0.0.1', '-n', '30') -PassThru
            Start-Sleep -Milliseconds 300
            $liveProcess.Refresh()
            Assert-HostFixture (-not $liveProcess.HasExited) 'live snapshot fixture process exited early'
            $liveRejected = $false
            try {
                $livePlan = Get-HeadlessMultiplayerSnapshotPlan -Context $context -Profile 'ClientCombatSolver' `
                    -CombatSolverDll $solverPlanArgs[2] -CombatSolverManifest $solverPlanArgs[3] -MemoryCleaner $solverPlanArgs[4] `
                    -RitsuRoot $ritsuRoot -RitsuManifest $ritsuManifest -RitsuLibTargetVersion '0.107.1' -BaseGameVersion '0.107.1'
                Set-HeadlessGameSnapshot $context $livePlan | Out-Null
            }
            catch {
                if ($_.Exception.Message -notlike '*process is alive*' -and $_.Exception.Message -notlike '*live*') { throw }
                $liveRejected = $true
            }
            Assert-HostFixture $liveRejected 'live private game did not fail closed for overlay update'
        }
        finally {
            if ($null -ne $liveProcess) {
                $liveProcess.Refresh()
                if (-not $liveProcess.HasExited) { Stop-Process -Id $liveProcess.Id -Force }
                $liveProcess.WaitForExit()
            }
        }

        $solverOverlayPath = Join-Path $context.GameRoot 'mods\CombatSolver\CombatSolver.dll'
        Remove-Item -LiteralPath $solverOverlayPath -Force
        $reparseFixtureAvailable = $true
        try {
            New-Item -ItemType SymbolicLink -Path $solverOverlayPath -Target (Join-Path $buildRoot 'CombatSolver.dll') | Out-Null
        }
        catch {
            if ($_.Exception.Message -notlike '*Administrator privilege*' -and
                $_.Exception.Message -notlike '*privilege*') { throw }
            $reparseFixtureAvailable = $false
        }
        if ($reparseFixtureAvailable) {
            $reparseRejected = $false
            try {
                $planAfterReparse = Get-HeadlessMultiplayerSnapshotPlan -Context $context -Profile 'ClientCombatSolver' `
                    -CombatSolverDll $solverPlanArgs[2] -CombatSolverManifest $solverPlanArgs[3] -MemoryCleaner $solverPlanArgs[4] `
                    -RitsuRoot $ritsuRoot -RitsuManifest $ritsuManifest -RitsuLibTargetVersion '0.107.1' -BaseGameVersion '0.107.1'
                Set-HeadlessGameSnapshot $context $planAfterReparse | Out-Null
            }
            catch {
                if ($_.Exception.Message -notlike '*reparse point*') { throw }
                $reparseRejected = $true
            }
            Assert-HostFixture $reparseRejected 'profile overlay reparse point was not rejected'
            $reparseEvidence = 'reparse-rejection'
        }
        else {
            $reparseEvidence = 'reparse-rejection-skipped-no-link-privilege'
        }

        if ($reparseFixtureAvailable) {
            Remove-Item -LiteralPath $solverOverlayPath -Force
        }
        $recoveredPlan = Get-HeadlessMultiplayerSnapshotPlan -Context $context -Profile 'ClientCombatSolver' `
            -CombatSolverDll $solverPlanArgs[2] -CombatSolverManifest $solverPlanArgs[3] -MemoryCleaner $solverPlanArgs[4] `
            -RitsuRoot $ritsuRoot -RitsuManifest $ritsuManifest -RitsuLibTargetVersion '0.107.1' -BaseGameVersion '0.107.1'
        $recoveredSync = Set-HeadlessGameSnapshot $context $recoveredPlan
        Assert-HostFixture ($recoveredSync.combatSolverAction -eq 'UPDATED' -and
            (Get-Content -LiteralPath $solverOverlayPath -Raw).Trim() -eq 'solver-v4') 'overlay recovery did not restore the Solver payload'

        $markerPath = Join-Path $context.GameRoot '.combatsolver-frozen-game.json'
        $validMarkerRaw = Get-Content -LiteralPath $markerPath -Raw
        $invalidMarker = $validMarkerRaw | ConvertFrom-Json -AsHashtable
        $invalidMarker.runtimeRoot = Join-Path $snapshotFixture 'foreign-instance'
        Write-HeadlessJson $markerPath $invalidMarker
        $ownershipRejected = $false
        try {
            Set-HeadlessGameSnapshot $context $recoveredPlan | Out-Null
        }
        catch {
            if ($_.Exception.Message -notlike '*does not match this runtime*') { throw }
            $ownershipRejected = $true
        }
        Assert-HostFixture $ownershipRejected 'snapshot ownership mismatch was not rejected'
        Assert-HostFixture (Test-Path -LiteralPath (Join-Path $context.GameRoot 'SlayTheSpire2.exe') -PathType Leaf) 'ownership mismatch removed the private game'
        Set-Content -LiteralPath $markerPath -Value $validMarkerRaw -Encoding UTF8

        $marker = Get-Content -LiteralPath (Join-Path $context.GameRoot '.combatsolver-frozen-game.json') -Raw | ConvertFrom-Json -AsHashtable
        Assert-HostFixture ($marker.schemaVersion -eq 2 -and $marker.snapshotKind -eq 'base-plus-profile-overlay') 'incremental snapshot marker was incomplete'
        Assert-HostFixture ($marker.baseGameId -and $marker.ritsuArtifactId -and $marker.combatSolverArtifactId) 'snapshot marker did not contain split artifact identities'
        Assert-HostFixture (@($marker.baseFiles).Count -eq 3 -and @($marker.ritsuFiles).Count -eq 3 -and @($marker.combatSolverFiles).Count -eq 3) 'snapshot marker file topology was incomplete'

        $prepareRuntimeBase = Join-Path (Get-HeadlessCanonicalPath (Join-Path $PSScriptRoot '..\..\..')) `
            ('.local\multiplayer-snapshot-contract-' + [Guid]::NewGuid().ToString('N'))
        $prepareScript = Join-Path $PSScriptRoot 'multiplayer-lab\prepare-instances.ps1'
        function Invoke-PrepareContract {
            param([string]$Profile, [string]$RuntimeRoot, [switch]$Force)
            $childArgs = @(
                '-NoLogo', '-NoProfile', '-File', $prepareScript,
                '-Profile', $Profile, '-Instance', ('contract-' + $Profile.ToLowerInvariant()),
                '-RuntimeRoot', $RuntimeRoot, '-Sts2GameRoot', $sourceGame,
                '-RitsuWorkshopRoot', $ritsuRoot, '-CombatSolverBuildDir', $buildRoot
            )
            if ($Force.IsPresent) { $childArgs += '-ForceRebuild' }
            $childOutput = @(& pwsh @childArgs)
            if ($LASTEXITCODE -ne 0) {
                throw "prepare-instances contract failed for ${Profile}: $($childOutput -join ' ')"
            }
            return (($childOutput -join [Environment]::NewLine) | ConvertFrom-Json)
        }
        $hostPrepare = Invoke-PrepareContract 'HostVanilla' (Join-Path $prepareRuntimeBase 'host')
        Assert-HostFixture ($hostPrepare.snapshotAction -eq 'FULL_REBUILD' -and $hostPrepare.copiedFiles -eq 3) 'prepare output did not report initial Host full rebuild'
        Set-Content -LiteralPath (Join-Path $buildRoot 'CombatSolver.dll') -Value 'solver-v5' -Encoding UTF8
        $hostPrepareReuse = Invoke-PrepareContract 'HostVanilla' (Join-Path $prepareRuntimeBase 'host')
        Assert-HostFixture ($hostPrepareReuse.snapshotAction -eq 'REUSED' -and $hostPrepareReuse.baseGameAction -eq 'REUSED' -and
            $hostPrepareReuse.combatSolverAction -eq 'REUSED' -and $hostPrepareReuse.copiedFiles -eq 0) 'prepare output did not report Host reuse'
        $clientPrepare = Invoke-PrepareContract 'ClientCombatSolver' (Join-Path $prepareRuntimeBase 'client')
        Assert-HostFixture ($clientPrepare.snapshotAction -eq 'FULL_REBUILD' -and $clientPrepare.copiedFiles -eq 9) 'prepare output did not report initial Client full rebuild'
        Set-Content -LiteralPath (Join-Path $buildRoot 'CombatSolver.dll') -Value 'solver-v6' -Encoding UTF8
        $clientPrepareOverlay = Invoke-PrepareContract 'ClientCombatSolver' (Join-Path $prepareRuntimeBase 'client')
        Assert-HostFixture ($clientPrepareOverlay.snapshotAction -eq 'OVERLAY_UPDATED' -and
            $clientPrepareOverlay.baseGameAction -eq 'REUSED' -and $clientPrepareOverlay.ritsuAction -eq 'REUSED' -and
            $clientPrepareOverlay.combatSolverAction -eq 'UPDATED' -and $clientPrepareOverlay.copiedFiles -eq 3) 'prepare output did not report Solver-only overlay update'
        Write-Output "MULTIPLAYER_SNAPSHOT_SYNC_SELFTEST_PASS base-persistence/userdata-persistence/solver-overlay/ritsu-overlay/host-isolation/base-rebuild/force-rebuild/live-rejection/ownership-rejection/prepare-output/$reparseEvidence"
        return
    }
    finally {
        if (Test-Path -LiteralPath $snapshotFixture) {
            Remove-Item -LiteralPath $snapshotFixture -Recurse -Force
        }
        if ($null -ne $prepareRuntimeBase -and (Test-Path -LiteralPath $prepareRuntimeBase)) {
            Remove-Item -LiteralPath $prepareRuntimeBase -Recurse -Force
        }
    }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('combatsolver-headless-host-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$script:UnknownGame = $false
$contexts = @()
foreach ($name in @('a', 'b', 'c')) {
    $contexts += @{
        Instance = $name; Root = Join-Path $testRoot $name; GameRoot = Join-Path $testRoot "$name/game"
        HostRoot = $testRoot; LeasePath = Join-Path $testRoot "$name.json"
        Mode = 'parallel'; MemoryMiB = 4096; Cpu = 2; QueueSeconds = 1
        LeaseToken = ''; ArtifactId = 'fixture-build'
    }
}
try {
    $a, $b, $c = $contexts
    Enter-HeadlessHostLease $a $null
    Enter-HeadlessHostLease $b $null
    Assert-HostFixture ((Get-HeadlessHostLeases $a).Count -eq 2) 'two independent parallel instances were not admitted'
    Assert-AdmissionRejected $c

    # A release is token/identity scoped, even when someone presents the path
    # of another live lease. It must not evict either admitted instance.
    $impostor = $a.Clone(); $impostor.LeaseToken = 'wrong-token'
    Exit-HeadlessHostLease $impostor
    Assert-HostFixture (Test-Path -LiteralPath $a.LeasePath) 'wrong token released another launcher'
    $c.Mode = 'exclusive'
    Assert-AdmissionRejected $c
    Exit-HeadlessHostLease $a
    Assert-HostFixture (Test-Path -LiteralPath $b.LeasePath) 'releasing one instance affected its peer'
    Exit-HeadlessHostLease $b
    Enter-HeadlessHostLease $c $null
    Assert-AdmissionRejected $a
    Exit-HeadlessHostLease $c

    $a.Cpu = 9
    Assert-AdmissionRejected $a
    $a.Cpu = 2; $a.MemoryMiB = 24576
    Assert-AdmissionRejected $a
    $a.MemoryMiB = 4096; $script:UnknownGame = $true
    Assert-AdmissionRejected $a
    $script:UnknownGame = $false

    # A reused PID with a different birth is stale, not permission to stop it.
    $identity = Get-HeadlessProcessIdentity ([Diagnostics.Process]::GetCurrentProcess())
    $staleIdentity = $identity.Clone()
    $staleIdentity.birth = ([DateTimeOffset]$identity.birth).AddSeconds(-1).ToString('O')
    Assert-HostFixture (-not (Get-HeadlessIdentityState $staleIdentity).alive) 'PID birth was ignored'
    Write-HeadlessJson $a.LeasePath @{ schemaVersion = 1; state = 'pending'; mode = 'parallel'; cpu = 2;
        memoryMiB = 4096; runtimeRoot = $a.Root; token = 'stale'; game = $null; launcher = $staleIdentity }
    Assert-HostFixture ((Get-HeadlessHostLeases $a).Count -eq 0) 'dead identity did not release stale admission'
    Assert-HostFixture (Get-HeadlessIdentityState $identity).alive 'stale lease cleanup disturbed its unrelated PID'

    # A warm-game record owns its reservation after the request launcher exits.
    # Only this fake game's liveness is stubbed; no executable is spawned.
    $originalIdentityState = ${function:Get-HeadlessIdentityState}
    $script:FakeGameAlive = $true
    function Get-HeadlessIdentityState([object]$Identity) {
        if ($null -ne $Identity -and [int]$Identity.pid -eq 987655) {
            return @{ alive = $script:FakeGameAlive; workingSetMiB = 1024 }
        }
        & $originalIdentityState $Identity
    }
    $a.LeaseToken = 'warm-token'
    Write-HeadlessJson $a.LeasePath @{ schemaVersion = 1; state = 'running'; mode = 'parallel'; cpu = 2;
        memoryMiB = 4096; runtimeRoot = $a.Root; token = $a.LeaseToken; launcher = $null;
        game = @{ pid = 987655; birth = $identity.birth; exe = Join-Path $a.Root 'game\SlayTheSpire2.exe' } }
    Exit-HeadlessHostLease $a
    Assert-HostFixture (Test-Path -LiteralPath $a.LeasePath) 'warm game lost its reservation'
    $script:FakeGameAlive = $false
    Exit-HeadlessHostLease $a
    Assert-HostFixture (-not (Test-Path -LiteralPath $a.LeasePath)) 'exited warm game retained its reservation'
    Write-Output 'HEADLESS_RUNTIME_SELFTEST_PASS parallel2/exclusive/resource/unknown/ownership/stale/warm'
} finally {
    # This fresh directory contains only this fixture's leases and lock. Never
    # invoke the game snapshot cleaner or touch a production host pool here.
    foreach ($context in $contexts) { Exit-HeadlessHostLease $context }
    foreach ($file in Get-ChildItem -LiteralPath $testRoot -File) {
        Remove-Item -LiteralPath $file.FullName -Force
    }
    Remove-Item -LiteralPath $testRoot -Force
}
