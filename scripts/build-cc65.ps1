# Builds ca65 and ld65 from the cc65 commit pinned in scripts/cc65.commit into
# .cache/cc65/bin. The source checkout lives in .cache/cc65-src. Both are git-ignored.
# Needs git, make and a MinGW gcc on PATH.
[CmdletBinding()]
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = Split-Path -Parent $PSScriptRoot
$sha = (Get-Content (Join-Path $PSScriptRoot 'cc65.commit') -Raw).Trim()
$src = Join-Path $root '.cache/cc65-src'
$bin = Join-Path $root '.cache/cc65/bin'
$short = $sha.Substring(0, 7)

if (-not $Force -and (Test-Path (Join-Path $bin 'ca65.exe'))) {
    $version = & (Join-Path $bin 'ca65.exe') --version 2>&1 | Out-String
    if ($version -match "Git $short") {
        Write-Host "ca65 and ld65 at $short are already built."
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

make -C (Join-Path $src 'src') -j $env:NUMBER_OF_PROCESSORS ca65 ld65

New-Item -ItemType Directory -Force $bin | Out-Null
foreach ($tool in 'ca65', 'ld65') {
    Copy-Item -Force (Join-Path $src "bin/$tool.exe") $bin
}
& (Join-Path $bin 'ca65.exe') --version
