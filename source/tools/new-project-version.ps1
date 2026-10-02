[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Minor', 'Major')]
    [string]$Kind,
    [string]$Title,
    [string[]]$Added = @(),
    [string[]]$Optimized = @(),
    [string[]]$Fixed = @(),
    [string[]]$Notes = @(),
    [switch]$Check,
    [string]$RepositoryRoot = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
)

$ErrorActionPreference = 'Stop'
$versionPath = Join-Path $RepositoryRoot 'source/PROJECT_VERSION'
$historyPath = Join-Path $RepositoryRoot 'source/docs/PROJECT_VERSION_HISTORY.md'
$version = (Get-Content -LiteralPath $versionPath -Raw).Trim()
$history = (Get-Content -LiteralPath $historyPath -Raw).Replace("`r`n", "`n")
$marker = '<!-- project-versions -->'

function ConvertTo-VersionCents([string]$Value) {
    if ($Value -notmatch '^(0|[1-9]\d*)\.(\d{2})$') {
        throw "Invalid project version: $Value (expected 1.01)."
    }
    return ([long]$Matches[1] * 100L + [long]$Matches[2])
}

$currentCents = ConvertTo-VersionCents $version
$entries = [regex]::Matches($history, '(?m)^## (?<version>\d+\.\d{2}) — [^\n]+\n\n(?<date>\d{4}-\d{2}-\d{2}) · (?<kind>基线|小更新|大更新) · `project-v\k<version>`\n')
$headers = [regex]::Matches($history, '(?m)^## \d+\.\d{2} — ')
if ($entries.Count -eq 0 -or $entries.Count -ne $headers.Count -or $entries[0].Groups['version'].Value -cne $version) {
    throw 'PROJECT_VERSION does not match a complete, newest-first version history.'
}
if ([regex]::Matches($history, [regex]::Escape($marker)).Count -ne 1) {
    throw 'Version history must contain exactly one project-versions marker.'
}
for ($i = $entries.Count - 1; $i -ge 0; $i--) {
    $entry = $entries[$i]
    $cents = ConvertTo-VersionCents $entry.Groups['version'].Value
    $entryKind = $entry.Groups['kind'].Value
    if ($i -eq $entries.Count - 1) {
        if ($cents -ne 101 -or $entryKind -cne '基线') { throw 'Version history must start at baseline 1.01.' }
    } else {
        $increment = switch ($entryKind) { '小更新' { 1L }; '大更新' { 10L }; default { throw 'Only the first version may be a baseline.' } }
        if ($cents -ne $previousCents + $increment) { throw "Incorrect increment at $($entry.Groups['version'].Value)." }
    }
    $previousCents = $cents
}
if ($Check) {
    Write-Output "Project version ${version}: $($entries.Count) entries; version increments and tags are consistent."
    return
}
if (-not $Kind -or [string]::IsNullOrWhiteSpace($Title) -or $Title -match '[\r\n]') {
    throw 'Provide -Kind Minor|Major and a nonempty, single-line -Title.'
}
$sections = [ordered]@{ '新增' = $Added; '优化' = $Optimized; '修复' = $Fixed; '边界' = $Notes }
if ($Added.Count + $Optimized.Count + $Fixed.Count -eq 0) {
    throw 'Provide at least one -Added, -Optimized or -Fixed update.'
}
foreach ($items in $sections.Values) {
    foreach ($item in $items) {
        if ([string]::IsNullOrWhiteSpace($item) -or $item -match '[\r\n]') { throw 'Each update must be nonempty and single-line.' }
    }
}
$nextCents = $currentCents + $(if ($Kind -eq 'Major') { 10L } else { 1L })
$whole = [long][Math]::Floor([decimal]$nextCents / 100)
$nextVersion = '{0}.{1:D2}' -f $whole, ($nextCents % 100)
$kindLabel = if ($Kind -eq 'Major') { '大更新' } else { '小更新' }
$date = Get-Date -Format 'yyyy-MM-dd'
$lines = [Collections.Generic.List[string]]::new()
$lines.Add("## $nextVersion — $($Title.Trim())")
$lines.Add('')
$lines.Add("$date · $kindLabel · ``project-v$nextVersion``")
$lines.Add('')
foreach ($section in $sections.GetEnumerator()) {
    foreach ($item in $section.Value) { $lines.Add("- $($section.Key)：$($item.Trim())") }
}
$entryText = ($lines -join "`n") + "`n`n"
if ($PSCmdlet.ShouldProcess($RepositoryRoot, "Record project version $nextVersion")) {
    $newHistory = $history.Replace("$marker`n`n", "$marker`n`n$entryText")
    if ($newHistory -ceq $history) { throw 'The history marker must be followed by one blank line.' }
    # Write the history first: a partial write is detected by -Check before the next update.
    [IO.File]::WriteAllText($historyPath, $newHistory, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($versionPath, "$nextVersion`n", [Text.UTF8Encoding]::new($false))
}
Write-Output "Project version: $version -> $nextVersion; milestone tag after committing: project-v$nextVersion"
