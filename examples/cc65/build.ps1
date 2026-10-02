# Builds the program into build/cc65.sim with nt65 and cl65, and runs it in sim65 unless
# -NoRun is given. The tools come from the path or from the paths given. Exits with sim65's
# exit code, which is the number of checks that failed, or 1 if the build fails.
[CmdletBinding()]
param(
    [string]$Nt65 = 'nt65',
    [string]$Cl65 = 'cl65',
    [string]$Sim65 = 'sim65',
    [switch]$NoRun
)

$ErrorActionPreference = 'Stop'
# A tool given as a path is found from where the script was run, not from here.
$Nt65, $Cl65, $Sim65 = foreach ($tool in $Nt65, $Cl65, $Sim65) {
    if (Test-Path $tool -PathType Leaf) { (Resolve-Path $tool).Path } else { (Get-Command $tool).Source }
}

# Runs a tool, printing what it says, and stops the build if it fails or warns.
function Run([string]$exe, [string[]]$arguments) {
    $said = (& $exe @arguments 2>&1 | Out-String).Trim()
    if ($said) { Write-Host $said }
    if ($LASTEXITCODE -ne 0 -or $said) {
        Write-Host ("$(Split-Path -Leaf $exe) " + ($arguments -join ' ')) -ForegroundColor Red
        exit 1
    }
}

Push-Location $PSScriptRoot
try {
    if (Test-Path build) { Remove-Item build -Recurse -Force }
    Run $Nt65 @('build', '--c-header', 'build/gen/nt65.h')

    # nt65's output, the hand-written ca65 and the C are each turned into an object file in
    # build/obj, and the C is compiled against the header nt65 has just written.
    New-Item -ItemType Directory -Force build/obj | Out-Null
    $sources = @(Get-ChildItem build/gen, asm -Filter *.s) + @(Get-ChildItem c -Filter *.c)
    $objects = foreach ($source in $sources | Sort-Object FullName) {
        $object = "build/obj/$($source.BaseName).o"
        Run $Cl65 @('-t', 'sim6502', '-c', '-g', '-O', '-I', 'build/gen', '-o', $object, $source.FullName)
        $object
    }

    # The debug file points at the .nt65 sources once remap-dbg has read the .lines files.
    Run $Cl65 (@('-t', 'sim6502', '-o', 'build/cc65.sim', '-m', 'build/cc65.map',
                 '-Wl', '--dbgfile,build/cc65.dbg') + $objects)
    Run $Nt65 @('remap-dbg', 'build/cc65.dbg')

    if ($NoRun) { exit 0 }
    # The cycle limit stops a program that hangs; the whole run takes far fewer.
    & $Sim65 -x 10000000 build/cc65.sim
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
