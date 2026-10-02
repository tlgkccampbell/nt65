# Runs the program in Atari800 and checks what it left in memory. Build first with build.ps1.
# Atari800 loads build/demo.xex as DOS would, one load segment at a time, and its monitor stops the
# program twice. The first stop is where RUNAD enters `main::start`, once every segment is in;
# the monitor saves the whole of memory there. It then sets the operating system's first
# countdown timer to `$Frames` frames, with its vector pointing at an `rts` in the cassette
# buffer, which the program never uses. The second stop is that `rts`, in the vertical blank
# `$Frames` frames on, where memory is saved again.
#
# A second run beside the first loads the program with BASIC in. The system check refuses it, and
# the monitor saves memory ten frames after the check starts to write why.
#
# The script then checks the following:
#
#   segments   the XEX's load segments, INITADs and RUNAD are as tests/expected.txt has them
#   loaded     at RUNAD, every load segment is in memory, the program's screen over the loader
#   state      at RUNAD and after the frames, the loader's frame counts, the operating system's
#              shadows and vectors, the interrupts' variables and the screen's last row are as
#              tests/expected.txt has them, and so is what the refused run wrote on the screen
#
# Atari800 runs the operating system and BASIC built into it, AltirraOS and Altirra BASIC, so it
# needs no ROM images. When it starts on them it tries to download Atari's own ROMs. The script
# hands it a proxy that refuses every connection, so the download fails at once, the run uses
# what is built in, and nothing touches the network.
#
# -Update writes tests/expected.txt from the run instead of checking it, which is then read and
# checked by hand. -Atari800 names Atari800, or the folder it is in, when it is not on the path.
# Exits 1 if a check fails or Atari800 does not finish.
[CmdletBinding()]
param(
    [int]$Frames = 100,
    [string]$Atari800,
    [switch]$Update
)

$ErrorActionPreference = 'Stop'
$build = Join-Path $PSScriptRoot 'build'
$image = Join-Path $build 'demo.xex'
if (-not (Test-Path $image)) { throw 'the program is not built: run build.ps1 first' }
$emulator = if (-not $Atari800) { (Get-Command atari800).Source }
            elseif (Test-Path $Atari800 -PathType Container) { (Get-ChildItem $Atari800 -Filter 'atari800*.exe' -Recurse | Select-Object -First 1).FullName }
            else { (Resolve-Path $Atari800).Path }

# The address of each name, from the label file `nt65 remap-dbg --labels` wrote, which names it
# by its path in the source.
. (Join-Path $PSScriptRoot '../labels.ps1')
$labels = Read-Labels (Join-Path $build 'demo.lbl')

# The program's names and segments, from ld65's debug file. The loader and the program's screen
# share their addresses, so a name is told apart by its segment as well as its address. ld65 has
# a name as the .s spells it, such as `rows__status` for `display::rows::status`, and the map nt65
# wrote beside each .s gives its path, in records such as `name rows__status, display::rows::status`.
$paths = @{}
$scopes = @{}
$segments = @{}
$symbols = @()
foreach ($line in Get-Content (Join-Path $build 'demo.dbg')) {
    $kind, $rest = $line -split "`t", 2
    $field = @{}
    foreach ($pair in $rest -split ',') {
        $key, $value = $pair -split '=', 2
        $field[$key] = "$value".Trim('"')
    }
    switch ($kind) {
        'file'  {
            if (-not (Test-Path "$($field.name).lines")) { break }
            $names = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
            foreach ($record in Get-Content "$($field.name).lines" | Where-Object { $_ -like 'name *' }) {
                $name, $path = $record.Substring(5) -split ', '
                $names[$name] = $path
            }
            foreach ($module in $field.mod -split '\+') { $paths[$module] = $names }
        }
        'scope' { $scopes[$field.id] = $field.mod }
        'seg'   { $segments[$field.id] = @{ Name = $field.name; Start = [Convert]::ToInt32($field.start, 16)
                                            Size = [Convert]::ToInt32($field.size, 16)
                                            Offset = if ($field.ooffs) { [int]$field.ooffs } else { -1 } } }
        'sym'   { if ($field.val -and $field.type -ne 'imp') { $symbols += $field } }
    }
}
$symbols = @(foreach ($symbol in $symbols) {
    $names = $paths[$scopes[$symbol.scope]]
    if ($names -and $names.ContainsKey($symbol.name)) {
        $symbol.path = $names[$symbol.name]
        $symbol
    }
})

