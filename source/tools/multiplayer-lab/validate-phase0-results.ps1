#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$MatrixPath,

    [Parameter(Mandatory)]
    [string[]]$ProbePath,

    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$requiredChecks = @(
    'vanillaHostAcceptedClient',
    'hostUnaware',
    'noCustomNetworkPackets',
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
    'singleplayerRegression',
    'localEndTurnSync',
    'localPlayCardSync',
    'fastActionStress',
    'wireModelCompatibility'
)

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
$probeFiles = @($ProbePath | ForEach-Object { Resolve-InputFile $_ })
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

$probeStatus = if ($probeRecordCount -eq 0) {
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
        Detail = if ($probeFailures.Count -eq 0) { $null } else { $probeFailures -join ' | ' }
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
    Write-Output "MULTIPLAYER_PHASE0_$status matrix=$matrixFile probe_records=$probeRecordCount"
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
