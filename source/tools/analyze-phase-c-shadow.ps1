param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Path
)

$ErrorActionPreference = 'Stop'

function Read-Number([string]$Line, [string]$Name) {
    $match = [regex]::Match($Line, "(?:^|\s)$([regex]::Escape($Name))=([^\s]+)")
    if (-not $match.Success) { return 0.0 }
    $value = 0.0
    if ([double]::TryParse(
        $match.Groups[1].Value,
        [System.Globalization.NumberStyles]::Float,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [ref]$value)) {
        return $value
    }
    return 0.0
}

$resolved = (Resolve-Path -LiteralPath $Path).Path
$tempRoot = $null
try {
    if ([System.IO.Path]::GetExtension($resolved).Equals('.zip', [StringComparison]::OrdinalIgnoreCase)) {
        $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("combatsolver-phase-c-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $tempRoot | Out-Null
        Expand-Archive -LiteralPath $resolved -DestinationPath $tempRoot -Force
        $scanRoot = $tempRoot
    } else {
        $scanRoot = $resolved
    }

    $files = if (Test-Path -LiteralPath $scanRoot -PathType Leaf) {
        @(Get-Item -LiteralPath $scanRoot)
    } else {
        @(Get-ChildItem -LiteralPath $scanRoot -Recurse -File)
    }

    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($file in $files) {
        if ($file.Length -gt 64MB) { continue }
        foreach ($line in [System.IO.File]::ReadLines($file.FullName)) {
            if ($line -notmatch 'shadow_replay_observations=') { continue }
            $observations = Read-Number $line 'shadow_replay_observations'
            $hits = Read-Number $line 'shadow_replay_validated_hits'
            $localCoreObservations = Read-Number $line 'shadow_local_core_observations'
            $localCoreHits = Read-Number $line 'shadow_local_core_validated_hits'
            $rows.Add([pscustomobject]@{
                file = $file.FullName
                transitions = [long](Read-Number $line 'transitions')
                terminalCacheHits = [long](Read-Number $line 'transition_cache_hits')
                observations = [long]$observations
                stores = [long](Read-Number $line 'shadow_replay_stores')
                validatedHits = [long]$hits
                collisionRejects = [long](Read-Number $line 'shadow_replay_collision_rejects')
                outputMismatches = [long](Read-Number $line 'shadow_replay_output_mismatches')
                droppedStores = [long](Read-Number $line 'shadow_replay_dropped_stores')
                localCoreObservations = [long]$localCoreObservations
                localCoreStores = [long](Read-Number $line 'shadow_local_core_stores')
                localCoreValidatedHits = [long]$localCoreHits
                localCoreCollisionRejects = [long](Read-Number $line 'shadow_local_core_collision_rejects')
                localCoreOutputMismatches = [long](Read-Number $line 'shadow_local_core_output_mismatches')
                localCoreDroppedStores = [long](Read-Number $line 'shadow_local_core_dropped_stores')
                localCoreHitRatio = if ($localCoreObservations -gt 0) {
                    $localCoreHits / $localCoreObservations
                } else { 0.0 }
                sampleLimit = [long](Read-Number $line 'shadow_replay_sample_limit')
                sampleCurrentTurn = [long](Read-Number $line 'shadow_replay_sample_current_turn')
                sampleFutureTurn = [long](Read-Number $line 'shadow_replay_sample_future_turn')
                sampleFutureTurnReserve = [long](Read-Number $line 'shadow_replay_sample_future_turn_reserve')
                sampleCurrentTurnLimited = $line -match 'shadow_replay_sample_current_turn_limited=true'
                sampleCapped = $line -match 'shadow_replay_sample_capped=true'
                validationMs = Read-Number $line 'shadow_replay_validation_ms'
                potentialSavedMs = Read-Number $line 'shadow_replay_potential_saved_ms'
                hitRatio = if ($observations -gt 0) { $hits / $observations } else { 0.0 }
            })
        }
    }

    $totals = [pscustomobject]@{
        resultLines = $rows.Count
        sampledSearches = @($rows | Where-Object observations -gt 0).Count
        cappedSearches = @($rows | Where-Object sampleCapped).Count
        currentTurnLimitedSearches = @($rows | Where-Object sampleCurrentTurnLimited).Count
        sampleCurrentTurn = [long](($rows | Measure-Object sampleCurrentTurn -Sum).Sum)
        sampleFutureTurn = [long](($rows | Measure-Object sampleFutureTurn -Sum).Sum)
        transitions = [long](($rows | Measure-Object transitions -Sum).Sum)
        terminalCacheHits = [long](($rows | Measure-Object terminalCacheHits -Sum).Sum)
        observations = [long](($rows | Measure-Object observations -Sum).Sum)
        stores = [long](($rows | Measure-Object stores -Sum).Sum)
        validatedHits = [long](($rows | Measure-Object validatedHits -Sum).Sum)
        collisionRejects = [long](($rows | Measure-Object collisionRejects -Sum).Sum)
        outputMismatches = [long](($rows | Measure-Object outputMismatches -Sum).Sum)
        droppedStores = [long](($rows | Measure-Object droppedStores -Sum).Sum)
        localCoreObservations = [long](($rows | Measure-Object localCoreObservations -Sum).Sum)
        localCoreStores = [long](($rows | Measure-Object localCoreStores -Sum).Sum)
        localCoreValidatedHits = [long](($rows | Measure-Object localCoreValidatedHits -Sum).Sum)
        localCoreCollisionRejects = [long](($rows | Measure-Object localCoreCollisionRejects -Sum).Sum)
        localCoreOutputMismatches = [long](($rows | Measure-Object localCoreOutputMismatches -Sum).Sum)
        localCoreDroppedStores = [long](($rows | Measure-Object localCoreDroppedStores -Sum).Sum)
        localCoreHitRatio = 0.0
        validationMs = [double](($rows | Measure-Object validationMs -Sum).Sum)
        potentialSavedMs = [double](($rows | Measure-Object potentialSavedMs -Sum).Sum)
        hitRatio = 0.0
        potentialNetSavedMs = 0.0
    }
    if ($totals.observations -gt 0) {
        $totals.hitRatio = $totals.validatedHits / [double]$totals.observations
    }
    if ($totals.localCoreObservations -gt 0) {
        $totals.localCoreHitRatio =
            $totals.localCoreValidatedHits / [double]$totals.localCoreObservations
    }
    $totals.potentialNetSavedMs = $totals.potentialSavedMs - $totals.validationMs

    [pscustomobject]@{
        source = $resolved
        totals = $totals
        searches = $rows
    } | ConvertTo-Json -Depth 6
}
finally {
    if ($tempRoot -and (Test-Path -LiteralPath $tempRoot)) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
