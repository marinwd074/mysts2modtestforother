#requires -Version 7.0

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
Set-Location -LiteralPath $repoRoot

$tracked = @(& git ls-files)
if ($LASTEXITCODE -ne 0) {
    throw "git ls-files failed with exit code $LASTEXITCODE."
}

$required = @(
    '.editorconfig',
    'CONTRIBUTING.md',
    'UPSTREAM.md',
    '.github/PULL_REQUEST_TEMPLATE.md',
    '.github/ISSUE_TEMPLATE/bug-report.yml',
    '.github/ISSUE_TEMPLATE/feature-request.yml',
    '.github/workflows/compatibility.yml',
    '.github/workflows/pinned-release-build.yml',
    '.github/workflows/release.yml',
    'source/docs/REPOSITORY_MAINTENANCE.md'
)

$missing = @($required | Where-Object { $_ -notin $tracked })
if ($missing.Count -gt 0) {
    throw "Required repository files are missing: $($missing -join ', ')"
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

Write-Output "REPOSITORY_HYGIENE_PASS tracked=$($tracked.Count) game_body=$($gameBody.Count) version=$projectVersion target=$($target.game_version)"
