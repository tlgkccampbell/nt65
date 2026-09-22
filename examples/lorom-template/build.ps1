# Builds build/lorom-template.sfc and build/lorom-template.spc with nt65, ca65 and ld65 from
# the path, or from the paths given, and Python 3 with Pillow for the asset tools, as `py`,
# `python3` or `python` or given.
[CmdletBinding()]
param(
    [string]$Nt65 = 'nt65',
    [string]$Ca65 = 'ca65',
    [string]$Ld65 = 'ld65',
    [string]$Python
)

$ErrorActionPreference = 'Stop'
# Python 3 with Pillow: the first of the launcher, python3 and python that can import it,
# since a machine may carry several Pythons and only one with Pillow installed.
if (-not $Python) {
    foreach ($candidate in 'py', 'python3', 'python') {
        if (Get-Command $candidate -ErrorAction SilentlyContinue) {
            & $candidate -c 'import PIL' 2>$null
            if ($LASTEXITCODE -eq 0) { $Python = $candidate; break }
        }
    }
    if (-not $Python) { Write-Error 'no Python with Pillow found: give one with -Python'; exit 1 }
}

# A tool given as a path is found from where the script was run, not from here.
$Nt65, $Ca65, $Ld65, $Python = foreach ($tool in $Nt65, $Ca65, $Ld65, $Python) {
    if (Test-Path $tool -PathType Leaf) { (Resolve-Path $tool).Path } else { $tool }
}

Push-Location $PSScriptRoot
try {
    # The tiles and samples the sources include, converted from tilesets/ and audio/. nt65
    # reads every file an .incbin names for its length, so this comes first.
    & ./tools/convert.ps1 -Python $Python
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $Nt65 build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    foreach ($source in Get-ChildItem build -Filter *.s) {
        & $Ca65 -g $source.FullName -o ([IO.Path]::ChangeExtension($source.FullName, '.o'))
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    # The sound driver and the music are linked into the last bank for spc_boot_apu to upload.
    $objects = @(
        'build/header.o', 'build/init.o', 'build/main.o', 'build/bg.o', 'build/player.o'
        'build/ppuclear.o', 'build/blarggapu.o', 'build/spcimage.o', 'build/musicseq.o'
    )
    & $Ld65 -C lorom256k.cfg -o build/lorom-template.sfc --dbgfile build/lorom-template.dbg -m build/lorom-template.map @objects
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    # The .s files carry no debug directives; this puts what the .s.lines maps beside them say
    # into the debug file, so an emulator shows the .nt65 source.
    & $Nt65 remap-dbg build/lorom-template.dbg
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    # The internal header's checksum, which only the linked image can supply.
    & $Python tools/fixchecksum.py build/lorom-template.sfc
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    # The same driver as an SPC700 state file, for music players.
    New-Item -ItemType Directory -Force build/spcfile | Out-Null
    & $Ca65 -g spcfile/spcheader.s -o build/spcfile/spcheader.o
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $Ld65 -C spcfile/spc.cfg -o build/lorom-template.spc -m build/lorom-template.spc.map build/spcfile/spcheader.o build/spcimage.o build/musicseq.o
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}
