param(
    [Parameter(Mandatory = $true)][string] $GameData,
    [Parameter(Mandatory = $true)][string] $RitsuProps,
    [Parameter(Mandatory = $true)][string] $RitsuRoot,
    [Parameter(Mandatory = $true)][string] $Workspace
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $PSScriptRoot 'OfflineSearchHarness/OfflineSearchHarness.csproj'
$gameDataPath = (Resolve-Path $GameData).Path
$workspacePath = [IO.Path]::GetFullPath($Workspace)
New-Item -ItemType Directory -Force -Path $workspacePath | Out-Null

dotnet build $project -c Release `
    -p:Sts2DataDir="$gameDataPath" `
    -p:RitsuLibReferencesProps="$RitsuProps" `
    -p:RitsuWorkshopRoot="$RitsuRoot" `
    -p:RitsuLibReferenceTarget='0.107.1' `
    -p:TreatWarningsAsErrors=true --nologo
if ($LASTEXITCODE -ne 0) {
    throw "OfflineSearchHarness build failed with exit $LASTEXITCODE."
}

$harness = Join-Path $PSScriptRoot 'OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll'
if (-not (Test-Path -LiteralPath $harness)) {
    throw "OfflineSearchHarness output missing: $harness"
}

function Invoke-E0Case {
    param(
        [Parameter(Mandatory = $true)][string] $Label,
        [Parameter(Mandatory = $true)][string[]] $Arguments
    )

    $out = Join-Path $workspacePath $Label
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    & dotnet $harness @Arguments --label $Label --out $out --profile Medium --dop 1 --budget-ms 5000 --search-mode Coordinator --use-portfolio
    if ($LASTEXITCODE -ne 0) {
        throw "E0 case $Label failed with exit $LASTEXITCODE."
    }

    $resultPath = Join-Path $out 'result.json'
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    $selected = $result.searchEfficiency.selected
    if ($null -eq $selected) {
        throw "E0 case $Label did not export a selected search-efficiency candidate."
    }
    foreach ($field in @('generatedMs', 'evaluatedMs', 'selectedMs', 'publishedMs')) {
        if ($null -eq $selected.$field) {
            throw "E0 case $Label is missing timeline field $field."
        }
    }
    if ($selected.generatedMs -gt $selected.evaluatedMs -or
        $selected.evaluatedMs -gt $selected.selectedMs -or
        $selected.selectedMs -gt $selected.publishedMs) {
        throw "E0 case $Label timeline is not monotonic."
    }

    [pscustomobject]@{
        case = $Label
        status = 'PASS'
        memberId = $selected.searchMemberId
        memberKind = $selected.memberKind
        generatedMs = [math]::Round([double]$selected.generatedMs, 3)
        evaluatedMs = [math]::Round([double]$selected.evaluatedMs, 3)
        selectedMs = [math]::Round([double]$selected.selectedMs, 3)
        publishedMs = [math]::Round([double]$selected.publishedMs, 3)
        expandedAtGeneration = $selected.expandedAtGeneration
        turnDepth = $selected.turnDepth
        evaluationContextId = $selected.evaluationContextId
        route = @($result.solverMetrics.actions | ForEach-Object {
            "$($_.turn):$($_.kind):$($_.cardId ?? $_.potionId ?? '-')"
        })
    }
}

$rows = @()
$rows += Invoke-E0Case -Label 'simple_attack_defense' -Arguments @(
    '--character', 'IRONCLAD',
    '--encounter', 'FUZZY_WURM_CRAWLER_WEAK',
    '--seed', 'E0-SIMPLE-001',
    '--ascension', '0',
    '--act-index', '0'
)

$generatedScenario = (Resolve-Path (Join-Path $PSScriptRoot 'GeneratedCombatScenarios/specified.json')).Path
$requestPath = Join-Path $workspacePath 'draw-energy.request.json'
@{
    schemaVersion = 1
    runId = 'e0-draw-energy'
    scenarioId = 'E0-DRAW-ENERGY'
    generatedScenarioPath = $generatedScenario
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $requestPath -Encoding utf8

$rows += Invoke-E0Case -Label 'draw_energy_combo' -Arguments @(
    '--request', $requestPath
)

# A true teammate-cooperation E0 row requires a current-HEAD PlayerCount=2 replay root.
# The cloud-pinned repository has no such replayable problem package; do not fabricate one
# from L1 contracts or a single-player root.
$rows += [pscustomobject]@{
    case = 'teammate_cooperation'
    status = 'UNVERIFIED_NO_REPLAYABLE_MULTIPLAYER_ROOT'
    memberId = $null
    memberKind = $null
    generatedMs = $null
    evaluatedMs = $null
    selectedMs = $null
    publishedMs = $null
    expandedAtGeneration = $null
    turnDepth = $null
    evaluationContextId = $null
    route = @()
}

$evidence = [pscustomobject]@{
    schemaVersion = 1
    source = 'pinned-0.107.1-offline-search-harness'
    rows = $rows
    multiplayerEvidenceStatus = 'UNVERIFIED'
    multiplayerEvidenceReason = 'No current-HEAD replayable PlayerCount=2 problem package is available in the repository/CI workspace.'
}
$evidencePath = Join-Path $workspacePath 'e0-search-efficiency-evidence.json'
$evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $evidencePath -Encoding utf8

Write-Host '| case | status | source member | generated ms | evaluated ms | selected ms | published ms |'
Write-Host '|---|---|---|---:|---:|---:|---:|'
foreach ($row in $rows) {
    Write-Host "| $($row.case) | $($row.status) | $($row.memberKind ?? '-')#$($row.memberId ?? '-') | $($row.generatedMs ?? '-') | $($row.evaluatedMs ?? '-') | $($row.selectedMs ?? '-') | $($row.publishedMs ?? '-') |"
}
Write-Host "evidence=$evidencePath"