# The XEX's records in order: each load segment with its first address and its bytes, and each
# INITAD and RUNAD with the address it gives, which are load segments of their own at $02E2 and
# $02E0. Each segment may start with $FFFF.
$xex = [IO.File]::ReadAllBytes($image)
$records = @()
for ($at = 0; $at -lt $xex.Length) {
    $first = $xex[$at] + 256 * $xex[$at + 1]
    if ($first -eq 0xFFFF) { $at += 2; continue }
    $last = $xex[$at + 2] + 256 * $xex[$at + 3]
    $bytes = [byte[]]$xex[($at + 4)..($at + 4 + $last - $first)]
    $vector = $first -in 0x02E0, 0x02E2 -and $last -eq $first + 1
    $records += @{ First = $first; Last = $last; Bytes = $bytes; Vector = $vector
                   Segments = @($segments.Values | Where-Object { $_.Size -gt 0 -and
                                $_.Offset -ge $at + 4 -and $_.Offset -lt $at + 4 + $bytes.Length } | Sort-Object Start) }
    $at += 4 + $bytes.Length
}

# Returns a record as a line: a load segment's addresses and the segments it holds, or the
# routine an INITAD or RUNAD calls, named from the last load segment that put it in memory.
function Describe($record) {
    if (-not $record.Vector) {
        return 'load     ${0:X4}-${1:X4} {2}' -f $record.First, $record.Last, (($record.Segments | ForEach-Object Name) -join ' ')
    }
    $target = $record.Bytes[0] + 256 * $record.Bytes[1]
    $kind = if ($record.First -eq 0x02E0) { 'runad' } else { 'initad' }
    $from = $records[0..([array]::IndexOf($records, $record))] |
        Where-Object { -not $_.Vector -and $target -ge $_.First -and $target -le $_.Last } | Select-Object -Last 1
    $ids = @($segments.Keys | Where-Object { $segments[$_] -in $from.Segments })
    $name = $symbols | Where-Object { $_.seg -in $ids -and [Convert]::ToInt32($_.val, 16) -eq $target } |
        ForEach-Object path | Sort-Object | Select-Object -First 1
    return '{0,-8} ${1:X4} {2}' -f $kind, $target, $name
}

# Returns the name at an address in a segment that is in memory once the program has loaded.
function Name([int]$address) {
    $loaded = $records | Where-Object { -not $_.Vector }
    $last = $loaded | Where-Object { $address -ge $_.First -and $address -le $_.Last } | Select-Object -Last 1
    $ids = if ($last) { @($segments.Keys | Where-Object { $segments[$_] -in $last.Segments }) } else { @() }
    $symbols | Where-Object { [Convert]::ToInt32($_.val, 16) -eq $address -and (-not $last -or $_.seg -in $ids) } |
        ForEach-Object path | Sort-Object | Select-Object -First 1
}

$work = Join-Path $build 'test'
New-Item -ItemType Directory -Force $work | Out-Null
$started = Join-Path $work 'start.bin'
$after = Join-Path $work 'memory.bin'
Remove-Item $started, $after -ErrorAction SilentlyContinue

# Returns the monitor's commands that run the program on for a number of frames from a stop and
# save memory in a file. CDTMV1 at $0218 is the first countdown timer, and CDTMA1 at $0226 is
# where the operating system calls when it reaches 0.
function Wait([int]$frames, [string]$file) {
    $stop = 0x0400
    'C 0218 {0:X2} {1:X2}' -f ($frames -band 0xFF), ($frames -shr 8)
    'C 0226 {0:X2} {1:X2}' -f ($stop -band 0xFF), ($stop -shr 8)
    'C {0:X4} 60' -f $stop
    'BPC {0:X4}' -f $stop
    'CONT'
    "WRITE 0000 FFFF $file"
}

# Starts Atari800 on the program, stopping at the routine named `$break`, with BASIC in or out,
# and gives its monitor the commands, which it reads at each stop until a CONT or a QUIT. SDL's
# dummy driver runs it with no window. The configuration file is the run's own, so that one the
# user keeps does not change what runs.
function Start-Atari800([string]$name, [string]$break, [string]$basic, [string[]]$commands) {
    $script = Join-Path $work "$name.txt"
    ($commands + 'QUIT') | Set-Content $script
    $saved = $env:SDL_VIDEODRIVER, $env:http_proxy
    try {
        $env:SDL_VIDEODRIVER = 'dummy'
        $env:http_proxy = 'http://127.0.0.1:9'
        $arguments = @('-config', "$name.cfg", '-no-autosave-config', '-xl', '-xl-rev', 'altirra',
                       '-basic-rev', 'altirra', $basic, '-nosound', '-turbo',
                       '-bpc', ('{0:X4}' -f (Address $labels $break)), '-run', $image)
        Start-Process $emulator -ArgumentList $arguments -WorkingDirectory $work -PassThru -WindowStyle Hidden `
            -RedirectStandardInput $script -RedirectStandardOutput (Join-Path $work "$name.log") `
            -RedirectStandardError (Join-Path $work "$name.err")
    }
    finally {
        $env:SDL_VIDEODRIVER, $env:http_proxy = $saved
    }
}

