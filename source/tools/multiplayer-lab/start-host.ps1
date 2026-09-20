#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstanceRoot,

    [ValidateSet('', 'host', 'join')]
    [string]$FastMpMode = '',

    [switch]$ForceSteamOff,

    [switch]$AllowSteam
)

try {
    & (Join-Path $PSScriptRoot 'start-instance.ps1') `
        -Role Host `
        -InstanceRoot $InstanceRoot `
        -FastMpMode $FastMpMode `
        -ForceSteamOff:$ForceSteamOff `
        -AllowSteam:$AllowSteam
    if (-not $?) { exit 1 }
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
