#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$LeftProbePath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$RightProbePath,

    [string]$OutputPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RecordValue {
    param(
        [Parameter(Mandatory = $true)]$Record,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($Record -is [System.Collections.IDictionary] -and $Record.Contains($Name)) {
        return $Record[$Name]
    }
    return $null
}

function Read-ProbeRecords {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolvedPaths = @(Resolve-Path -Path $Path -ErrorAction Stop)
    if ($resolvedPaths.Count -ne 1) {
        throw "Probe path must resolve to exactly one file: $Path"
    }
    $resolved = $resolvedPaths[0].Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Probe path is not a file: $resolved"
    }

    $records = [Collections.Generic.List[object]]::new()
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $resolved) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $record = $line | ConvertFrom-Json -AsHashtable -ErrorAction Stop
        }
        catch {
            throw "Invalid Probe JSON at $resolved`:$lineNumber"
        }

        if ($record -isnot [System.Collections.IDictionary]) {
            throw "Probe record is not an object at $resolved`:$lineNumber"
        }
        if ((Get-RecordValue $record 'schemaVersion') -ne 1) {
            throw "Unsupported Probe schemaVersion at $resolved`:$lineNumber"
        }
        if (-not ($record.Contains('sequence') -and $record.Contains('worldVersion'))) {
            throw "Probe record is missing sequence/worldVersion at $resolved`:$lineNumber"
        }
        if (-not $record.Contains('enemies')) {
            throw "Probe record is missing enemies at $resolved`:$lineNumber"
        }
        $records.Add($record)
    }

    if ($records.Count -eq 0) {
        throw "Probe contains no records: $resolved"
    }

    return [pscustomobject]@{
        Path = $resolved
        Records = @($records)
    }
}

function Get-ProbeSegments {
    param([Parameter(Mandatory = $true)][object[]]$Records)

    $segments = [Collections.Generic.List[object]]::new()
    $current = [Collections.Generic.List[object]]::new()
    $previousSequence = $null
    $previousWorldVersion = $null

    foreach ($record in $Records) {
        [long]$sequence = Get-RecordValue $record 'sequence'
        [long]$worldVersion = Get-RecordValue $record 'worldVersion'
        if ($current.Count -gt 0 -and
            ($sequence -le $previousSequence -or $worldVersion -le $previousWorldVersion)) {
            $segments.Add(@($current))
            $current = [Collections.Generic.List[object]]::new()
        }
        $current.Add($record)
        $previousSequence = $sequence
        $previousWorldVersion = $worldVersion
    }

    if ($current.Count -gt 0) {
        $segments.Add(@($current))
    }

    return @($segments)
}

function Get-Seed {
    param([Parameter(Mandatory = $true)]$Record)

    $fingerprint = [string](Get-RecordValue $Record 'hardFingerprint')
    if ($fingerprint -match '(?:^|;)seed=([^;]+)') {
        return $Matches[1]
    }
    return ''
}

function Get-EnemyStateKey {
    param([Parameter(Mandatory = $true)]$Record)

    return (@((Get-RecordValue $Record 'enemies')) | ForEach-Object { [string]$_ }) -join "`u{1F}"
}

function Get-UniqueEnemyStates {
    param([Parameter(Mandatory = $true)][object[]]$Records)

    $states = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($record in $Records) {
        $key = Get-EnemyStateKey $record
        if (-not [string]::IsNullOrWhiteSpace($key)) {
            [void]$states.Add($key)
        }
    }
    return $states
}

$left = Read-ProbeRecords $LeftProbePath
$right = Read-ProbeRecords $RightProbePath
if ([StringComparer]::OrdinalIgnoreCase.Equals($left.Path, $right.Path)) {
    throw 'Left and right Probe paths must be different independent captures.'
}

$leftSegments = Get-ProbeSegments @($left.Records)
$rightSegments = Get-ProbeSegments @($right.Records)
$segmentResults = [Collections.Generic.List[object]]::new()
$segmentCount = [Math]::Min($leftSegments.Count, $rightSegments.Count)

for ($index = 0; $index -lt $segmentCount; $index++) {
    $leftRecords = @($leftSegments[$index])
    $rightRecords = @($rightSegments[$index])
    $leftStates = Get-UniqueEnemyStates $leftRecords
    $rightStates = Get-UniqueEnemyStates $rightRecords
    $leftOnly = [Collections.Generic.List[string]]::new()
    $rightOnly = [Collections.Generic.List[string]]::new()

    foreach ($state in $leftStates) {
        if (-not $rightStates.Contains($state)) {
            $leftOnly.Add($state)
        }
    }
    foreach ($state in $rightStates) {
        if (-not $leftStates.Contains($state)) {
            $rightOnly.Add($state)
        }
    }

    $segmentResults.Add([ordered]@{
            segment = $index + 1
            leftSeed = Get-Seed $leftRecords[0]
            rightSeed = Get-Seed $rightRecords[0]
            leftRecords = $leftRecords.Count
            rightRecords = $rightRecords.Count
            commonEnemyStates = @($leftStates | Where-Object { $rightStates.Contains($_) }).Count
            leftOnlyEnemyStates = $leftOnly.Count
            rightOnlyEnemyStates = $rightOnly.Count
            leftOnlySample = @($leftOnly | Select-Object -First 3)
            rightOnlySample = @($rightOnly | Select-Object -First 3)
        })
}

$allSegmentsComparable = $leftSegments.Count -eq $rightSegments.Count -and
    $segmentResults.Count -eq $leftSegments.Count
$allStatesEqual = $allSegmentsComparable -and
    @($segmentResults | Where-Object {
            $_.leftSeed -ne $_.rightSeed -or
            $_.leftOnlyEnemyStates -ne 0 -or
            $_.rightOnlyEnemyStates -ne 0
        }).Count -eq 0
$status = if ($allStatesEqual) { 'PASS' } else { 'UNVERIFIED' }

$result = [ordered]@{
    schemaVersion = 1
    status = $status
    comparison = 'independent-client-public-enemy-state'
    leftProbe = $left.Path
    rightProbe = $right.Path
    leftRecords = @($left.Records).Count
    rightRecords = @($right.Records).Count
    leftSegments = $leftSegments.Count
    rightSegments = $rightSegments.Count
    segments = @($segmentResults)
    notes = @(
        'PASS requires equal per-segment seed and equal sets of observed ordered enemy public states.',
        'Different sampling windows remain UNVERIFIED; this report never promotes the Phase 0 matrix automatically.',
        'This compares two independent CombatSolver clients and does not alter the HostVanilla profile or network protocol.'
    )
}

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $outputFullPath = [IO.Path]::GetFullPath($OutputPath)
    if ($outputFullPath -notlike 'D:\*') {
        throw "Multiplayer comparison output must be on D:; received $outputFullPath"
    }
    $outputParent = Split-Path -Parent $outputFullPath
    New-Item -ItemType Directory -Path $outputParent -Force | Out-Null
    $result.outputPath = $outputFullPath
    $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $outputFullPath -Encoding utf8NoBOM
}

$result | ConvertTo-Json -Depth 10
if ($status -eq 'UNVERIFIED') {
    exit 2
}
