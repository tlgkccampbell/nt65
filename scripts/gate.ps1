# The gate, once per stage or unit of work: the pinned cc65 build, the whole solution
# (warnings are errors), the fast suite, the ca65 oracle suite and the VS Code client.
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
Step 'vscode client' { node --check (Join-Path $root 'editors/vscode/extension.js') }

Write-Host ("gate passed in {0:0.0}s" -f $total.Elapsed.TotalSeconds) -ForegroundColor Green
