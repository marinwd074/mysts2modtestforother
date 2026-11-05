#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameDir,
    [string]$OutputDirectory,
    [ValidateSet('choice-rng', 'completion-scope', 'continuation-audit', 'model-id-cache',
        'card-choice-identity', 'upstream-port', 'upstream-search', 'dark-embrace-pact',
        'rolling-review', 'continuation-replay', 'pendulum-draw', 'turn-based-energy',
        'rng-restore', 'projected-shuffle', 'root-history', 'request-hydration')]
    [string[]]$Modes = @('choice-rng', 'completion-scope', 'continuation-audit', 'model-id-cache',
        'card-choice-identity', 'upstream-port', 'upstream-search', 'dark-embrace-pact',
        'rolling-review', 'continuation-replay', 'pendulum-draw', 'turn-based-energy',
        'rng-restore', 'projected-shuffle', 'root-history', 'request-hydration')
)

# Runs .NET contracts and the in-process pinned harness. No game launcher, Steam,
# Godot display server, multiplayer connection or deployment entry is invoked.
# The snapshot infrastructure self-test uses a renamed cmd.exe fixture, not the game.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$sourceRoot = Join-Path $repoRoot 'source'
$gameRoot = (Resolve-Path -LiteralPath $GameDir).Path
$dataRoot = Join-Path $gameRoot 'data_sts2_windows_x86_64'
$target = Get-Content -LiteralPath (Join-Path $sourceRoot 'build-target.json') -Raw | ConvertFrom-Json
$release = Get-Content -LiteralPath (Join-Path $gameRoot 'release_info.json') -Raw | ConvertFrom-Json
if (([string]$release.version).TrimStart('v') -cne [string]$target.game_version) {
    throw "Wrong game version: expected $($target.game_version), found $($release.version)."
}
foreach ($assembly in @('sts2.dll', 'GodotSharp.dll', '0Harmony.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $dataRoot $assembly) -PathType Leaf)) {
        throw "Missing game assembly: $assembly"
    }
}
if ($Modes.Count -eq 0) { throw 'Select at least one pinned mode.' }
$modesToRun = @($Modes | Select-Object -Unique)
$localRoot = Join-Path $repoRoot '.local'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $localRoot ('offline-validation/' + [guid]::NewGuid().ToString('N'))
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory, $repoRoot)
if (-not $outputRoot.StartsWith($localRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Validation output must be under the repository .local directory.'
}
if (Test-Path -LiteralPath $outputRoot) { throw 'Choose a new output directory; prior evidence is not overwritten.' }
New-Item -ItemType Directory -Path $outputRoot | Out-Null
$steps = [Collections.Generic.List[object]]::new()
$notes = [Collections.Generic.List[string]]::new()
$solverHash = $null
$nativeHash = (Get-FileHash -LiteralPath (Join-Path $dataRoot 'sts2.dll') -Algorithm SHA256).Hash
$failure = $null
$oldOverride = $env:OFFLINE_HARNESS_COMBATSOLVER_DLL

function Invoke-OfflineStep {
    param([string]$Name, [string]$Command, [string[]]$Arguments)
    $logPath = Join-Path $outputRoot "$Name.log"
    $timer = [Diagnostics.Stopwatch]::StartNew()
    & $Command @Arguments *> $logPath
    $exitCode = $LASTEXITCODE
    $timer.Stop()
    $steps.Add([pscustomobject]@{
        name = $Name; status = if ($exitCode -eq 0) { 'PASS' } else { 'FAIL' }
        exitCode = $exitCode; elapsedMs = $timer.ElapsedMilliseconds; log = $logPath
    })
    foreach ($match in Select-String -LiteralPath $logPath -Pattern '^SKIP:|reparse-rejection-skipped-no-link-privilege') {
        $notes.Add("${Name}: $($match.Line.Trim())")
    }
    Write-Host ("{0}: {1} ({2} ms)" -f $Name, $steps[$steps.Count - 1].status, $timer.ElapsedMilliseconds)
    if ($exitCode -ne 0) {
        Get-Content -LiteralPath $logPath -Tail 25 | Write-Host
        throw "$Name failed; see $logPath"
    }
}

Push-Location -LiteralPath $repoRoot
try {
    foreach ($gate in @('verify-target-version', 'verify-refactor-boundaries', 'verify-repository-hygiene')) {
        Invoke-OfflineStep $gate 'pwsh' @('-NoProfile', '-File', (Join-Path $PSScriptRoot "$gate.ps1"))
    }
    $buildProperties = @("-p:Sts2Dir=$gameRoot", "-p:Sts2DataDir=$dataRoot", '-p:CopyModOnBuild=false')
    Invoke-OfflineStep 'release-build' 'dotnet' (@('build', (Join-Path $sourceRoot 'CombatSolver.csproj'),
        '-c', 'Release', '--nologo', '-v:minimal') + $buildProperties)
    Invoke-OfflineStep 'l1-contracts' 'pwsh' @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'run-contract-tests.ps1'))
    $python = Get-Command python -ErrorAction Stop
    Invoke-OfflineStep 'portfolio-contracts' $python.Source @((Join-Path $PSScriptRoot 'BeamWidthPortfolioChecks/run.py'))
    $harnessRoot = Join-Path $PSScriptRoot 'U0U1PinnedHarness'
    Invoke-OfflineStep 'pinned-build' 'dotnet' (@('build', (Join-Path $harnessRoot 'U0U1PinnedHarness.csproj'),
        '-c', 'Release', '--nologo', '-v:minimal') + $buildProperties)
    $solverDll = Join-Path $sourceRoot '.godot/mono/temp/bin/Release/CombatSolver.dll'
    $solverHash = (Get-FileHash -LiteralPath $solverDll -Algorithm SHA256).Hash
    $copiedSolver = Join-Path $harnessRoot 'bin/Release/net9.0/CombatSolver.dll'
    if ((Get-FileHash -LiteralPath $copiedSolver -Algorithm SHA256).Hash -cne $solverHash) {
        throw 'Pinned harness copied a different CombatSolver build.'
    }
    $env:OFFLINE_HARNESS_COMBATSOLVER_DLL = $solverDll
    foreach ($mode in $modesToRun) {
        Invoke-OfflineStep "pinned-$mode" 'dotnet' @((Join-Path $harnessRoot 'bin/Release/net9.0/U0U1PinnedHarness.dll'),
            $mode, '--out', (Join-Path $outputRoot $mode))
    }
    if ((Get-FileHash -LiteralPath (Join-Path $dataRoot 'sts2.dll') -Algorithm SHA256).Hash -cne $nativeHash) {
        throw 'The game assembly changed during validation; this is not a stable pinned run.'
    }
}
catch {
    $failure = $_.Exception.Message
    Write-Warning $failure
}
finally {
    $env:OFFLINE_HARNESS_COMBATSOLVER_DLL = $oldOverride
    Pop-Location
    [ordered]@{
        status = if ($null -eq $failure) { 'PASS' } else { 'FAIL' }
        evidenceLevel = 'code-level: contracts and pinned in-process simulation; Godot services bypassed'
        gameVersion = [string]$target.game_version; gameDllSha256 = $nativeHash
        solverDllSha256 = $solverHash; modes = $modesToRun
        steps = $steps.ToArray(); limitations = $notes.ToArray(); failure = $failure
        unverified = @('native UI and input timing', 'real Host/Client concurrency', 'complete natural-game coverage')
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot 'summary.json') -Encoding utf8
}
if ($null -ne $failure) { exit 1 }
Write-Output "OFFLINE_VALIDATION_PASS steps=$($steps.Count) summary=$(Join-Path $outputRoot 'summary.json')"
