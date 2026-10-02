# Builds cc65's tools from the cc65 commit pinned in scripts/cc65.commit into .cache/cc65/bin,
# along with the sim6502 target's runtime library and linker configuration in .cache/cc65/lib
# and .cache/cc65/cfg, and copies cc65's C headers (include) and assembly includes (asminc) into
# .cache/cc65. That layout is the one cl65 searches beside its own directory, so cl65 finds
# everything without CC65_HOME. The source checkout lives in .cache/cc65-src. Both are
# git-ignored. Needs git, make and a C compiler on the path; on Windows that is a MinGW gcc.
[CmdletBinding()]
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = Split-Path -Parent $PSScriptRoot
$sha = (Get-Content (Join-Path $PSScriptRoot 'cc65.commit') -Raw).Trim()
$src = Join-Path $root '.cache/cc65-src'
$cache = Join-Path $root '.cache/cc65'
$bin = Join-Path $cache 'bin'
$short = $sha.Substring(0, 7)
# The suffix make gives the programs it builds, which is the only part of this script that
# differs between Windows and other systems.
$exe = if ($IsWindows) { '.exe' } else { '' }
# ar65 is needed only to build the library. sim65 runs the example that calls nt65 from C.
$tools = 'cc65', 'ca65', 'ld65', 'ar65', 'cl65', 'sim65'
$outputs = @($tools | ForEach-Object { "bin/$_$exe" }) + 'lib/sim6502.lib', 'cfg/sim6502.cfg'

# The build is skipped only when everything it builds is there, so a missing piece is built again.
$built = @($outputs | Where-Object { Test-Path (Join-Path $cache $_) }).Count -eq $outputs.Count
if (-not $Force -and $built) {
    $version = & (Join-Path $bin "ca65$exe") --version 2>&1 | Out-String
    if ($version -match "Git $short") {
        Write-Host "cc65's tools and the sim6502 library at $short are already built."
        exit 0
    }
}

if (-not (Test-Path (Join-Path $src '.git'))) {
    New-Item -ItemType Directory -Force $src | Out-Null
    git -C $src init --quiet
    git -C $src remote add origin https://github.com/cc65/cc65.git
}
git -C $src fetch --quiet --depth 1 origin $sha
git -C $src checkout --quiet --force FETCH_HEAD

make -C (Join-Path $src 'src') -j ([Environment]::ProcessorCount) @tools
# The library's makefile writes into ../lib but expects the top-level makefile to have made it.
New-Item -ItemType Directory -Force (Join-Path $src 'lib') | Out-Null
make -C (Join-Path $src 'libsrc') -j ([Environment]::ProcessorCount) sim6502

# A file is copied only when it differs, so a tool another build is running is left alone
# when there is nothing new to put in its place.
function Update([string]$from, [string]$to) {
    if (-not (Test-Path $to) -or (Get-FileHash $from).Hash -ne (Get-FileHash $to).Hash) {
        New-Item -ItemType Directory -Force (Split-Path -Parent $to) | Out-Null
        Copy-Item -Force $from $to
    }
}
foreach ($output in $outputs) {
    Update (Join-Path $src $output) (Join-Path $cache $output)
}
foreach ($directory in 'include', 'asminc') {
    Copy-Item -Recurse -Force (Join-Path $src $directory) $cache
}
& (Join-Path $bin "ca65$exe") --version
