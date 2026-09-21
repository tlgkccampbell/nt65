# The gate, once per stage or unit of work: the pinned cc65 build, the whole solution
# (warnings are errors), the fast suite, the ca65 oracle suite, the corpus programs built
# end to end with the command line, and the VS Code client.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$total = [Diagnostics.Stopwatch]::StartNew()

function Step([string]$name, [scriptblock]$body) {
    Write-Host "== $name" -ForegroundColor Cyan
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & $body
    if ($LASTEXITCODE -ne 0) {
        Write-Host "gate FAILED at: $name" -ForegroundColor Red
        exit $LASTEXITCODE
    }
    Write-Host ("   {0} took {1:0.0}s" -f $name, $watch.Elapsed.TotalSeconds)
}

Step 'cc65' { & (Join-Path $PSScriptRoot 'build-cc65.ps1') }
Step 'build' { dotnet build (Join-Path $root 'Norristown.slnx') --nologo -v q -clp:NoSummary }
Step 'tests' { & (Join-Path $PSScriptRoot 'test.ps1') -NoBuild }
Step 'ca65 oracle' { & (Join-Path $PSScriptRoot 'test.ps1') -NoBuild -Ca65 }
Step 'corpus builds' { & (Join-Path $PSScriptRoot 'corpus.ps1') }
Step 'vscode client' {
    foreach ($file in @('extension.js', 'views.js')) {
        node --check (Join-Path $root "editors/vscode/$file")
        if ($LASTEXITCODE -ne 0) { return }
    }
}

Write-Host ("gate passed in {0:0.0}s" -f $total.Elapsed.TotalSeconds) -ForegroundColor Green
