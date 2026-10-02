# Builds every corpus program and every example end to end against the pinned cc65: the
# command line, the project file and its globs, `--depfile`, `--c-header`, then ca65, cc65,
# ld65 and `remap-dbg`. The test suite compiles the same sources in process and assembles
# them through the ca65 oracle; only a real build exercises `nt65 build` itself and the files
# it writes, which is why this is a gate step rather than a test.
#
# It runs the same steps as each program's build.sh and the interop Makefile. Those remain
# the documented way to build a program by hand; the gate does not depend on `make` or `sh`
# for steps PowerShell can run, and it wants a clean build, not the incremental build the
# Makefile exists for.
#
# Each program builds, and runs its test, in a process of its own, all at once. A program's
# build and its emulator sessions touch only its own directory, so nothing is shared between
# them, and the step takes as long as its slowest program rather than all of them together.
# The output is held back and printed in the order below once every program has finished.
#
# -Nt65 names an nt65 built somewhere else, for when an editor's language server holds the
# usual build output open and it cannot be rebuilt in place. -Only builds one program, which
# is how each of those processes is started, and is also a quick way to check one by hand.
[CmdletBinding()]
param([string]$Configuration = 'Debug', [string]$Nt65, [string]$Only)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = if ($IsWindows) { '.exe' } else { '' }
$nt65 = if ($Nt65) { $Nt65 } else { Join-Path $root "src/Norristown.Cli/bin/$Configuration/net10.0/nt65$exe" }
$bin = Join-Path $root '.cache/cc65/bin'
$ca65 = Join-Path $bin "ca65$exe"
$cc65 = Join-Path $bin "cc65$exe"
$ld65 = Join-Path $bin "ld65$exe"

foreach ($tool in $nt65, $ca65, $ld65) {
    if (-not (Test-Path $tool)) {
        Write-Host "not built: $tool" -ForegroundColor Red
        exit 1
    }
}

# A clean run of ca65, cc65 or ld65 prints nothing, so any output counts as a failure unless
# -MayWarn is given. `nt65 build` is allowed to warn, and its output is printed either way.
function Run([string]$exe, [string[]]$arguments, [switch]$MayWarn) {
    $said = (& $exe @arguments 2>&1 | Out-String).Trim()
    $failed = $LASTEXITCODE -ne 0 -or (-not $MayWarn -and $said.Length -gt 0)
    if ($failed) {
        Write-Host ("$(Split-Path -Leaf $exe) " + ($arguments -join ' ')) -ForegroundColor Red
    }
    if ($said.Length -gt 0) {
        Write-Host $said
    }
    if ($failed) {
        exit 1
    }
}

function Sources([string]$directory, [string]$pattern) {
    if (-not (Test-Path $directory)) { return @() }
    Get-ChildItem $directory -Filter $pattern -Recurse -File | Sort-Object FullName
}

# Runs one of an example's own scripts with its output held back, and prints that output and
# the failure only when the script fails.
function Quietly([string]$script, [hashtable]$arguments, [string]$failure) {
    $said = & $script @arguments *>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        Write-Host $said.Trim()
        Write-Host $failure -ForegroundColor Red
        exit 1
    }
}

# Builds an example with its own script and, where its emulator is installed, runs its test,
# then reports the image's size and whether the test ran.
function Example([string]$name, [string]$emulator, [string]$image) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $directory = Join-Path $root "examples/$name"
    Quietly (Join-Path $directory 'build.ps1') @{ Nt65 = $nt65; Ca65 = $ca65; Ld65 = $ld65 } "${name}: the build failed"
    $ready = [bool](Get-Command $emulator -ErrorAction SilentlyContinue)
    if ($ready) {
        Quietly (Join-Path $directory 'test.ps1') @{} "${name}: its test failed"
    }
    $ran = if ($ready) { 'its test passed' } else { "its test did not run, because $emulator is not on the path" }
    Write-Host ("   {0,-11} {1,7:N0} bytes, {2} in {3:0.0}s" -f $name, (Get-Item (Join-Path $directory $image)).Length, $ran, $watch.Elapsed.TotalSeconds)
}

