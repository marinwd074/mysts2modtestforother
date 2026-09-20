# Windows headless infrastructure only. Each launcher runs in a separate pwsh.
# The host lease is deliberately independent of the request/process protocol.

function Get-HeadlessCanonicalPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
}

function Assert-HeadlessNoReparsePoint([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Headless managed paths must not traverse a reparse point: $current"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Write-HeadlessJson([string]$Path, [object]$Value) {
    $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temporary -Encoding UTF8
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    } finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Copy-HeadlessProfileTree([string]$Source, [string]$Destination) {
    Assert-HeadlessNoReparsePoint $Source
    Assert-HeadlessNoReparsePoint $Destination
    foreach ($item in Get-ChildItem -LiteralPath $Source -Recurse -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Cannot isolate a profile containing a reparse point: $($item.FullName)"
        }
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force
}

function New-HeadlessRuntimeContext(
    [string]$RepositoryRoot,
    [string]$SourceGameRoot,
    [string]$Instance,
    [string]$ExecutionMode,
    [int]$MemoryReservationMiB,
    [int]$CpuReservation,
    [int]$QueueTimeoutSeconds
) {
    $repository = Get-HeadlessCanonicalPath $RepositoryRoot
    $source = Get-HeadlessCanonicalPath $SourceGameRoot
    if ([string]::IsNullOrWhiteSpace($Instance)) {
        $bytes = [Text.Encoding]::UTF8.GetBytes($repository.ToUpperInvariant())
        $Instance = "wt-" + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).Substring(0, 16).ToLowerInvariant()
    }
    if ($Instance -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
        throw "HeadlessInstance must be a 1-64 character identifier (letters, digits, dot, underscore, hyphen)."
    }
    $localData = [Environment]::GetFolderPath('LocalApplicationData')
    $root = if ([string]::IsNullOrWhiteSpace($env:COMBATSOLVER_HEADLESS_ROOT)) {
        Join-Path $localData "CombatSolver\headless-instances\$Instance"
    } else { $env:COMBATSOLVER_HEADLESS_ROOT }
    $root = Get-HeadlessCanonicalPath $root
    $hostRoot = if ([string]::IsNullOrWhiteSpace($env:COMBATSOLVER_HEADLESS_HOST_ROOT)) {
        Join-Path $localData 'CombatSolver\headless-host-v1'
    } else { $env:COMBATSOLVER_HEADLESS_HOST_ROOT }
    $hostRoot = Get-HeadlessCanonicalPath $hostRoot
    foreach ($protected in @($source, $repository, [Environment]::GetFolderPath('UserProfile'), $localData,
            [Environment]::GetFolderPath('ApplicationData'))) {
        $protected = Get-HeadlessCanonicalPath $protected
        if ($protected.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
            $protected.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Headless runtime cannot own a protected directory or its ancestor: $root"
        }
    }
    if ($root.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $root.Equals($hostRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $root.StartsWith($hostRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $hostRoot.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The instance runtime must be separate from the source game and host lease directory."
    }
    Assert-HeadlessNoReparsePoint $root
    Assert-HeadlessNoReparsePoint $hostRoot
    foreach ($child in @('game', 'Roaming', 'Local')) {
        Assert-HeadlessNoReparsePoint (Join-Path $root $child)
    }
    $rootBytes = [Text.Encoding]::UTF8.GetBytes($root.ToUpperInvariant())
    $leaseKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rootBytes)).ToLowerInvariant()
    return @{
        Instance = $Instance; Root = $root; SourceGameRoot = $source; RepositoryRoot = $repository
        GameRoot = Join-Path $root 'game'; HostRoot = $hostRoot
        LeasePath = Join-Path $hostRoot "$leaseKey.json"
        Mode = $ExecutionMode; MemoryMiB = $MemoryReservationMiB
        Cpu = $CpuReservation; QueueSeconds = $QueueTimeoutSeconds
        LeaseToken = ''; ArtifactId = ''
    }
}

function Initialize-HeadlessRuntimeOwner([hashtable]$Context) {
    $path = Join-Path $Context.Root 'instance.json'
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $owner = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
        if ($owner.schemaVersion -ne 1 -or $owner.instance -ne $Context.Instance -or
            -not [string]::Equals($owner.repositoryRoot, $Context.RepositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals($owner.runtimeRoot, $Context.Root, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Headless runtime ownership does not match this instance: $path"
        }
    } else {
        # A pre-existing game tree without this ownership marker is never adopted.
        if (Test-Path -LiteralPath $Context.GameRoot) {
            throw "An unowned game tree already exists in the requested runtime: $($Context.GameRoot)"
        }
        Write-HeadlessJson $path @{ schemaVersion = 1; instance = $Context.Instance; runtimeRoot = $Context.Root; repositoryRoot = $Context.RepositoryRoot }
    }
}

function Get-HeadlessSnapshotPlan(
    [hashtable]$Context,
    [string]$CombatSolverDll,
    [string]$CombatSolverManifest,
    [string]$MemoryCleaner,
    [string]$RitsuRoot,
    [string]$RitsuManifest,
    [string]$RitsuLibTargetVersion
) {
    # Every payload is bound, including other mods and non-DLL mod assets. No
    # hardlinks/junctions: a build in another worktree must not mutate this image.
    $sources = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in Get-ChildItem -LiteralPath $Context.SourceGameRoot -Recurse -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Cannot freeze a game tree containing a reparse point: $($item.FullName)"
        }
        if (-not $item.PSIsContainer) {
            $relative = [IO.Path]::GetRelativePath($Context.SourceGameRoot, $item.FullName).Replace('/', '\')
            # The installed game may retain the legacy top-level mod payload
            # beside the packaged mods/CombatSolver directory. The snapshot
            # below injects the packaged payload explicitly; carrying both
            # makes STS2 discover CombatSolver twice and abort startup.
            if ([string]::Equals($relative, 'mods\CombatSolver.dll', [StringComparison]::OrdinalIgnoreCase) -or
                [string]::Equals($relative, 'mods\CombatSolver.json', [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }
            $sources[$relative] = $item.FullName
        }
    }
    $sources['mods\CombatSolver\CombatSolver.dll'] = $CombatSolverDll
    $sources['mods\CombatSolver\CombatSolver.json'] = $CombatSolverManifest
    $sources['mods\CombatSolver\CombatSolver.MemoryCleaner.exe'] = $MemoryCleaner
    $sources['mods\.combatsolver-headless-ritsulib\STS2-RitsuLib.json'] = $RitsuManifest
    $variantManifest = Join-Path $RitsuRoot 'ritsulib-variants.manifest'
    if (Test-Path -LiteralPath $variantManifest -PathType Leaf) {
        foreach ($item in Get-ChildItem -LiteralPath $RitsuRoot -Recurse -Force) {
            if ($item.PSIsContainer -or $item.Name -in @('mod_manifest.json', 'RitsuLib.References.props')) {
                continue
            }
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Cannot freeze a RitsuLib bundle containing a reparse point: $($item.FullName)"
            }
            $relative = [IO.Path]::GetRelativePath($RitsuRoot, $item.FullName)
            $sources[(Join-Path 'mods\.combatsolver-headless-ritsulib' $relative)] = $item.FullName
        }
    } else {
        $legacyDll = Join-Path $RitsuRoot (Join-Path "lib" (Join-Path $RitsuLibTargetVersion 'STS2-RitsuLib.dll'))
        $sources['mods\.combatsolver-headless-ritsulib\STS2-RitsuLib.dll'] = $legacyDll
    }
    $files = [Collections.Generic.List[object]]::new()
    $identity = [Text.StringBuilder]::new()
    foreach ($relative in @($sources.Keys | Sort-Object -CaseSensitive)) {
        Assert-LauncherNotCancelled
        $source = $sources[$relative]
        Assert-HeadlessNoReparsePoint $source
        $fileHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        $files.Add(@{ relative = $relative; source = $source; sha256 = $fileHash })
        [void]$identity.Append($relative).Append([char]0).Append($fileHash).Append([char]0)
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes($identity.ToString())
    return @{ id = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)); files = $files }
}

