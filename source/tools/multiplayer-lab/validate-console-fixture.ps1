#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$FixturePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'console-fixture-common.ps1')

try {
    $fixture = Read-MultiplayerConsoleFixture -FixturePath $FixturePath
    [ordered]@{
        status = 'PASS'
        schemaVersion = $fixture.SchemaVersion
        name = $fixture.Name
        waitFor = $fixture.WaitFor
        commands = @($fixture.Commands)
    } | ConvertTo-Json -Depth 6
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
