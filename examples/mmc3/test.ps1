# Runs the cartridge in MAME's NES for a number of frames and checks what it did. Build first
# with build.ps1. test.lua holds the joypad's right for a stretch of the run, watches the bus, and
# at the end saves the NES's RAM and a picture of the screen, build/test/mmc3.png. The script then
# checks the following:
#
#   split    the IRQ wrote PPUSCROLL twice in every frame, always on the same line, which is one
#            of the blank rows between the playfield and the status bar's text
#   banks    the main loop's checks found every program bank it switched to where it asked for
#            it, in every one of its many rounds, while the interrupts switched banks under it
#   drums    the DMC read the whole drum sample once for each drum the tune started
#   status   the status bar's text is what `status::update` writes from the counts
#   state    the counts, the tune's place, the ship's place, the status bar's text, the split's
#            line and a hash of the screen are as tests/expected.txt has them
#
# How many rounds of switches the main loop makes depends on how long each takes, which any change
# to its code changes. So the state leaves out that count, the digits of the status bar that show
# it, and the line of the screen they are on.
# -Update writes tests/expected.txt from the run instead of checking it, which is then read and
# checked by hand. -Mame names MAME's program or its folder when it is not on the path. Exits 1
# if a check fails or MAME does not finish.
[CmdletBinding()]
param(
    [int]$Frames = 200,
    [string]$Mame,
    [switch]$Update
)

$ErrorActionPreference = 'Stop'
$build = Join-Path $PSScriptRoot 'build'
$image = Join-Path $build 'mmc3.nes'
if (-not (Test-Path $image)) { throw 'the cartridge is not built: run build.ps1 first' }
$emulator = if (-not $Mame) { (Get-Command mame).Source }
            elseif (Test-Path $Mame -PathType Container) { (Get-ChildItem $Mame -Filter 'mame*.exe' -Recurse | Select-Object -First 1).FullName }
            else { (Resolve-Path $Mame).Path }

. (Join-Path $PSScriptRoot '../labels.ps1')
$labels = Read-Labels (Join-Path $build 'mmc3.lbl')

# The program's numbers that the checks need, which its sources define.
$statusLine = 192                   # main::STATUS_LINE, the status bar's first line
$drumLength = 241                   # audio::DRUM_LENGTH, the drum's bytes
$right = @(50, 100)                 # the frames the joypad holds right through
$switchesLines = @(208, 215)        # the status bar's second row of text, which shows the switches

$work = Join-Path $build 'test'
New-Item -ItemType Directory -Force $work | Out-Null
$memory = Join-Path $work 'memory.bin'
$events = Join-Path $work 'events.txt'
$shot = Join-Path $work 'mmc3.png'
Remove-Item $memory, $events, $shot -ErrorAction SilentlyContinue

$script = Join-Path $work 'session.lua'
@(
    'session = {'
    "    frames = 0x$('{0:X4}' -f (Address $labels 'main::frames')),"
    "    stop = $Frames,"
    "    right = { $($right[0]), $($right[1]) },"
    "    skip = { $($switchesLines[0]), $($switchesLines[1]) },"
    "    drum = 0x$('{0:X4}' -f (Address $labels 'audio::drum')),"
    "    length = $drumLength,"
    "    memory = [[$memory]],"
    "    events = [[$events]],"
    "    shot = [[$shot]],"
    '}'
    # MAME runs the script in an environment of its own, which the other has to share.
    "assert(loadfile([[$(Join-Path $PSScriptRoot 'test.lua')]], 't', _ENV))()"
) | Set-Content $script