function Invoke-HeadlessCancellationCheck {
    $cancellationCheck = Get-Command Assert-LauncherNotCancelled -ErrorAction SilentlyContinue
    if ($null -ne $cancellationCheck) {
        Assert-LauncherNotCancelled
    }
}

function New-HeadlessSnapshotFileSet(
    [System.Collections.IDictionary]$Sources,
    [string]$IdentityPrefix = ''
) {
    $files = [Collections.Generic.List[object]]::new()
    $identity = [Text.StringBuilder]::new()
    [void]$identity.Append($IdentityPrefix)
    $relativePaths = @()
    if ($Sources.Count -gt 0) {
        $relativePaths = @($Sources.Keys | Sort-Object -CaseSensitive)
    }
    foreach ($relative in $relativePaths) {
        Invoke-HeadlessCancellationCheck
        $source = [string]$Sources[$relative]
        Assert-HeadlessNoReparsePoint $source
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Snapshot input is not a file: $source"
        }
        $fileHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        [void]$files.Add([ordered]@{
                relative = [string]$relative
                source = $source
                sha256 = $fileHash
            })
        [void]$identity.Append([string]$relative).Append([char]0).Append($fileHash).Append([char]0)
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes($identity.ToString())
    return @{
        id = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
        files = [object[]]$files
    }
}

function Get-HeadlessSnapshotManifestValue([object]$Manifest, [string]$Name) {
    if ($null -eq $Manifest) { return }
    if ($Manifest -is [System.Collections.IDictionary] -and $Manifest.Contains($Name)) {
        $value = $Manifest[$Name]
        if ($null -eq $value) { return }
        return $value
    }
    $property = $Manifest.PSObject.Properties[$Name]
    if ($null -ne $property) {
        if ($null -eq $property.Value) { return }
        return $property.Value
    }
    return
}

function ConvertTo-HeadlessSnapshotManifestFiles([object[]]$Files) {
    $manifestFiles = [Collections.Generic.List[object]]::new()
    foreach ($file in @($Files)) {
        if ($null -eq $file) { continue }
        [void]$manifestFiles.Add([ordered]@{
                relative = [string]$file.relative
                sha256 = [string]$file.sha256
            })
    }
    return ,([object[]]$manifestFiles)
}

function Assert-HeadlessSnapshotRelativePath([string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath -match '(^|[\\/])\.\.([\\/]|$)' -or
        $RelativePath -match '^[A-Za-z]:' -or
        $RelativePath.Contains([char]0)) {
        throw "Snapshot relative path is unsafe: $RelativePath"
    }
}

