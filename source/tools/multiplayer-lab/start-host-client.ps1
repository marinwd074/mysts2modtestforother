#requires -Version 7.4

[CmdletBinding()]
param(
    [string]$Sts2GameRoot = '',

    [string]$RitsuWorkshopRoot = '',

    [string]$LabRoot = '',

    [UInt64]$ClientId = 1000,

    [switch]$ForceWarmup,

    [switch]$AllowSteam
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($ClientId -eq 0) {
    throw 'ClientId must be non-zero when Host and Client run on the same computer.'
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if ([string]::IsNullOrWhiteSpace($LabRoot)) {
    $LabRoot = Join-Path $repositoryRoot '.local\multiplayer-lab'
}
$LabRoot = [IO.Path]::GetFullPath($LabRoot)
$hostRoot = Join-Path $LabRoot 'runtime-mp-host'
$clientRoot = Join-Path $LabRoot 'runtime-mp-client-solver'
$buildRoot = Join-Path $repositoryRoot 'artifacts\CombatSolver'
$solverDll = Join-Path $buildRoot 'CombatSolver.dll'
$prepareScript = Join-Path $PSScriptRoot 'prepare-instances.ps1'
$startHostScript = Join-Path $PSScriptRoot 'start-host.ps1'
$startClientScript = Join-Path $PSScriptRoot 'start-client.ps1'

if (-not (Test-Path -LiteralPath $solverDll -PathType Leaf)) {
    throw "Build the production mod first; CombatSolver.dll was not found at $solverDll."
}

$pwshCommand = Get-Command -Name pwsh -CommandType Application | Select-Object -First 1
if ($null -eq $pwshCommand) {
    throw 'PowerShell 7 (pwsh) is required.'
}
$script:pwshPath = $pwshCommand.Source

function Invoke-LabScriptJson {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ScriptPath,

        [string[]]$ScriptArguments = @()
    )

    $arguments = @('-NoLogo', '-NoProfile', '-File', $ScriptPath) + $ScriptArguments
    $output = @(& $script:pwshPath @arguments)
    $exitCode = $LASTEXITCODE
    $jsonText = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    if ($exitCode -ne 0) {
        throw "'$ScriptPath' failed with exit code $exitCode.`n$jsonText"
    }
    if ([string]::IsNullOrWhiteSpace($jsonText)) {
        throw "'$ScriptPath' completed without its expected JSON result."
    }

    return ConvertFrom-Json -InputObject $jsonText -AsHashtable
}

$commonPrepareArguments = @()
if (-not [string]::IsNullOrWhiteSpace($Sts2GameRoot)) {
    $commonPrepareArguments += @('-Sts2GameRoot', $Sts2GameRoot)
}
if (-not [string]::IsNullOrWhiteSpace($RitsuWorkshopRoot)) {
    $commonPrepareArguments += @('-RitsuWorkshopRoot', $RitsuWorkshopRoot)
}
$commonPrepareArguments += @('-CombatSolverBuildDir', $buildRoot)

$hostPrepareArguments = @(
    '-Profile', 'HostVanilla',
    '-Instance', 'mp-host',
    '-RuntimeRoot', $hostRoot
) + $commonPrepareArguments
$clientPrepareArguments = @(
    '-Profile', 'ClientCombatSolver',
    '-Instance', 'mp-client-solver',
    '-RuntimeRoot', $clientRoot
) + $commonPrepareArguments

$hostPreparation = Invoke-LabScriptJson -ScriptPath $prepareScript -ScriptArguments $hostPrepareArguments
$clientPreparation = Invoke-LabScriptJson -ScriptPath $prepareScript -ScriptArguments $clientPrepareArguments

$changedActions = @('INSTALLED', 'UPDATED')
$warmupRequired = $ForceWarmup.IsPresent -or
    ([string]$clientPreparation.ritsuAction -in $changedActions) -or
    ([string]$clientPreparation.combatSolverAction -in $changedActions)
$clientMode = if ($warmupRequired) { 'safe-execute' } else { 'safe-execute-lab' }

$hostArguments = @('-InstanceRoot', $hostRoot)
$clientArguments = @(
    '-InstanceRoot', $clientRoot,
    '-ClientId', [string]$ClientId,
    '-MultiplayerMode', $clientMode
)
if ($AllowSteam.IsPresent) {
    $hostArguments += '-AllowSteam'
    $clientArguments += '-AllowSteam'
} else {
    $hostArguments += '-ForceSteamOff'
    $clientArguments += '-ForceSteamOff'
}

$hostResult = Invoke-LabScriptJson -ScriptPath $startHostScript -ScriptArguments $hostArguments
$clientResult = Invoke-LabScriptJson -ScriptPath $startClientScript -ScriptArguments $clientArguments

if ($warmupRequired) {
    Write-Host 'Client started in warm-up mode. Let the mod load and complete the game restart; if the Client remains open, close it and rerun this script to start safe-execute-lab.'
} else {
    Write-Host 'Host and Client started. The Client is using safe-execute-lab.'
}

[ordered]@{
    status = if ($warmupRequired) { 'WARMUP_REQUIRED' } else { 'STARTED' }
    productionDll = $solverDll
    hostPreparation = $hostPreparation
    clientPreparation = $clientPreparation
    host = $hostResult
    client = $clientResult
    clientMultiplayerMode = $clientMode
    forceSteamOff = -not $AllowSteam.IsPresent
} | ConvertTo-Json -Depth 12
