#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $BaselineRoot,
    [Parameter(Mandatory = $true)][string] $CurrentRoot,
    [Parameter(Mandatory = $true)][string] $OutputPath,
    [string[]] $Scenarios = @('simple', 'draw_energy', 'teammate'),
    [double] $RegressionThresholdPercent = 2.0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-Samples {
    param([string] $Root, [string] $Scenario)
    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -Filter "e0-$Scenario.json" | Sort-Object FullName)
    if ($files.Count -lt 3) {
        throw "H1 requires at least three clean-process samples for $Scenario under $Root; found $($files.Count)."
    }
    return @($files | ForEach-Object {
        $json = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
        [pscustomobject]@{
            path = $_.FullName
            scenario = [string]$json.Scenario
            playerCount = [int]$json.PlayerCount
            rootHand = @($json.RootHand)
            route = @($json.Route)
            hpLoss = [int]$json.ProjectedBattleHpLost
            potionCount = [int]$json.ProjectedBattlePotionCount
            endedTurn = if ($null -eq $json.CombatEndedTurn) { $null } else { [int]$json.CombatEndedTurn }
            victory = $null -ne $json.CombatEndedTurn
            boundary = [string]$json.BoundaryReason
            nodes = [long]$json.TotalExpandedNodes
            transitions = [long]$json.TotalTransitionCount
            generatedMs = [double]$json.GeneratedMs
            publishedMs = [double]$json.PublishedMs
        }
    })
}

function Median {
    param([double[]] $Values)
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    $middle = [int][math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) { return [double]$sorted[$middle] }
    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0
}

function Quality-Signature {
    param($Item)
    return "$($Item.hpLoss)|$($Item.potionCount)|$($Item.victory)|$($Item.endedTurn)|$($Item.boundary)"
}

function Route-Signature {
    param($Item)
    return (@($Item.route) -join [Environment]::NewLine)
}

function Dominates {
    param($Left, $Right)
    $leftVictoryRank = if ($Left.victory) { 1 } else { 0 }
    $rightVictoryRank = if ($Right.victory) { 1 } else { 0 }

    $notWorse =
        $Left.hpLoss -le $Right.hpLoss -and
        $Left.potionCount -le $Right.potionCount -and
        $leftVictoryRank -ge $rightVictoryRank

    if ($Left.victory -and $Right.victory) {
        $notWorse = $notWorse -and ([int]$Left.endedTurn -le [int]$Right.endedTurn)
    }

    if (-not $notWorse) { return $false }

    $strict =
        $Left.hpLoss -lt $Right.hpLoss -or
        $Left.potionCount -lt $Right.potionCount -or
        $leftVictoryRank -gt $rightVictoryRank

    if ($Left.victory -and $Right.victory) {
        $strict = $strict -or ([int]$Left.endedTurn -lt [int]$Right.endedTurn)
    }
    return $strict
}

$rows = @()
$hardFailures = [Collections.Generic.List[string]]::new()

