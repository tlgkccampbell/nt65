# The edit loop: builds the test project and runs the fast suite (units, fixtures, server).
# -Ca65 runs only the tests that assemble with the pinned ca65. -Fixture runs only fixtures
# whose name contains the text. -Update rewrites expected fixture output instead of
# comparing it. -Benchmark builds Release and runs only the timings, which print what an edit
# costs; no gate runs them.
[CmdletBinding()]
param(
    [switch]$Ca65,
    [string]$Fixture,
    [switch]$Update,
    [switch]$Benchmark,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$configuration = if ($Benchmark) { 'Release' } else { 'Debug' }
if (-not $NoBuild) {
    dotnet build (Join-Path $root 'tests/Norristown.Tests/Norristown.Tests.csproj') --nologo -v q -clp:NoSummary -c $configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$runner = Join-Path $root "tests/Norristown.Tests/bin/$configuration/net10.0/Norristown.Tests.exe"

if ($Benchmark) {
    & $runner -noLogo -trait 'Category=Benchmark' -parallel none -showLiveOutput
    exit $LASTEXITCODE
}

# Not -stopOnFail: xUnit then exits as if cancelled (Ctrl+C status), which upsets the calling
# shell. The gate stops at the first failing step, which is the fail-fast that matters.
$runnerArgs = @('-noLogo')
$runnerArgs += if ($Ca65) { '-trait', 'Category=Oracle' } else { '-trait-', 'Category=Oracle', '-trait-', 'Category=Benchmark' }
if ($Fixture -and -not $Ca65) { $runnerArgs += '-class', 'Norristown.Tests.Fixtures.FixtureTests' }

$saved = $env:NT65_FIXTURE, $env:NT65_UPDATE
try {
    $env:NT65_FIXTURE = $Fixture
    $env:NT65_UPDATE = if ($Update) { '1' } else { '' }
    & $runner @runnerArgs
    exit $LASTEXITCODE
}
finally {
    $env:NT65_FIXTURE, $env:NT65_UPDATE = $saved
}
