# Runs the card in x16emu and checks what it left on the screen and in the RAM banks. Build first
# with build.ps1. The emulator runs without a window, loads the PRG and runs it, and the card goes
# back to BASIC when it is done. BASIC then asks the emulator's test bench for a command, and the
# script's commands send the emulator to $FFFF, where it saves memory, the banks and video memory
# to build/test/dump.bin and stops. The script then checks the following:
#
#   screen     the text on the screen is tests/screen.txt
#   colours    each cell's colours are as tests/colours.txt has them
#   reverse    the cells shown in reverse are the ones tests/reverse.txt lists
#   banks      RAM banks 1 and 2 hold build/card.prg.01 and build/card.prg.02
#
# -Update writes the three files in tests from the run instead of checking them, which are then
# read and checked by hand. -X16emu names x16emu, or the folder it is in, when it is not on the
# path. Exits 1 if a check fails or the emulator does not finish.
[CmdletBinding()]
param(
    [string]$X16emu,
    [switch]$Update
)

$ErrorActionPreference = 'Stop'
$build = Join-Path $PSScriptRoot 'build'
$image = Join-Path $build 'card.prg'
if (-not (Test-Path $image)) { throw 'the card is not built: run build.ps1 first' }
$emulator = if (-not $X16emu) { (Get-Command x16emu).Source }
            elseif (Test-Path $X16emu -PathType Container) { (Get-ChildItem $X16emu -Filter 'x16emu*.exe' -Recurse | Select-Object -First 1).FullName }
            else { (Resolve-Path $X16emu).Path }

$work = Join-Path $build 'test'
New-Item -ItemType Directory -Force $work | Out-Null
$dump = Join-Path $work 'dump.bin'
Remove-Item $dump -ErrorAction SilentlyContinue

# The test bench takes commands once BASIC is waiting for a line. These write `jmp $FFFF` at
# $0400, which the card does not use, and run it.
$commands = Join-Path $work 'commands.txt'
[IO.File]::WriteAllText($commands, "STM 0400 4C`nSTM 0401 FF`nSTM 0402 FF`nRUN 0400`n")

# The disk is the build folder, where the banks' files are. 64 KB of banked RAM is eight banks,
# all the card needs. RAM starts as zeros, so every run is the same run, and the warnings about
# the Rockwell instructions, which a 65816 in the X16 would not have, are left out.
$banks = 8
$arguments = @('-testbench', '-prg', "`"$image`"", '-run', '-fsroot', "`"$build`"", '-startin', "`"$build`"",
               '-ram', ($banks * 8), '-zeroram', '-rockwell', '-dump', 'RBV')
