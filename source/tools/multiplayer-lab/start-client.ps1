#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstanceRoot,

    [ValidateSet('', 'host', 'join')]
    [string]$FastMpMode = '',

    [ValidateSet('', 'probe', 'advisor', 'safe-execute', 'safe-execute-lab')]
    [string]$MultiplayerMode = '',

    [UInt64]$ClientId = 0,

    [switch]$ForceSteamOff,

    [switch]$AllowSteam
)

try {
    & (Join-Path $PSScriptRoot 'start-instance.ps1') `
        -Role Client `
        -InstanceRoot $InstanceRoot `
        -FastMpMode $FastMpMode `
        -MultiplayerMode $MultiplayerMode `
        -ClientId $ClientId `
        -ForceSteamOff:$ForceSteamOff `
        -AllowSteam:$AllowSteam
    if (-not $?) { exit 1 }
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