# The two runs go side by side. The first runs the program; the second has BASIC in, which the
# system check refuses, and stops in `syscheck::fail` before it writes why.
$refused = Join-Path $work 'refused.bin'
Remove-Item $refused -ErrorAction SilentlyContinue
$runs = @(
    Start-Atari800 'run' 'main::start' '-nobasic' (@('WRITE 0000 FFFF start.bin') + (Wait $Frames 'memory.bin'))
    Start-Atari800 'refused' 'syscheck::fail' '-basic' (Wait 10 'refused.bin')
)
foreach ($run in $runs) {
    if (-not $run.WaitForExit(30000)) { $run.Kill() }
}
foreach ($file in $started, $after, $refused) {
    if (-not (Test-Path $file)) {
        Write-Host "Atari800 did not finish: see the logs in build/test" -ForegroundColor Red
        exit 1
    }
}

$failed = @()
$memory = @{ start = [IO.File]::ReadAllBytes($started); after = [IO.File]::ReadAllBytes($after)
             refused = [IO.File]::ReadAllBytes($refused) }

# Returns the bytes from an address, `$count` of them, as memory held them at a stop.
function Bytes([string]$stop, [int]$address, [int]$count) { [byte[]]$memory[$stop][$address..($address + $count - 1)] }

# Returns bytes of screen memory as the text they show, for the codes `nt65::atari::screen` maps.
function Text([byte[]]$codes) {
    -join ($codes | ForEach-Object {
        if ($_ -lt 0x40) { [char]($_ + 0x20) } elseif ($_ -ge 0x61 -and $_ -le 0x7A) { [char]$_ } else { '#' }
    })
}

# At RUNAD, each load segment is in memory, and where two load at the same addresses the later
# one is, which puts the program's screen where the loading screen was.
$loaded = @{}
foreach ($record in $records | Where-Object { $_.First -notin 0x02E0, 0x02E2 }) {
    for ($i = 0; $i -lt $record.Bytes.Length; $i++) { $loaded[$record.First + $i] = $record.Bytes[$i] }
}
$differ = @($loaded.Keys | Where-Object { $memory.start[$_] -ne $loaded[$_] } | Sort-Object)
if ($differ) { $failed += 'loaded: at RUNAD, memory differs from the XEX at ' + (($differ | Select-Object -First 8 | ForEach-Object { '${0:X4}' -f $_ }) -join ' ') }

# Returns the bytes at a label, at a stop, as hex.
function Hex([string]$stop, [string]$label, [int]$count = 1) {
    (Bytes $stop (Address $labels $label) $count | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
}

# Returns the address a vector holds, at a stop, as a label.
function Vector([string]$stop, [string]$label) {
    $target = (Bytes $stop (Address $labels $label) 1)[0] + 256 * (Bytes $stop ((Address $labels $label) + 1) 1)[0]
    if ($name = Name $target) { $name } else { '${0:X4}' -f $target }
}

$lines = @("; The XEX's records, what the program holds at RUNAD and after $Frames frames, and the screen"
           "; when BASIC is in. test.ps1 -Update writes it.")
$lines += $records | ForEach-Object { Describe $_ }
$state = [ordered]@{
    'start loader::started'  = Hex start 'loader::started'
    'start loader::finished' = Hex start 'loader::finished'
    'start os::SDMCTL'       = Hex start 'atari::os::SDMCTL'
    'start os::COLOR2'       = Hex start 'atari::os::COLOR2'
    'after os::SDMCTL'       = Hex after 'atari::os::SDMCTL'
    'after os::SDLSTL'       = Vector after 'atari::os::SDLSTL'
    'after os::VDSLST'       = Vector after 'atari::os::VDSLST'
    'after os::VVBLKD'       = Vector after 'atari::os::VVBLKD'
    'after bars::frames'     = Hex after 'bars::frames' 2
    'after bars::band'       = Hex after 'bars::band'
    'after bars::phase'      = Hex after 'bars::phase'
    'after status row'       = (Text (Bytes after (Address $labels 'display::rows::status') 40)).TrimEnd()
}
# The screen the refused run left, from the address in SAVMSC at $58, two rows of 40.
$screen = (Bytes refused 0x58 1)[0] + 256 * (Bytes refused 0x59 1)[0]
foreach ($row in 0, 1) { $state["refused screen row $row"] = (Text (Bytes refused ($screen + 40 * $row) 40)).TrimEnd() }
$lines += foreach ($part in $state.Keys) { '{0,-23} {1}' -f $part, $state[$part] }
$expected = Join-Path $PSScriptRoot 'tests/expected.txt'
if ($Update) {
    [IO.File]::WriteAllText($expected, (($lines -join "`n") + "`n"))
    Write-Host 'tests/expected.txt written'
} elseif ($difference = Compare-Object @(Get-Content $expected) $lines -SyncWindow 0) {
    $failed += 'segments or state: they differ from tests/expected.txt:'
    $failed += $difference | ForEach-Object { "  $($_.SideIndicator) $($_.InputObject)" }
}

if ($failed) {
    $failed | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    exit 1
}
Write-Host "the program passed after $Frames frames"
