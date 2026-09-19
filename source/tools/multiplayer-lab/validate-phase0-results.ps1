#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$MatrixPath,

    [string[]]$ProbePath,

    [ValidateSet('MP-0A', 'MP-0B', 'MP-0', 'All')]
    [string]$Phase = 'All',

    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$connectionChecks = @(
    'vanillaHostAcceptedClient',
    'hostUnaware',
    'noCustomNetworkPackets',
    'wireModelCompatibility'
)
$readOnlyChecks = @(
    'localPlayerIdentity',
    'localHand',
    'localDrawPile',
    'drawAfterDraw',
    'shuffleOrder',
    'localDiscardExhaust',
    'enemyStateSync',
    'multiplayerScaling',
    'remoteWorldDelta',
    'probeReadOnly',
    'lifecycle',
    'singleplayerRegression'
)
$requiredChecks = if ($Phase -eq 'MP-0A') {
    $connectionChecks
} elseif ($Phase -eq 'MP-0B') {
    $readOnlyChecks
} else {
    $connectionChecks + $readOnlyChecks
}
$probeRequired = $Phase -in @('MP-0B', 'MP-0', 'All')
$requiredProfileResults = if ($Phase -in @('MP-0A', 'MP-0', 'All')) {
    @(
        'HostVanilla + ClientVanilla',
        'HostVanilla + ClientRitsuOnly',
        'HostVanilla + ClientCombatSolver'
    )
} else {
    @()
}

function Get-MapValue {
    param(
        [Parameter(Mandatory)]$Map,
        [Parameter(Mandatory)][string]$Name
    )

    if ($Map -is [System.Collections.IDictionary] -and $Map.Contains($Name)) {
        return $Map[$Name]
    }
    return $null
}

function Has-MapKey {
    param(
        [Parameter(Mandatory)]$Map,
        [Parameter(Mandatory)][string]$Name
    )

    return $Map -is [System.Collections.IDictionary] -and $Map.Contains($Name)
}

function Resolve-InputFile {
    param([Parameter(Mandatory)][string]$Path)

    return (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
}

$matrixFile = Resolve-InputFile $MatrixPath
$probeFiles = @($ProbePath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    ForEach-Object { Resolve-InputFile $_ })
$matrix = Get-Content -LiteralPath $matrixFile -Raw | ConvertFrom-Json -AsHashtable
if ((Get-MapValue $matrix 'schemaVersion') -ne 1) {
    throw "Phase 0 matrix schemaVersion must be 1: $matrixFile"
}
$matrixChecks = Get-MapValue $matrix 'checks'
if ($matrixChecks -isnot [System.Collections.IDictionary]) {
    throw "Phase 0 matrix must contain a checks object: $matrixFile"
}

$matrixResults = [System.Collections.Generic.List[object]]::new()
foreach ($checkName in $requiredChecks) {
    if (-not (Has-MapKey $matrixChecks $checkName)) {
        $matrixResults.Add([pscustomobject]@{
                Name = $checkName
                Status = 'UNVERIFIED'
                Evidence = $null
                Detail = 'missing check'
            })
        continue
    }

    $entry = Get-MapValue $matrixChecks $checkName
    $status = if ($entry -is [System.Collections.IDictionary]) {
        [string](Get-MapValue $entry 'status')
    } else {
        'UNVERIFIED'
    }
    $evidence = if ($entry -is [System.Collections.IDictionary]) {
        [string](Get-MapValue $entry 'evidence')
    } else {
        ''
    }

    if ($status -notin @('PASS', 'FAIL', 'UNVERIFIED')) {
        $status = 'UNVERIFIED'
        $detail = 'status must be PASS, FAIL, or UNVERIFIED'
    } elseif ($status -eq 'PASS' -and [string]::IsNullOrWhiteSpace($evidence)) {
        $status = 'UNVERIFIED'
        $detail = 'PASS requires an evidence reference'
    } elseif ([string]::IsNullOrWhiteSpace($evidence)) {
        $detail = 'no evidence reference'
    } else {
        $detail = $null
    }

    $matrixResults.Add([pscustomobject]@{
            Name = $checkName
            Status = $status
            Evidence = if ([string]::IsNullOrWhiteSpace($evidence)) { $null } else { $evidence }
            Detail = $detail
    })
}

if ($requiredProfileResults.Count -gt 0) {
    $profileResults = Get-MapValue $matrix 'profileResults'
    $profileListValid = $null -ne $profileResults -and $profileResults -is [System.Collections.IEnumerable] -and $profileResults -isnot [System.Collections.IDictionary] -and $profileResults -isnot [string]
    $profileEntries = if ($profileListValid) {
        @($profileResults)
    } else {
        @()
    }

    foreach ($profileName in $requiredProfileResults) {
        $profileEntry = $null
        foreach ($candidate in $profileEntries) {
            $candidateMatches = $candidate -is [System.Collections.IDictionary] -and [string](Get-MapValue $candidate 'name') -eq $profileName
            if ($candidateMatches) {
                $profileEntry = $candidate
                break
            }
        }

        if ($null -eq $profileEntry) {
            $matrixResults.Add([pscustomobject]@{
                    Name = "profile:$profileName"
                    Status = 'UNVERIFIED'
                    Evidence = $null
                    Detail = 'missing profile result'
                })
            continue
        }

        $status = [string](Get-MapValue $profileEntry 'status')
        $evidence = [string](Get-MapValue $profileEntry 'evidence')
        if ($status -notin @('PASS', 'FAIL', 'UNVERIFIED')) {
            $status = 'UNVERIFIED'
            $detail = 'status must be PASS, FAIL, or UNVERIFIED'
        } elseif ($status -eq 'PASS' -and [string]::IsNullOrWhiteSpace($evidence)) {
            $status = 'UNVERIFIED'
            $detail = 'PASS requires an evidence reference'
        } elseif ([string]::IsNullOrWhiteSpace($evidence)) {
            $detail = 'no evidence reference'
        } else {
            $detail = $null
        }

        $matrixResults.Add([pscustomobject]@{
                Name = "profile:$profileName"
                Status = $status
                Evidence = if ([string]::IsNullOrWhiteSpace($evidence)) { $null } else { $evidence }
                Detail = $detail
            })
    }
}

$probeFailures = [System.Collections.Generic.List[string]]::new()
$probeRecordCount = 0
$probeFileCount = 0
foreach ($probeFile in $probeFiles) {
    $probeFileCount++
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $probeFile) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $record = $line | ConvertFrom-Json -AsHashtable
        } catch {
            $probeFailures.Add(('{0}:{1} invalid JSON' -f $probeFile, $lineNumber))
            continue
        }

        $probeRecordCount++
        if ($record -isnot [System.Collections.IDictionary]) {
            $probeFailures.Add(('{0}:{1} record is not an object' -f $probeFile, $lineNumber))
            continue
        }

        if ((Get-MapValue $record 'schemaVersion') -ne 1) {
            $probeFailures.Add(('{0}:{1} unsupported schemaVersion' -f $probeFile, $lineNumber))
        }
        if ([string]::IsNullOrWhiteSpace([string](Get-MapValue $record 'hardFingerprint'))) {
            $probeFailures.Add(('{0}:{1} missing hardFingerprint' -f $probeFile, $lineNumber))
        }
        if ([string]::IsNullOrWhiteSpace([string](Get-MapValue $record 'localNetId'))) {
            $probeFailures.Add(('{0}:{1} missing localNetId' -f $probeFile, $lineNumber))
        }
        foreach ($field in @(
                'localHand',
                'localDrawPile',
                'localDiscard',
                'localExhaust',
                'enemies',
                'rngStates',
                'multiplayerScalingHooks',
                'cardMultiplayerConstraint')) {
            if (-not (Has-MapKey $record $field)) {
                $probeFailures.Add(('{0}:{1} missing {2}' -f $probeFile, $lineNumber, $field))
            }
        }
        if ($null -eq (Get-MapValue $record 'multiplayerScalingHooks')) {
            $probeFailures.Add(('{0}:{1} multiplayerScalingHooks is unavailable' -f $probeFile, $lineNumber))
        }
        if ([string]::IsNullOrWhiteSpace([string](Get-MapValue $record 'cardMultiplayerConstraint'))) {
            $probeFailures.Add(('{0}:{1} missing cardMultiplayerConstraint' -f $probeFile, $lineNumber))
        }
        if ((Get-MapValue $record 'readOnly') -ne $true) {
            $probeFailures.Add(('{0}:{1} readOnly contract is not true' -f $probeFile, $lineNumber))
        }
        foreach ($field in @('searchStarted', 'actionsEnqueued', 'customNetworkPacketSent')) {
            if ((Get-MapValue $record $field) -ne $false) {
                $probeFailures.Add(('{0}:{1} forbidden {2} flag' -f $probeFile, $lineNumber, $field))
            }
        }
        try {
            $worldVersion = [long](Get-MapValue $record 'worldVersion')
            if ($worldVersion -lt 0) {
                $probeFailures.Add(('{0}:{1} negative worldVersion' -f $probeFile, $lineNumber))
            }
        } catch {
            $probeFailures.Add(('{0}:{1} invalid worldVersion' -f $probeFile, $lineNumber))
        }
    }
}

