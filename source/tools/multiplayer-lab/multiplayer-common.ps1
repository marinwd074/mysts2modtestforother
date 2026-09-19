function Get-MultiplayerFullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
}

function Assert-MultiplayerPathWithin {
    param(
        [Parameter(Mandatory = $true)][string]$Child,
        [Parameter(Mandatory = $true)][string]$Parent
    )

    $childFull = (Get-MultiplayerFullPath $Child) + [IO.Path]::DirectorySeparatorChar
    $parentFull = (Get-MultiplayerFullPath $Parent) + [IO.Path]::DirectorySeparatorChar
    if (-not $childFull.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the expected multiplayer instance: $Child"
    }
}

function Read-MultiplayerInstance {
    param([Parameter(Mandatory = $true)][string]$InstanceRoot)

    $root = Get-MultiplayerFullPath $InstanceRoot
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "Multiplayer instance root was not found: $root"
    }
    Assert-HeadlessNoReparsePoint $root

    $ownerPath = Join-Path $root 'instance.json'
    $profilePath = Join-Path $root 'multiplayer-profile.json'
    if (-not (Test-Path -LiteralPath $ownerPath -PathType Leaf)) {
        throw "Multiplayer instance ownership marker is missing: $ownerPath"
    }
    if (-not (Test-Path -LiteralPath $profilePath -PathType Leaf)) {
        throw "Multiplayer profile marker is missing: $profilePath"
    }

    $owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json -AsHashtable
    if ($owner.schemaVersion -ne 1 -or
        -not [String]::Equals([string]$owner.runtimeRoot, $root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Multiplayer instance ownership marker does not match: $ownerPath"
    }
    $profile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json -AsHashtable
    if ($profile.schemaVersion -ne 1 -or
        -not [String]::Equals([string]$profile.runtimeRoot, $root, [StringComparison]::OrdinalIgnoreCase) -or
        [string]::IsNullOrWhiteSpace([string]$profile.profile)) {
        throw "Multiplayer profile marker is invalid: $profilePath"
    }

    $gameRoot = Join-Path $root 'game'
    $gameExe = Join-Path $gameRoot 'SlayTheSpire2.exe'
    $frozenMarker = Join-Path $gameRoot '.combatsolver-frozen-game.json'
    if (-not (Test-Path -LiteralPath $gameExe -PathType Leaf) -or
        -not (Test-Path -LiteralPath $frozenMarker -PathType Leaf)) {
        throw "Multiplayer game snapshot is missing or unowned: $gameRoot"
    }
    if (-not [String]::Equals([string]$profile.gameRoot, $gameRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Multiplayer profile game root does not match its instance: $profilePath"
    }
    Assert-HeadlessNoReparsePoint $gameRoot

    return [ordered]@{
        Root = $root
        OwnerPath = $ownerPath
        Owner = $owner
        ProfilePath = $profilePath
        Profile = $profile
        GameRoot = $gameRoot
        GameExe = $gameExe
        ProcessMarkerPath = Join-Path $root 'process.json'
        LogsRoot = Join-Path $root 'logs'
        RoamingRoot = Join-Path $root 'Roaming'
        LocalRoot = Join-Path $root 'Local'
    }
}

function Get-MultiplayerProcessIdentity {
    param(
        [Parameter(Mandatory = $true)][Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][string]$ExpectedExecutable
    )

    $safeHandle = $Process.SafeHandle
    $Process.Refresh()
    if ($Process.HasExited) {
        throw "The multiplayer process exited before ownership could be recorded."
    }
    $actualExecutable = Get-MultiplayerFullPath $Process.MainModule.FileName
    $expectedFull = Get-MultiplayerFullPath $ExpectedExecutable
    if (-not [String]::Equals($actualExecutable, $expectedFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Started process executable did not match the private snapshot: $actualExecutable"
    }
    return [ordered]@{
        pid = $Process.Id
        processStartTimeUtc = $Process.StartTime.ToUniversalTime().ToString('O')
        executable = $actualExecutable
    }
}

function Get-MultiplayerOwnedProcessState {
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary]$Instance)

    $markerPath = [string]$Instance.ProcessMarkerPath
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        return [ordered]@{ state = 'Absent'; marker = $null; process = $null }
    }

    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json -AsHashtable
    [int]$pidValue = 0
    if ($marker.schemaVersion -ne 1 -or
        -not [int]::TryParse([string]$marker.pid, [ref]$pidValue) -or $pidValue -le 0 -or
        -not [String]::Equals([string]$marker.runtimeRoot, [string]$Instance.Root, [StringComparison]::OrdinalIgnoreCase) -or
        -not [String]::Equals([string]$marker.executable, [string]$Instance.GameExe, [StringComparison]::OrdinalIgnoreCase) -or
        [string]::IsNullOrWhiteSpace([string]$marker.processStartTimeUtc)) {
        throw "Invalid multiplayer process marker preserved: $markerPath"
    }

    $candidate = Get-Process -Id $pidValue -ErrorAction SilentlyContinue
    if ($null -eq $candidate) {
        return [ordered]@{ state = 'Stale'; marker = $marker; process = $null }
    }

    $safeHandle = $candidate.SafeHandle
    $candidate.Refresh()
    if ($candidate.HasExited) {
        return [ordered]@{ state = 'Stale'; marker = $marker; process = $null }
    }

    $actualExecutable = Get-MultiplayerFullPath $candidate.MainModule.FileName
    if (-not [String]::Equals($actualExecutable, [string]$Instance.GameExe, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Process marker PID $pidValue is live but has a different executable; preserving it."
    }
    $expectedBirth = [DateTimeOffset]::Parse([string]$marker.processStartTimeUtc).UtcDateTime
    $actualBirth = $candidate.StartTime.ToUniversalTime()
    if ([Math]::Abs(($actualBirth - $expectedBirth).TotalSeconds) -gt 1) {
        throw "Process marker PID $pidValue was reused; preserving the marker and process."
    }

    return [ordered]@{ state = 'Owned'; marker = $marker; process = $candidate }
}

function Remove-MultiplayerProcessMarker {
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary]$Instance)

    $markerPath = [string]$Instance.ProcessMarkerPath
    Assert-MultiplayerPathWithin -Child $markerPath -Parent ([string]$Instance.Root)
    if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
        Remove-Item -LiteralPath $markerPath -Force -ErrorAction Stop
    }
}

function Stop-MultiplayerOwnedProcess {
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary]$Instance)

    $state = Get-MultiplayerOwnedProcessState $Instance
    if ($state.state -eq 'Absent') {
        return [ordered]@{ state = 'Absent'; pid = $null }
    }
    if ($state.state -eq 'Stale') {
        Remove-MultiplayerProcessMarker $Instance
        return [ordered]@{ state = 'StaleRemoved'; pid = $state.marker.pid }
    }

    $process = $state.process
    Stop-Process -Id $process.Id -Force -ErrorAction Stop
    [void]$process.WaitForExit(5000)
    if (-not $process.HasExited) {
        throw "Owned multiplayer process did not exit: PID $($process.Id)"
    }
    $pidValue = $process.Id
    Remove-MultiplayerProcessMarker $Instance
    return [ordered]@{ state = 'Stopped'; pid = $pidValue }
}