foreach ($scenario in $Scenarios) {
    $baseline = @(Read-Samples $BaselineRoot $scenario)
    $current = @(Read-Samples $CurrentRoot $scenario)

    $all = @($baseline + $current)
    $rootHands = @($all | ForEach-Object { @($_.rootHand) -join ',' } | Sort-Object -Unique)
    $playerCounts = @($all | ForEach-Object playerCount | Sort-Object -Unique)
    if ($rootHands.Count -ne 1 -or $playerCounts.Count -ne 1) {
        $hardFailures.Add("$scenario fixture identity drifted between A/B.")
    }

    $timeLimited = @($all | Where-Object {
        [string]::Equals($_.boundary, 'TimeLimit', [StringComparison]::OrdinalIgnoreCase)
    })
    if ($timeLimited.Count -gt 0) {
        $paths = @($timeLimited | ForEach-Object path) -join ', '
        $hardFailures.Add("$scenario contains TimeLimit samples and cannot establish fixed-work H1 evidence: $paths")
    }

    $baselineQuality = @($baseline | ForEach-Object { Quality-Signature $_ } | Sort-Object -Unique)
    $currentQuality = @($current | ForEach-Object { Quality-Signature $_ } | Sort-Object -Unique)
    if ($baselineQuality.Count -ne 1) {
        $hardFailures.Add("$scenario baseline quality is not deterministic across clean processes.")
    }
    if ($currentQuality.Count -ne 1) {
        $hardFailures.Add("$scenario current quality is not deterministic across clean processes.")
    }

    $a = $baseline[0]
    $b = $current[0]
    $qualityEqual = (Quality-Signature $a) -ceq (Quality-Signature $b)
    $baselineDominates = Dominates $a $b
    $currentDominates = Dominates $b $a
    if ($baselineDominates) {
        $hardFailures.Add("$scenario current result is dominated by the historical baseline on recorded H1 quality axes.")
    }

    $baselineRoutes = @($baseline | ForEach-Object { Route-Signature $_ } | Sort-Object -Unique)
    $currentRoutes = @($current | ForEach-Object { Route-Signature $_ } | Sort-Object -Unique)
    $routeStableA = $baselineRoutes.Count -eq 1
    $routeStableB = $currentRoutes.Count -eq 1
    $sameRoute = $routeStableA -and $routeStableB -and $baselineRoutes[0] -ceq $currentRoutes[0]

    $baselineWork = @($baseline | ForEach-Object { "$($_.nodes)|$($_.transitions)" } | Sort-Object -Unique)
    $currentWork = @($current | ForEach-Object { "$($_.nodes)|$($_.transitions)" } | Sort-Object -Unique)
    $workStableA = $baselineWork.Count -eq 1
    $workStableB = $currentWork.Count -eq 1
    $sameWork = $workStableA -and $workStableB -and $baselineWork[0] -ceq $currentWork[0]

    $baselinePublished = Median @($baseline | ForEach-Object { [double]$_.publishedMs })
    $currentPublished = Median @($current | ForEach-Object { [double]$_.publishedMs })
    $baselineGenerated = Median @($baseline | ForEach-Object { [double]$_.generatedMs })
    $currentGenerated = Median @($current | ForEach-Object { [double]$_.generatedMs })

    $performanceComparable = $qualityEqual -and $sameRoute -and $sameWork
    $publishedChangePercent = if ($baselinePublished -gt 0) {
        100.0 * ($currentPublished / $baselinePublished - 1.0)
    } else { $null }
    $generatedChangePercent = if ($baselineGenerated -gt 0) {
        100.0 * ($currentGenerated / $baselineGenerated - 1.0)
    } else { $null }

    $performanceStatus = 'INCOMPARABLE'
    if ($performanceComparable) {
        if ($null -ne $publishedChangePercent -and $publishedChangePercent -gt $RegressionThresholdPercent) {
            $performanceStatus = 'REGRESSION'
            $hardFailures.Add(("$scenario fixed-work final-publication median regressed by " +
                "{0:F2}% (> {1:F2}%)." -f $publishedChangePercent, $RegressionThresholdPercent))
        } else {
            $performanceStatus = 'PASS'
        }
    }

    $qualityStatus =
        if ($baselineDominates) { 'REGRESSION' }
        elseif ($qualityEqual) { 'EQUAL' }
        elseif ($currentDominates) { 'IMPROVED' }
        else { 'TRADEOFF_OR_INCOMPARABLE' }

    $rows += [pscustomobject]@{
        scenario = $scenario
        samplesBaseline = $baseline.Count
        samplesCurrent = $current.Count
        qualityStatus = $qualityStatus
        routeStableBaseline = $routeStableA
        routeStableCurrent = $routeStableB
        sameRoute = $sameRoute
        sameWork = $sameWork
        performanceComparable = $performanceComparable
        performanceStatus = $performanceStatus
        baseline = [pscustomobject]@{
            hpLoss = $a.hpLoss
            potionCount = $a.potionCount
            victory = $a.victory
            endedTurn = $a.endedTurn
            boundary = $a.boundary
            nodes = $a.nodes
            transitions = $a.transitions
            generatedMedianMs = [math]::Round($baselineGenerated, 3)
            publishedMedianMs = [math]::Round($baselinePublished, 3)
            route = @($a.route)
        }
        current = [pscustomobject]@{
            hpLoss = $b.hpLoss
            potionCount = $b.potionCount
            victory = $b.victory
            endedTurn = $b.endedTurn
            boundary = $b.boundary
            nodes = $b.nodes
            transitions = $b.transitions
            generatedMedianMs = [math]::Round($currentGenerated, 3)
            publishedMedianMs = [math]::Round($currentPublished, 3)
            route = @($b.route)
        }
        generatedChangePercent = if ($null -eq $generatedChangePercent) { $null } else { [math]::Round($generatedChangePercent, 3) }
        publishedChangePercent = if ($null -eq $publishedChangePercent) { $null } else { [math]::Round($publishedChangePercent, 3) }
    }
}

$overall =
    if ($hardFailures.Count -gt 0) { 'FAIL' }
    elseif (@($rows | Where-Object { -not $_.performanceComparable }).Count -gt 0) { 'PASS_QUALITY_PERF_PARTIAL' }
    else { 'PASS' }

$result = [pscustomobject]@{
    schemaVersion = 1
    phase = 'H1'
    baselineRef = 'd745b0018e27f2013f7df580bde99523f1ec12ed'
    regressionThresholdPercent = $RegressionThresholdPercent
    overall = $overall
    rows = $rows
    failures = @($hardFailures)
    limitations = @(
        'E0 fixed-input evidence measures final selected candidate generation/publication, not the first real Host/Client deployable route.',
        'Timing is accepted only when recorded quality, exact route, expanded nodes and transitions are all equal.',
        'H2 real Host/Client evidence remains required for high-HP reuse to lethal-window replan behavior.'
    )
}

$parent = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($parent)) {
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
}
$result | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $OutputPath -Encoding utf8

Write-Host '| scenario | quality | same route | same work | perf | baseline published ms | current published ms | change |'
Write-Host '|---|---|---|---|---|---:|---:|---:|'
foreach ($row in $rows) {
    $changeText = if ($null -eq $row.publishedChangePercent) { '-' } else { "$($row.publishedChangePercent)%" }
    Write-Host ("| {0} | {1} | {2} | {3} | {4} | {5:F3} | {6:F3} | {7} |" -f
        $row.scenario,
        $row.qualityStatus,
        $row.sameRoute,
        $row.sameWork,
        $row.performanceStatus,
        $row.baseline.publishedMedianMs,
        $row.current.publishedMedianMs,
        $changeText)
}
Write-Host "overall=$overall evidence=$OutputPath"

if ($hardFailures.Count -gt 0) {
    foreach ($failure in $hardFailures) { Write-Error $failure -ErrorAction Continue }
    exit 1
}