# Each program: its directory, the arguments to `nt65 build`, where the generated ca65 lands,
# the linker configuration, the image, the hand-written ca65 (and the CPU to assemble it for)
# and C beside it, and a script that generates the files the sources include with `.incbin`
# before nt65 reads their sizes.
$programs = @(
    @{ Name = 'c64';    Directory = 'tests/corpus/c64';    Build = @();
       Generated = 'build';             Config = 'c64.cfg';  Image = 'build/game.prg' }
    @{ Name = 'snes';   Directory = 'tests/corpus/snes';   Build = @();
       Generated = 'build';             Config = 'snes.cfg'; Image = 'build/game.sfc' }
    @{ Name = 'interop'; Directory = 'tests/corpus/interop';
       Build = @('--config', 'release', '--depfile', 'build/release/nt65.d', '--c-header', 'build/release/gen/nt65.h');
       Generated = 'build/release/gen'; Config = 'link.cfg';  Image = 'build/release/app.bin';
       HandWritten = 'asm'; HandWrittenCpu = '65c02'; C = 'c' }
    @{ Name = 'lorom-template'; Directory = 'examples/lorom-template'; Build = @();
       Generated = 'build';             Config = 'lorom256k.cfg'; Image = 'build/lorom-template.sfc';
       HandWritten = 'spc'; HandWrittenCpu = 'none'; Prepare = 'tools/convert.ps1' }
)

function Build([hashtable]$program) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Push-Location (Join-Path $root $program.Directory)
    try {
        # Build from clean every time: the gate checks that a full build works, not an
        # incremental one, and a stale object file would link and hide a failure.
        if (Test-Path 'build') { Remove-Item 'build' -Recurse -Force }
        if ($program.Prepare) {
            & (Join-Path (Get-Location) $program.Prepare)
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        Run $nt65 (@('build') + $program.Build) -MayWarn

        $objects = @()
        foreach ($source in Sources $program.Generated '*.s') {
            $object = [IO.Path]::ChangeExtension($source.FullName, '.o')
            Run $ca65 @('-g', $source.FullName, '-o', $object)
            $objects += $object
        }
        foreach ($source in Sources $program.HandWritten '*.s') {
            $object = Join-Path 'build/asm' ($source.BaseName + '.o')
            New-Item -ItemType Directory -Force 'build/asm' | Out-Null
            Run $ca65 @('--cpu', $program.HandWrittenCpu, '-g', '-I', $program.HandWritten, '-o', $object, $source.FullName)
            $objects += $object
        }

        # The C is compiled against the header nt65 just wrote, which is what the header is
        # for. It is not linked: the cc65 runtime it would need is not part of this program,
        # and the hand-written `asm/main.s` takes its place in the image.
        foreach ($source in Sources $program.C '*.c') {
            New-Item -ItemType Directory -Force 'build/c' | Out-Null
            $assembly = Join-Path 'build/c' ($source.BaseName + '.s')
            Run $cc65 @('-t', 'none', '-I', $program.Generated, '-I', (Join-Path $root '.cache/cc65/include'),
                '-o', $assembly, $source.FullName)
            Run $ca65 @('-g', '-I', (Join-Path $root '.cache/cc65/asminc'),
                '-o', [IO.Path]::ChangeExtension($assembly, '.o'), $assembly)
        }

        $debug = [IO.Path]::ChangeExtension($program.Image, '.dbg')
        Run $ld65 (@('-C', $program.Config, '-o', $program.Image, '--dbgfile', $debug) + ($objects | Sort-Object))
        Run $nt65 @('remap-dbg', $debug)

        $image = Get-Item $program.Image
        if ($image.Length -eq 0) {
            Write-Host "$($program.Name): linked, but wrote no bytes" -ForegroundColor Red
            exit 1
        }
        Write-Host ("   {0,-11} {1,7:N0} bytes in {2:0.0}s" -f $program.Name, $image.Length, $watch.Elapsed.TotalSeconds)
    }
    finally {
        Pop-Location
    }
}

$examples = [ordered]@{}
foreach ($program in $programs) {
    $examples[$program.Name] = [scriptblock]::Create("Build `$programs[$($examples.Count)]")
}

# msbasic builds ten targets, each a configuration of one program, and checks each image
# against the original ROM's hash; its own script knows the targets and the comparison.
$examples['msbasic'] = {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Quietly (Join-Path $root 'examples/msbasic/build.ps1') @{ Nt65 = $nt65; Ca65 = $ca65; Ld65 = $ld65 } `
        'msbasic: a target failed or differs from its original'
    Write-Host ("   {0,-11} {1,7} targets in {2:0.0}s, each the original ROM" -f 'msbasic', 10, $watch.Elapsed.TotalSeconds)
}

# The cc65 example is C that calls nt65, built with cl65 and run in sim65, whose exit code is the
# number of the C program's checks that failed.
$examples['cc65'] = {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Quietly (Join-Path $root 'examples/cc65/build.ps1') @{ Nt65 = $nt65; Cl65 = (Join-Path $bin "cl65$exe"); Sim65 = (Join-Path $bin "sim65$exe") } `
        'cc65: the build failed or a check in the C program failed'
    Write-Host ("   {0,-11} {1,7} passed in sim65 in {2:0.0}s" -f 'cc65', 'checks', $watch.Elapsed.TotalSeconds)
}

