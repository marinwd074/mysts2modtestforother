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
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Name
    )
    $value = Get-Token $Text $Name
    if ($null -eq $value -or $value -notmatch '^-?\d+$') { return $null }
    return [int]$value
}

$starts = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/Multiplayer\] MP_BEAM_RETENTION_AB_START\b'
    })
$diffs = @($records | Where-Object {
        $_.Text -match '\[CombatSolver/Multiplayer\] MP_BEAM_RETENTION_AB\b' -and
        $_.Text -notmatch '\bMP_BEAM_RETENTION_AB_START\b'
    })

$problems = [Collections.Generic.List[string]]::new()
$parsed = [Collections.Generic.List[object]]::new()
foreach ($record in $diffs) {
    $sample = Get-IntToken $record.Text 'sample'
    $turn = Get-IntToken $record.Text 'turn'
    $pool = Get-IntToken $record.Text 'pool'
    $limit = Get-IntToken $record.Text 'limit'
    $legacyOnlyRaw = Get-IntToken $record.Text 'legacy_only_raw'
    $legacyOnlySelected = Get-IntToken $record.Text 'legacy_only_selected'
    $legacyRank = Get-IntToken $record.Text 'legacy_rank'
    $productionRawRank = Get-IntToken $record.Text 'production_raw_rank'
    $productionSelectedRank = Get-IntToken $record.Text 'production_selected_rank'

    if ($null -in @(
            $sample, $turn, $pool, $limit, $legacyOnlyRaw, $legacyOnlySelected,
            $legacyRank, $productionRawRank, $productionSelectedRank)) {
        $problems.Add("Malformed MP_BEAM_RETENTION_AB at $($record.Path):$($record.LineNumber)")
        continue
    }
    if ($sample -lt 1 -or $pool -le $limit -or $limit -lt 1) {
        $problems.Add("Invalid sample/pool/limit at $($record.Path):$($record.LineNumber)")
    }
    if ($legacyOnlyRaw -lt 0 -or $legacyOnlySelected -lt 0 -or
        ($legacyOnlyRaw -eq 0 -and $legacyOnlySelected -eq 0)) {
        $problems.Add("A difference row must report a positive legacy-only count at $($record.Path):$($record.LineNumber)")
    }
    if ($legacyRank -lt 1 -or $legacyRank -gt $limit) {
        $problems.Add("Legacy witness rank must be inside legacy top-k at $($record.Path):$($record.LineNumber)")
    }
    if ($productionRawRank -lt 1 -or $productionRawRank -gt $pool) {
        $problems.Add("Production raw rank is outside the candidate pool at $($record.Path):$($record.LineNumber)")
    }
    if ($productionSelectedRank -lt 0) {
        $problems.Add("Production selected rank cannot be negative at $($record.Path):$($record.LineNumber)")
    }

    $parsed.Add([pscustomobject]@{
        path = $record.Path
        lineNumber = $record.LineNumber
        sample = $sample
        turn = $turn
        pool = $pool
        limit = $limit
        legacyOnlyRaw = $legacyOnlyRaw
        legacyOnlySelected = $legacyOnlySelected
        legacyRank = $legacyRank
        productionRawRank = $productionRawRank
        productionSelectedRank = $productionSelectedRank
        prefix = Get-Token $record.Text 'prefix'
    })
}

$status = if ($problems.Count -gt 0) {
    'FAIL'
} elseif ($starts.Count -eq 0) {
    'UNVERIFIED'
} elseif ($diffs.Count -gt 0) {
    'DIFFERENCE'
} else {
    'NO_DIFFERENCE'
}

$result = [ordered]@{
    schemaVersion = 1
    status = $status
    validatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    logFiles = @($resolvedLogs)
    startMarkerCount = $starts.Count
    differenceCount = $diffs.Count
    differences = @($parsed)
    problems = @($problems)
    interpretation = switch ($status) {
        'DIFFERENCE' { 'At least one production multiplayer Beam boundary displaced a candidate that the legacy single-player Beam ordering would keep in top-k on the same candidate pool.' }
        'NO_DIFFERENCE' { 'Beam A/B diagnostics were active, but this run did not observe a legacy-top-k candidate displaced by multiplayer TeamObjective at the sampled production Beam boundaries.' }
        'UNVERIFIED' { 'No MP_BEAM_RETENTION_AB_START marker was observed. Enable detailed diagnostic logs and use a real multiplayer search root.' }
        default { 'Beam A/B evidence is malformed or internally inconsistent.' }
    }
    limitations = @(
        'DIFFERENCE proves a ranking/retention divergence on a real production candidate pool; it does not by itself prove that the displaced route is the globally best hand-play route.',
        'NO_DIFFERENCE applies only to the sampled run and logged boundaries.',
        'The validator is read-only and does not change search candidates, BeamWidth, node budget, time budget, or production selection.'
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
    Write-Output ("MULTIPLAYER_BEAM_AB_{0} logs={1} starts={2} differences={3}" -f
        $status, $resolvedLogs.Count, $starts.Count, $diffs.Count)
    if ($problems.Count -gt 0) {
        foreach ($problem in $problems) { Write-Output "FAIL $problem" }
    }
    foreach ($item in $parsed | Select-Object -First 12) {
        Write-Output ("DIFF turn={0} pool={1} limit={2} legacy_rank={3} production_raw_rank={4} production_selected_rank={5} legacy_only_raw={6} legacy_only_selected={7} prefix={8}" -f
            $item.turn, $item.pool, $item.limit, $item.legacyRank, $item.productionRawRank,
            $item.productionSelectedRank, $item.legacyOnlyRaw, $item.legacyOnlySelected, $item.prefix)
    }
}

if ($status -eq 'FAIL') { exit 1 }
if ($status -eq 'UNVERIFIED') { exit 2 }
exit 0
