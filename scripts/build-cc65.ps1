# Builds cc65, ca65 and ld65 from the cc65 commit pinned in scripts/cc65.commit into
# .cache/cc65/bin, and copies cc65's C headers (include) and assembly includes (asminc) into
# .cache/cc65. The source checkout lives in .cache/cc65-src. Both are git-ignored.
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
# The suffix make gives the programs it builds, which is the only part of this script that
# differs between Windows and other systems.
$exe = if ($IsWindows) { '.exe' } else { '' }

# The build is skipped only when every tool it builds is there, so a missing one is built again.
$built = @('cc65', 'ca65', 'ld65' | Where-Object { Test-Path (Join-Path $bin "$_$exe") }).Count -eq 3
if (-not $Force -and $built) {
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
