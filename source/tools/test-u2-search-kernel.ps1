#requires -Version 7.0

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path

function Read-RepoFile {
    param([Parameter(Mandatory)][string]$RelativePath)
    return [System.IO.File]::ReadAllText((Join-Path $repositoryRoot $RelativePath))
}

function Assert-Contains {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Needle,
        [Parameter(Mandatory)][string]$Label
    )
    if (-not $Text.Contains($Needle, [System.StringComparison]::Ordinal)) {
        throw "U2 contract missing: $Label"
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Needle,
        [Parameter(Mandatory)][string]$Label
    )
    if ($Text.Contains($Needle, [System.StringComparison]::Ordinal)) {
        throw "U2 forbidden coupling present: $Label"
    }
}

$controller = Read-RepoFile 'src/Runtime/SolverController.cs'
$contracts = Read-RepoFile 'src/Search/MultiplayerLocalCrossTurnContracts.cs'
$solver = Read-RepoFile 'src/Search/CombatBeamSolver.cs'
$coordinator = Read-RepoFile 'src/Search/CombatSearchCoordinator.cs'
$endTurn = Read-RepoFile 'src/Search/CombatBeamSolver.EndTurnExpansion.cs'
$ordering = Read-RepoFile 'src/Search/CombatBeamSolver.FinalPlanOrdering.cs'
$fixture = Read-RepoFile 'tools/OfflineSearchHarness/U0BaselineFixture.cs'

# Shared full-search kernel: route horizon chooses planning capability.
Assert-Contains $controller 'bool useFullSearchKernel =' 'shared full-search-kernel switch'
Assert-Contains $controller 'MultiplayerLocalCrossTurnContracts.CanUseFullSearchHeuristics(routePolicy)' 'single/multiplayer full-search contract'
Assert-Contains $controller 'SolverPotionPolicy effectivePotionPolicy = useFullSearchKernel' 'configured potion search follows search kernel'
Assert-Contains $controller 'PotionStrategySnapshot effectivePotionStrategy = useFullSearchKernel' 'configured potion strategy follows search kernel'
Assert-NotContains $controller 'capabilities.CanUsePotionsAutomatically' 'deployment potion authority must not shape search candidates'
Assert-Contains $controller 'SearchPolicySnapshot policy = new(' 'shared policy construction'
Assert-Contains $controller 'settings.Profile,' 'same configured search profile enters policy'
Assert-Contains $controller '&& useFullSearchKernel' 'novelty/full-horizon features use shared kernel'
Assert-Contains $controller 'GrowthBudgets = useFullSearchKernel ? settings.GrowthBudgets : default' 'growth budget parity'
Assert-Contains $controller 'RelicTargets = useFullSearchKernel' 'relic target parity'
Assert-Contains $controller 'GrowthOpportunityTargets = useFullSearchKernel' 'growth opportunity parity'
Assert-Contains $controller 'IgnoreLongTermRewards = settings.IgnoreLongTermRewards || !useFullSearchKernel' 'long-term reward parity'

# Same solver/profile path for both full-route modes.
Assert-Contains $solver 'private readonly SolverSearchProfile _profile = searchProfile ?? SolverSearchProfile.Default;' 'shared solver profile'
Assert-Contains $solver 'private readonly SearchRoutePolicy _routePolicy = policy.RoutePolicy;' 'route semantics stay explicit'
Assert-Contains $coordinator 'new CombatBeamSolver(' 'single shared beam solver construction'
Assert-Contains $contracts 'SearchRoutePolicy.SinglePlayerFullRoute' 'singleplayer full route contract'
Assert-Contains $contracts 'SearchRoutePolicy.MultiplayerLocalCrossTurn' 'multiplayer local-cross-turn contract'

# Real multiplayer semantics must remain visible instead of being deleted for equality.
Assert-Contains $contracts 'ShouldExcludeMultiplayerOnlyCard(' 'MultiplayerOnly action exclusion remains'
Assert-Contains $solver 'MultiplayerLocalCrossTurnContracts.ShouldExcludeMultiplayerOnlyCard(' 'candidate generator keeps MultiplayerOnly boundary'
Assert-Contains $endTurn 'policy.RoutePolicy != SearchRoutePolicy.MultiplayerLocalCrossTurn' 'Joint forecast remains multiplayer-only'
Assert-Contains $endTurn 'simulator.State.RootCapturedPlayers.Count <= 1' 'no-teammate/single-captured-player path falls back to shared end-turn simulation'
Assert-Contains $ordering 'routePolicy == SearchRoutePolicy.MultiplayerLocalCrossTurn' 'team/scenario objective remains explicit multiplayer semantics'
Assert-Contains $contracts 'ShouldStopBeforeSharedRngShuffle(' 'shared-RNG trust boundary remains'
Assert-Contains $fixture 'NoTeammateEvents' 'deterministic no-teammate fixture remains'
Assert-Contains $fixture 'ShadowTeammatePlanner.ReplayForecastActions(' 'teammate fixture reuses production simulation'

Write-Output 'U2SearchKernelChecks PASS: shared full-search planning kernel + explicit multiplayer semantic boundaries'
