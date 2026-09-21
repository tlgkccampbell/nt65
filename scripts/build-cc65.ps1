# Builds cc65, ca65 and ld65 from the cc65 commit pinned in scripts/cc65.commit into
# .cache/cc65/bin, with the headers C and its assembly include in .cache/cc65. The source checkout lives in
# .cache/cc65-src. Both are git-ignored.
# Needs git, make and a C compiler on the path; on Windows that is a MinGW gcc.
[CmdletBinding()]
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = Split-Path -Parent $PSScriptRoot
$sha = (Get-Content (Join-Path $PSScriptRoot 'cc65.commit') -Raw).Trim()
$src = Join-Path $root '.cache/cc65-src'
$bin = Join-Path $root '.cache/cc65/bin'
$short = $sha.Substring(0, 7)
# What make names the programs it builds: everything else here is the same on either system.
$exe = if ($IsWindows) { '.exe' } else { '' }

if (-not $Force -and (Test-Path (Join-Path $bin "ca65$exe")) -and (Test-Path (Join-Path $bin "cc65$exe"))) {
    $version = & (Join-Path $bin "ca65$exe") --version 2>&1 | Out-String
    if ($version -match "Git $short") {
        Write-Host "cc65, ca65 and ld65 at $short are already built."
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

make -C (Join-Path $src 'src') -j ([Environment]::ProcessorCount) cc65 ca65 ld65

New-Item -ItemType Directory -Force $bin | Out-Null
foreach ($tool in 'cc65', 'ca65', 'ld65') {
    Copy-Item -Force (Join-Path $src "bin/$tool$exe") $bin
}
foreach ($directory in 'include', 'asminc') {
    Copy-Item -Recurse -Force (Join-Path $src $directory) (Join-Path $root '.cache/cc65')
}
& (Join-Path $bin "ca65$exe") --version
