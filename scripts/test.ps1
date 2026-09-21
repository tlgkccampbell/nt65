# The edit loop: builds the test project and runs the fast suite (units, fixtures, server).
# -Ca65 runs only the tests that assemble with the pinned ca65, the corpus programs among
# them. -Fixture runs only fixtures and corpus programs whose name contains the text. -Update
# rewrites expected fixture output instead of comparing it. -Benchmark builds Release and runs
# only the timings, which print what an edit costs; no gate runs them. -Thorough also compiles
# every fixture with its files reversed and shuffled, which the gate asks for and an edit need not.
[CmdletBinding()]
param(
    [switch]$Ca65,
    [string]$Fixture,
    [switch]$Update,
    [switch]$Thorough,
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
$exe = if ($IsWindows) { '.exe' } else { '' }
$runner = Join-Path $root "tests/Norristown.Tests/bin/$configuration/net10.0/Norristown.Tests$exe"

if ($Benchmark) {
    # The test project asks for the server collector, which the suite wants and an editor does
    # not: the timings are what an edit costs in the shipped binaries, so they are taken with the
    # collector those binaries use.
    $savedGc = $env:DOTNET_gcServer
    try {
        $env:DOTNET_gcServer = '0'
        & $runner -noLogo -trait 'Category=Benchmark' -parallel none -showLiveOutput
        exit $LASTEXITCODE
    }
    finally {
        $env:DOTNET_gcServer = $savedGc
    }
}

# Not -stopOnFail: xUnit then exits as if cancelled (Ctrl+C status), which upsets the calling
# shell. The gate stops at the first failing step, which is the fail-fast that matters.
#
# -parallelMode all runs the tests of a class beside each other rather than one after another,
# which is what the two longest classes want: a replay of hundreds of edits is a step at a time
# whatever else runs, and the three random-edit seeds are three replays that need not queue.
$runnerArgs = @('-noLogo', '-parallelMode', 'all')
$runnerArgs += if ($Ca65) { '-trait', 'Category=Oracle' } else { '-trait-', 'Category=Oracle', '-trait-', 'Category=Benchmark' }
if ($Fixture -and -not $Ca65) { $runnerArgs += '-class', 'Norristown.Tests.Fixtures.FixtureTests' }

# Tier 0 is instrumented so that tier 1 can be told what the program did, and a suite that is
# over in three seconds never calls anything often enough to be paid back: the instrumented code
# is what most of the run executes. The benchmarks above keep it, because the shipped binaries do.
$saved = $env:NT65_FIXTURE, $env:NT65_UPDATE, $env:NT65_THOROUGH, $env:DOTNET_TieredPGO
try {
    $env:DOTNET_TieredPGO = '0'
    $env:NT65_FIXTURE = $Fixture
    $env:NT65_UPDATE = if ($Update) { '1' } else { '' }
    $env:NT65_THOROUGH = if ($Thorough) { '1' } else { '' }
    & $runner @runnerArgs
    exit $LASTEXITCODE
}
finally {
    $env:NT65_FIXTURE, $env:NT65_UPDATE, $env:NT65_THOROUGH, $env:DOTNET_TieredPGO = $saved
}
