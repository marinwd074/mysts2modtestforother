#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string[]]$LogPath,
    [string]$OutputPath = '',
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$records = [Collections.Generic.List[object]]::new()
$paths = [Collections.Generic.List[string]]::new()
foreach ($value in $LogPath) {
    $path = (Resolve-Path -LiteralPath $value -ErrorAction Stop).Path
    $paths.Add($path)
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $path) {
        $lineNumber++
        $message = [string]$line
        if ($message.TrimStart().StartsWith('{')) {
            try { $message = [string]($message | ConvertFrom-Json -ErrorAction Stop).Message }
            catch { throw "Invalid journal JSON at ${path}:${lineNumber}: $($_.Exception.Message)" }
        }
        $records.Add([pscustomobject]@{
            Index = $records.Count
            Path = $path
            Line = $lineNumber
            Message = $message
        })
    }
}

function Evidence($record) {
    if ($null -eq $record) { return $null }
    return '{0}:{1}: {2}' -f $record.Path, $record.Line, $record.Message
}

$checks = [Collections.Generic.List[object]]::new()
function Add-Check([string]$name, [string]$status, $record, [string]$detail = '') {
    $checks.Add([ordered]@{
        name = $name
        status = $status
        evidence = Evidence $record
        detail = if ($detail) { $detail } else { $null }
    })
}

$fixture = @($records | Where-Object Message -match '\[CombatSolver/MultiplayerFixture\] FIXTURE_COMPLETE\b.*\bname=tag-team-source\b')
$enable = @($records | Where-Object Message -match '\[CombatSolver/MultiplayerSafeExecute\] MP_SAFE_AUTO enabled=true\b')
$prefix = @($records | Where-Object Message -match '\[CombatSolver/MultiplayerSafeExecute\] DEPLOY_PREFIX_STOP\b.*\baction=multiplayer_only_card\b')
$stop = @($records | Where-Object Message -match '\[CombatSolver/MultiplayerSafeExecute\] MP_SAFE_AUTO_STOP\b.*\breason=multiplayer_only_card\b')

if ($fixture.Count -eq 1) { Add-Check 'sourceFixtureCompleted' PASS $fixture[0] }
elseif ($fixture.Count -gt 1) { Add-Check 'sourceFixtureCompleted' FAIL $fixture[1] 'Source fixture completed more than once.' }
else { Add-Check 'sourceFixtureCompleted' UNVERIFIED $null 'No source fixture completion was observed.' }

if ($enable.Count -eq 1) { Add-Check 'safeAutoEnabledOnce' PASS $enable[0] }
elseif ($enable.Count -gt 1) { Add-Check 'safeAutoEnabledOnce' FAIL $enable[1] 'Safe Auto was enabled more than once.' }
else { Add-Check 'safeAutoEnabledOnce' UNVERIFIED $null 'No Safe Auto enable marker was observed.' }

$start = if ($enable.Count -gt 0) { $enable[0].Index } else { -1 }
$boundary = @($prefix | Where-Object Index -gt $start)
$boundaryRecord = if ($boundary.Count -gt 0) { $boundary[0] } else { $null }
if ($null -ne $boundaryRecord -and $fixture.Count -eq 1 -and $fixture[0].Index -lt $boundaryRecord.Index) {
    Add-Check 'multiplayerOnlyClassified' PASS $boundaryRecord
} else {
    Add-Check 'multiplayerOnlyClassified' UNVERIFIED $boundaryRecord 'No controlled multiplayer_only_card deployment boundary was observed.'
}

$stopAfter = @($stop | Where-Object Index -gt $(if ($null -ne $boundaryRecord) { $boundaryRecord.Index } else { $start }))
$stopRecord = if ($stopAfter.Count -gt 0) { $stopAfter[0] } else { $null }
if ($null -ne $stopRecord) { Add-Check 'safeAutoStopped' PASS $stopRecord }
elseif ($null -ne $boundaryRecord) { Add-Check 'safeAutoStopped' FAIL $boundaryRecord 'Safe Auto did not stop after the classified boundary.' }
else { Add-Check 'safeAutoStopped' UNVERIFIED $null 'No classified boundary was observed.' }

if ($null -ne $boundaryRecord -and $null -ne $stopRecord) {
    $window = @($records | Where-Object { $_.Index -ge $boundaryRecord.Index -and $_.Index -le $stopRecord.Index })
    $fixtureStart = if ($fixture.Count -gt 0) { $fixture[0].Index } else { $boundaryRecord.Index }
    $tagTeam = @($records | Where-Object {
        $_.Index -ge $fixtureStart -and $_.Index -le $stopRecord.Index -and
        $_.Message -match 'NATIVE_ACTION_CAPTURED\b.*\btype=PlayCardAction\b.*\bcard=TAG_TEAM\b'
    })
    $laterAction = @($window | Where-Object Message -match 'NATIVE_ACTION_CAPTURED\b|MP2B_SAFE_END_TURN_ACCEPTED\b')
    $custom = @($window | Where-Object Message -match 'custom_network_api_used=true\b')
    if ($tagTeam.Count -eq 0 -and $laterAction.Count -eq 0 -and $custom.Count -eq 0) {
        Add-Check 'noDeploymentAcrossBoundary' PASS $stopRecord
    } else {
        $bad = @($tagTeam + $laterAction + $custom | Sort-Object Index | Select-Object -First 1)[0]
        Add-Check 'noDeploymentAcrossBoundary' FAIL $bad 'Tag Team, a later native action, Safe EndTurn, or a custom network API crossed the boundary.'
    }
} else {
    Add-Check 'noDeploymentAcrossBoundary' UNVERIFIED $null 'The boundary and stop window is incomplete.'
}

$status = if (@($checks | Where-Object status -eq FAIL).Count) { 'FAIL' }
elseif (@($checks | Where-Object status -eq UNVERIFIED).Count) { 'UNVERIFIED' }
else { 'PASS' }
$result = [ordered]@{
    schemaVersion = 1
    status = $status
    logFiles = @($paths)
    checks = @($checks)
    limitation = 'The source fixture and journal ordering prove the observed boundary; Host/Client identity and visible state still require human confirmation.'
}
$output = $result | ConvertTo-Json -Depth 6
if ($OutputPath) {
    $full = [IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Path (Split-Path -Parent $full) -Force | Out-Null
    [IO.File]::WriteAllText($full, $output, [Text.UTF8Encoding]::new($false))
}
if ($Json) { Write-Output $output }
else {
    Write-Output "MULTIPLAYER_ONLY_BOUNDARY_$status"
    foreach ($check in $checks) { Write-Output "$($check.status) $($check.name)" }
}
if ($status -eq 'FAIL') { exit 1 }
if ($status -eq 'UNVERIFIED') { exit 2 }
