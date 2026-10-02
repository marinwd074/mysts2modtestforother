#requires -Version 7.0

[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '../..')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
Set-Location -LiteralPath $repoRoot

$tracked = @(& git -c core.quotepath=false ls-files)
if ($LASTEXITCODE -ne 0) {
    throw "git ls-files failed with exit code $LASTEXITCODE."
}

$required = @(
    '.editorconfig',
    'README.md',
    'CONTRIBUTING.md',
    'UPSTREAM.md',
    '.github/PULL_REQUEST_TEMPLATE.md',
    '.github/ISSUE_TEMPLATE/bug-report.yml',
    '.github/ISSUE_TEMPLATE/feature-request.yml',
    '.github/workflows/compatibility.yml',
    '.github/workflows/pinned-release-build.yml',
    '.github/workflows/release.yml',
    'source/docs/REPOSITORY_MAINTENANCE.md',
    'source/PROJECT_VERSION',
    'source/docs/PROJECT_VERSION_HISTORY.md',
    'source/tools/new-project-version.ps1',
    '.github/README.md',
    '.github/workflows/README.md',
    'game-body/README.md',
    'source/README.md',
    'source/docs/README.md',
    'source/docs/baseline/README.md',
    'source/docs/compat/README.md',
    'source/docs/multiplayer/README.md',
    'source/src/README.md',
    'source/src/Api/README.md',
    'source/src/Compatibility/README.md',
    'source/src/Diagnostics/README.md',
    'source/src/Engine/README.md',
    'source/src/Engine/Common/README.md',
    'source/src/Engine/InCombat/README.md',
    'source/src/Engine/Common/Mirrors/README.md',
    'source/src/Engine/InCombat/Extensions/README.md',
    'source/src/Engine/InCombat/Mirrors/README.md',
    'source/src/Engine/InCombat/Simulation/README.md',
    'source/src/Prediction/README.md',
    'source/src/Replay/README.md',
    'source/src/Runtime/README.md',
    'source/src/Search/README.md',
    'source/src/Strategy/README.md',
    'source/src/Testing/README.md',
    'source/src/UI/README.md',
    'source/tools/README.md',
    'source/tools/multiplayer-lab/README.md'
)

$missing = @($required | Where-Object { $_ -notin $tracked })
if ($missing.Count -gt 0) {
    throw "Required repository files are missing: $($missing -join ', ')"
}

