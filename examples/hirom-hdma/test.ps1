# Runs the demo in MAME's Super NES for a number of frames and checks what it left in its
# registers, its memory and on the screen. Build first with build.ps1. session.lua writes the
# state once the demo has counted the frames, with the screen and a snapshot,
# build/test/snap/demo.png, and leaves MAME. The script then checks the following:
#
#   wave     HDMA channel 1 reads the table, in bank $C1, of the buffer `wave::shown` names,
#            and that table's scrolls from bank $7E
#   state    the frame count, both HDMA channels' registers, the wave's buffers, the ramp and a
#            hash of the screen are as tests/expected.txt has them
#
# -Update writes tests/expected.txt from the run instead of checking it, which is then read and
# checked by hand, against the snapshot too. -Mame names MAME, or the folder it is in, when it is
# not on the path; its ROMs are looked for beside it. Exits 1 if a check fails or MAME does not
# finish.
[CmdletBinding()]
param(
    [int]$Frames = 150,
    [string]$Mame,
    [switch]$Update
)

$ErrorActionPreference = 'Stop'
$build = Join-Path $PSScriptRoot 'build'
$image = Join-Path $build 'hirom-hdma.sfc'
if (-not (Test-Path $image)) { throw 'the demo is not built: run build.ps1 first' }
$emulator = if (-not $Mame) { (Get-Command mame).Source }
            elseif (Test-Path $Mame -PathType Container) { (Get-ChildItem $Mame -Filter 'mame*.exe' | Select-Object -First 1).FullName }
            else { (Resolve-Path $Mame).Path }

$labels = @{}
foreach ($line in Get-Content (Join-Path $build 'hirom-hdma.lbl')) {
    $_, $address, $name = $line -split ' '
    $labels[$name.TrimStart('.')] = [Convert]::ToInt32($address, 16)
}

# The memory the test reads, by name: each an address and a count of bytes. The sizes are the
# ones wave.nt65 and gradient.nt65 lay out. A name a module keeps to itself, such as `shown`, is
# in the label file under its own name, without its module's.
$regions = [ordered]@{
    frames   = $labels['main__frames'], 4
    channel1 = 0x4310, 8
    channel2 = 0x4320, 5
    shown    = $labels['shown'], 1
    ready    = $labels['wave__ready'], 1
    scrolls  = $labels['wave__scrolls'], (2 * 2 * $labels['wave__LINES'])
    ramp     = $labels['gradient__ramp'], (7 * (1 + 32 * 4) + 1)
}

$work = Join-Path $build 'test'
New-Item -ItemType Directory -Force $work | Out-Null
$state = Join-Path $work 'state.txt'
$screen = Join-Path $work 'screen.bin'
Remove-Item $state, $screen -ErrorAction SilentlyContinue

$script = Join-Path $work 'session.lua'
@(
    'session = {'
    "    frames = 0x$('{0:X6}' -f $labels['main__frames']),"
    "    wanted = $Frames,"
    "    state = [[$state]],"
    "    screen = [[$screen]],"
    '    regions = {'
    foreach ($name in $regions.Keys) { "        { `"$name`", 0x$('{0:X6}' -f $regions[$name][0]), $($regions[$name][1]) }," }
    '    },'
    '}'
    # MAME runs the script in an environment of its own, which the other has to share.
    "assert(loadfile([[$(Join-Path $PSScriptRoot 'session.lua')]], 't', _ENV))()"
) | Set-Content $script

# What MAME writes of its own goes in the work folder, and a run stops after a minute of the
# Super NES's time.
$roms = Join-Path (Split-Path $emulator) 'roms'
$arguments = @('snes', '-rompath', "`"$roms`"", '-cart', "`"$image`"", '-video', 'none', '-sound', 'none',
               '-nothrottle', '-skip_gameinfo', '-nonvram_save', '-seconds_to_run', '60',
               '-autoboot_script', "`"$script`"")
$run = Start-Process $emulator -ArgumentList $arguments -PassThru -WindowStyle Hidden -WorkingDirectory $work `
    -RedirectStandardOutput (Join-Path $work 'mame.log')
$run.WaitForExit()
if ($run.ExitCode -ne 0 -or -not (Test-Path $state) -or -not (Test-Path $screen)) {
    Write-Host 'MAME did not finish: see build/test/mame.log' -ForegroundColor Red
    exit 1
}

$lines = @("; What the demo holds after $Frames frames. test.ps1 -Update writes it.")
$lines += Get-Content $state
$lines += 'screen ' + (Get-FileHash $screen -Algorithm SHA256).Hash.ToLowerInvariant()

$failed = @()
# The wave's channel reads the table of the buffer shown, which points into bank $7E.
function Field([string]$name) { @((($lines | Where-Object { $_ -like "$name *" }) -split ' ') | Select-Object -Skip 1) }
$channel = Field 'channel1'
$shown = [Convert]::ToInt32(@(Field 'shown')[0], 16)
$address = [Convert]::ToInt32($channel[3] + $channel[2], 16)
$table = ($labels['wave__tables'] + $shown * 7) -band 0xFFFF
if ($address -ne $table -or $channel[4] -ne 'C1' -or $channel[7] -ne '7E') {
    $failed += "wave: channel 1 reads `$$($channel[4]):$('{0:X4}' -f $address) and `$$($channel[7]), not buffer $shown's table at `$C1:$('{0:X4}' -f $table) and `$7E"
}

$expected = Join-Path $PSScriptRoot 'tests/expected.txt'
if ($Update) {
    [IO.File]::WriteAllText($expected, (($lines -join "`n") + "`n"))
    Write-Host 'tests/expected.txt written'
} elseif ($difference = Compare-Object @(Get-Content $expected) $lines -SyncWindow 0) {
    $failed += 'state: what the demo holds differs from tests/expected.txt:'
    $failed += $difference | ForEach-Object { "  $($_.SideIndicator) $($_.InputObject)" }
}

if ($failed) {
    $failed | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    exit 1
}
Write-Host "the demo passed after $Frames frames"
