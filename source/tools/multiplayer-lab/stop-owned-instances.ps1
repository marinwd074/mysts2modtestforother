#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$InstanceRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '..\headless-runtime.ps1')
. (Join-Path $PSScriptRoot 'multiplayer-common.ps1')

$results = [Collections.Generic.List[object]]::new()
$errors = [Collections.Generic.List[string]]::new()
foreach ($root in $InstanceRoot) {
    try {
        $instance = Read-MultiplayerInstance $root
        $results.Add([ordered]@{
                instanceRoot = $instance.Root
                profile = [string]$instance.Profile.profile
                result = Stop-MultiplayerOwnedProcess $instance
                runtimeEvidenceEligible = $false
            })
    }
    catch {
        $errors.Add("$root :: $($_.Exception.ToString())")
    }
}

$output = [ordered]@{
    schemaVersion = 1
    stoppedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    results = @($results)
    errors = @($errors)
    runtimeEvidenceEligible = $false
}
$output | ConvertTo-Json -Depth 10
if ($errors.Count -gt 0) {
    exit 1
}
exit 0
