# Packages into artifacts/: the nt65 command as a .NET tool, and the VS Code extension with the
# language server published into it. Both run on an installed .NET 10 runtime.
#   dotnet tool install --global nt65 --configfile artifacts/nuget.config
#   code --install-extension artifacts/nt65-<version>.vsix
# -Version packages as that version instead of the one in the tree, which is what the release
# workflow passes the tag as.
[CmdletBinding()]
param([string]$Version)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$extension = Join-Path $root 'editors/vscode'
$server = [IO.Path]::GetFullPath((Join-Path $extension 'server'))

New-Item -ItemType Directory -Force $artifacts | Out-Null
$packVersion = @(if ($Version) { "-p:Version=$Version" })
dotnet pack (Join-Path $root 'src/Norristown.Cli/Norristown.Cli.csproj') --nologo -v q -c Release -o $artifacts @packVersion

# A framework-dependent server, so one extension serves every platform .NET runs on. Windows
# locks a running server's files, and a delete that fails halfway leaves a server that cannot
# start, so a running one stops the script before anything is removed.
if ($IsWindows) {
    $running = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
        Where-Object { $_.CommandLine -like "*$server*" }
    if ($running) {
        throw "the packaged language server is running (pid $($running.ProcessId -join ', ')): close the VS Code windows using it first"
    }
}
if (Test-Path $server) { Remove-Item -Recurse -Force $server }
dotnet publish (Join-Path $root 'src/Norristown.LanguageServer/Norristown.LanguageServer.csproj') --nologo -v q `
    -c Release -o $server -p:UseAppHost=false -p:PublishDocumentationFile=false

Push-Location $extension
try {
    npm ci --no-audit --no-fund
    # A .vsix version is three numbers, so a pre-release tag packages as the release it
    # precedes; the package.json in the tree is left as it is either way.
    $vsceVersion = @(if ($Version) {
        $Version.Split('-')[0]; '--no-git-tag-version'; '--no-update-package-json'
    })
    npm run package -- --out $artifacts @vsceVersion
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
