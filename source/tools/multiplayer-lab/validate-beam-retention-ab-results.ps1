#requires -Version 7.4

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
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Name
    )
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
$comparisons = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/Multiplayer\] MP_BEAM_RETENTION_AB\b' -and
        $_.Text -notmatch 'MP_BEAM_RETENTION_AB_(?:START|FINAL)\b'
    })
$finals = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/Multiplayer\] MP_BEAM_RETENTION_AB_FINAL\b'
    })

$problems = [Collections.Generic.List[string]]::new()
$parsed = [Collections.Generic.List[object]]::new()

foreach ($comparison in $comparisons) {
    $sample = Get-IntToken $comparison.Text 'sample'
    $boundary = Get-IntToken $comparison.Text 'boundary'
    $limit = Get-IntToken $comparison.Text 'limit'
    $legacyOnlyRaw = Get-IntToken $comparison.Text 'legacy_only_raw'
    $legacyOnlySelected = Get-IntToken $comparison.Text 'legacy_only_selected'
    $legacyRank = Get-IntToken $comparison.Text 'legacy_rank'
    $productionRawRank = Get-IntToken $comparison.Text 'production_raw_rank'
    $productionSelectedRank = Get-IntToken $comparison.Text 'production_selected_rank'

    if ($null -in @($sample, $boundary, $limit, $legacyOnlyRaw, $legacyOnlySelected,
                    $legacyRank, $productionRawRank, $productionSelectedRank)) {
        $problems.Add("Malformed MP_BEAM_RETENTION_AB at $($comparison.Path):$($comparison.LineNumber)")
        continue
    }
    if ($sample -lt 1 -or $boundary -lt 0 -or $limit -lt 1) {
        $problems.Add("Invalid sample/boundary/limit at line $($comparison.LineNumber)")
    }
    if ($legacyOnlyRaw -lt 0 -or $legacyOnlySelected -lt 0) {
        $problems.Add("Negative divergence count at line $($comparison.LineNumber)")
    }
    if ($legacyRank -lt 1 -or $legacyRank -gt $limit) {
        $problems.Add("legacy_rank must be inside legacy top-k at line $($comparison.LineNumber)")
    }

    $matchingFinal = @($finals | Where-Object {
            (Get-IntToken $_.Text 'sample') -eq $sample -and
            (Get-IntToken $_.Text 'boundary') -eq $boundary
        })
    if ($legacyOnlySelected -gt 0) {
        if ($matchingFinal.Count -ne 1) {
            $problems.Add("Expected exactly one MP_BEAM_RETENTION_AB_FINAL for sample=$sample boundary=$boundary, got $($matchingFinal.Count)")
            continue
        }
    } elseif ($matchingFinal.Count -gt 0) {
        $problems.Add("Unexpected final marker for a sample with legacy_only_selected=0: sample=$sample boundary=$boundary")
        continue
    }

    $classification = if ($legacyOnlySelected -le 0) {
        'raw_rank_difference_only'
    } else {
        $final = $matchingFinal[0]
        $rescued = Get-BoolToken $final.Text 'rescued_by_outer_portfolio'
        $survived = Get-BoolToken $final.Text 'survived_incumbent'
        $beamPruned = Get-BoolToken $final.Text 'beam_pruned'
        $finalPruned = Get-BoolToken $final.Text 'final_pruned'
        $portfolioRank = Get-IntToken $final.Text 'portfolio_rank'
        $finalRank = Get-IntToken $final.Text 'final_rank'

        if ($null -in @($rescued, $survived, $beamPruned, $finalPruned, $portfolioRank, $finalRank)) {
            $problems.Add("Malformed MP_BEAM_RETENTION_AB_FINAL for sample=$sample boundary=$boundary")
            'malformed'
        } else {
            if ($beamPruned -ne (-not $rescued)) {
                $problems.Add("beam_pruned inconsistent with rescued_by_outer_portfolio for sample=$sample")
            }
            if ($finalPruned -ne (-not $survived)) {
                $problems.Add("final_pruned inconsistent with survived_incumbent for sample=$sample")
            }
            if ($rescued -ne ($portfolioRank -gt 0)) {
                $problems.Add("portfolio_rank inconsistent with rescued_by_outer_portfolio for sample=$sample")
            }
            if ($survived -ne ($finalRank -gt 0)) {
                $problems.Add("final_rank inconsistent with survived_incumbent for sample=$sample")
            }
            if ($survived -and -not $rescued) {
                $problems.Add("A route cannot survive incumbent after being absent from the outer portfolio for sample=$sample")
            }

            if ($beamPruned) {
                'beam_pruned'
            } elseif (-not $survived) {
                'rescued_then_incumbent_pruned'
            } else {
                'rescued_by_outer_portfolio'
            }
        }
    }

    $parsed.Add([pscustomobject]@{
            Sample = $sample
            Boundary = $boundary
            LegacyOnlyRaw = $legacyOnlyRaw
            LegacyOnlySelected = $legacyOnlySelected
            LegacyRank = $legacyRank
            ProductionRawRank = $productionRawRank
            ProductionSelectedRank = $productionSelectedRank
            Classification = $classification
            Evidence = "$($comparison.Path):$($comparison.LineNumber)"
        })
}

