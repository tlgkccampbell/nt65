# Packages nt65 from the working tree and installs both packages on this machine: the nt65 command
# as a global .NET tool, and the extension into VS Code.
#   pwsh scripts/install.ps1
# Each run packages a version of its own, 0.0.0-dev.<timestamp>, because NuGet caches a package by
# its version and would otherwise install the copy of 0.0.0-dev it already holds. `nt65 --version`
# reports the stamp, so it also says which build is installed.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$version = "0.0.0-dev.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"

# The stamped packages of earlier runs would otherwise pile up in artifacts/.
Remove-Item (Join-Path $artifacts 'nt65.*.nupkg') -ErrorAction Ignore
& (Join-Path $PSScriptRoot 'package.ps1') -Version $version

# `dotnet tool update` replaces an installed tool; a tool that is running, such as `nt65 lsp`
# under another editor, holds its files and makes the update fail.
$config = Join-Path $artifacts 'nuget.config'
$installed = dotnet tool list --global | Select-String -Pattern '^nt65\s' -Quiet
$verb = if ($installed) { 'update' } else { 'install' }
dotnet tool $verb --global nt65 --version $version --configfile $config

# A .vsix version has no pre-release part, so every run packages 0.0.0; --force reinstalls it.
$vsix = Join-Path $artifacts 'nt65-0.0.0.vsix'
code --install-extension $vsix --force

nt65 --version
Write-Host 'Run "Developer: Reload Window" in open VS Code windows to start the new extension.'