$run = Start-Process $emulator -ArgumentList $arguments -PassThru -WindowStyle Hidden -WorkingDirectory $work `
    -RedirectStandardInput $commands -RedirectStandardOutput (Join-Path $work 'x16emu.log')
if (-not $run.WaitForExit(30000)) {
    $run.Kill()
    $run.WaitForExit()
}
if ($run.ExitCode -ne 0 -or -not (Test-Path $dump)) {
    Write-Host 'x16emu did not finish: see build/test/x16emu.log' -ForegroundColor Red
    exit 1
}

# The dump is main memory up to $9FFF, then each RAM bank, then video memory.
$bytes = [IO.File]::ReadAllBytes($dump)
$bankAt = { param($bank) 0xA000 + $bank * 0x2000 }
$video = & $bankAt $banks

# The text map is 128 cells to a row, and each cell is a screen code and its colours.
$map = 0x1B000
$columns = 80
$rows = 60
function Cell([int]$row, [int]$column) { $video + $map + $row * 256 + $column * 2 }

# A screen code of the lowercase and uppercase characters as the character it shows: $00 to $1F
# are `@`, the lower case and a few signs, $20 to $3F are ASCII, $41 to $5A are the capitals, and
# the rest are graphics, of which the lines of a box are drawn as ASCII. Bit 7 is reverse video.
$graphics = @{ 0x40 = '-'; 0x5D = '|'; 0x70 = '+'; 0x6E = '+'; 0x6D = '+'; 0x7D = '+' }
function Character([byte]$code) {
    $code = $code -band 0x7F
    if ($code -eq 0) { return '@' }
    if ($code -lt 0x1B) { return [char]($code + 0x60) }
    if ($code -lt 0x20) { return '#' }
    if ($code -lt 0x40) { return [char]$code }
    if ($code -ge 0x41 -and $code -le 0x5A) { return [char]$code }
    if ($graphics.ContainsKey([int]$code)) { return $graphics[[int]$code] }
    return '#'
}

# A cell's colours as one character: `.` for white on blue, which the KERNAL starts with, the
# foreground's hex digit for another colour on blue, and `?` for any other background.
function Colours([byte]$colours) {
    if ($colours -eq 0x61) { return '.' }
    if (($colours -shr 4) -eq 6) { return '{0:X}' -f ($colours -band 0x0F) }
    return '?'
}

# The lines without the blank cells at the end of each, and without the blank lines at the end.
function Trimmed([string[]]$lines, [char]$blank) {
    $lines = @($lines | ForEach-Object { $_.TrimEnd($blank) })
    $count = $lines.Count
    while ($count -gt 0 -and $lines[$count - 1] -eq '') { $count-- }
    if ($count -gt 0) { $lines[0..($count - 1)] }
}

$screen = Trimmed @(for ($r = 0; $r -lt $rows; $r++) {
    -join (0..($columns - 1) | ForEach-Object { Character $bytes[(Cell $r $_)] })
}) ' '
$colours = Trimmed @(for ($r = 0; $r -lt $rows; $r++) {
    -join (0..($columns - 1) | ForEach-Object { Colours $bytes[(Cell $r $_) + 1] })
}) '.'
$reverse = @(for ($r = 0; $r -lt $rows; $r++) {
    $cells = @(0..($columns - 1) | Where-Object { $bytes[(Cell $r $_)] -band 0x80 })
    if ($cells.Count -gt 0) { 'row {0}: columns {1} to {2}' -f $r, $cells[0], $cells[-1] }
})

$failed = @()
foreach ($check in @(
    @{ Name = 'screen';  File = 'screen.txt';  Lines = $screen }
    @{ Name = 'colours'; File = 'colours.txt'; Lines = $colours }
    @{ Name = 'reverse'; File = 'reverse.txt'; Lines = $reverse })) {
    $expected = Join-Path $PSScriptRoot "tests/$($check.File)"
    if ($Update) {
        [IO.File]::WriteAllText($expected, (($check.Lines -join "`n") + "`n"))
        Write-Host "tests/$($check.File) written"
    } elseif ($difference = Compare-Object @(Get-Content $expected) @($check.Lines) -SyncWindow 0) {
        $failed += "$($check.Name): differs from tests/$($check.File):"
        $failed += $difference | ForEach-Object { "  $($_.SideIndicator) $($_.InputObject)" }
    }
}

# Each bank's file, after the two bytes that say where it loads, is what the bank starts with.
foreach ($bank in 1, 2) {
    $name = Join-Path $build ('card.prg.{0:D2}' -f $bank)
    if (-not (Test-Path $name)) {
        $failed += "banks: build/card.prg.$('{0:D2}' -f $bank) is missing"
        continue
    }
    $file = [IO.File]::ReadAllBytes($name)
    $start = & $bankAt $bank
    $held = [byte[]]$bytes[$start..($start + $file.Length - 3)]
    if (Compare-Object ([byte[]]$file[2..($file.Length - 1)]) $held -SyncWindow 0) {
        $failed += "banks: RAM bank $bank does not hold build/card.prg.$('{0:D2}' -f $bank)"
    }
}

if ($failed) {
    $failed | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    exit 1
}
if (-not $Update) { Write-Host 'the card passed' }