if ($starts.Count -eq 0) {
    $status = 'UNVERIFIED'
    $classification = 'diagnostics_not_observed'
} elseif ($problems.Count -gt 0) {
    $status = 'FAIL'
    $classification = 'invalid_evidence'
} elseif ($comparisons.Count -eq 0) {
    $status = 'PASS'
    $classification = 'no_difference_observed'
} elseif (@($parsed | Where-Object Classification -eq 'beam_pruned').Count -gt 0) {
    $status = 'PASS'
    $classification = 'beam_pruning_observed'
} elseif (@($parsed | Where-Object Classification -eq 'rescued_then_incumbent_pruned').Count -gt 0) {
    $status = 'PASS'
    $classification = 'incumbent_pruning_observed'
} elseif (@($parsed | Where-Object Classification -eq 'rescued_by_outer_portfolio').Count -gt 0) {
    $status = 'PASS'
    $classification = 'outer_portfolio_rescue_observed'
} else {
    $status = 'PASS'
    $classification = 'raw_rank_difference_only'
}

$result = [ordered]@{
    schemaVersion = 1
    status = $status
    classification = $classification
    validatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    logFiles = @($resolvedLogs)
    startMarkerCount = $starts.Count
    comparisonCount = $comparisons.Count
    finalMarkerCount = $finals.Count
    beamPrunedCount = @($parsed | Where-Object Classification -eq 'beam_pruned').Count
    rescuedByOuterPortfolioCount = @($parsed | Where-Object Classification -eq 'rescued_by_outer_portfolio').Count
    incumbentPrunedCount = @($parsed | Where-Object Classification -eq 'rescued_then_incumbent_pruned').Count
    rawRankOnlyCount = @($parsed | Where-Object Classification -eq 'raw_rank_difference_only').Count
    samples = @($parsed)
    problems = @($problems)
    limitations = @(
        'This validator classifies production Beam retention telemetry; it does not decide whether the legacy-only route is strategically better.',
        'A real quality conclusion still requires the user-identified better legal prefix or another route-quality comparison from the same multiplayer root.',
        'Detailed diagnostic logs must be enabled for MP_BEAM_RETENTION_AB markers to exist.'
    )
}

$jsonText = $result | ConvertTo-Json -Depth 8
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $outputFull = [IO.Path]::GetFullPath($OutputPath)
    $parent = Split-Path -Parent $outputFull
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [IO.File]::WriteAllText($outputFull, $jsonText, [Text.UTF8Encoding]::new($false))
}
if ($Json) {
    Write-Output $jsonText
} else {
    Write-Output ("MULTIPLAYER_BEAM_RETENTION_AB_{0} classification={1} starts={2} comparisons={3} finals={4}" -f
        $status, $classification, $starts.Count, $comparisons.Count, $finals.Count)
    foreach ($problem in $problems) {
        Write-Output "FAIL $problem"
    }
}

if ($status -eq 'FAIL') { exit 1 }
if ($status -eq 'UNVERIFIED') { exit 2 }
