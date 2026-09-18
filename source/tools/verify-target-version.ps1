#requires -Version 7.4

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$targetPath = Join-Path $repositoryRoot 'build-target.json'
$target = Get-Content -LiteralPath $targetPath -Raw | ConvertFrom-Json
$gameVersion = [string]$target.game_version
$ritsuVersion = [string]$target.ritsu_lib_target_version
$symbol = [string]$target.compatibility_symbol
$failures = [Collections.Generic.List[string]]::new()

function Assert-Target([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $failures.Add($Message) }
}

Assert-Target (-not [string]::IsNullOrWhiteSpace($gameVersion)) 'build-target.json game_version is empty.'
Assert-Target ($gameVersion -eq $ritsuVersion) 'game_version and ritsu_lib_target_version must match.'
Assert-Target ($symbol -match '^STS2_[0-9]+$') 'compatibility_symbol has an unexpected form.'

$manifest = Get-Content -LiteralPath (Join-Path $repositoryRoot 'CombatSolver.json') -Raw | ConvertFrom-Json
Assert-Target ([string]$manifest.min_game_version -eq $gameVersion) 'CombatSolver.json min_game_version differs from build-target.json.'

$activeFiles = @(
    'CombatSolver.csproj',
    'Directory.Build.props',
    'Directory.Build.targets',
    'local.props.example',
    'README.md',
    'tools/OfflineSearchHarness/OfflineSearchHarness.csproj',
    'tools/CoverageCatalog/CoverageCatalog.csproj',
    'tools/CoverageCatalog/Program.cs',
    'tools/AdaptedOnPlayChecks/AdaptedOnPlayChecks.csproj',
    'tools/headless-runtime.ps1',
    'tools/run-unattended-test.ps1',
    'tools/run-unattended-test.sh'
)
foreach ($relativePath in $activeFiles) {
    $path = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $failures.Add("Missing active target file: $relativePath")
        continue
    }
    if ($null -ne (Select-String -LiteralPath $path -Pattern '0\.111\.0' -AllMatches)) {
        $failures.Add("Stale 0.111.0 reference in active target file: $relativePath")
    }
}

$props = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
Assert-Target ($props -match "<CombatSolverTargetGameVersion[^>]*>$([regex]::Escape($gameVersion))</CombatSolverTargetGameVersion>") 'Directory.Build.props game target differs from build-target.json.'
Assert-Target ($props -match "<CombatSolverTargetRitsuLibVersion[^>]*>$([regex]::Escape($ritsuVersion))</CombatSolverTargetRitsuLibVersion>") 'Directory.Build.props RitsuLib target differs from build-target.json.'
Assert-Target ($props -match "<CombatSolverCompatibilityConstant[^>]*>$([regex]::Escape($symbol))</CombatSolverCompatibilityConstant>") 'Directory.Build.props compatibility symbol differs from build-target.json.'

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output "TARGET_VERSION_PASS game=$gameVersion ritsu=$ritsuVersion symbol=$symbol"
