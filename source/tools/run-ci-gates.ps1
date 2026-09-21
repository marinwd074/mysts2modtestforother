#requires -Version 7.4

[CmdletBinding()]
param(
    [switch]$NoRestore,
    [switch]$SkipPython
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sourceRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $sourceRoot '..')).Path

function Invoke-Gate {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Script,
        [string[]]$Arguments = @()
    )

    Write-Output "GATE: $Name"
    & pwsh -NoLogo -NoProfile -File (Join-Path $sourceRoot $Script) @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

Push-Location -LiteralPath $repositoryRoot
try {
    Invoke-Gate 'TargetVersion' 'tools/verify-target-version.ps1'
    Invoke-Gate 'RefactorBoundaries' 'tools/verify-refactor-boundaries.ps1'

    $contractArguments = @()
    if ($NoRestore) { $contractArguments += '-NoRestore' }
    if ($SkipPython) { $contractArguments += '-SkipPython' }
    Invoke-Gate 'L1Contracts' 'tools/run-contract-tests.ps1' $contractArguments

    git diff --check
    if ($LASTEXITCODE -ne 0) {
        throw "git diff --check failed with exit code $LASTEXITCODE."
    }

    Write-Output 'CI_GATES_PASS'
}
finally {
    Pop-Location
}
