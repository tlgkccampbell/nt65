# Builds build/hello.sfc with nt65, ca65 and ld65 from the path, or from the paths given.
[CmdletBinding()]
param(
    [string]$Nt65 = 'nt65',
    [string]$Ca65 = 'ca65',
    [string]$Ld65 = 'ld65'
)

$ErrorActionPreference = 'Stop'

# A tool given as a path is found from where the script was run, not from here.
$Nt65, $Ca65, $Ld65 = foreach ($tool in $Nt65, $Ca65, $Ld65) {
    if (Test-Path $tool -PathType Leaf) { (Resolve-Path $tool).Path } else { $tool }
}

Push-Location $PSScriptRoot
try {
    & $Nt65 build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    foreach ($source in Get-ChildItem build -Filter *.s) {
        & $Ca65 -g $source.FullName -o ([IO.Path]::ChangeExtension($source.FullName, '.o'))
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    $objects = (Get-ChildItem build -Filter *.o).FullName
    & $Ld65 -C snes.cfg -o build/hello.sfc --dbgfile build/hello.dbg @objects
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    # The .s files carry no debug directives; this puts what the .s.lines maps beside them say
    # into the debug file, so an emulator shows the .nt65 source.
    & $Nt65 remap-dbg build/hello.dbg
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}