$probeStatus = if ($probeFiles.Count -eq 0 -and -not $probeRequired) {
    'NOT_REQUIRED'
} elseif ($probeRecordCount -eq 0) {
    'UNVERIFIED'
} elseif ($probeFailures.Count -gt 0) {
    'FAIL'
} else {
    'PASS'
}

$allResults = @($matrixResults) + @([pscustomobject]@{
        Name = 'probeJsonlContract'
        Status = $probeStatus
        Evidence = ($probeFiles -join ';')
        Detail = if ($probeFiles.Count -eq 0 -and $probeRequired) {
            'MP-0B requires at least one real Client Probe JSONL file'
        } elseif ($probeFailures.Count -eq 0) {
            $null
        } else {
            $probeFailures -join ' | '
        }
    })
$status = if (@($allResults | Where-Object Status -eq 'FAIL').Count -gt 0) {
    'FAIL'
} elseif (@($allResults | Where-Object Status -eq 'UNVERIFIED').Count -gt 0) {
    'UNVERIFIED'
} else {
    'PASS'
}

$result = [ordered]@{
    schemaVersion = 1
    status = $status
    phase = $Phase
    matrixPath = $matrixFile
    probeFiles = $probeFiles
    probeFileCount = $probeFileCount
    probeRecordCount = $probeRecordCount
    checks = @($allResults | ForEach-Object {
            [ordered]@{
                name = $_.Name
                status = $_.Status
                evidence = $_.Evidence
                detail = $_.Detail
            }
        })
}

if ($Json) {
    $result | ConvertTo-Json -Depth 8
} else {
    Write-Output "MULTIPLAYER_$Phase`_$status matrix=$matrixFile probe_records=$probeRecordCount"
    foreach ($item in $allResults) {
        $suffix = if ([string]::IsNullOrWhiteSpace([string]$item.Detail)) { '' } else { " detail=$($item.Detail)" }
        Write-Output ("{0} {1}{2}" -f $item.Status, $item.Name, $suffix)
    }
}

if ($status -eq 'FAIL') {
    exit 1
}
if ($status -eq 'UNVERIFIED') {
    exit 2
}
