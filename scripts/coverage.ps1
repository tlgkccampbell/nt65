# Measures and prints the line coverage of the fast suite; nothing fails on the result. The
# gate does not run this: the profiler makes the suite several times slower, and nothing here
# decides whether a change is good. CI runs it and puts the table in the job summary.
#   pwsh scripts/coverage.ps1 [-Types 12]
[CmdletBinding()]
param([int]$Types = 12)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path -Parent $PSScriptRoot
$report = Join-Path $root 'artifacts/coverage.cobertura.xml'

New-Item -ItemType Directory -Force (Split-Path $report) | Out-Null
dotnet build (Join-Path $root 'tests/Norristown.Tests/Norristown.Tests.csproj') --nologo -v q -clp:NoSummary
dotnet tool restore

# The suite is run through test.ps1, as the edit loop runs it, so both cover the same set of
# tests; collection follows child processes, so it includes the test runner that pwsh starts.
dotnet dotnet-coverage collect --output $report --output-format cobertura -- `
    pwsh -NoProfile (Join-Path $PSScriptRoot 'test.ps1') -NoBuild

# Gather each type's lines. The report writes a class entry for each lambda and nested class as
# well as for the type itself, so those are folded into the type, and a line number is counted
# once however many entries list it. The report names the assembly as the package, and each
# type by its full name, which begins with the namespace Norristown.
$covered = @{}
$total = @{}
$core = ([xml](Get-Content $report -Raw)).SelectSingleNode("//package[@name='Norristown.Core']")
foreach ($class in $core.SelectNodes('.//class')) {
    $name = ($class.name -split '[/+]')[0]
    if ($name -match '^(.*?)\.<') { $name = $Matches[1] }
    foreach ($line in $class.SelectNodes('.//line')) {
        $key = "$name#$($line.number)"
        $total[$key] = $name
        if ($line.hits -ne '0') { $covered[$key] = $true }
    }
}

$rows = $total.GetEnumerator() | Group-Object { $_.Value } | ForEach-Object {
    $lines = $_.Count
    $hit = @($_.Group | Where-Object { $covered.ContainsKey($_.Key) }).Count
    [pscustomobject]@{
        Type = $_.Name -replace '^Norristown\.', ''
        Lines = $lines
        Covered = $hit
        Percent = if ($lines) { 100.0 * $hit / $lines } else { 0 }
    }
} | Sort-Object Lines -Descending | Select-Object -First $Types

$table = @("| type | lines | covered | % |", "|---|--:|--:|--:|")
$table += $rows | ForEach-Object { "| {0} | {1} | {2} | {3:0.0} |" -f $_.Type, $_.Lines, $_.Covered, $_.Percent }
$table += ""
$table += "The {0} largest types of Norristown.Core, as the fast suite reaches them." -f $rows.Count

$table | Write-Host
if ($env:GITHUB_STEP_SUMMARY) {
    @("## Coverage", "") + $table | Add-Content $env:GITHUB_STEP_SUMMARY
}
