# Writes the syntax classes the node table describes: the red node classes under
# Syntax/Nodes/Generated, the kind-to-class switch and the visitors. -Check compares instead and
# fails when what is checked in is stale. The generator references nothing, so this runs whatever
# state the code it writes is in: change the table, regenerate, then fix what the compiler says.
[CmdletBinding()]
param(
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'tools/Norristown.SyntaxGenerator/Norristown.SyntaxGenerator.csproj'

$arguments = @('run', '--project', $project, '--nologo', '-v', 'q')
if ($Check) { $arguments += @('--', '--check') }
dotnet @arguments
exit $LASTEXITCODE
