#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $EvidencePath,

    [string] $OutputPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-E0Result {
    param(
        [Parameter(Mandatory = $true)][hashtable] $Result,
        [Parameter(Mandatory = $true)][int] $ExitCode
    )

    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        $script:OutputPath = Join-Path (Get-Location) 'e0-multiplayer-evidence.json'
    }
    $parent = Split-Path -Parent $script:OutputPath
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $Result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $script:OutputPath -Encoding utf8
    Write-Host ("E0_MULTIPLAYER_{0} evidence={1}" -f $Result.status, $script:OutputPath)
    exit $ExitCode
}

function Parse-KeyValues {
    param([Parameter(Mandatory = $true)][string] $Message)

    $fields = @{}
    foreach ($match in [regex]::Matches($Message, '(?<key>[A-Za-z0-9_]+)=(?<value>[^\s]+)')) {
        $fields[$match.Groups['key'].Value] = $match.Groups['value'].Value
    }
    return $fields
}

$temporaryRoot = $null
try {
    $resolved = (Resolve-Path -LiteralPath $EvidencePath -ErrorAction Stop).Path
    $isFile = Test-Path -LiteralPath $resolved -PathType Leaf
    $isDirectory = Test-Path -LiteralPath $resolved -PathType Container
    $extension = [IO.Path]::GetExtension($resolved)
    $isZip = $isFile -and $extension.Equals('.zip', [StringComparison]::OrdinalIgnoreCase)

    if ($isZip) {
        $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("combatsolver-e0-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
        Expand-Archive -LiteralPath $resolved -DestinationPath $temporaryRoot -Force
        $scanRoot = $temporaryRoot
    }
    elseif ($isDirectory) {
        $scanRoot = $resolved
    }
    else {
        $scanRoot = Split-Path -Parent $resolved
    }

    if ($isFile -and -not $isZip) {
        $files = @(Get-Item -LiteralPath $resolved)
    }
    else {
        $files = @(Get-ChildItem -LiteralPath $scanRoot -File -Recurse -Force |
            Where-Object { $_.Extension -in @('.jsonl', '.log', '.txt') })
    }

    if ($files.Count -eq 0) {
        Write-E0Result -ExitCode 2 -Result @{
            schemaVersion = 1
            status = 'UNVERIFIED'
            reason = 'No JSONL/log/text evidence files were found.'
            source = $resolved
        }
    }

    $runtimeMultiplayerObserved = $false
    $probePlayerCountTwo = $false
    $candidates = [Collections.Generic.List[object]]::new()

    foreach ($file in $files) {
        $current = $null
        $lineNumber = 0
        foreach ($line in Get-Content -LiteralPath $file.FullName -ErrorAction Stop) {
            $lineNumber++
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            $message = $line
            try {
                $record = $line | ConvertFrom-Json -ErrorAction Stop
                if ($null -ne $record.PSObject.Properties['Message']) {
                    $message = [string]$record.Message
                }
                if ($null -ne $record.PSObject.Properties['PlayerCount'] -and
                    [int]$record.PlayerCount -eq 2) {
                    $probePlayerCountTwo = $true
                }
            }
            catch {
                # Godot/plain logs are valid evidence inputs too.
            }

            if ($message.Contains('[CombatSolver/MultiplayerAdvisor] SEARCH_DEBOUNCED_START', [StringComparison]::Ordinal) -or
                $message.Contains('[CombatSolver/MultiplayerProbe] CAPABILITY_BOUNDARY entered=true', [StringComparison]::Ordinal)) {
                $runtimeMultiplayerObserved = $true
            }

            if ($message.Contains('[CombatSolver/Test] SEARCH_E0_TIMELINE ', [StringComparison]::Ordinal)) {
                $fields = Parse-KeyValues $message
                $current = [pscustomobject]@{
                    sourceFile = $file.FullName
                    sourceLine = $lineNumber
                    timeline = $fields
                    members = [Collections.Generic.List[object]]::new()
                    phases = [Collections.Generic.List[object]]::new()
                }
                $candidates.Add($current)
                continue
            }

            if ($null -eq $current) {
                continue
            }
            if ($message.Contains('[CombatSolver/Test] SEARCH_E0_MEMBER ', [StringComparison]::Ordinal)) {
                $current.members.Add([pscustomobject](Parse-KeyValues $message))
            }
            elseif ($message.Contains('[CombatSolver/Test] SEARCH_E0_PHASE ', [StringComparison]::Ordinal)) {
                $current.phases.Add([pscustomobject](Parse-KeyValues $message))
            }
        }
    }

    if (-not ($runtimeMultiplayerObserved -or $probePlayerCountTwo)) {
        Write-E0Result -ExitCode 2 -Result @{
            schemaVersion = 1
            status = 'UNVERIFIED'
            reason = 'No real multiplayer runtime marker or PlayerCount=2 probe snapshot was found.'
            source = $resolved
            candidateCount = $candidates.Count
        }
    }

    $matching = @($candidates | Where-Object {
        $timeline = $_.timeline
        $context = [string]($timeline['context'] ?? '')
        $timeline['player_count'] -eq '2' -and
        $context.Contains('route=MultiplayerLocalCrossTurn;', [StringComparison]::Ordinal) -and
        $context.Contains('team=True;', [StringComparison]::Ordinal) -and
        $context.Contains('scenario=True;', [StringComparison]::Ordinal)
    })

    if ($matching.Count -eq 0) {
        Write-E0Result -ExitCode 2 -Result @{
            schemaVersion = 1
            status = 'UNVERIFIED'
            reason = 'No PlayerCount=2 E0 timeline with multiplayer team objective and scenario reevaluation was found.'
            source = $resolved
            candidateCount = $candidates.Count
        }
    }

    $selected = $null
    foreach ($candidate in $matching) {
        $phaseNames = @($candidate.phases | ForEach-Object { [string]$_.phase })
        if ($phaseNames -contains 'shadow' -and $phaseNames -contains 'scenario_matrix') {
            $selected = $candidate
        }
    }
    if ($null -eq $selected) {
        Write-E0Result -ExitCode 2 -Result @{
            schemaVersion = 1
            status = 'UNVERIFIED'
            reason = 'Multiplayer E0 timeline exists, but it did not exercise both Shadow and Scenario Matrix.'
            source = $resolved
            matchingCandidateCount = $matching.Count
        }
    }

    $timeline = $selected.timeline
    $required = @('candidate_id', 'member_id', 'member_kind', 'generated_ms', 'evaluated_ms',
        'selected_ms', 'published_ms', 'expanded_at_generation', 'turn_depth', 'context')
    foreach ($name in $required) {
        if (-not $timeline.ContainsKey($name)) {
            Write-E0Result -ExitCode 1 -Result @{
                schemaVersion = 1
                status = 'FAIL'
                reason = "Selected E0 timeline is missing $name."
                source = $selected.sourceFile
                sourceLine = $selected.sourceLine
            }
        }
    }

    $generated = [double]::Parse($timeline['generated_ms'], [Globalization.CultureInfo]::InvariantCulture)
    $evaluated = [double]::Parse($timeline['evaluated_ms'], [Globalization.CultureInfo]::InvariantCulture)
    $chosen = [double]::Parse($timeline['selected_ms'], [Globalization.CultureInfo]::InvariantCulture)
    $published = [double]::Parse($timeline['published_ms'], [Globalization.CultureInfo]::InvariantCulture)
    if ($generated -gt $evaluated -or $evaluated -gt $chosen -or $chosen -gt $published) {
        Write-E0Result -ExitCode 1 -Result @{
            schemaVersion = 1
            status = 'FAIL'
            reason = 'E0 selected-candidate timestamps are not monotonic.'
            source = $selected.sourceFile
            sourceLine = $selected.sourceLine
            generatedMs = $generated
            evaluatedMs = $evaluated
            selectedMs = $chosen
            publishedMs = $published
        }
    }

    $phaseTotals = [ordered]@{
        shadow = [ordered]@{ calls = 0; exclusiveMs = 0.0 }
        scenario_matrix = [ordered]@{ calls = 0; exclusiveMs = 0.0 }
        materialization = [ordered]@{ calls = 0; exclusiveMs = 0.0 }
        materialization_replay = [ordered]@{ calls = 0; exclusiveMs = 0.0 }
    }
    foreach ($phase in $selected.phases) {
        $name = [string]$phase.phase
        if (-not $phaseTotals.Contains($name)) {
            continue
        }
        $phaseTotals[$name].calls += [int]$phase.calls
        $phaseTotals[$name].exclusiveMs += [double]::Parse(
            [string]$phase.exclusive_ms,
            [Globalization.CultureInfo]::InvariantCulture)
    }
    if ($phaseTotals.shadow.calls -le 0 -or $phaseTotals.scenario_matrix.calls -le 0) {
        Write-E0Result -ExitCode 2 -Result @{
            schemaVersion = 1
            status = 'UNVERIFIED'
            reason = 'Shadow or Scenario Matrix was present but had no calls.'
            source = $selected.sourceFile
            sourceLine = $selected.sourceLine
            phases = $phaseTotals
        }
    }

    $member = @($selected.members | Where-Object {
        [string]$_.member_id -eq [string]$timeline['member_id']
    } | Select-Object -Last 1)

    Write-E0Result -ExitCode 0 -Result @{
        schemaVersion = 1
        status = 'PASS'
        source = $selected.sourceFile
        sourceLine = $selected.sourceLine
        runtimeMultiplayerObserved = $runtimeMultiplayerObserved
        probePlayerCountTwo = $probePlayerCountTwo
        playerCount = 2
        candidateId = [long]$timeline['candidate_id']
        sourceMember = ("{0}#{1}" -f $timeline['member_kind'], $timeline['member_id'])
        generatedMs = [math]::Round($generated, 3)
        evaluatedMs = [math]::Round($evaluated, 3)
        selectedMs = [math]::Round($chosen, 3)
        publishedMs = [math]::Round($published, 3)
        selectedToPublishedMs = [math]::Round($published - $chosen, 3)
        expandedAtGeneration = [long]$timeline['expanded_at_generation']
        turnDepth = [int]$timeline['turn_depth']
        evaluationContext = [string]$timeline['context']
        selectedMember = if ($member.Count -gt 0) { $member[0] } else { $null }
        phases = $phaseTotals
    }
}
finally {
    if ($null -ne $temporaryRoot -and (Test-Path -LiteralPath $temporaryRoot)) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
