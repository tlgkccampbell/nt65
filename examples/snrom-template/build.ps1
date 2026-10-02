# Builds build/snrom-template.nes for SGROM and SNROM, with the MMC1 driver, and
# build/uorom-template.nes for UNROM and UOROM, with the UNROM driver, with nt65, ca65 and ld65
# from the path, or from the paths given, and Python 3 with Pillow for the tile converter, as `py`,
# `python3` or `python` or given. Compares both images with the SHA-256 in expected.sha256, and
# exits 1 if either differs; -Update writes the new ones there instead.
[CmdletBinding()]
param(
    [string]$Nt65 = 'nt65',
    [string]$Ca65 = 'ca65',
    [string]$Ld65 = 'ld65',
    [string]$Python,
    [switch]$Update
)

$ErrorActionPreference = 'Stop'
# A tool given as a path is found from where the script was run, not from here.
$Nt65, $Ca65, $Ld65 = foreach ($tool in $Nt65, $Ca65, $Ld65) {
    if (Test-Path $tool -PathType Leaf) { (Resolve-Path $tool).Path } else { $tool }
}
if ($Python -and (Test-Path $Python -PathType Leaf)) { $Python = (Resolve-Path $Python).Path }

function Run([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Each image: the configuration nt65 builds it with, the linker configuration, and the driver
# among the objects, which are linked in upstream's order because ld65 lays each segment out in
# the order of the objects that write to it.
$images = [ordered]@{
    'snrom-template.nes' = @{ Config = 'snrom'; Cfg = 'snrom2mbit.cfg'; Driver = 'mmc1' }
    'uorom-template.nes' = @{ Config = 'uorom'; Cfg = 'uorom2mbit.cfg'; Driver = 'unrom' }
}

Push-Location $PSScriptRoot
try {
    # The tiles chrram.nt65 includes, converted from tilesets/. nt65 reads every file an .incbin
    # names for its length, so this comes first.
    & ./tools/convert.ps1 -Python $Python
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $hashes = [ordered]@{}
    foreach ($image in $images.Keys) {
        $target = $images[$image]
        $out = "build/$($target.Config)"
        Run $Nt65 @('build', '--config', $target.Config)

        $objects = foreach ($module in 'main', 'init', 'bg', 'player', 'pads', 'ppuclear', $target.Driver, 'chrram', 'bankcalltable') {
            Run $Ca65 @('-g', "$out/$module.s", '-o', "$out/$module.o")
            "$out/$module.o"
        }
        $debug = [IO.Path]::ChangeExtension("build/$image", '.dbg')
        Run $Ld65 (@('-C', $target.Cfg, '-o', "build/$image", '--dbgfile', $debug, '-m', "$out/map.txt") + $objects)

        # The .s files carry no debug directives; this puts what the .s.lines maps beside them say
        # into the debug file, so an emulator shows the .nt65 source.
        Run $Nt65 @('remap-dbg', $debug)
        $hashes[$image] = (Get-FileHash "build/$image" -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    if ($Update) {
        $lines = $hashes.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value)`n" }
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot expected.sha256), -join $lines)
        Write-Host 'wrote expected.sha256'
        exit 0
    }
    $expected = @{}
    foreach ($line in Get-Content expected.sha256) {
        $name, $hash = $line -split ' '
        $expected[$name] = $hash
    }
    $failed = $false
    foreach ($image in $images.Keys) {
        if ($hashes[$image] -ne $expected[$image]) {
            Write-Host "$image differs from expected.sha256; run with -Update if the change is intended" -ForegroundColor Red
            $failed = $true
        } else {
            Write-Host ('{0,-18} {1,7:N0} bytes, as expected' -f $image, (Get-Item "build/$image").Length)
        }
    }
    if ($failed) { exit 1 }
}
finally {
    Pop-Location
}
