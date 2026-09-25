# The gate, once per stage or unit of work: the pinned cc65 build, the whole solution
# (warnings are errors), the fast suite, the ca65 oracle suite, the corpus programs built
# end to end with the command line, and the VS Code client. The corpus builds run beside the
# two suites.
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

# The corpus builds need only the command line built above, so they run in a process of their
# own beside the tests and the oracle. Their output is held back and printed in the gate's order
# once they finish, and a gate that stops early stops them too.
$held = Join-Path ([IO.Path]::GetTempPath()) "nt65-gate-$PID"
$corpus = Start-Process (Get-Process -Id $PID).Path -PassThru -NoNewWindow `
    -ArgumentList '-NoProfile', '-File', (Join-Path $PSScriptRoot 'corpus.ps1') `
    -RedirectStandardOutput "$held.out" -RedirectStandardError "$held.err"
try {
    Step 'tests' { & (Join-Path $PSScriptRoot 'test.ps1') -NoBuild -Thorough }
    Step 'ca65 oracle' { & (Join-Path $PSScriptRoot 'test.ps1') -NoBuild -Ca65 }
    Step 'corpus builds' {
        $corpus.WaitForExit()
        Get-Content "$held.out", "$held.err" -ErrorAction SilentlyContinue | Write-Host
        $global:LASTEXITCODE = $corpus.ExitCode
    }
}
finally {
    if (-not $corpus.HasExited) { $corpus.Kill($true) }
    Remove-Item "$held.out", "$held.err" -ErrorAction SilentlyContinue
}
Step 'vscode client' {
    foreach ($file in @('extension.js', 'views.js')) {
        node --check (Join-Path $root "editors/vscode/$file")
        if ($LASTEXITCODE -ne 0) { return }
    }
}

Write-Host ("gate passed in {0:0.0}s" -f $total.Elapsed.TotalSeconds) -ForegroundColor Green
