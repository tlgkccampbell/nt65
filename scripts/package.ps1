# Packages version 1 into artifacts/: the nt65 command as a .NET tool, and the VS Code extension
# with the language server published into it. Both run on an installed .NET 10 runtime.
#   dotnet tool install --global nt65 --configfile artifacts/nuget.config
#   code --install-extension artifacts/nt65-<version>.vsix
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$extension = Join-Path $root 'editors/vscode'
$server = Join-Path $extension 'server'

New-Item -ItemType Directory -Force $artifacts | Out-Null
dotnet pack (Join-Path $root 'src/Norristown.Cli/Norristown.Cli.csproj') --nologo -v q -c Release -o $artifacts

# A framework-dependent server, so one extension serves every platform .NET runs on.
if (Test-Path $server) { Remove-Item -Recurse -Force $server }
dotnet publish (Join-Path $root 'src/Norristown.LanguageServer/Norristown.LanguageServer.csproj') --nologo -v q `
    -c Release -o $server -p:UseAppHost=false -p:PublishDocumentationFile=false

Push-Location $extension
try {
    npm ci --no-audit --no-fund
    npm run package -- --out $artifacts
}
finally {
    Pop-Location
}
# A package source of the artifacts alone, which works where a NuGet configuration maps
# package sources and `--add-source` is refused.
@'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nt65" value="." />
  </packageSources>
  <packageSourceMapping>
    <clear />
  </packageSourceMapping>
</configuration>
'@ | Set-Content (Join-Path $artifacts 'nuget.config')

Get-ChildItem $artifacts | Format-Table Name, Length
