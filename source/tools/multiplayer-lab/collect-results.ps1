#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$InstanceRoot,

    [string]$OutputDirectory = '',

    [string[]]$ProbePath = @(),

    [string]$MatrixPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '..\headless-runtime.ps1')
. (Join-Path $PSScriptRoot 'multiplayer-common.ps1')

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot '..\..\..\.local\multiplayer-lab\results'
}
$outputRoot = Get-MultiplayerFullPath $OutputDirectory
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$runId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([guid]::NewGuid().ToString('N').Substring(0, 8))
$runRoot = Join-Path $outputRoot $runId
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

$instanceRecords = [Collections.Generic.List[object]]::new()
$collectedProbePaths = [Collections.Generic.List[string]]::new()
foreach ($root in $InstanceRoot) {
    $instance = Read-MultiplayerInstance $root
    $label = ([string]$instance.Profile.profile).ToLowerInvariant()
    $destination = Join-Path $runRoot $label
    Assert-MultiplayerPathWithin -Child $destination -Parent $runRoot
    New-Item -ItemType Directory -Path $destination -Force | Out-Null

    $copiedFiles = [Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath $instance.LogsRoot -PathType Container) {
        foreach ($log in Get-ChildItem -LiteralPath $instance.LogsRoot -File -Force) {
            $target = Join-Path $destination $log.Name
            Copy-Item -LiteralPath $log.FullName -Destination $target -Force
            $copiedFiles.Add($target)
        }
    }

    # Multiplayer lab processes redirect CombatBugReportPaths into an owned
    # instance directory via COMBATSOLVER_MULTIPLAYER_INSTANCE. Do not inspect
    # the normal desktop report root here: it may contain evidence from another run.
    $probeRoot = Join-Path $instance.Root 'diagnostics\CombatSolver-BugReports\logs\CombatSolver'
    Assert-MultiplayerPathWithin -Child $probeRoot -Parent $instance.Root
    if (Test-Path -LiteralPath $probeRoot -PathType Container) {
        $probeDestination = Join-Path $destination 'probe'
        New-Item -ItemType Directory -Path $probeDestination -Force | Out-Null
        foreach ($probe in Get-ChildItem -LiteralPath $probeRoot -File -Filter '*.jsonl' -Force -Recurse) {
            $relativeProbePath = [IO.Path]::GetRelativePath($probeRoot, $probe.FullName)
            $target = Join-Path $probeDestination $relativeProbePath
            Assert-MultiplayerPathWithin -Child $target -Parent $probeDestination
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $probe.FullName -Destination $target -Force
            $copiedFiles.Add($target)
            if ($probe.Name -like 'multiplayer-probe-*.jsonl') {
                $collectedProbePaths.Add($target)
            }
        }
    }

    $instanceRecords.Add([ordered]@{
            profile = [string]$instance.Profile.profile
            runtimeRoot = $instance.Root
            sourceLogsRoot = $instance.LogsRoot
            destination = $destination
            files = @($copiedFiles)
        })
}

foreach ($probePathValue in $ProbePath) {
    $sourceProbe = (Resolve-Path -LiteralPath $probePathValue -ErrorAction Stop).Path
    if (-not (Test-Path -LiteralPath $sourceProbe -PathType Leaf)) {
        throw "Probe path is not a file: $sourceProbe"
    }
    $probeInputRoot = Join-Path $runRoot 'probe-inputs'
    New-Item -ItemType Directory -Path $probeInputRoot -Force | Out-Null
    $target = Join-Path $probeInputRoot ("input-{0}-{1}" -f $collectedProbePaths.Count, (Split-Path -Leaf $sourceProbe))
    Copy-Item -LiteralPath $sourceProbe -Destination $target -Force
    $collectedProbePaths.Add($target)
}

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
$allChecks = $connectionChecks + $readOnlyChecks
$templateChecks = [ordered]@{}
foreach ($check in $allChecks) {
    $templateChecks[$check] = [ordered]@{
        status = 'UNVERIFIED'
        evidence = $null
    }
}
$matrixTemplate = [ordered]@{
    schemaVersion = 1
    phase = 'MP-0'
    status = 'UNVERIFIED'
    checks = $templateChecks
    profiles = @('HostVanilla + ClientVanilla', 'HostVanilla + ClientRitsuOnly', 'HostVanilla + ClientCombatSolver')
    notes = @(
        'This is a template only; collection never promotes UNVERIFIED to PASS.',
        'localPlayCardSync, localEndTurnSync, and fastActionStress belong to MP-2 Safe Execute.',
        'FastMP command-line startup is not lobby or wire evidence.'
    )
}
$matrixOutputPath = Join-Path $runRoot 'phase0-matrix.template.json'
if (-not [string]::IsNullOrWhiteSpace($MatrixPath)) {
    $sourceMatrix = (Resolve-Path -LiteralPath $MatrixPath -ErrorAction Stop).Path
    Copy-Item -LiteralPath $sourceMatrix -Destination $matrixOutputPath -Force
    $matrixOutputPath = (Get-MultiplayerFullPath $matrixOutputPath)
}
else {
    Write-HeadlessJson $matrixOutputPath $matrixTemplate
}

$manifest = [ordered]@{
    schemaVersion = 1
    collectionStatus = 'UNVERIFIED'
    runtimeEvidenceEligible = $false
    collectedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    runRoot = $runRoot
    instances = @($instanceRecords)
    probePaths = @($collectedProbePaths)
    matrixPath = $matrixOutputPath
    safety = [ordered]@{
        sourceInstancesModified = $false
        formalGameInstallModified = $false
        customNetworkProtocolUsed = $false
        passStatusInferred = $false
    }
}
$manifestPath = Join-Path $runRoot 'collection.json'
Write-HeadlessJson $manifestPath $manifest
$manifest | ConvertTo-Json -Depth 10
