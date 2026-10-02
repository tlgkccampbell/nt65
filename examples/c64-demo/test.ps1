# Runs the demo in VICE for a number of frames and checks what it left in memory. Build first
# with build.ps1. VICE stops the demo at the top of its main loop once the frames have passed,
# saves the whole of memory and a screenshot, build/test/demo.png, and leaves through its debug
# cartridge. The script then checks the following:
#
#   screen     screen memory is screens/title.txt, as the decruncher unpacked it
#   colours    colour memory is screens/colours.txt, likewise
#   sorted     plex::order holds each object once, from the top of the screen down
#   stable     raster::stable::steady ran on the same cycle of its line in every frame
#   state      the frame count, the music's place, the SID's registers and the objects are as
#              tests/expected.txt has them
#
# -Update writes tests/expected.txt from the run instead of checking it, which is then read and
# checked by hand. -Vice names VICE's x64sc when it is not on the path. Exits 1 if a check fails
# or VICE does not finish.
[CmdletBinding()]
param(
    [int]$Frames = 100,
    [string]$Vice,
    [switch]$Update
)

$ErrorActionPreference = 'Stop'
$build = Join-Path $PSScriptRoot 'build'
$image = Join-Path $build 'demo.prg'
if (-not (Test-Path $image)) { throw 'the demo is not built: run build.ps1 first' }
$emulator = if (-not $Vice) { (Get-Command x64sc).Source }
            elseif (Test-Path $Vice -PathType Container) { (Get-ChildItem $Vice -Filter 'x64sc*.exe' -Recurse | Select-Object -First 1).FullName }
            else { (Resolve-Path $Vice).Path }

. (Join-Path $PSScriptRoot '../labels.ps1')
$labels = Read-Labels (Join-Path $build 'demo.lbl')

# The cycle of its line on which `stable` reaches `steady`, in VICE's count of a line's cycles:
# `stable` starts on cycle 38 or 39, and the read of the raster makes both reach `steady` 28
# cycles after 38, on cycle 3 of the next line.
$steady = 3

$work = Join-Path $build 'test'
New-Item -ItemType Directory -Force $work | Out-Null
$memory = Join-Path $work 'memory.bin'
$shot = Join-Path $work 'demo.png'
Remove-Item $memory, $shot -ErrorAction SilentlyContinue

# Checkpoints 1 to 3 save memory and the screen at the top of the main loop after `$Frames`
# frames, and leave once they have. Checkpoint 4 leaves with 2 if `steady` is ever off its cycle.
$loop = '{0:x4}' -f (Address $labels 'main::loop')
$script = Join-Path $work 'demo.mon'
@(
    "tr exec `$$loop"
    "ignore 1 $('{0:x}' -f $Frames)"
    "command 1 `"bsave \`"$($memory.Replace('\', '/'))\`" 0 0000 ffff`""
    "tr exec `$$loop"
    "ignore 2 $('{0:x}' -f $Frames)"
    "command 2 `"screenshot \`"$($shot.Replace('\', '/'))\`" 2`""
    "tr exec `$$loop"
    "ignore 3 $('{0:x}' -f ($Frames + 1))"
    'command 3 "> d7ff 00"'
    "tr exec `$$('{0:x4}' -f (Address $labels 'raster::stable::steady')) if CY != `$$('{0:x2}' -f $steady)"
    'command 4 "> d7ff 02"'
) | Set-Content $script

# The autostart's random delay is off, so that every run is the same run.
$arguments = @('-default', '-console', '-warp', '+sound', '-debugcart', '+autostart-delay-random',
               '-limitcycles', '20000000', '-autostartprgmode', '1', '-moncommands', $script, '-autostart', $image)
$run = Start-Process $emulator -ArgumentList $arguments -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $work 'vice.log')
$run.WaitForExit()

$failed = @()
if ($run.ExitCode -eq 2) { $failed += 'stable: raster::stable::steady ran on another cycle of its line' }
elseif ($run.ExitCode -ne 0 -or -not (Test-Path $memory)) {
    Write-Host "VICE did not finish: see build/test/vice.log" -ForegroundColor Red
    exit 1
}
$ram = [IO.File]::ReadAllBytes($memory)

# Returns the bytes from an address, `$count` of them.
function Bytes([int]$address, [int]$count) { [byte[]]$ram[$address..($address + $count - 1)] }

# The screen and its colours, against what the packer packed. Colour memory has four bits.
$title = [IO.File]::ReadAllBytes((Join-Path $build 'screens/title.bin'))
if (Compare-Object $title (Bytes 0x0400 1000) -SyncWindow 0) { $failed += 'screen: screen memory is not screens/title.txt' }
$colours = [IO.File]::ReadAllBytes((Join-Path $build 'screens/colours.bin'))
if (Compare-Object $colours (Bytes 0xD800 1000 | ForEach-Object { $_ -band 0x0F }) -SyncWindow 0) {
    $failed += 'colours: colour memory is not screens/colours.txt'
}

# The order the multiplexer shows the objects in, each once and from the top down.
$order = Bytes (Address $labels 'plex::order') 16
$ypos = Bytes (Address $labels 'plex::objects::ypos') 16
$sorted = (($order | Sort-Object) -join ',') -eq ((0..15) -join ',')
for ($i = 1; $sorted -and $i -lt 16; $i++) { $sorted = $ypos[$order[$i - 1]] -le $ypos[$order[$i]] }
if (-not $sorted) { $failed += "sorted: plex::order is $($order -join ' ') for Y $($ypos -join ' ')" }

# What the run leaves, a line to each part, as hex bytes.
$state = [ordered]@{
    'raster::frames'      = Bytes (Address $labels 'raster::frames') 1
    'music::step'         = Bytes (Address $labels 'music::step') 1
    'music::tick'         = Bytes (Address $labels 'music::tick') 1
    'sid'                 = Bytes 0xD400 25
    'plex::order'         = $order
    'plex::objects::xpos' = Bytes (Address $labels 'plex::objects::xpos') 16
    'plex::objects::xhigh' = Bytes (Address $labels 'plex::objects::xhigh') 16
    'plex::objects::ypos' = $ypos
}
$lines = @("; What the demo holds after $Frames frames, at the top of its main loop. test.ps1 -Update writes it.")
$lines += foreach ($part in $state.Keys) { '{0,-21} {1}' -f $part, (($state[$part] | ForEach-Object { '{0:X2}' -f $_ }) -join ' ') }
$expected = Join-Path $PSScriptRoot 'tests/expected.txt'
if ($Update) {
    [IO.File]::WriteAllText($expected, (($lines -join "`n") + "`n"))
    Write-Host 'tests/expected.txt written'
} elseif ($difference = Compare-Object @(Get-Content $expected) $lines -SyncWindow 0) {
    $failed += 'state: memory differs from tests/expected.txt:'
    $failed += $difference | ForEach-Object { "  $($_.SideIndicator) $($_.InputObject)" }
}

if ($failed) {
    $failed | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    exit 1
}
Write-Host "the demo passed after $Frames frames"
