#requires -Version 7.0

[CmdletBinding()]
param(
    [string]$HarnessDll = (Join-Path $PSScriptRoot 'U2DegenerateHarness/bin/Release/net9.0/U2DegenerateHarness.dll'),
    [string]$Workspace = (Join-Path $PSScriptRoot '../.local/u2-degenerate-equivalence'),
    [string]$Character = 'IRONCLAD',
    [string]$Encounter = 'FUZZY_WURM_CRAWLER_WEAK',
    [string]$Seed = 'U2DEGENERATE1',
    [int]$Beam = 24,
    [int]$Nodes = 4000,
    [int]$BudgetMilliseconds = 600000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$HarnessDll = [System.IO.Path]::GetFullPath($HarnessDll)
$Workspace = [System.IO.Path]::GetFullPath($Workspace)
if (-not (Test-Path -LiteralPath $HarnessDll)) {
    throw "U2DegenerateHarness not built: $HarnessDll"
}

Remove-Item -LiteralPath $Workspace -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $Workspace -Force | Out-Null

$harnessArgs = @(
    '--character', $Character,
    '--encounter', $Encounter,
    '--seed', $Seed,
    '--beam', "$Beam",
    '--nodes', "$Nodes",
    '--budget-ms', "$BudgetMilliseconds",
    '--out', $Workspace
)
& dotnet $HarnessDll @harnessArgs
if ($LASTEXITCODE -ne 0) {
    throw "U2 degenerate equivalence harness failed with exit code $LASTEXITCODE."
}

$evidencePath = Join-Path $Workspace 'u2-degenerate-equivalence.json'
if (-not (Test-Path -LiteralPath $evidencePath)) {
    throw "U2 evidence missing: $evidencePath"
}
$evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json -Depth 100
if ($evidence.status -ne 'PASS') {
    throw "U2 evidence did not report PASS: $($evidence.status)"
}
if ($evidence.root.playerCount -ne 1) {
    throw "U2 fixture is not degenerate single-player root: playerCount=$($evidence.root.playerCount)"
}

Write-Output 'U2DegenerateEquivalence PASS: same captured state, same objective, same fixed budget, identical first action / terminal value / fixed-tie-break sequence'
Write-Output "Evidence: $evidencePath"
