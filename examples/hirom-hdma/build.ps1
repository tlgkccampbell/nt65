# Builds the demo into build/hirom-hdma.sfc, with nt65, ca65 and ld65 from the path or from the
# paths given, and writes the header's checksum. Writes a debug file beside the image that points
# at the .nt65 sources, and a label file, which test.ps1 reads. Exits 1 if any step fails.
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

# Writes a Super NES header's checksum at an offset in an image, after its complement. The
# checksum is the sum of every byte of the image, taken with the complement $FFFF and the checksum
# 0, whose four bytes add up to what any complement and checksum do.
function Set-Checksum([string]$image, [int]$at) {
    $bytes = [IO.File]::ReadAllBytes($image)
    $bytes[$at], $bytes[$at + 1], $bytes[$at + 2], $bytes[$at + 3] = 0xFF, 0xFF, 0, 0
    $sum = [Linq.Enumerable]::Sum([int[]]$bytes) -band 0xFFFF
    $bytes[$at], $bytes[$at + 1] = (($sum -bxor 0xFFFF) -band 0xFF), (($sum -bxor 0xFFFF) -shr 8)
    $bytes[$at + 2], $bytes[$at + 3] = ($sum -band 0xFF), ($sum -shr 8)
    [IO.File]::WriteAllBytes($image, $bytes)
}

function Run([string]$exe, [string[]]$arguments) {
    $said = (& $exe @arguments 2>&1 | Out-String).Trim()
    if ($said) { Write-Host $said }
    if ($LASTEXITCODE -ne 0) { throw "$(Split-Path -Leaf $exe) failed" }
}

Push-Location $PSScriptRoot
try {
    if (Test-Path 'build') { Remove-Item 'build' -Recurse -Force }
    Run $Nt65 @('build')
    $objects = foreach ($source in Get-ChildItem 'build' -Filter *.s -Recurse | Sort-Object FullName) {
        $object = [IO.Path]::ChangeExtension($source.FullName, '.o')
        Run $Ca65 @('-g', $source.FullName, '-o', $object)
        $object
    }
    Run $Ld65 (@('-C', 'hirom.cfg', '-o', 'build/hirom-hdma.sfc', '-m', 'build/hirom-hdma.map',
                 '-Ln', 'build/hirom-hdma.lbl', '--dbgfile', 'build/hirom-hdma.dbg') + $objects)
    Run $Nt65 @('remap-dbg', 'build/hirom-hdma.dbg')
    # The header is at $00FFC0, which is $FFC0 in the image, and its complement at $FFDC.
    Set-Checksum (Join-Path $PSScriptRoot 'build/hirom-hdma.sfc') 0xFFDC
    Write-Host ('hirom-hdma {0,7:N0} bytes in build/hirom-hdma.sfc' -f (Get-Item 'build/hirom-hdma.sfc').Length)
}
catch {
    Write-Host "the demo failed to build: $_" -ForegroundColor Red
    exit 1
}
finally {
    Pop-Location
}