function Get-HeadlessSnapshotTargetPath(
    [hashtable]$Context,
    [string]$RelativePath
) {
    Assert-HeadlessSnapshotRelativePath $RelativePath
    $root = Get-HeadlessCanonicalPath $Context.GameRoot
    $target = Get-HeadlessCanonicalPath (Join-Path $root ($RelativePath.Replace('/', '\')))
    $rootPrefix = $root + [IO.Path]::DirectorySeparatorChar
    if (-not $target.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Snapshot path escaped its owned game root: $RelativePath"
    }
    Assert-HeadlessNoReparsePoint $target
    return $target
}

function Assert-HeadlessGameSnapshotNotRunning([hashtable]$Context) {
    $expectedExecutable = Join-Path $Context.GameRoot 'SlayTheSpire2.exe'
    $candidates = @(Get-Process -Name 'SlayTheSpire2' -ErrorAction SilentlyContinue)
    if ($candidates.Count -eq 0) {
        # Some renamed Windows executables are not returned by the -Name
        # provider filter even though ProcessName is already normalized.
        $candidates = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
                $_.ProcessName -eq 'SlayTheSpire2'
            })
    }
    $cimCandidates = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'SlayTheSpire2.exe'" -ErrorAction Stop)
    foreach ($processInfo in $cimCandidates) {
        if ([string]::IsNullOrWhiteSpace([string]$processInfo.ExecutablePath)) {
            throw 'Cannot conclusively inspect a live SlayTheSpire2 process; refusing snapshot update.'
        }
        if ([string]::Equals([string]$processInfo.ExecutablePath, $expectedExecutable,
                [StringComparison]::OrdinalIgnoreCase)) {
            $candidate = Get-Process -Id ([int]$processInfo.ProcessId) -ErrorAction Stop
            if (@($candidates | Where-Object { $_.Id -eq $candidate.Id }).Count -eq 0) {
                $candidates += $candidate
            }
        }
    }
    foreach ($candidate in $candidates) {
        $candidateHandle = $candidate.SafeHandle
        if (-not $candidate.HasExited -and [string]::Equals($candidate.MainModule.FileName,
                $expectedExecutable, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Cannot update a private game snapshot while its process is alive."
        }
    }
}

function Copy-HeadlessSnapshotFiles(
    [hashtable]$Context,
    [string]$DestinationRoot,
    [object[]]$Files
) {
    foreach ($file in @($Files)) {
        if ($null -eq $file) { continue }
        Invoke-HeadlessCancellationCheck
        $source = [string]$file.source
        Assert-HeadlessNoReparsePoint $source
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Snapshot input is not a file: $source"
        }
        $relative = [string]$file.relative
        Assert-HeadlessSnapshotRelativePath $relative
        $destination = Get-HeadlessCanonicalPath (Join-Path $DestinationRoot ($relative.Replace('/', '\')))
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        Assert-HeadlessNoReparsePoint $destination
        [void](Copy-Item -LiteralPath $source -Destination $destination -Force -PassThru)
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne [string]$file.sha256) {
            throw "A source payload changed while the private game was being synchronized: $source"
        }
    }
}

function Remove-HeadlessSnapshotFiles(
    [hashtable]$Context,
    [object[]]$Files
) {
    foreach ($file in @($Files)) {
        if ($null -eq $file) { continue }
        $target = Get-HeadlessSnapshotTargetPath $Context ([string]$file.relative)
        $item = Get-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
        if ($null -eq $item) { continue }
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to remove a profile overlay through a reparse point: $target"
        }
        if ($item.PSIsContainer) {
            throw "Refusing to remove a profile overlay directory as a file: $target"
        }
        Remove-Item -LiteralPath $target -Force -ErrorAction Stop
    }
}

function Test-HeadlessSnapshotFilesMatch(
    [hashtable]$Context,
    [object[]]$Files
) {
    foreach ($file in @($Files)) {
        if ($null -eq $file) { continue }
        $target = Get-HeadlessSnapshotTargetPath $Context ([string]$file.relative)
        $item = Get-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
        if ($null -eq $item -or $item.PSIsContainer) { return $false }
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne [string]$file.sha256) {
            return $false
        }
    }
    return $true
}

function Get-HeadlessManagedOverlayTarget(
    [hashtable]$Context,
    [string]$RootRelative
) {
    return Get-HeadlessSnapshotTargetPath $Context $RootRelative
}

function Assert-HeadlessManagedOverlayTree(
    [hashtable]$Context,
    [string]$RootRelative,
    [object[]]$AllowedFiles,
    [switch]$AllowRitsuMarker
) {
    $root = Get-HeadlessManagedOverlayTarget $Context $RootRelative
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { return }
    Assert-HeadlessNoReparsePoint $root
    $allowed = @{}
    foreach ($file in @($AllowedFiles)) {
        if ($null -eq $file) { continue }
        $allowed[[string]$file.relative.Replace('/', '\')] = $true
    }
    if ($AllowRitsuMarker) {
        $allowed[(Join-Path $RootRelative '.combatsolver-headless-only').Replace('/', '\')] = $true
    }
    foreach ($entry in Get-ChildItem -LiteralPath $root -Recurse -Force) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to replace a managed overlay containing a reparse point: $($entry.FullName)"
        }
        if (-not $entry.PSIsContainer) {
            $relative = [IO.Path]::GetRelativePath($Context.GameRoot, $entry.FullName).Replace('/', '\')
            if (-not $allowed.ContainsKey($relative)) {
                throw "Refusing to replace an unowned managed overlay file: $($entry.FullName)"
            }
        }
    }
}

function Remove-HeadlessPrivateTemporaryTree(
    [hashtable]$Context,
    [string]$Path,
    [string]$Prefix
) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $pathFull = Get-HeadlessCanonicalPath $Path
    $parent = [IO.Path]::GetDirectoryName($pathFull)
    $leaf = [IO.Path]::GetFileName($pathFull)
    if (-not $parent.Equals($Context.Root, [StringComparison]::OrdinalIgnoreCase) -or
        -not $leaf.StartsWith($Prefix, [StringComparison]::Ordinal)) {
        throw "Refusing to remove an unexpected private temporary tree: $pathFull"
    }
    Assert-HeadlessNoReparsePoint $pathFull
    foreach ($entry in Get-ChildItem -LiteralPath $pathFull -Recurse -Force) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing temporary cleanup through a reparse point: $($entry.FullName)"
        }
    }
    Remove-Item -LiteralPath $pathFull -Recurse -Force -ErrorAction Stop
}

function Remove-HeadlessManagedOverlayTree(
    [hashtable]$Context,
    [string]$RootRelative
) {
    $root = Get-HeadlessManagedOverlayTarget $Context $RootRelative
    if (-not (Test-Path -LiteralPath $root)) { return }
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "Refusing to remove a managed overlay that is not a directory: $root"
    }
    Assert-HeadlessNoReparsePoint $root
    foreach ($entry in Get-ChildItem -LiteralPath $root -Recurse -Force) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing managed overlay cleanup through a reparse point: $($entry.FullName)"
        }
    }
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction Stop
}