# Check destinations against Git as well as disk: an ignored local report must
# not make a documentation link appear valid on just one developer's machine.
$trackedDestinations = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($path in $tracked) {
    [void]$trackedDestinations.Add($path)
    $parent = $path
    while ($parent.Contains('/')) {
        $parent = $parent.Substring(0, $parent.LastIndexOf('/'))
        [void]$trackedDestinations.Add($parent)
    }
}
[void]$trackedDestinations.Add('.')
$markdown = @($tracked | Where-Object { $_ -match '\.md$' })
$brokenLinks = [System.Collections.Generic.List[string]]::new()
$localLinkCount = 0
$inlineLinkPattern = '\[[^\]\r\n]*\]\(\s*(?<target><[^>\r\n]+>|(?:\\.|[^\s()\\]|\((?:\\.|[^()])*\))+)'
$referenceLinkPattern = '^\s{0,3}\[[^\]\r\n]+\]:\s*(?<target><[^>\r\n]+>|\S+)'
foreach ($path in $markdown) {
    $body = [IO.File]::ReadAllText((Join-Path $repoRoot $path))
    # Preserve line numbers when removing comments.
    $body = [regex]::Replace($body, '(?s)<!--.*?-->', {
        param($comment)
        [regex]::Replace($comment.Value, '[^\r\n]', ' ')
    })
    $lines = $body -split '\r?\n'
    $fenceCharacter = ''
    $fenceLength = 0
    for ($lineNumber = 0; $lineNumber -lt $lines.Count; $lineNumber++) {
        $line = $lines[$lineNumber]
        if ($fenceLength -gt 0) {
            if ($line -match '^\s*(?<fence>`{3,}|~{3,})\s*$' -and
                $Matches.fence[0].ToString() -ceq $fenceCharacter -and $Matches.fence.Length -ge $fenceLength) {
                $fenceLength = 0
            }
            continue
        }
        if ($line -match '^\s*(?<fence>`{3,}|~{3,})') {
            $fenceCharacter = $Matches.fence[0].ToString()
            $fenceLength = $Matches.fence.Length
            continue
        }
        $line = [regex]::Replace($line, '(?<ticks>`+)(?!`).*?\k<ticks>(?!`)', '')
        $links = @([regex]::Matches($line, $inlineLinkPattern)) + @([regex]::Matches($line, $referenceLinkPattern))
        foreach ($link in $links) {
            $target = $link.Groups['target'].Value.Trim('<', '>')
            $location = "${path}:$($lineNumber + 1) -> $target"
            if ($target -match '^(?:[A-Za-z]:[\\/]|file:)') {
                $brokenLinks.Add("$location (machine-local path)")
                continue
            }
            if ($target -match '^(?:[A-Za-z][A-Za-z0-9+.-]*:|#|//)') { continue }
            $target = ($target -split '[?#]', 2)[0]
            if (-not $target) { continue }
            $target = [uri]::UnescapeDataString([Net.WebUtility]::HtmlDecode($target))
            $target = [regex]::Replace($target, '\\([\\() ])', '$1').Replace('\', '/')
            $localLinkCount++
            if ([IO.Path]::IsPathRooted($target)) {
                $brokenLinks.Add("$location (use a repository-relative path)")
                continue
            }
            $destination = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent (Join-Path $repoRoot $path)) $target))
            $relative = [IO.Path]::GetRelativePath($repoRoot, $destination).Replace('\', '/').TrimEnd('/')
            if (-not $trackedDestinations.Contains($relative) -or -not (Test-Path -LiteralPath $destination)) {
                $brokenLinks.Add("$location (destination is missing or untracked)")
            }
        }
    }
}
if ($brokenLinks.Count -gt 0) {
    throw "Repository documentation links are invalid:`n$($brokenLinks -join "`n")"
}

$forbiddenPatterns = @(
    '^runtime-evidence/',
    '^artifacts/',
    '^source/coverage/',
    '^source/\.godot/',
    '^source/docs/.*/evidence/',
    '^source/tools/.*(?:Experimental|Prototype)',
    '^source/tools/(?:publish-release|quark-release-bundle)\.ps1$',
    '(^|/)(?:bin|obj)/',
    '\.(?:zip|7z|rar|log|tmp|bak|user|suo)$',
    '(^|/)results[^/]*\.json$'
)

$forbidden = [System.Collections.Generic.List[string]]::new()
foreach ($path in $tracked) {
    foreach ($pattern in $forbiddenPatterns) {
        if ($path -match $pattern) {
            $forbidden.Add($path)
            break
        }
    }
}
if ($forbidden.Count -gt 0) {
    throw "Tracked generated/obsolete files violate repository policy: $($forbidden -join ', ')"
}

$gameBody = @($tracked | Where-Object { $_ -like 'game-body/*' })
if ($gameBody.Count -eq 0) {
    throw 'Pinned game-body snapshot is missing.'
}

$attributes = Get-Content -LiteralPath '.gitattributes' -Raw
if (-not $attributes.Contains('game-body/** filter=lfs diff=lfs merge=lfs -text')) {
    throw 'game-body must remain covered by the Git LFS rule.'
}

$target = Get-Content -LiteralPath 'source/build-target.json' -Raw | ConvertFrom-Json
$manifest = Get-Content -LiteralPath 'source/CombatSolver.json' -Raw | ConvertFrom-Json
[xml]$project = Get-Content -LiteralPath 'source/CombatSolver.csproj' -Raw
$versionNode = $project.SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $versionNode) {
    throw 'CombatSolver.csproj has no Version element.'
}
$projectVersion = [string]$versionNode.InnerText

if ([string]$manifest.version -cne $projectVersion) {
    throw "Version drift: manifest=$($manifest.version) project=$projectVersion"
}
if ([string]$manifest.min_game_version -cne [string]$target.game_version) {
    throw "Game target drift: manifest=$($manifest.min_game_version) build-target=$($target.game_version)"
}
if ([string]$target.game_version -cne '0.107.1') {
    throw "Repository hygiene currently expects pinned game target 0.107.1, got $($target.game_version)."
}

& (Join-Path $PSScriptRoot 'new-project-version.ps1') -Check -RepositoryRoot $repoRoot
Write-Output "REPOSITORY_HYGIENE_PASS tracked=$($tracked.Count) markdown=$($markdown.Count) local_links=$localLinkCount game_body=$($gameBody.Count) version=$projectVersion target=$($target.game_version)"
