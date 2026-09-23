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
$globalIndex = 0
foreach ($pathValue in $LogPath) {
    $path = (Resolve-Path -LiteralPath $pathValue -ErrorAction Stop).Path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "P0 baseline log path is not a file: $path"
    }
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $path) {
        $lineNumber++
        $normalized = [string]$line
        try {
            $parsed = $normalized | ConvertFrom-Json -ErrorAction Stop
            if ($null -ne $parsed.PSObject.Properties['Message']) {
                $normalized = [string]$parsed.Message
            }
        } catch {
            # Plain-text game logs are expected too.
        }
        $records.Add([pscustomobject]@{
                Index = $globalIndex++
                Path = $path
                LineNumber = $lineNumber
                Text = $normalized
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

function Format-Evidence {
    param($Record)
    return '{0}:{1}: {2}' -f $Record.Path, $Record.LineNumber, $Record.Text
}

$findings = [Collections.Generic.List[object]]::new()
function Add-Finding {
    param(
        [Parameter(Mandatory)][string]$Classification,
        [Parameter(Mandatory)]$Record,
        [Parameter(Mandatory)][string]$Reason
    )
    $findings.Add([ordered]@{
            classification = $Classification
            reason = $Reason
            evidence = Format-Evidence $Record
        })
}

foreach ($record in $records) {
    $text = $record.Text

    if ($text -match '\[CombatSolver/Evidence\] ROUTE_REPLAY\b' -and
        $text -match '"firstScalarDifference"\s*:\s*\d+') {
        Add-Finding simulation_error $record 'Selected-route replay diverged from the search snapshot.'
        continue
    }

    if ($text -match '\[CombatSolver/Test\] MANUAL_ROUTE_IMPROVED\b' -or
        $text -match '找到更优世界线') {
        Add-Finding search_miss_evidence $record 'A better observed route exists; same-root and same-budget verification is still required before calling it a proven search miss.'
        continue
    }

    if ($text -match '\[CombatSolver/Test\] MP_LOCAL_XTURN_CONTINUATION_REJECTED\b') {
        $reason = Get-Token $text 'reason'
        $localExact = Get-Token $text 'local_state_exact'
        if ($reason -eq 'remote_public_mismatch' -and $localExact -eq 'true') {
            Add-Finding teammate_prediction_deviation $record 'Local continuation state matched, but readable teammate state diverged from the selected Shadow/Joint worldline; expected_remote_fp and actual_remote_fp identify the compared worlds when present.'
        } else {
            Add-Finding runtime_state_mismatch $record ("Continuation rejected at runtime: reason=" + ($reason ?? 'unknown') + " local_state_exact=" + ($localExact ?? 'unknown'))
        }
        continue
    }

    if ($text -match '\[CombatSolver/Test\] SEARCH_REUSE_MISS\b') {
        $reason = Get-Token $text 'continuation_reject_reason'
        $localExact = Get-Token $text 'local_state_exact'
        if ($reason -eq 'remote_public_mismatch' -and $localExact -eq 'true') {
            Add-Finding teammate_prediction_deviation $record 'Fresh search was triggered solely by readable teammate-state divergence.'
        } elseif ($reason -ne $null -and $reason -ne 'none') {
            Add-Finding runtime_state_mismatch $record ("Reuse miss: reason=" + $reason + " local_state_exact=" + ($localExact ?? 'unknown'))
        }
    }
}

$classifications = @($findings | ForEach-Object { $_.classification } | Select-Object -Unique)
if ($classifications.Count -eq 0) {
    $classifications = @('unclassified')
}

$priority = @(
    'simulation_error',
    'search_miss_evidence',
    'teammate_prediction_deviation',
    'runtime_state_mismatch',
    'unclassified'
)
$primary = $priority | Where-Object { $classifications -contains $_ } | Select-Object -First 1

$result = [ordered]@{
    schemaVersion = 1
    phase = 'P0'
    primaryClassification = $primary
    classifications = @($classifications)
    findingCount = $findings.Count
    findings = @($findings)
    notes = @(
        'search_miss_evidence is not a proof of search incompleteness until the better route is reproduced from the same root under the same total budget.',
        'teammate_prediction_deviation means the selected Shadow/Joint forecast did not match the observed teammate state; expected/actual fingerprints identify the worlds but do not measure semantic distance.',
        'runtime_state_mismatch covers continuation admission failures that are not isolated teammate forecast drift.'
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
    Write-Output ("P0_BASELINE_CLASSIFICATION primary={0} findings={1}" -f $primary, $findings.Count)
    foreach ($classification in $classifications) {
        Write-Output ("CLASS {0}" -f $classification)
    }
}
