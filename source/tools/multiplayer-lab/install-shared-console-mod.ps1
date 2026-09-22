#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ConsoleModPath,

    [Parameter(Mandatory = $true)]
    [string[]]$InstanceRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '..\headless-runtime.ps1')
. (Join-Path $PSScriptRoot 'multiplayer-common.ps1')

$source = Get-MultiplayerFullPath $ConsoleModPath
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "Console mod file was not found: $source"
}
if (-not [string]::Equals([IO.Path]::GetExtension($source), '.json', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Shared console installer expects a JSON-only console enabler mod: $source"
}
Assert-HeadlessNoReparsePoint $source

$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
$sourceName = [IO.Path]::GetFileName($source)
$results = [Collections.Generic.List[object]]::new()

foreach ($rootInput in $InstanceRoot) {
    $instance = Read-MultiplayerInstance $rootInput
    $processState = Get-MultiplayerOwnedProcessState $instance
    if ($processState.state -eq 'Owned') {
        throw "Refusing to modify a running multiplayer instance: $($instance.Root)"
    }

    $modsRoot = Join-Path $instance.GameRoot 'mods'
    Assert-MultiplayerPathWithin -Child $modsRoot -Parent $instance.GameRoot
    New-Item -ItemType Directory -Path $modsRoot -Force | Out-Null

    $target = Join-Path $modsRoot $sourceName
    Assert-MultiplayerPathWithin -Child $target -Parent $instance.GameRoot
    Copy-Item -LiteralPath $source -Destination $target -Force

    $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not [string]::Equals($targetHash, $sourceHash, [StringComparison]::Ordinal)) {
        throw "Console mod hash mismatch after copy: $target"
    }

    $results.Add([ordered]@{
        instance = [string]$instance.Profile.instance
        profile = [string]$instance.Profile.profile
        runtimeRoot = [string]$instance.Root
        target = $target
        sha256 = $targetHash
    })
}

[ordered]@{
    status = 'SHARED_CONSOLE_MOD_INSTALLED'
    source = $source
    fileName = $sourceName
    sha256 = $sourceHash
    instanceCount = $results.Count
    instances = @($results)
    note = 'Install the same file on every participating Host/Client after prepare-instances. Re-run after snapshot rebuild.'
} | ConvertTo-Json -Depth 8
