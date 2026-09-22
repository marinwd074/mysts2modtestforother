#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$InstanceRoot,

    [string]$WorkshopContentRoot = 'D:\SteamLibrary\steamapps\workshop\content\2868840',

    [string]$TheBookOfAgesItemId = '3747634356',

    [string]$BaseLibItemId = '3737335127'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '..\headless-runtime.ps1')
. (Join-Path $PSScriptRoot 'multiplayer-common.ps1')

function Get-UniqueModFile {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $matches = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force |
        Where-Object { [string]::Equals($_.Name, $Name, [StringComparison]::OrdinalIgnoreCase) })
    if ($matches.Count -ne 1) {
        throw "Expected exactly one $Name under $Root; found $($matches.Count)."
    }
    if (($matches[0].Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing workshop input through a reparse point: $($matches[0].FullName)"
    }
    return $matches[0].FullName
}

$workshopRoot = Get-MultiplayerFullPath $WorkshopContentRoot
$bookRoot = Join-Path $workshopRoot $TheBookOfAgesItemId
$baseLibRoot = Join-Path $workshopRoot $BaseLibItemId

foreach ($requiredRoot in @($bookRoot, $baseLibRoot)) {
    if (-not (Test-Path -LiteralPath $requiredRoot -PathType Container)) {
        throw "Required Steam Workshop item is not downloaded: $requiredRoot"
    }
    Assert-HeadlessNoReparsePoint $requiredRoot
}

$bookFiles = [ordered]@{
    'TheBookOfAges.json' = Get-UniqueModFile $bookRoot 'TheBookOfAges.json'
    'TheBookOfAges.dll'  = Get-UniqueModFile $bookRoot 'TheBookOfAges.dll'
    'TheBookOfAges.pck'  = Get-UniqueModFile $bookRoot 'TheBookOfAges.pck'
}
$baseLibFiles = [ordered]@{
    'BaseLib.json' = Get-UniqueModFile $baseLibRoot 'BaseLib.json'
    'BaseLib.dll'  = Get-UniqueModFile $baseLibRoot 'BaseLib.dll'
    'BaseLib.pck'  = Get-UniqueModFile $baseLibRoot 'BaseLib.pck'
}

$bookManifest = Get-Content -LiteralPath $bookFiles['TheBookOfAges.json'] -Raw | ConvertFrom-Json
if ([string]$bookManifest.id -ne 'TheBookOfAges') {
    throw "Unexpected TheBookOfAges manifest id: $($bookManifest.id)"
}
if ([version]([string]$bookManifest.min_game_version) -gt [version]'0.107.1') {
    throw "TheBookOfAges requires game $($bookManifest.min_game_version), newer than pinned 0.107.1."
}
$baseManifest = Get-Content -LiteralPath $baseLibFiles['BaseLib.json'] -Raw | ConvertFrom-Json
if ([string]$baseManifest.id -ne 'BaseLib') {
    throw "Unexpected BaseLib manifest id: $($baseManifest.id)"
}

$sourceHashes = [ordered]@{}
foreach ($entry in @($bookFiles.GetEnumerator()) + @($baseLibFiles.GetEnumerator())) {
    $sourceHashes[$entry.Key] = (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash.ToLowerInvariant()
}

$results = [Collections.Generic.List[object]]::new()
foreach ($rootInput in $InstanceRoot) {
    $instance = Read-MultiplayerInstance $rootInput
    $state = Get-MultiplayerOwnedProcessState $instance
    if ($state.state -eq 'Owned') {
        throw "Refusing to modify a running multiplayer instance: $($instance.Root)"
    }

    $modsRoot = Join-Path $instance.GameRoot 'mods'
    Assert-MultiplayerPathWithin -Child $modsRoot -Parent $instance.GameRoot
    New-Item -ItemType Directory -Path $modsRoot -Force | Out-Null

    # BaseLib's documented manual layout is three files directly under mods.
    foreach ($name in $baseLibFiles.Keys) {
        $target = Join-Path $modsRoot $name
        Assert-MultiplayerPathWithin -Child $target -Parent $instance.GameRoot
        Copy-Item -LiteralPath $baseLibFiles[$name] -Destination $target -Force
        $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $sourceHashes[$name]) {
            throw "BaseLib copy hash mismatch: $target"
        }
    }

    $bookTargetRoot = Join-Path $modsRoot 'TheBookOfAges'
    Assert-MultiplayerPathWithin -Child $bookTargetRoot -Parent $instance.GameRoot
    New-Item -ItemType Directory -Path $bookTargetRoot -Force | Out-Null
    foreach ($name in $bookFiles.Keys) {
        $target = Join-Path $bookTargetRoot $name
        Copy-Item -LiteralPath $bookFiles[$name] -Destination $target -Force
        $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $sourceHashes[$name]) {
            throw "TheBookOfAges copy hash mismatch: $target"
        }
    }

    $results.Add([ordered]@{
        instance = [string]$instance.Profile.instance
        profile = [string]$instance.Profile.profile
        runtimeRoot = [string]$instance.Root
        bookVersion = [string]$bookManifest.version
        baseLibVersion = [string]$baseManifest.version
    })
}

[ordered]@{
    status = 'THE_BOOK_OF_AGES_INSTALLED'
    workshopItem = $TheBookOfAgesItemId
    baseLibWorkshopItem = $BaseLibItemId
    pinnedGameVersion = '0.107.1'
    bookVersion = [string]$bookManifest.version
    baseLibVersion = [string]$baseManifest.version
    hashes = $sourceHashes
    instances = @($results)
    note = 'All participating instances receive byte-identical BaseLib and TheBookOfAges files. Re-run after snapshot rebuild.'
} | ConvertTo-Json -Depth 8
