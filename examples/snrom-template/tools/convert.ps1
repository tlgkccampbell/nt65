# Converts the pictures in tilesets/ into build/assets with the Python tool beside this script,
# as tiles in the 2-bit format the NES's PPU reads. build.ps1 runs it before nt65, which measures
# every file an .incbin names. Needs Python 3 and Pillow, found on the path or given.
[CmdletBinding()]
param([string]$Python)

$ErrorActionPreference = 'Stop'
# Python 3 with Pillow: the first of the launcher, python3 and python that can import it,
# since a machine may carry several Pythons and only one with Pillow installed. The one found is
# named by its own path, because the launcher runs a script with the Python its `#!` line names,
# which need not be the one that passed the check.
if (-not $Python) {
    foreach ($candidate in 'py', 'python3', 'python') {
        if (Get-Command $candidate -ErrorAction SilentlyContinue) {
            $found = & $candidate -c 'import PIL, sys; print(sys.executable)' 2>$null
            if ($LASTEXITCODE -eq 0) { $Python = $found; break }
        }
    }
    if (-not $Python) { Write-Error 'no Python with Pillow found: give one with -Python'; exit 1 }
}
$example = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $example 'build/assets'
New-Item -ItemType Directory -Force $assets | Out-Null

# The background tiles and the sprite tiles, which load_chr_ram_far copies into CHR RAM one after
# the other.
foreach ($name in 'bggfx', 'spritegfx') {
    & $Python -W ignore::DeprecationWarning "$PSScriptRoot/pilbmp2nes.py" "$example/tilesets/$name.png" "$assets/$name.chr"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
