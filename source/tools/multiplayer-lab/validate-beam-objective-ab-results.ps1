#requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$LogPath,

    [string]$OutputPath = '',

    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$records = [Collections.Generic.List[object]]::new()
$resolvedLogs = [Collections.Generic.List[string]]::new()
$globalIndex = 0
foreach ($pathValue in $LogPath) {
    $path = (Resolve-Path -LiteralPath $pathValue -ErrorAction Stop).Path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Beam A/B log path is not a file: $path"
    }
    $resolvedLogs.Add($path)
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $path) {
        $lineNumber++
        $records.Add([pscustomobject]@{
                Index = $globalIndex++
                Path = $path
                LineNumber = $lineNumber
                Text = [string]$line
            })
    }
}

function Get-Token {
    param([Parameter(Mandatory)][string]$Text, [Parameter(Mandatory)][string]$Name)
    $pattern = '(?:^|\s)' + [regex]::Escape($Name) + '=([^\s]+)'
    if ($Text -match $pattern) { return $Matches[1] }
    return $null
}

function Get-IntToken {
    param([Parameter(Mandatory)][string]$Text, [Parameter(Mandatory)][string]$Name)
    $value = Get-Token $Text $Name
    if ($null -eq $value -or $value -notmatch '^-?\d+$') { return $null }
    return [int]$value
}

function Get-BoolToken {
    param([Parameter(Mandatory)][string]$Text, [Parameter(Mandatory)][string]$Name)
    $value = Get-Token $Text $Name
    if ($value -eq 'true') { return $true }
    if ($value -eq 'false') { return $false }
    return $null
}

$starts = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/Multiplayer\] MP_BEAM_RETENTION_AB_START\b'
    })
$observations = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/Multiplayer\] MP_BEAM_RETENTION_AB\s'
    })
$finals = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/Multiplayer\] MP_BEAM_RETENTION_AB_FINAL\b'
    })

$problems = [Collections.Generic.List[string]]::new()
$incomplete = [Collections.Generic.List[string]]::new()
$beamPruned = 0
$rescued = 0
$rawOnly = 0
$finalPruned = 0

$finalByKey = @{}
foreach ($final in $finals) {
    $sample = Get-IntToken $final.Text 'sample'
    $boundary = Get-IntToken $final.Text 'boundary'
    $portfolioRescued = Get-BoolToken $final.Text 'rescued_by_outer_portfolio'
    $survivedIncumbent = Get-BoolToken $final.Text 'survived_incumbent'
    $isBeamPruned = Get-BoolToken $final.Text 'beam_pruned'
    $isFinalPruned = Get-BoolToken $final.Text 'final_pruned'
    if ($null -in @($sample, $boundary, $portfolioRescued, $survivedIncumbent, $isBeamPruned, $isFinalPruned)) {
        $problems.Add("Malformed MP_BEAM_RETENTION_AB_FINAL at $($final.Path):$($final.LineNumber)")
        continue
    }
    if ($isBeamPruned -ne (-not $portfolioRescued)) {
        $problems.Add("beam_pruned disagrees with rescued_by_outer_portfolio for sample=$sample boundary=$boundary")
    }
    if ($isFinalPruned -ne (-not $survivedIncumbent)) {
        $problems.Add("final_pruned disagrees with survived_incumbent for sample=$sample boundary=$boundary")
    }
    $key = "$sample/$boundary"
    if ($finalByKey.ContainsKey($key)) {
        $problems.Add("Duplicate final marker for sample=$sample boundary=$boundary")
    } else {
        $finalByKey[$key] = $final
    }
    if ($isBeamPruned) { $beamPruned++ }
    elseif ($portfolioRescued) { $rescued++ }
    if ($isFinalPruned) { $finalPruned++ }
}

foreach ($observation in $observations) {
    $sample = Get-IntToken $observation.Text 'sample'
    $boundary = Get-IntToken $observation.Text 'boundary'
    $legacyOnlyRaw = Get-IntToken $observation.Text 'legacy_only_raw'
    $legacyOnlySelected = Get-IntToken $observation.Text 'legacy_only_selected'
    if ($null -in @($sample, $boundary, $legacyOnlyRaw, $legacyOnlySelected)) {
        $problems.Add("Malformed MP_BEAM_RETENTION_AB at $($observation.Path):$($observation.LineNumber)")
        continue
    }
    if ($legacyOnlyRaw -le 0 -and $legacyOnlySelected -le 0) {
        $problems.Add("A/B observation reports no difference for sample=$sample boundary=$boundary")
    }
    if ($legacyOnlySelected -gt 0) {
        $key = "$sample/$boundary"
        if (-not $finalByKey.ContainsKey($key)) {
            $incomplete.Add("Missing final retention marker for sample=$sample boundary=$boundary")
        }
    } elseif ($legacyOnlyRaw -gt 0) {
        $rawOnly++
    }
}

$status = 'PASS'
$classification = 'no_difference_observed'
if ($starts.Count -eq 0) {
    $status = 'UNVERIFIED'
    $classification = 'diagnostic_not_active'
} elseif ($problems.Count -gt 0) {
    $status = 'FAIL'
    $classification = 'malformed_trace'
} elseif ($incomplete.Count -gt 0) {
    $status = 'UNVERIFIED'
    $classification = 'incomplete_trace'
} elseif ($beamPruned -gt 0) {
    $classification = 'beam_pruned_legacy_candidate'
} elseif ($rescued -gt 0) {
    $classification = 'rescued_by_outer_portfolio'
} elseif ($rawOnly -gt 0) {
    $classification = 'raw_order_only'
}

$result = [ordered]@{
    schemaVersion = 1
    status = $status
    classification = $classification
    validatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    logFiles = @($resolvedLogs)
    startCount = $starts.Count
    observationCount = $observations.Count
    finalCount = $finals.Count
    beamPrunedCount = $beamPruned
    rescuedByOuterPortfolioCount = $rescued
    rawOrderOnlyCount = $rawOnly
    finalPrunedCount = $finalPruned
    problems = @($problems)
    incomplete = @($incomplete)
    limitations = @(
        'This validator classifies production Beam retention A/B telemetry. It does not decide whether the legacy-only route is strategically better.',
        'A real quality conclusion still requires a legal hand-play prefix or other route-quality evidence from the same battle.',
        'No-difference evidence is meaningful only when MP_BEAM_RETENTION_AB_START is present.'
    )
}

$jsonText = $result | ConvertTo-Json -Depth 6
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $outputFull = [IO.Path]::GetFullPath($OutputPath)
    $parent = Split-Path -Parent $outputFull
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    Set-Content -LiteralPath $outputFull -Value $jsonText -Encoding utf8
}

if ($Json) {
    Write-Output $jsonText
} else {
    Write-Output "MULTIPLAYER_BEAM_OBJECTIVE_AB_$status classification=$classification observations=$($observations.Count) finals=$($finals.Count) beam_pruned=$beamPruned rescued=$rescued raw_only=$rawOnly"
}

switch ($status) {
    'PASS' { exit 0 }
    'FAIL' { exit 1 }
    default { exit 2 }
}
