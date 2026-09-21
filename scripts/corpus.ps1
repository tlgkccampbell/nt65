# Every corpus program and every example, built end to end against the pinned cc65: the
# command line, the project file and its globs, `--depfile`, `--c-header`, then ca65, cc65,
# ld65 and `remap-dbg`. The test suite compiles the same sources in process and assembles
# them through the oracle; what only a build reaches is `nt65 build` itself and the files it
# writes, which is why this is a gate step rather than a test.
#
# It drives the same steps as each program's build.sh and the interop Makefile. Those stay
# as the documented way to build one by hand; the gate does not take `make` or `sh` as a
# dependency for what PowerShell can run, and an incremental build is what the Makefile is
# for, not what a gate wants.
[CmdletBinding()]
param([string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$nt65 = Join-Path $root "src/Norristown.Cli/bin/$Configuration/net10.0/nt65.exe"
$bin = Join-Path $root '.cache/cc65/bin'
$ca65 = Join-Path $bin 'ca65.exe'
$cc65 = Join-Path $bin 'cc65.exe'
$ld65 = Join-Path $bin 'ld65.exe'

foreach ($tool in $nt65, $ca65, $ld65) {
    if (-not (Test-Path $tool)) {
        Write-Host "not built: $tool" -ForegroundColor Red
        exit 1
    }
}

# A clean assembly or link says nothing at all, so anything either of them says is a failure.
# nt65 is allowed to warn — the interop program's C header warns about two routines whose
# linker names C cannot spell, on purpose — and what it says is shown either way.
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

# Each program: where it is, what `nt65 build` is given, where the output lands, the linker
# configuration, the image, and the hand-written ca65 and C beside it.
$programs = @(
    @{ Name = 'c64';    Directory = 'tests/corpus/c64';    Build = @();
       Generated = 'build';             Config = 'c64.cfg';  Image = 'build/game.prg' }
    @{ Name = 'snes';   Directory = 'tests/corpus/snes';   Build = @();
       Generated = 'build';             Config = 'snes.cfg'; Image = 'build/game.sfc' }
    @{ Name = 'interop'; Directory = 'tests/corpus/interop';
       Build = @('--config', 'release', '--depfile', 'build/release/nt65.d', '--c-header', 'build/release/gen/nt65.h');
       Generated = 'build/release/gen'; Config = 'link.cfg';  Image = 'build/release/app.bin';
       HandWritten = 'asm'; HandWrittenCpu = '65c02'; C = 'c' }
    @{ Name = 'snes-hello'; Directory = 'examples/snes-hello'; Build = @();
       Generated = 'build';             Config = 'snes.cfg'; Image = 'build/hello.sfc' }
)

foreach ($program in $programs) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Push-Location (Join-Path $root $program.Directory)
    try {
        # From nothing every time: the gate is asking whether a build works, not whether an
        # incremental one does, and a stale object file would link either way.
        if (Test-Path 'build') { Remove-Item 'build' -Recurse -Force }
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
        # and the hand-written `asm/main.s` stands in for it in the image.
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
