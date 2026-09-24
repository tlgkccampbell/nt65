# Runs the monitor for a platform in its emulator, to try it by hand. Build first with build.ps1.
# The C64's runs in VICE, with the label file for VICE's monitor. The IIGS's runs in MAME, which
# boots with no disk and then loads the file as BRUN would; `X` goes to BASIC.SYSTEM, which is
# not there, so close MAME instead.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('c64', 'apple2gs')][string]$Platform,
    [string]$Vice,
    [string]$Mame
)

$ErrorActionPreference = 'Stop'

# An emulator from the path, or from the folder or program given.
function Emulator([string]$name, [string]$given) {
    if (-not $given) { return (Get-Command $name).Source }
    if (Test-Path $given -PathType Container) { return (Get-ChildItem $given -Filter "$name*.exe" -Recurse | Select-Object -First 1).FullName }
    return (Resolve-Path $given).Path
}

$build = Join-Path $PSScriptRoot "build/$Platform"
if ($Platform -eq 'c64') {
    $image = Join-Path $build 'monitor.prg'
    if (-not (Test-Path $image)) { throw "c64 is not built: run build.ps1 first" }
    & (Emulator 'x64sc' $Vice) -autostart $image -moncommands (Join-Path $build 'monitor.lbl')
    return
}

$image = Join-Path $build 'monitor.bin'
if (-not (Test-Path $image)) { throw "apple2gs is not built: run build.ps1 first" }
$mame = Emulator 'mame' $Mame
$script = Join-Path $build 'run.lua'
@(
    "session = { image = [[$image]], load = 0x2000 }"
    # MAME runs the script in an environment of its own, which the other has to share.
    "assert(loadfile([[$(Join-Path $PSScriptRoot 'mame-session.lua')]], 't', _ENV))()"
) | Set-Content $script
# The ROMs are looked for beside MAME, and what MAME writes of its own goes in the build folder.
Start-Process $mame -WorkingDirectory $build -ArgumentList @(
    'apple2gs', '-rompath', "`"$(Join-Path (Split-Path $mame) 'roms')`"", '-window', '-skip_gameinfo',
    '-autoboot_script', "`"$script`"")
