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
        Set-Content -LiteralPath (Join-Path $sourceGame 'SlayTheSpire2.exe') -Value 'base-executable-v1' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $sourceGame 'data.bin') -Value 'base-data-v1' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $sourceGame 'build.version') -Value '0.107.1' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $ritsuRoot 'mod_manifest.json') -Value '{"id":"RitsuLib"}' -Encoding UTF8
        New-Item -ItemType Directory -Path (Join-Path $ritsuRoot 'lib\0.107.1') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $ritsuRoot 'lib\0.107.1\STS2-RitsuLib.dll') -Value 'ritsu-v1' -Encoding UTF8
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
        Assert-HostFixture ($hostSync.copiedBaseFiles -eq 3) 'initial base snapshot file count was incorrect'

        Set-Content -LiteralPath (Join-Path $buildRoot 'CombatSolver.dll') -Value 'solver-v2' -Encoding UTF8
        $hostPlanAfterSolverChange = Get-HeadlessMultiplayerSnapshotPlan @solverPlanArgs
        $hostSyncAfterSolverChange = Set-HeadlessGameSnapshot $context $hostPlanAfterSolverChange
        Assert-HostFixture ($hostSyncAfterSolverChange.syncMode -eq 'unchanged') 'HostVanilla rebuilt for a CombatSolver-only change'
        Assert-HostFixture ($hostSyncAfterSolverChange.copiedBaseFiles -eq 0) 'HostVanilla copied base files after a CombatSolver-only change'
        Assert-HostFixture ($hostSyncAfterSolverChange.copiedOverlayFiles -eq 0) 'HostVanilla copied overlay files after a CombatSolver-only change'

        Set-Content -LiteralPath (Join-Path $context.GameRoot 'data.bin') -Value 'tampered-base' -Encoding UTF8
        $hostPlanAfterBaseTamper = Get-HeadlessMultiplayerSnapshotPlan @solverPlanArgs
        $hostSyncAfterBaseTamper = Set-HeadlessGameSnapshot $context $hostPlanAfterBaseTamper
        Assert-HostFixture ($hostSyncAfterBaseTamper.syncMode -eq 'full-rebuild') 'tampered base snapshot was not rebuilt'
        Assert-HostFixture ((Get-Content -LiteralPath (Join-Path $context.GameRoot 'data.bin') -Raw).Trim() -eq 'base-data-v1') 'base snapshot tamper was not repaired'

        $clientPlan = Get-HeadlessMultiplayerSnapshotPlan -Context $context -Profile 'ClientCombatSolver' `
            -CombatSolverDll $solverPlanArgs[2] -CombatSolverManifest $solverPlanArgs[3] -MemoryCleaner $solverPlanArgs[4] `
            -RitsuRoot $ritsuRoot -RitsuManifest $ritsuManifest -RitsuLibTargetVersion '0.107.1' -BaseGameVersion '0.107.1'
        $clientSync = Set-HeadlessGameSnapshot $context $clientPlan
        Assert-HostFixture ($clientSync.syncMode -eq 'overlay-incremental') 'ClientCombatSolver did not add its profile overlay incrementally'
        Assert-HostFixture ($clientSync.copiedBaseFiles -eq 0) 'adding a profile overlay recopied the base snapshot'
        $baseHashBeforeOverlayUpdate = (Get-FileHash -LiteralPath (Join-Path $context.GameRoot 'data.bin') -Algorithm SHA256).Hash

        Set-Content -LiteralPath (Join-Path $buildRoot 'CombatSolver.dll') -Value 'solver-v3' -Encoding UTF8
        $clientPlanAfterSolverChange = Get-HeadlessMultiplayerSnapshotPlan -Context $context -Profile 'ClientCombatSolver' `
            -CombatSolverDll $solverPlanArgs[2] -CombatSolverManifest $solverPlanArgs[3] -MemoryCleaner $solverPlanArgs[4] `
            -RitsuRoot $ritsuRoot -RitsuManifest $ritsuManifest -RitsuLibTargetVersion '0.107.1' -BaseGameVersion '0.107.1'
        $clientSyncAfterSolverChange = Set-HeadlessGameSnapshot $context $clientPlanAfterSolverChange
        Assert-HostFixture ($clientSyncAfterSolverChange.syncMode -eq 'overlay-incremental') 'CombatSolver change did not use overlay sync'
        Assert-HostFixture ($clientSyncAfterSolverChange.copiedBaseFiles -eq 0) 'CombatSolver change recopied the base snapshot'
        Assert-HostFixture ($clientSyncAfterSolverChange.copiedOverlayFiles -eq 1) 'CombatSolver change copied more than its changed payload'
        Assert-HostFixture ((Get-Content -LiteralPath (Join-Path $context.GameRoot 'mods\CombatSolver\CombatSolver.dll') -Raw).Trim() -eq 'solver-v3') 'CombatSolver payload was not updated'
        Assert-HostFixture ((Get-FileHash -LiteralPath (Join-Path $context.GameRoot 'data.bin') -Algorithm SHA256).Hash -eq $baseHashBeforeOverlayUpdate) 'base snapshot changed during CombatSolver overlay sync'

        Set-Content -LiteralPath (Join-Path $ritsuRoot 'lib\0.107.1\STS2-RitsuLib.dll') -Value 'ritsu-v2' -Encoding UTF8
        $clientPlanAfterRitsuChange = Get-HeadlessMultiplayerSnapshotPlan -Context $context -Profile 'ClientCombatSolver' `
            -CombatSolverDll $solverPlanArgs[2] -CombatSolverManifest $solverPlanArgs[3] -MemoryCleaner $solverPlanArgs[4] `
            -RitsuRoot $ritsuRoot -RitsuManifest $ritsuManifest -RitsuLibTargetVersion '0.107.1' -BaseGameVersion '0.107.1'
        $clientSyncAfterRitsuChange = Set-HeadlessGameSnapshot $context $clientPlanAfterRitsuChange
        Assert-HostFixture ($clientSyncAfterRitsuChange.syncMode -eq 'overlay-incremental') 'RitsuLib change did not use overlay sync'
        Assert-HostFixture ($clientSyncAfterRitsuChange.copiedBaseFiles -eq 0) 'RitsuLib change recopied the base snapshot'
        Assert-HostFixture ($clientSyncAfterRitsuChange.copiedOverlayFiles -eq 1) 'RitsuLib change copied more than its changed payload'
        Assert-HostFixture ((Get-Content -LiteralPath (Join-Path $context.GameRoot 'mods\.combatsolver-headless-ritsulib\STS2-RitsuLib.dll') -Raw).Trim() -eq 'ritsu-v2') 'RitsuLib payload was not updated'
        Assert-HostFixture ((Get-Content -LiteralPath (Join-Path $context.GameRoot 'mods\CombatSolver\CombatSolver.dll') -Raw).Trim() -eq 'solver-v3') 'RitsuLib update disturbed the CombatSolver payload'

        Set-Content -LiteralPath (Join-Path $sourceGame 'data.bin') -Value 'base-data-v2' -Encoding UTF8
        $planAfterBaseChange = Get-HeadlessMultiplayerSnapshotPlan -Context $context -Profile 'ClientCombatSolver' `
            -CombatSolverDll $solverPlanArgs[2] -CombatSolverManifest $solverPlanArgs[3] -MemoryCleaner $solverPlanArgs[4] `
            -RitsuRoot $ritsuRoot -RitsuManifest $ritsuManifest -RitsuLibTargetVersion '0.107.1' -BaseGameVersion '0.107.1'
        $syncAfterBaseChange = Set-HeadlessGameSnapshot $context $planAfterBaseChange
        Assert-HostFixture ($syncAfterBaseChange.syncMode -eq 'full-rebuild') 'base-game change did not trigger a full rebuild'
        Assert-HostFixture ((Get-Content -LiteralPath (Join-Path $context.GameRoot 'data.bin') -Raw).Trim() -eq 'base-data-v2') 'full base rebuild did not update the base payload'

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
            Set-Content -LiteralPath (Join-Path $buildRoot 'CombatSolver.dll') -Value 'solver-v4' -Encoding UTF8
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

        $marker = Get-Content -LiteralPath (Join-Path $context.GameRoot '.combatsolver-frozen-game.json') -Raw | ConvertFrom-Json -AsHashtable
        Assert-HostFixture ($marker.schemaVersion -eq 2 -and $marker.snapshotKind -eq 'base-plus-profile-overlay') 'incremental snapshot marker was incomplete'
        Assert-HostFixture (@($marker.baseFiles).Count -eq 3 -and @($marker.overlayFiles).Count -eq 5) 'snapshot marker file topology was incomplete'
        Write-Output "MULTIPLAYER_SNAPSHOT_SYNC_SELFTEST_PASS base-persistence/solver-overlay/ritsu-overlay/host-isolation/base-rebuild/$reparseEvidence"
        return
    }
    finally {
        if (Test-Path -LiteralPath $snapshotFixture) {
            Remove-Item -LiteralPath $snapshotFixture -Recurse -Force
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