# What MAME writes of its own goes in the work folder, and a run stops after a minute of the
# NES's time.
$roms = Join-Path (Split-Path $emulator) 'roms'
$arguments = @('nes', '-rompath', "`"$roms`"", '-cart', "`"$image`"", '-video', 'none', '-sound', 'none',
               '-nothrottle', '-skip_gameinfo', '-nonvram_save', '-seconds_to_run', '60',
               '-snapshot_directory', "`"$work`"", '-autoboot_script', "`"$script`"")
$run = Start-Process $emulator -ArgumentList $arguments -PassThru -WindowStyle Hidden -WorkingDirectory $work `
    -RedirectStandardOutput (Join-Path $work 'mame.log')
$run.WaitForExit()
if ($run.ExitCode -ne 0 -or -not (Test-Path $memory) -or -not (Test-Path $events)) {
    Write-Host 'MAME did not finish: see build/test/mame.log' -ForegroundColor Red
    exit 1
}
$ram = [IO.File]::ReadAllBytes($memory)
$seen = @{}
foreach ($line in Get-Content $events) {
    $name, $rest = $line -split ' ', 2
    $seen[$name] = $rest
}

# Returns the bytes from an address, `$count` of them.
function Bytes([int]$address, [int]$count) { [byte[]]$ram[$address..($address + $count - 1)] }

# Returns the little-endian word at an address.
function Word([int]$address) { $ram[$address] + 256 * $ram[$address + 1] }

# Returns the status bar's tiles as the text they show, read back through the font's charmap: a
# space, then the digits, then the letters.
function Text([byte[]]$tiles) {
    -join ($tiles | ForEach-Object {
        if ($_ -eq 0) { ' ' }
        elseif ($_ -le 10) { [char](0x30 + $_ - 1) }
        elseif ($_ -le 36) { [char](0x41 + $_ - 11) }
        else { '#' }
    })
}

$failed = @()

# The IRQ's writes to PPUSCROLL: two a frame, in every frame, all on one line of the blank rows
# between the playfield and the status bar's text.
$splits = @($seen['splits'] -split ' ' | Where-Object { $_ } | ForEach-Object {
    $line, $count = $_ -split ':'
    [pscustomobject]@{ Line = [int]$line; Count = [int]$count }
})
if ($splits.Count -ne 1) {
    $failed += "split: PPUSCROLL was written during the picture on lines $($seen['splits'])"
} elseif ($splits[0].Line -lt $statusLine - 8 -or $splits[0].Line -ge $statusLine + 8) {
    $failed += "split: PPUSCROLL was written on line $($splits[0].Line), outside the blank rows"
} elseif ($splits[0].Count -ne 2 * $Frames) {
    $failed += "split: PPUSCROLL was written $($splits[0].Count) times during the picture in $Frames frames"
}

# The main loop's checks of its banks: none failed, and there were many in each frame, which is
# what makes it all but certain that the interrupts came in the middle of its switches.
$frameCount = Word (Address $labels 'main::frames')
$switches = Word (Address $labels 'main::switches')
$errors = $ram[(Address $labels 'main::errors')]
if ($errors -ne 0) { $failed += "banks: $errors of the main loop's switches found another bank there" }
if ($switches -lt 50 * $frameCount) { $failed += "banks: the main loop made only $switches rounds of switches" }

# The DMC's reads of the drum: the whole sample, once for each drum.
$drums = $ram[(Address $labels 'audio::drums')]
$fetches = [int]$seen['fetches']
if ($fetches -ne $drums * $drumLength) {
    $failed += "drums: the DMC read $fetches bytes of the drum for $drums drums of $drumLength bytes"
}

# The status bar's text, against the counts it shows. The switches it shows are the count when it
# was written, earlier in the frame.
$rows = Bytes (Address $labels 'status::rows') 64
$text = @((Text $rows[0..31]), (Text $rows[32..63]))
$shown = ($text -join ' ').Trim() -replace '\s+', ' '
$wanted = '^FRAME {0:X4} DRUMS {1:X2} SWITCHES [0-9A-F]{{4}} ERRORS {2:X2}$' -f $frameCount, $drums, $errors
if ($shown -notmatch $wanted) { $failed += "status: the status bar says `"$shown`"" }

# What the run leaves, a line to each part.
$state = [ordered]@{
    'main::frames'    = '{0:X4}' -f $frameCount
    'main::errors'    = '{0:X2}' -f $errors
    'main::scroll'    = '{0:X4}' -f (Word (Address $labels 'main::scroll'))
    'main::oam[0]'    = (Bytes (Address $labels 'main::oam') 4 | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
    'audio::drums'    = '{0:X2}' -f $drums
    'audio::offset'   = '{0:X2}' -f $ram[(Address $labels 'audio::offset')]
    'status::rows'    = "`"$($text[0])`""
    ' '               = "`"$($text[1] -replace '(?<=SWITCHES )[0-9A-F]{4}', '....')`""
    'split line'      = ($splits | ForEach-Object { $_.Line }) -join ' '
    'drum bytes read' = $fetches
    'screen'          = $seen['screen']
}
$lines = @("; What the cartridge holds after $Frames frames, and what MAME saw. test.ps1 -Update writes it.")
$lines += foreach ($part in $state.Keys) { '{0,-16} {1}' -f $part, $state[$part] }
$expected = Join-Path $PSScriptRoot 'tests/expected.txt'
if ($Update) {
    [IO.File]::WriteAllText($expected, (($lines -join "`n") + "`n"))
    Write-Host 'tests/expected.txt written'
} elseif ($difference = Compare-Object @(Get-Content $expected) $lines -SyncWindow 0) {
    $failed += 'state: the run differs from tests/expected.txt:'
    $failed += $difference | ForEach-Object { "  $($_.SideIndicator) $($_.InputObject)" }
}

if ($failed) {
    $failed | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    exit 1
}
Write-Host "the cartridge passed after $Frames frames"
