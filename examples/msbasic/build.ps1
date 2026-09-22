# Builds every target, or the ones named, into build/<target>/<target>.bin with nt65, ca65 and
# ld65 from the path or from the paths given, and compares each image with the original ROM,
# by the SHA-256 in original.sha256. Exits 1 if any build fails or any image differs.
[CmdletBinding()]
param(
    [string[]]$Target = @('cbmbasic1', 'cbmbasic2', 'kbdbasic', 'osi', 'kb9', 'applesoft',
                          'microtan', 'aim65', 'sym1', 'w65c816sxb'),
    [string]$Nt65 = 'nt65',
    [string]$Ca65 = 'ca65',
    [string]$Ld65 = 'ld65'
)

$ErrorActionPreference = 'Stop'
# A tool given as a path is found from where the script was run, not from here.
$Nt65, $Ca65, $Ld65 = foreach ($tool in $Nt65, $Ca65, $Ld65) {
    if (Test-Path $tool -PathType Leaf) { (Resolve-Path $tool).Path } else { (Get-Command $tool).Source }
}

function Run([string]$exe, [string[]]$arguments) {
    $said = (& $exe @arguments 2>&1 | Out-String).Trim()
    if ($said) { Write-Host $said }
    return $LASTEXITCODE -eq 0
}

Push-Location $PSScriptRoot
try {
    $originals = @{}
    foreach ($line in Get-Content original.sha256) {
        $name, $hash = $line -split ' '
        $originals[$name] = $hash
    }

    # nt65 is most of the time, and the targets are independent, each with its own output
    # directory, so every target's nt65 runs at once.
    $builds = foreach ($t in $Target) {
        $info = [Diagnostics.ProcessStartInfo]::new($Nt65)
        foreach ($argument in 'build', '--config', $t) { $info.ArgumentList.Add($argument) }
        $info.WorkingDirectory = $PSScriptRoot
        $info.RedirectStandardError = $true
        $info.UseShellExecute = $false
        [pscustomobject]@{ Target = $t; Process = [Diagnostics.Process]::Start($info) }
    }

    $failed = $false
    foreach ($build in $builds) {
        $t = $build.Target
        $said = $build.Process.StandardError.ReadToEnd().Trim()
        $build.Process.WaitForExit()
        if ($said) { Write-Host $said }
        # The root module places every other, so each target is one ca65 source and one object.
        $ok = $build.Process.ExitCode -eq 0 -and
              (Run $Ca65 @('-g', "build/$t/msbasic.s", '-o', "build/$t/msbasic.o")) -and
              (Run $Ld65 @('-C', "cfg/$t.cfg", '-o', "build/$t/$t.bin", '--dbgfile', "build/$t/$t.dbg",
                           '-m', "build/$t/$t.map", "build/$t/msbasic.o")) -and
              # The .s carries no debug directives; this points the debug file at the .nt65 sources.
              (Run $Nt65 @('remap-dbg', "build/$t/$t.dbg"))
        if (-not $ok) {
            Write-Host "$t failed" -ForegroundColor Red
            $failed = $true
        } elseif ((Get-FileHash "build/$t/$t.bin" -Algorithm SHA256).Hash.ToLowerInvariant() -ne $originals[$t]) {
            Write-Host "$t differs from the original" -ForegroundColor Red
            $failed = $true
        } else {
            Write-Host ('{0,-11} {1,6:N0} bytes, the same as the original' -f $t, (Get-Item "build/$t/$t.bin").Length)
        }
    }
    if ($failed) { exit 1 }
}
finally {
    Pop-Location
}