# The SNROM template builds once per mapper, each a configuration of one program with its own
# driver and linker configuration, and checks each image against the original ca65 build's hash;
# its own script converts the tiles and knows the objects, their order and the comparison.
$examples['snrom-template'] = {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Quietly (Join-Path $root 'examples/snrom-template/build.ps1') @{ Nt65 = $nt65; Ca65 = $ca65; Ld65 = $ld65 } `
        'snrom-template: a mapper failed or differs from the original'
    Write-Host ("   {0,-11} {1,7} mappers in {2:0.0}s, each the original image" -f 'snrom', 2, $watch.Elapsed.TotalSeconds)
}

# The monitor builds once per platform, from a library each platform's project shares, and each
# platform's sessions run where its emulator is installed: VICE for the C64, MAME for the IIGS,
# the Super NES and the NES.
$examples['monitor'] = {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $monitor = Join-Path $root 'examples/monitor'
    Quietly (Join-Path $monitor 'build.ps1') @{ Nt65 = $nt65; Ca65 = $ca65; Ld65 = $ld65 } 'monitor: a platform failed to build'
    $emulators = [ordered]@{ c64 = 'x64sc'; apple2gs = 'mame'; snes = 'mame'; nes = 'mame' }
    $ready = @($emulators.Keys | Where-Object { Get-Command $emulators[$_] -ErrorAction SilentlyContinue })
    if ($ready.Count -gt 0) {
        Quietly (Join-Path $monitor 'test.ps1') @{ Platform = $ready } 'monitor: a session differs or did not finish'
    }
    $ran = if ($ready.Count -gt 0) { "its sessions passed on $($ready -join ', ')" } else { 'no sessions ran' }
    Write-Host ("   {0,-11} {1,7} built, {2} in {3:0.0}s" -f 'monitor', "$($emulators.Count) platforms", $ran, $watch.Elapsed.TotalSeconds)
    foreach ($p in $emulators.Keys | Where-Object { $_ -notin $ready }) {
        Write-Host ("   {0,-11} {1,7} sessions did not run, because {2} is not on the path" -f '', $p, $emulators[$p]) -ForegroundColor Yellow
    }
}

# The C64 demo packs its screens before nt65 measures them, so its own script builds it. Where
# VICE is installed, its test runs it for a hundred frames and checks what it left in memory.
$examples['c64-demo'] = { Example 'c64-demo' 'x64sc' 'build/demo.prg' }

# The Atari XEX builds with its own script. Where Atari800 is installed, its test loads the XEX
# twice, once to run it for a hundred frames and once with BASIC in, and checks what each left in
# memory.
$examples['atari-xex'] = { Example 'atari-xex' 'atari800' 'build/demo.xex' }

# The X16 card is a PRG and a file for each RAM bank it loads, so its own script builds it. Where
# x16emu is installed, its test runs it without a window and checks the screen and the banks.
$examples['x16'] = { Example 'x16' 'x16emu' 'build/card.prg' }

# The HiROM demo's own script builds it and writes its header's checksum. Where MAME is installed,
# its test runs it for 150 frames and checks its HDMA channels, its ramp and the screen.
$examples['hirom-hdma'] = { Example 'hirom-hdma' 'mame' 'build/hirom-hdma.sfc' }

# The MMC3 cartridge builds with its own script. Where MAME is installed, its test runs it for two
# hundred frames and checks what it left in memory and what MAME saw on the bus.
$examples['mmc3'] = { Example 'mmc3' 'mame' 'build/mmc3.nes' }

if ($Only) {
    if (-not $examples.Contains($Only)) {
        Write-Host "no such program: $Only (one of $($examples.Keys -join ', '))" -ForegroundColor Red
        exit 1
    }
    & $examples[$Only]
    exit 0
}

# Every program's process starts at once. Each one's output, standard error included, is
# collected whole so that programs finishing together do not interleave their lines.
$pwsh = (Get-Process -Id $PID).Path
$script = $PSCommandPath
$finished = @($examples.Keys) | ForEach-Object -ThrottleLimit $examples.Count -Parallel {
    $said = & $using:pwsh -NoProfile -File $using:script -Configuration $using:Configuration -Nt65 $using:nt65 -Only $_ 2>&1 | Out-String
    [pscustomobject]@{ Name = $_; Code = $LASTEXITCODE; Said = $said.TrimEnd() }
}

$failed = $false
foreach ($name in $examples.Keys) {
    $result = $finished | Where-Object Name -eq $name
    if ($result.Said.Length -gt 0) { Write-Host $result.Said }
    if ($result.Code -ne 0) {
        Write-Host "${name}: failed" -ForegroundColor Red
        $failed = $true
    }
}
if ($failed) { exit 1 }
