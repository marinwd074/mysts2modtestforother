#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstanceRoot,

    [ValidateSet('', 'host', 'join')]
    [string]$FastMpMode = '',

    [switch]$ForceSteamOff
)

try {
    & (Join-Path $PSScriptRoot 'start-instance.ps1') `
        -Role Client `
        -InstanceRoot $InstanceRoot `
        -FastMpMode $FastMpMode `
        -ForceSteamOff:$ForceSteamOff
    if (-not $?) { exit 1 }
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
