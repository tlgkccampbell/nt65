# Converts the pictures in tilesets/ and the sounds in audio/ into build/assets with the
# Python tools beside this script: the tiles in the 2-bit and 4-bit formats the S-PPU reads,
# and the samples as BRR, the S-DSP's format. build.ps1 runs it before nt65, which measures
# every file an .incbin names. Needs Python 3 and Pillow, found on the path or given.
[CmdletBinding()]
param([string]$Python)

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
$example = Split-Path -Parent $PSScriptRoot
$tools = $PSScriptRoot
$assets = Join-Path $example 'build/assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function Run([string[]]$arguments) {
    & $Python @arguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# .chrgb denotes the 2-bit tile format used by Game Boy and Game Boy Color, as well as Super
# NES mode 0 (all planes), mode 1 (third plane), and modes 4 and 5 (second plane). .chrsfc is
# the 4-bit format sprites use in every mode.
Run @("$tools/pilbmp2nes.py", '--planes=0,1', "$example/tilesets/bggfx.png", "$assets/bggfx.chrgb")
Run @("$tools/pilbmp2nes.py", '--planes=0,1;2,3', "$example/tilesets/swinging2.png", "$assets/swinging2.chrsfc")

# Two of the samples are synthesized: a plucked string and a hi-hat.
Run @("$tools/karplus.py", '-o', "$assets/karplusbass.wav", '-n', '1024', '-p', '64', '-r', '4186', '-e', 'square:1:4', '-a', '30000', '-g', '1.0', '-f', '.5')
Run @("$tools/makehat.py", "$assets/hat.wav")
Run @("$tools/wav2brr.py", '--loop', "$assets/karplusbass.wav", "$assets/karplusbassloop.brr")
Run @("$tools/wav2brr.py", "$assets/hat.wav", "$assets/hat.brr")
Run @("$tools/wav2brr.py", "$example/audio/kickgen.wav", "$assets/kickgen.brr")
Run @("$tools/wav2brr.py", "$example/audio/decentsnare.wav", "$assets/decentsnare.brr")
