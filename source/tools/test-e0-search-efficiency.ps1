param(
    [Parameter(Mandatory = $true)][string] $GameData,
    [Parameter(Mandatory = $true)][string] $RitsuProps,
    [Parameter(Mandatory = $true)][string] $RitsuRoot,
    [Parameter(Mandatory = $true)][string] $Workspace
)

$ErrorActionPreference = 'Stop'
$workspacePath = [IO.Path]::GetFullPath($Workspace)
New-Item -ItemType Directory -Force -Path $workspacePath | Out-Null
$project = Join-Path $PSScriptRoot 'E0PinnedHarness/E0PinnedHarness.csproj'
$gameDataPath = (Resolve-Path $GameData).Path

dotnet build $project -c Release `
    -p:Sts2DataDir="$gameDataPath" `
    -p:RitsuLibReferencesProps="$RitsuProps" `
    -p:RitsuWorkshopRoot="$RitsuRoot" `
    -p:RitsuLibReferenceTarget='0.107.1' `
    -p:TreatWarningsAsErrors=true --nologo
if ($LASTEXITCODE -ne 0) {
    throw "E0PinnedHarness build failed with exit $LASTEXITCODE."
}

$harness = Join-Path $PSScriptRoot 'E0PinnedHarness/bin/Release/net9.0/E0PinnedHarness.dll'
$rows = @()
foreach ($scenario in @('simple', 'draw_energy')) {
    $out = Join-Path $workspacePath $scenario
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    & dotnet $harness --scenario $scenario --out $out
    if ($LASTEXITCODE -ne 0) {
        throw "E0 required scenario $scenario failed with exit $LASTEXITCODE."
    }
    $evidence = Get-Content -LiteralPath (Join-Path $out "e0-$scenario.json") -Raw | ConvertFrom-Json
    $rows += [pscustomobject]@{
        case = $scenario
        status = 'PASS'
        sourceMember = "$($evidence.memberKind)#$($evidence.searchMemberId)"
        generatedMs = [math]::Round([double]$evidence.generatedMs, 3)
        evaluatedMs = [math]::Round([double]$evidence.evaluatedMs, 3)
        selectedMs = [math]::Round([double]$evidence.selectedMs, 3)
        publishedMs = [math]::Round([double]$evidence.publishedMs, 3)
        expandedAtGeneration = $evidence.expandedAtGeneration
        turnDepth = $evidence.turnDepth
        route = @($evidence.route)
        reason = $null
    }
}

$teammateOut = Join-Path $workspacePath 'teammate'
New-Item -ItemType Directory -Force -Path $teammateOut | Out-Null
& dotnet $harness --scenario teammate --out $teammateOut
$teammateExit = $LASTEXITCODE
$teammatePath = Join-Path $teammateOut 'e0-teammate.json'
if ($teammateExit -eq 0 -and (Test-Path -LiteralPath $teammatePath)) {
    $evidence = Get-Content -LiteralPath $teammatePath -Raw | ConvertFrom-Json
    $rows += [pscustomobject]@{
        case = 'teammate_cooperation'
        status = 'PASS_DETACHED_TWO_PLAYER'
        sourceMember = "$($evidence.memberKind)#$($evidence.searchMemberId)"
        generatedMs = [math]::Round([double]$evidence.generatedMs, 3)
        evaluatedMs = [math]::Round([double]$evidence.evaluatedMs, 3)
        selectedMs = [math]::Round([double]$evidence.selectedMs, 3)
        publishedMs = [math]::Round([double]$evidence.publishedMs, 3)
        expandedAtGeneration = $evidence.expandedAtGeneration
        turnDepth = $evidence.turnDepth
        route = @($evidence.route)
        reason = 'Pinned detached two-player model; not Host/Client network evidence.'
    }
} else {
    $rows += [pscustomobject]@{
        case = 'teammate_cooperation'
        status = 'UNVERIFIED_NO_REPLAYABLE_MULTIPLAYER_ROOT'
        sourceMember = $null
        generatedMs = $null
        evaluatedMs = $null
        selectedMs = $null
        publishedMs = $null
        expandedAtGeneration = $null
        turnDepth = $null
        route = @()
        reason = "Detached two-player fixture unavailable on pinned runtime; harness_exit=$teammateExit. Real Host/Client evidence still required."
    }
}

$result = [pscustomobject]@{
    schemaVersion = 1
    source = 'pinned-0.107.1-production-coordinator'
    rows = $rows
}
$evidencePath = Join-Path $workspacePath 'e0-search-efficiency-evidence.json'
$result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $evidencePath -Encoding utf8

Write-Host '| case | status | source member | generated ms | evaluated ms | selected ms | published ms |'
Write-Host '|---|---|---|---:|---:|---:|---:|'
foreach ($row in $rows) {
    Write-Host "| $($row.case) | $($row.status) | $($row.sourceMember ?? '-') | $($row.generatedMs ?? '-') | $($row.evaluatedMs ?? '-') | $($row.selectedMs ?? '-') | $($row.publishedMs ?? '-') |"
}
Write-Host "evidence=$evidencePath"