function Sync-HeadlessManagedOverlay(
    [hashtable]$Context,
    [string]$RootRelative,
    [object[]]$OldFiles,
    [object[]]$NewFiles,
    [switch]$IncludeRitsuMarker
) {
    $oldFiles = @($OldFiles | Where-Object { $null -ne $_ })
    $newFiles = @($NewFiles | Where-Object { $null -ne $_ })
    $target = Get-HeadlessManagedOverlayTarget $Context $RootRelative
    $targetItem = Get-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
    if ($null -ne $targetItem -and -not $targetItem.PSIsContainer) {
        throw "Refusing to replace a managed overlay file as a directory: $target"
    }

    # A marker written by a prior attempt can describe either side of a
    # completed rename. Allow both known manifests during recovery, but never
    # allow an unrelated file to be deleted by an overlay replacement.
    $allowRitsuMarker = $IncludeRitsuMarker.IsPresent -or
        [string]::Equals($RootRelative, 'mods\.combatsolver-headless-ritsulib', [StringComparison]::OrdinalIgnoreCase)
    Assert-HeadlessManagedOverlayTree $Context $RootRelative (@($oldFiles) + @($newFiles)) `
        -AllowRitsuMarker:$allowRitsuMarker
    if ($newFiles.Count -eq 0 -and $null -eq $targetItem) {
        return [ordered]@{ changed = $false; copiedFiles = 0; removedFiles = 0 }
    }

    $stagePrefix = '.multiplayer-overlay-stage-'
    $backupPrefix = '.multiplayer-overlay-backup-'
    $stage = Join-Path $Context.Root ($stagePrefix + [Guid]::NewGuid().ToString('N'))
    $backup = Join-Path $Context.Root ($backupPrefix + [Guid]::NewGuid().ToString('N'))
    $oldMoved = $false
    $newMoved = $false
    $operationCompleted = $false
    try {
        New-Item -ItemType Directory -Path $stage -Force | Out-Null
        Copy-HeadlessSnapshotFiles $Context $stage $newFiles
        $stagedTarget = Get-HeadlessCanonicalPath (Join-Path $stage ($RootRelative.Replace('/', '\')))
        if ($IncludeRitsuMarker.IsPresent) {
            New-Item -ItemType Directory -Path $stagedTarget -Force | Out-Null
            Assert-HeadlessNoReparsePoint $stagedTarget
            $stagedMarker = Join-Path $stagedTarget '.combatsolver-headless-only'
            Assert-HeadlessNoReparsePoint $stagedMarker
            Set-Content -LiteralPath $stagedMarker -Value 'CombatSolver private frozen dependency' -Encoding UTF8
        }
        if (Test-Path -LiteralPath $stagedTarget -PathType Container) {
            Assert-HeadlessNoReparsePoint $stagedTarget
            foreach ($entry in Get-ChildItem -LiteralPath $stagedTarget -Recurse -Force) {
                if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Refusing staged overlay through a reparse point: $($entry.FullName)"
                }
            }
        }

        if ($null -ne $targetItem) {
            Move-Item -LiteralPath $target -Destination $backup -Force
            $oldMoved = $true
        }
        if (Test-Path -LiteralPath $stagedTarget -PathType Container) {
            $targetParent = [IO.Path]::GetDirectoryName($target)
            New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
            Assert-HeadlessNoReparsePoint $targetParent
            Move-Item -LiteralPath $stagedTarget -Destination $target -Force
            $newMoved = $true
        }

        if (Test-Path -LiteralPath $backup) {
            Remove-HeadlessPrivateTemporaryTree $Context $backup $backupPrefix
        }
        $newRelative = @{}
        foreach ($file in $newFiles) {
            $newRelative[[string]$file.relative] = $true
        }
        $removedFiles = @($oldFiles | Where-Object {
                -not $newRelative.ContainsKey([string]$_.relative)
            }).Count
        $operationCompleted = $true
        return [ordered]@{
            changed = $true
            copiedFiles = $newFiles.Count
            removedFiles = $removedFiles
        }
    }
    catch {
        $failure = $_
        try {
            if ($newMoved -and (Test-Path -LiteralPath $target -PathType Container)) {
                Remove-HeadlessManagedOverlayTree $Context $RootRelative
            }
            if ($oldMoved -and (Test-Path -LiteralPath $backup -PathType Container)) {
                Move-Item -LiteralPath $backup -Destination $target -Force
            }
        }
        catch {
            throw "Managed overlay update failed and rollback also failed: $($failure.Exception.Message); rollback=$($_.Exception.Message)"
        }
        throw $failure
    }
    finally {
        if (Test-Path -LiteralPath $stage) {
            Remove-HeadlessPrivateTemporaryTree $Context $stage $stagePrefix
        }
        if ($operationCompleted -and (Test-Path -LiteralPath $backup)) {
            Remove-HeadlessPrivateTemporaryTree $Context $backup $backupPrefix
        }
    }
}

function Get-HeadlessMultiplayerSnapshotPlan(
    [hashtable]$Context,
    [string]$Profile,
    [string]$CombatSolverDll,
    [string]$CombatSolverManifest,
    [string]$MemoryCleaner,
    [string]$RitsuRoot,
    [string]$RitsuManifest,
    [string]$RitsuLibTargetVersion,
    [string]$BaseGameVersion = ''
) {
    if ($Profile -notin @('HostVanilla', 'ClientVanilla', 'ClientRitsuOnly', 'ClientCombatSolver')) {
        throw "Unsupported multiplayer snapshot profile: $Profile"
    }

    $needsRitsu = $Profile -in @('ClientRitsuOnly', 'ClientCombatSolver')
    $needsSolver = $Profile -eq 'ClientCombatSolver'
    Assert-HeadlessNoReparsePoint $Context.SourceGameRoot
    $baseSources = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in Get-ChildItem -LiteralPath $Context.SourceGameRoot -Recurse -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Cannot freeze a multiplayer game tree containing a reparse point: $($item.FullName)"
        }
        if (-not $item.PSIsContainer) {
            $relative = [IO.Path]::GetRelativePath($Context.SourceGameRoot, $item.FullName).Replace('/', '\')
            $rootSegment = $relative.Split('\')[0]
            if ([string]::Equals($rootSegment, 'mods', [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }
            $baseSources[$relative] = $item.FullName
        }
    }

    $ritsuSources = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    if ($needsRitsu) {
        if (-not (Test-Path -LiteralPath $RitsuRoot -PathType Container)) {
            throw "RitsuLib workshop directory was not found: $RitsuRoot"
        }
        Assert-HeadlessNoReparsePoint $RitsuRoot
        if (-not (Test-Path -LiteralPath $RitsuManifest -PathType Leaf)) {
            throw "RitsuLib manifest was not found: $RitsuManifest"
        }
        $ritsuSources['mods\.combatsolver-headless-ritsulib\STS2-RitsuLib.json'] = $RitsuManifest
        $variantManifest = Join-Path $RitsuRoot 'ritsulib-variants.manifest'
        if (Test-Path -LiteralPath $variantManifest -PathType Leaf) {
            foreach ($item in Get-ChildItem -LiteralPath $RitsuRoot -Recurse -Force) {
                if ($item.PSIsContainer -or $item.Name -in @('mod_manifest.json', 'RitsuLib.References.props')) {
                    continue
                }
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Cannot freeze a RitsuLib bundle containing a reparse point: $($item.FullName)"
                }
                $relative = [IO.Path]::GetRelativePath($RitsuRoot, $item.FullName).Replace('/', '\')
                $ritsuSources[(Join-Path 'mods\.combatsolver-headless-ritsulib' $relative)] = $item.FullName
            }
        }
        else {
            $legacyDll = Join-Path $RitsuRoot (Join-Path 'lib' (Join-Path $RitsuLibTargetVersion 'STS2-RitsuLib.dll'))
            $ritsuSources['mods\.combatsolver-headless-ritsulib\STS2-RitsuLib.dll'] = $legacyDll
        }
    }

    $combatSolverSources = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    if ($needsSolver) {
        foreach ($requiredSource in @($CombatSolverDll, $CombatSolverManifest, $MemoryCleaner)) {
            if (-not (Test-Path -LiteralPath $requiredSource -PathType Leaf)) {
                throw "CombatSolver snapshot input was not found: $requiredSource"
            }
        }
        $combatSolverSources['mods\CombatSolver\CombatSolver.dll'] = $CombatSolverDll
        $combatSolverSources['mods\CombatSolver\CombatSolver.json'] = $CombatSolverManifest
        $combatSolverSources['mods\CombatSolver\CombatSolver.MemoryCleaner.exe'] = $MemoryCleaner
    }

    $baseSet = New-HeadlessSnapshotFileSet $baseSources ("game_version=$BaseGameVersion" + [char]0)
    $ritsuSet = New-HeadlessSnapshotFileSet $ritsuSources
    $combatSolverSet = New-HeadlessSnapshotFileSet $combatSolverSources
    $combinedIdentity = "profile=$Profile" + [char]0 + $baseSet.id + [char]0 +
        $ritsuSet.id + [char]0 + $combatSolverSet.id + [char]0
    $bytes = [Text.Encoding]::UTF8.GetBytes($combinedIdentity)
    $overlayIdentity = "ritsu=$($ritsuSet.id)" + [char]0 +
        "combat_solver=$($combatSolverSet.id)" + [char]0
    $overlayBytes = [Text.Encoding]::UTF8.GetBytes($overlayIdentity)
    return @{
        id = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
        profile = $Profile
        baseGameId = $baseSet.id
        ritsuArtifactId = $ritsuSet.id
        combatSolverArtifactId = $combatSolverSet.id
        # Keep the previous field names in the plan for callers that only
        # display the composite/base identity during migration.
        baseSnapshotId = $baseSet.id
        overlayId = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($overlayBytes))
        baseFiles = @($baseSet.files)
        ritsuFiles = @($ritsuSet.files)
        combatSolverFiles = @($combatSolverSet.files)
        overlayFiles = @($ritsuSet.files) + @($combatSolverSet.files)
        files = @($baseSet.files) + @($ritsuSet.files) + @($combatSolverSet.files)
    }
}

function Remove-HeadlessOwnedGameTree([hashtable]$Context, [string]$Path) {
    $pathFull = Get-HeadlessCanonicalPath $Path
    $parent = [IO.Path]::GetDirectoryName($pathFull)
    if (-not $parent.Equals($Context.Root, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath (Join-Path $pathFull '.combatsolver-frozen-game.json') -PathType Leaf)) {
        throw "Refusing to remove an unowned private game snapshot: $pathFull"
    }
    $owner = Get-Content -LiteralPath (Join-Path $pathFull '.combatsolver-frozen-game.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($owner.schemaVersion -notin @(1, 2) -or $owner.runtimeRoot -ne $Context.Root) {
        throw "Private game snapshot ownership does not match this runtime: $pathFull"
    }
    Assert-HeadlessNoReparsePoint $pathFull
    foreach ($entry in Get-ChildItem -LiteralPath $pathFull -Recurse -Force) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing snapshot cleanup through a reparse point: $($entry.FullName)"
        }
    }
    Remove-Item -LiteralPath $pathFull -Recurse -Force
    Write-Host "UNATTENDED_SNAPSHOT_REMOVED path=$pathFull source_game_preserved=true"
}

function Set-HeadlessGameSnapshot(
    [hashtable]$Context,
    [hashtable]$Plan,
    [switch]$ForceFullRebuild
) {
    # The ordinary unattended runner still supplies the legacy all-files
    # snapshot plan. Keep its historical full-rebuild semantics while the
    # Multiplayer Lab supplies the explicit base/overlay plan below.
    if (-not $Plan.ContainsKey('baseGameId')) {
        $legacyFiles = @($Plan.files | Where-Object { $null -ne $_ })
        $Plan = @{
            id = [string]$Plan.id
            profile = 'LegacyHeadless'
            baseGameId = [string]$Plan.id
            ritsuArtifactId = ''
            combatSolverArtifactId = ''
            baseSnapshotId = [string]$Plan.id
            overlayId = ''
            baseFiles = $legacyFiles
            ritsuFiles = [object[]]@()
            combatSolverFiles = [object[]]@()
            overlayFiles = [object[]]@()
        }
    }
    $planBaseFiles = @($Plan.baseFiles | Where-Object { $null -ne $_ })
    $planRitsuFiles = @($Plan.ritsuFiles | Where-Object { $null -ne $_ })
    $planCombatSolverFiles = @($Plan.combatSolverFiles | Where-Object { $null -ne $_ })
    Assert-HeadlessNoReparsePoint $Context.GameRoot
    $manifest = Join-Path $Context.GameRoot '.combatsolver-frozen-game.json'
    $existing = $null
    if (Test-Path -LiteralPath $manifest -PathType Leaf) {
        $existing = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json -AsHashtable
        $existingSchema = [int](Get-HeadlessSnapshotManifestValue $existing 'schemaVersion')
        if ($existingSchema -notin @(1, 2) -or
            -not [string]::Equals([string](Get-HeadlessSnapshotManifestValue $existing 'runtimeRoot'),
                [string]$Context.Root, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Private game snapshot manifest does not match this runtime: $manifest"
        }
    }

    $existingSchema = if ($null -eq $existing) { 0 } else {
        [int](Get-HeadlessSnapshotManifestValue $existing 'schemaVersion')
    }
    $existingBaseFiles = @(Get-HeadlessSnapshotManifestValue $existing 'baseFiles')
    $existingRitsuFiles = @(Get-HeadlessSnapshotManifestValue $existing 'ritsuFiles')
    $existingCombatSolverFiles = @(Get-HeadlessSnapshotManifestValue $existing 'combatSolverFiles')
    $existingOverlayFiles = @(Get-HeadlessSnapshotManifestValue $existing 'overlayFiles')
    if ($existingSchema -eq 2 -and $existingRitsuFiles.Count -eq 0 -and
        $existingCombatSolverFiles.Count -eq 0 -and $existingOverlayFiles.Count -gt 0) {
        $existingRitsuFiles = @($existingOverlayFiles | Where-Object {
                [string]$_.relative -like 'mods\.combatsolver-headless-ritsulib\*'
            })
        $existingCombatSolverFiles = @($existingOverlayFiles | Where-Object {
                [string]$_.relative -like 'mods\CombatSolver\*'
            })
    }
    $existingLayoutComplete = $existingSchema -eq 2 -and
        @('baseGameId', 'ritsuArtifactId', 'combatSolverArtifactId', 'ritsuFiles', 'combatSolverFiles' |
            Where-Object { -not $existing.ContainsKey($_) }).Count -eq 0
    $existingBaseGameId = [string](Get-HeadlessSnapshotManifestValue $existing 'baseGameId')
    if ([string]::IsNullOrWhiteSpace($existingBaseGameId)) {
        $existingBaseGameId = [string](Get-HeadlessSnapshotManifestValue $existing 'baseSnapshotId')
    }
    $baseFilesMatch = $existingLayoutComplete -and
        @($existingBaseFiles).Count -eq $planBaseFiles.Count -and
        (Test-HeadlessSnapshotFilesMatch $Context $existingBaseFiles)
    $baseGameMatches = $existingLayoutComplete -and
        [string]::Equals($existingBaseGameId, [string]$Plan.baseGameId,
            [StringComparison]::OrdinalIgnoreCase) -and
        $baseFilesMatch -and
        (Test-Path -LiteralPath (Join-Path $Context.GameRoot 'SlayTheSpire2.exe') -PathType Leaf)
    $needsFullRebuild = $ForceFullRebuild.IsPresent -or -not $baseGameMatches
    $ritsuFilesMatch = $existingLayoutComplete -and
        @($existingRitsuFiles).Count -eq $planRitsuFiles.Count -and
        (Test-HeadlessSnapshotFilesMatch $Context $existingRitsuFiles)
    $combatSolverFilesMatch = $existingLayoutComplete -and
        @($existingCombatSolverFiles).Count -eq $planCombatSolverFiles.Count -and
        (Test-HeadlessSnapshotFilesMatch $Context $existingCombatSolverFiles)
    $ritsuMatches = $existingLayoutComplete -and
        [string]::Equals([string](Get-HeadlessSnapshotManifestValue $existing 'ritsuArtifactId'),
            [string]$Plan.ritsuArtifactId, [StringComparison]::OrdinalIgnoreCase) -and
        $ritsuFilesMatch
    $combatSolverMatches = $existingLayoutComplete -and
        [string]::Equals([string](Get-HeadlessSnapshotManifestValue $existing 'combatSolverArtifactId'),
            [string]$Plan.combatSolverArtifactId, [StringComparison]::OrdinalIgnoreCase) -and
        $combatSolverFilesMatch
    $profileMatches = $existingLayoutComplete -and
        [string]::Equals([string](Get-HeadlessSnapshotManifestValue $existing 'profile'),
            [string]$Plan.profile, [StringComparison]::OrdinalIgnoreCase)
    if (-not $needsFullRebuild -and $profileMatches -and $ritsuMatches -and $combatSolverMatches) {
        return [ordered]@{
            syncMode = 'unchanged'
            snapshotAction = 'REUSED'
            baseGameAction = 'REUSED'
            ritsuAction = 'REUSED'
            combatSolverAction = 'REUSED'
            baseGameId = $Plan.baseGameId
            ritsuArtifactId = $Plan.ritsuArtifactId
            combatSolverArtifactId = $Plan.combatSolverArtifactId
            baseSnapshotId = $Plan.baseGameId
            overlayId = $Plan.overlayId
            copiedBaseFiles = 0
            copiedOverlayFiles = 0
            removedOverlayFiles = 0
            copiedFiles = 0
        }
    }

    # The caller has already stopped its old game and holds both the instance
    # lock and an admitted pending host lease. Never overwrite a loaded image.
    Assert-HeadlessGameSnapshotNotRunning $Context

    $baseManifestFiles = ConvertTo-HeadlessSnapshotManifestFiles $planBaseFiles
    $ritsuManifestFiles = ConvertTo-HeadlessSnapshotManifestFiles $planRitsuFiles
    $combatSolverManifestFiles = ConvertTo-HeadlessSnapshotManifestFiles $planCombatSolverFiles
    $overlayManifestFiles = @($ritsuManifestFiles) + @($combatSolverManifestFiles)
    $manifestFiles = @($baseManifestFiles) + @($overlayManifestFiles)
    $manifestValue = [ordered]@{
        schemaVersion = 2
        snapshotKind = 'base-plus-profile-overlay'
        runtimeRoot = $Context.Root
        profile = $Plan.profile
        artifactId = $Plan.id
        baseGameId = $Plan.baseGameId
        ritsuArtifactId = $Plan.ritsuArtifactId
        combatSolverArtifactId = $Plan.combatSolverArtifactId
        baseSnapshotId = $Plan.baseGameId
        overlayId = $Plan.overlayId
        baseFiles = $baseManifestFiles
        ritsuFiles = $ritsuManifestFiles
        combatSolverFiles = $combatSolverManifestFiles
        overlayFiles = $overlayManifestFiles
        files = $manifestFiles
    }

    if ($needsFullRebuild) {
        $staging = Join-Path $Context.Root ('.game-stage-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $staging | Out-Null
        Write-HeadlessJson (Join-Path $staging '.combatsolver-frozen-game.json') @{
            schemaVersion = 2; runtimeRoot = $Context.Root; snapshotKind = 'base-plus-profile-overlay'
        }
        try {
            Copy-HeadlessSnapshotFiles $Context $staging $planBaseFiles
            Copy-HeadlessSnapshotFiles $Context $staging $planRitsuFiles
            Copy-HeadlessSnapshotFiles $Context $staging $planCombatSolverFiles
            $snapshotFiles = @($planBaseFiles) + @($planRitsuFiles) + @($planCombatSolverFiles)
            $hasPrivateRitsuDependency = @($snapshotFiles | Where-Object {
                    [string]$_.relative -like 'mods\.combatsolver-headless-ritsulib\*'
                }).Count -gt 0
            if ($hasPrivateRitsuDependency) {
                $dependency = Join-Path $staging 'mods\.combatsolver-headless-ritsulib'
                New-Item -ItemType Directory -Path $dependency -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $dependency '.combatsolver-headless-only') -Value 'CombatSolver private frozen dependency' -Encoding UTF8
            }
            Write-HeadlessJson (Join-Path $staging '.combatsolver-frozen-game.json') $manifestValue
            if (Test-Path -LiteralPath $Context.GameRoot) {
                Remove-HeadlessOwnedGameTree $Context $Context.GameRoot
            }
            Move-Item -LiteralPath $staging -Destination $Context.GameRoot
            return [ordered]@{
                syncMode = 'full-rebuild'
                snapshotAction = 'FULL_REBUILD'
                baseGameAction = 'REBUILT'
                ritsuAction = if ($planRitsuFiles.Count -gt 0) { 'INSTALLED' } else { 'REUSED' }
                combatSolverAction = if ($planCombatSolverFiles.Count -gt 0) { 'INSTALLED' } else { 'REUSED' }
                baseGameId = $Plan.baseGameId
                ritsuArtifactId = $Plan.ritsuArtifactId
                combatSolverArtifactId = $Plan.combatSolverArtifactId
                baseSnapshotId = $Plan.baseGameId
                overlayId = $Plan.overlayId
                copiedBaseFiles = $planBaseFiles.Count
                copiedOverlayFiles = $planRitsuFiles.Count + $planCombatSolverFiles.Count
                removedOverlayFiles = 0
                copiedFiles = $planBaseFiles.Count + $planRitsuFiles.Count + $planCombatSolverFiles.Count
            }
        } finally {
            if (Test-Path -LiteralPath $staging -PathType Container) {
                Remove-HeadlessOwnedGameTree $Context $staging
            }
        }
    }

    $ritsuChanged = -not $ritsuMatches
    $combatSolverChanged = -not $combatSolverMatches
    $ritsuSync = [ordered]@{ changed = $false; copiedFiles = 0; removedFiles = 0 }
    $combatSolverSync = [ordered]@{ changed = $false; copiedFiles = 0; removedFiles = 0 }
    if ($ritsuChanged) {
        $ritsuSync = Sync-HeadlessManagedOverlay $Context `
            'mods\.combatsolver-headless-ritsulib' $existingRitsuFiles $planRitsuFiles `
            -IncludeRitsuMarker:($planRitsuFiles.Count -gt 0)
    }
    if ($combatSolverChanged) {
        $combatSolverSync = Sync-HeadlessManagedOverlay $Context `
            'mods\CombatSolver' $existingCombatSolverFiles $planCombatSolverFiles
    }

    Write-HeadlessJson $manifest $manifestValue
    $metadataChanged = -not $profileMatches
    return [ordered]@{
        syncMode = 'overlay-incremental'
        snapshotAction = if ($ritsuChanged -or $combatSolverChanged -or $metadataChanged) {
            'OVERLAY_UPDATED'
        } else { 'REUSED' }
        baseGameAction = 'REUSED'
        ritsuAction = if ($ritsuChanged) {
            if ($existingRitsuFiles.Count -eq 0 -and $planRitsuFiles.Count -gt 0) { 'INSTALLED' } else { 'UPDATED' }
        } else { 'REUSED' }
        combatSolverAction = if ($combatSolverChanged) {
            if ($existingCombatSolverFiles.Count -eq 0 -and $planCombatSolverFiles.Count -gt 0) { 'INSTALLED' } else { 'UPDATED' }
        } else { 'REUSED' }
        baseGameId = $Plan.baseGameId
        ritsuArtifactId = $Plan.ritsuArtifactId
        combatSolverArtifactId = $Plan.combatSolverArtifactId
        baseSnapshotId = $Plan.baseGameId
        overlayId = $Plan.overlayId
        copiedBaseFiles = 0
        copiedOverlayFiles = $ritsuSync.copiedFiles + $combatSolverSync.copiedFiles
        removedOverlayFiles = $ritsuSync.removedFiles + $combatSolverSync.removedFiles
        copiedFiles = $ritsuSync.copiedFiles + $combatSolverSync.copiedFiles
    }
}

