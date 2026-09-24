# Builds the monitor for every platform, or the ones named, into build/<platform>, with nt65,
# ca65 and ld65 from the path or from the paths given. Exits 1 if any build fails.
[CmdletBinding()]
param(
    [string[]]$Platform = @('c64', 'apple2gs'),
    [string]$Nt65 = 'nt65',
    [string]$Ca65 = 'ca65',
    [string]$Ld65 = 'ld65'
)

$ErrorActionPreference = 'Stop'
# A tool given as a path is found from where the script was run, not from here.
$Nt65, $Ca65, $Ld65 = foreach ($tool in $Nt65, $Ca65, $Ld65) {
    if (Test-Path $tool -PathType Leaf) { (Resolve-Path $tool).Path } else { (Get-Command $tool).Source }
}

# What each platform links into: its linker configuration and the image it writes.
$platforms = @{
    c64      = @{ Config = 'c64.cfg'; Image = 'monitor.prg' }
    apple2gs = @{ Config = 'apple2gs.cfg'; Image = 'monitor.bin' }
}

function Run([string]$exe, [string[]]$arguments) {
    $said = (& $exe @arguments 2>&1 | Out-String).Trim()
    if ($said) { Write-Host $said }
    if ($LASTEXITCODE -ne 0) { throw "$(Split-Path -Leaf $exe) failed" }
}

$failed = $false
foreach ($p in $Platform) {
    $settings = $platforms[$p]
    if (-not $settings) { throw "no platform `"$p`": the platforms are $($platforms.Keys -join ', ')" }
    # Each platform is a project of its own, whose files are the monitor's and its own.
    Push-Location (Join-Path $PSScriptRoot $p)
    try {
        $out = Join-Path $PSScriptRoot "build/$p"
        if (Test-Path $out) { Remove-Item $out -Recurse -Force }
        Run $Nt65 @('build')
        $objects = foreach ($source in Get-ChildItem $out -Filter *.s -Recurse | Sort-Object FullName) {
            $object = [IO.Path]::ChangeExtension($source.FullName, '.o')
            Run $Ca65 @('-g', $source.FullName, '-o', $object)
            $object
        }
        $image = Join-Path $out $settings.Image
        $stem = [IO.Path]::ChangeExtension($image, $null).TrimEnd('.')
        # The label file is VICE's, for its monitor; the debug file points at the .nt65 sources.
        Run $Ld65 (@('-C', $settings.Config, '-o', $image, '-m', "$stem.map", '-Ln', "$stem.lbl",
                     '--dbgfile', "$stem.dbg") + $objects)
        Run $Nt65 @('remap-dbg', "$stem.dbg")
        Write-Host ('{0,-8} {1,6:N0} bytes in build/{0}/{2}' -f $p, (Get-Item $image).Length, $settings.Image)
    }
    catch {
        Write-Host "$p failed: $_" -ForegroundColor Red
        $failed = $true
    }
    finally {
        Pop-Location
    }
}
if ($failed) { exit 1 }
