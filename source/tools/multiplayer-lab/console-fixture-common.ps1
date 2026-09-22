Set-StrictMode -Version Latest

$script:MultiplayerConsoleFixtureAllowedVerbs = @(
    'card',
    'power',
    'energy',
    'block',
    'potion',
    'draw'
)

function Read-MultiplayerConsoleFixture {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FixturePath
    )

    $path = (Resolve-Path -LiteralPath $FixturePath -ErrorAction Stop).Path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Console fixture is not a file: $path"
    }

    $fixture = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ([int]$fixture.schemaVersion -ne 1) {
        throw 'Console fixture schemaVersion must be 1.'
    }
    $name = [string]$fixture.name
    if ([string]::IsNullOrWhiteSpace($name)) {
        throw 'Console fixture name is required.'
    }
    $waitFor = [string]$fixture.waitFor
    if (-not [string]::Equals($waitFor, 'local_playable_turn', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsupported console fixture waitFor: $waitFor"
    }

    [string[]]$commands = @($fixture.commands | ForEach-Object { [string]$_ })
    if ($commands.Count -lt 1 -or $commands.Count -gt 64) {
        throw "Console fixture must contain 1-64 commands; received $($commands.Count)."
    }

    for ($index = 0; $index -lt $commands.Count; $index++) {
        $command = $commands[$index]
        if ([string]::IsNullOrWhiteSpace($command)) {
            throw "Console fixture command $index is empty."
        }
        if ($command.Length -gt 256 -or $command.Contains([char]10) -or $command.Contains([char]13)) {
            throw "Console fixture command $index has an invalid format."
        }
        $verb = ($command.Trim() -split '\s+', 2)[0].ToLowerInvariant()
        if ($verb -notin $script:MultiplayerConsoleFixtureAllowedVerbs) {
            throw "Console fixture command $index uses disallowed verb '$verb'."
        }
    }

    return [ordered]@{
        Path = $path
        SchemaVersion = 1
        Name = $name
        WaitFor = 'local_playable_turn'
        Commands = $commands
    }
}
