# Builds the demo into build/demo.prg, with nt65, ca65 and ld65 from the path or from the paths
# given: packs the screens, which nt65 measures, then compiles, assembles and links. Writes a
# debug file beside the program that points at the .nt65 sources, and a label file for VICE's
# monitor. Exits 1 if any step fails.
[CmdletBinding()]
param(
    [string]$Nt65 = 'nt65',
    [string]$Ca65 = 'ca65',
    [string]$Ld65 = 'ld65'
)

$ErrorActionPreference = 'Stop'
# A tool given as a path is found from where the script was run, not from here.
$Nt65, $Ca65, $Ld65 = foreach ($tool in $Nt65, $Ca65, $Ld65) {
    if (Test-Path $tool -PathType Leaf) { (Resolve-Path $tool).Path } else { (Get-Command $tool).Source }
}

function Run([string]$exe, [string[]]$arguments) {
    $said = (& $exe @arguments 2>&1 | Out-String).Trim()
    if ($said) { Write-Host $said }
    if ($LASTEXITCODE -ne 0) { throw "$(Split-Path -Leaf $exe) failed" }
}

Push-Location $PSScriptRoot
try {
    if (Test-Path 'build') { Remove-Item 'build' -Recurse -Force }
    & (Join-Path $PSScriptRoot 'tools/pack.ps1')
    Run $Nt65 @('build')
    $objects = foreach ($source in Get-ChildItem 'build' -Filter *.s -Recurse | Sort-Object FullName) {
        $object = [IO.Path]::ChangeExtension($source.FullName, '.o')
        Run $Ca65 @('-g', $source.FullName, '-o', $object)
        $object
    }
    Run $Ld65 (@('-C', 'c64.cfg', '-o', 'build/demo.prg', '-m', 'build/demo.map', '--dbgfile', 'build/demo.dbg') + $objects)
    Run $Nt65 @('remap-dbg', 'build/demo.dbg', '--labels', 'build/demo.lbl')
    Write-Host ('demo {0,6:N0} bytes in build/demo.prg' -f (Get-Item 'build/demo.prg').Length)
}
catch {
    Write-Host "the demo failed to build: $_" -ForegroundColor Red
    exit 1
}
finally {
    Pop-Location
}