function Get-HeadlessProcessIdentity([Diagnostics.Process]$Process) {
    $handle = $Process.SafeHandle
    $Process.Refresh()
    if ($Process.HasExited) { throw "Cannot claim an exited headless process." }
    return @{ pid = $Process.Id; birth = $Process.StartTime.ToUniversalTime().ToString('O'); exe = $Process.MainModule.FileName }
}

function Get-HeadlessIdentityState([object]$Identity) {
    if ($null -eq $Identity) { return @{ alive = $false; workingSetMiB = 0 } }
    if ([int]$Identity.pid -le 0 -or [string]::IsNullOrWhiteSpace([string]$Identity.birth) -or
        [string]::IsNullOrWhiteSpace([string]$Identity.exe)) {
        throw "Malformed headless process identity; refusing to reclaim its lease."
    }
    $candidate = Get-Process -Id ([int]$Identity.pid) -ErrorAction SilentlyContinue
    if ($null -eq $candidate) { return @{ alive = $false; workingSetMiB = 0 } }
    # Access errors propagate: an unreadable process is not a dead process.
    $handle = $candidate.SafeHandle
    $candidate.Refresh()
    if ($candidate.HasExited) { return @{ alive = $false; workingSetMiB = 0 } }
    $birth = ([DateTimeOffset]$Identity.birth).UtcDateTime.ToString('O')
    $matches = $candidate.StartTime.ToUniversalTime().ToString('O') -eq $birth
    if ($matches -and -not [string]::Equals($candidate.MainModule.FileName, [string]$Identity.exe, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Live process birth matches but executable changed; preserving its lease.'
    }
    return @{ alive = $matches; workingSetMiB = if ($matches) { [math]::Ceiling($candidate.WorkingSet64 / 1MB) } else { 0 } }
}

function Open-HeadlessHostLock([hashtable]$Context, [switch]$Cleanup) {
    New-Item -ItemType Directory -Path $Context.HostRoot -Force | Out-Null
    Assert-HeadlessNoReparsePoint $Context.HostRoot
    $deadline = [DateTime]::UtcNow.AddSeconds($(if ($Cleanup) { 5 } else { $Context.QueueSeconds }))
    while ($true) {
        if (-not $Cleanup) { Assert-LauncherNotCancelled }
        try {
            return [IO.File]::Open((Join-Path $Context.HostRoot 'admission.lock'),
                [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        } catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) { throw "Timed out acquiring the headless host admission lock." }
            Start-Sleep -Milliseconds 100
        }
    }
}

function Get-HeadlessHostCapacity {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    return @{ cpu = [Environment]::ProcessorCount; totalMiB = [math]::Floor($os.TotalVisibleMemorySize / 1024);
        availableMiB = [math]::Floor($os.FreePhysicalMemory / 1024) }
}

function Get-HeadlessHostLeases([hashtable]$Context) {
    $leases = [Collections.Generic.List[object]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $Context.HostRoot -Filter '*.json' -File) {
        $lease = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable
        if ($lease.schemaVersion -ne 1 -or $lease.state -notin @('queued', 'pending', 'running') -or
            $lease.mode -notin @('exclusive', 'parallel') -or [int]$lease.cpu -lt 1 -or [int]$lease.memoryMiB -lt 1 -or
            [string]::IsNullOrWhiteSpace([string]$lease.runtimeRoot) -or
            [string]::IsNullOrWhiteSpace([string]$lease.token)) {
            throw "Unknown or invalid host lease; preserving it: $($file.FullName)"
        }
        $game = Get-HeadlessIdentityState $lease.game
        $launcher = Get-HeadlessIdentityState $lease.launcher
        if (-not $game.alive -and -not $launcher.alive) {
            if ($lease.state -eq 'pending' -and (Test-HeadlessUnboundGame $lease.runtimeRoot)) {
                throw 'An orphan private game may exist between spawn and registration; preserving its pending lease.'
            }
            Remove-Item -LiteralPath $file.FullName -Force
            continue
        }
        if ($game.alive -and -not [string]::Equals([string]$lease.game.exe,
                (Join-Path $lease.runtimeRoot 'game\SlayTheSpire2.exe'), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Live host lease executable does not belong to its private runtime: $($file.FullName)"
        }
        $lease.path = $file.FullName
        $lease.gameAlive = $game.alive
        $lease.outstandingMiB = [math]::Max(0, [int]$lease.memoryMiB - $game.workingSetMiB)
        $leases.Add($lease)
    }
    return ,$leases
}

function Test-HeadlessUnboundGame([string]$RuntimeRoot) {
    $executable = Join-Path $RuntimeRoot 'game\SlayTheSpire2.exe'
    foreach ($candidate in @(Get-Process -Name 'SlayTheSpire2' -ErrorAction SilentlyContinue)) {
        $handle = $candidate.SafeHandle
        $candidate.Refresh()
        if (-not $candidate.HasExited -and [string]::Equals($candidate.MainModule.FileName, $executable, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

function Enter-HeadlessHostLease([hashtable]$Context, [Diagnostics.Process]$ExistingGame) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Context.QueueSeconds)
    $launcher = Get-HeadlessProcessIdentity ([Diagnostics.Process]::GetCurrentProcess())
    $game = if ($null -ne $ExistingGame) { Get-HeadlessProcessIdentity $ExistingGame } else { $null }
    $Context.LeaseToken = [Guid]::NewGuid().ToString('N')
    $queuedAt = [DateTime]::UtcNow.ToString('O')
    $reportedWaiting = $false
    while ($true) {
        Assert-LauncherNotCancelled
        $hostLock = Open-HeadlessHostLock $Context
        try {
            $leases = Get-HeadlessHostLeases $Context
            $own = @($leases | Where-Object { $_.path -eq $Context.LeasePath })
            if ($own.Count -gt 0 -and -not [string]::Equals($own[0].runtimeRoot, $Context.Root, [StringComparison]::OrdinalIgnoreCase)) {
                throw "The host lease path belongs to another runtime."
            }
            if ($own.Count -gt 0 -and $own[0].gameAlive -and
                ($null -eq $game -or [int]$own[0].game.pid -ne $game.pid -or
                    ([DateTimeOffset]$own[0].game.birth) -ne ([DateTimeOffset]$game.birth))) {
                throw "A different live process owns this instance's host lease."
            }
            $others = @($leases | Where-Object { $_.path -ne $Context.LeasePath })
            $active = @($others | Where-Object { $_.state -ne 'queued' })
            $knownGames = @($leases | Where-Object { $_.gameAlive } | ForEach-Object { [int]$_.game.pid })
            if ($null -ne $game) { $knownGames += [int]$game.pid }
            $unknownGames = @(Get-Process -Name 'SlayTheSpire2' -ErrorAction SilentlyContinue |
                Where-Object { $_.Id -notin $knownGames })
            $capacity = Get-HeadlessHostCapacity
            $otherMemory = 0L; $otherOutstanding = 0L; $otherCpu = 0
            foreach ($lease in $active) {
                $otherMemory += [int]$lease.memoryMiB
                $otherOutstanding += [int]$lease.outstandingMiB
                $otherCpu += [int]$lease.cpu
            }
            $ownWorkingSet = if ($null -eq $game) { 0 } else { (Get-HeadlessIdentityState $game).workingSetMiB }
            $ownOutstanding = [math]::Max(0, $Context.MemoryMiB - $ownWorkingSet)
            $earlierQueued = @($others | Where-Object { $_.state -eq 'queued' -and
                    [DateTimeOffset]$_.queuedAt -lt [DateTimeOffset]$queuedAt })
            $canEnter = $unknownGames.Count -eq 0 -and
                ($null -ne $game -or $earlierQueued.Count -eq 0) -and
                ($Context.Mode -ne 'exclusive' -or $active.Count -eq 0) -and
                @($active | Where-Object { $_.mode -eq 'exclusive' }).Count -eq 0 -and $active.Count -lt 2 -and
                ($otherCpu + $Context.Cpu) -le $capacity.cpu -and
                ($otherMemory + $Context.MemoryMiB) -le ($capacity.totalMiB - 2048) -and
                ($otherOutstanding + $ownOutstanding + 2048) -le $capacity.availableMiB
            $record = @{ schemaVersion = 1; instance = $Context.Instance; runtimeRoot = $Context.Root;
                token = $Context.LeaseToken; launcher = $launcher; game = $game; artifactId = $Context.ArtifactId;
                mode = $Context.Mode; memoryMiB = $Context.MemoryMiB; cpu = $Context.Cpu; queuedAt = $queuedAt;
                state = if ($canEnter) { if ($null -eq $game) { 'pending' } else { 'running' } } else {
                    if ($null -eq $game) { 'queued' } else { 'running' }
                }
            }
            # A warm game continues owning its old reservation/mode until an
            # upgrade can be admitted; queuing must never make it invisible.
            if (-not $canEnter -and $null -ne $game -and $own.Count -gt 0) {
                $record.mode = $own[0].mode; $record.memoryMiB = $own[0].memoryMiB; $record.cpu = $own[0].cpu
            }
            Write-HeadlessJson $Context.LeasePath $record
            if ($canEnter) {
                Write-Host "UNATTENDED_ADMITTED instance=$($Context.Instance) mode=$($Context.Mode) memory_mib=$($Context.MemoryMiB) cpu=$($Context.Cpu)"
                return
            }
        } finally { $hostLock.Dispose() }
        if ([DateTime]::UtcNow -ge $deadline) { throw "Headless host admission timed out; no other instance was stopped." }
        if (-not $reportedWaiting) {
            Write-Host "UNATTENDED_QUEUED instance=$($Context.Instance) mode=$($Context.Mode)"
            $reportedWaiting = $true
        }
        Start-Sleep -Milliseconds 250
    }
}

function Set-HeadlessHostGame([hashtable]$Context, [Diagnostics.Process]$Game) {
    $identity = Get-HeadlessProcessIdentity $Game
    if (-not [string]::Equals($identity.exe, (Join-Path $Context.GameRoot 'SlayTheSpire2.exe'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Started headless executable is not the instance's private game."
    }
    $hostLock = Open-HeadlessHostLock $Context
    try {
        $lease = Get-Content -LiteralPath $Context.LeasePath -Raw | ConvertFrom-Json -AsHashtable
        if ($lease.token -ne $Context.LeaseToken -or $lease.runtimeRoot -ne $Context.Root) {
            throw "Headless host lease changed before process registration."
        }
        $lease.game = $identity; $lease.state = 'running'
        Write-HeadlessJson $Context.LeasePath $lease
    } finally { $hostLock.Dispose() }
}

function Exit-HeadlessHostLease([hashtable]$Context, [int]$GameProcessId = 0, [string]$GameBirth = '') {
    $hostLock = Open-HeadlessHostLock $Context -Cleanup
    try {
        if (-not (Test-Path -LiteralPath $Context.LeasePath -PathType Leaf)) { return }
        $lease = Get-Content -LiteralPath $Context.LeasePath -Raw | ConvertFrom-Json -AsHashtable
        if ($lease.runtimeRoot -ne $Context.Root) { throw "Cannot release another runtime's host lease." }
        $matchesToken = -not [string]::IsNullOrEmpty($Context.LeaseToken) -and $lease.token -eq $Context.LeaseToken
        $matchesGame = $null -ne $lease.game -and $GameProcessId -gt 0 -and
            [int]$lease.game.pid -eq $GameProcessId -and
            ([DateTimeOffset]$lease.game.birth) -eq ([DateTimeOffset]$GameBirth)
        if (-not $matchesToken -and -not $matchesGame) { return }
        if ((Get-HeadlessIdentityState $lease.game).alive) {
            # Passed/ready may retain a warm game. Its host reservation lives
            # until this exact process exits, not until its launcher returns.
            return
        }
        if ($lease.state -eq 'pending' -and (Test-HeadlessUnboundGame $Context.Root)) { return }
        Remove-Item -LiteralPath $Context.LeasePath -Force
    } finally { $hostLock.Dispose() }
}
