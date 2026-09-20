# Writes the syntax classes the node table describes: the red node classes under
# Syntax/Nodes/Generated, the kind-to-class switch and the visitors. The generator is part of
# the test project, so this builds that and runs the one test that writes instead of comparing;
# `scripts/test.ps1` is what tells you afterwards that nothing else broke.
[CmdletBinding()]
param(
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $NoBuild) {
    dotnet build (Join-Path $root 'tests/Norristown.Tests/Norristown.Tests.csproj') --nologo -v q -clp:NoSummary
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$runner = Join-Path $root 'tests/Norristown.Tests/bin/Debug/net10.0/Norristown.Tests.exe'

$saved = $env:NT65_SYNTAX_UPDATE
try {
    $env:NT65_SYNTAX_UPDATE = '1'
    & $runner -noLogo -method 'Norristown.Tests.Syntax.GeneratedSyntaxTests.GeneratedFilesMatchTheTable'
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    $env:NT65_SYNTAX_UPDATE = $saved
}

Write-Host 'the generated syntax is up to date; build and run scripts/test.ps1 next' -ForegroundColor Green
