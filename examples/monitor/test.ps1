# Runs the monitor's sessions in VICE and checks what each leaves on the screen. Build first
# with build.ps1. A session is tests/<platform>/<name>.txt: the screen as it should be, in
# which the lines that start with the monitor's `.` prompt are what is typed. -Update writes
# each screen as it came out instead. Exits 1 if any session differs or does not finish.
[CmdletBinding()]
param(
    [string[]]$Session = @(),
    [string]$Vice,
    [switch]$Update
)

$ErrorActionPreference = 'Stop'

# Each platform's emulator, the file it runs, the routines where a session starts typing and
# where it ends, and where the screen is.
$platforms = @{
    c64 = @{ Emulator = 'x64sc'; Image = 'monitor.prg'; Start = 'monitor__main'; End = 'platform__exit'
             Screen = '0400 07e7'; Columns = 40 }
}

# VICE from the path, or from the folder or program given.
function Emulator([string]$name) {
    if (-not $Vice) { return (Get-Command $name).Source }
    if (Test-Path $Vice -PathType Container) { return (Get-ChildItem $Vice -Filter "$name*" -Recurse | Select-Object -First 1).FullName }
    return (Resolve-Path $Vice).Path
}

# A C64 screen code as the character it shows: $00 to $1F are `@`, the capitals and a few
# signs, $20 to $3F are ASCII, and bit 7 is the cursor's reverse video.
function Character([byte]$code) {
    $code = $code -band 0x7F
    if ($code -lt 0x20) { return [char]($code + 0x40) }
    if ($code -lt 0x40) { return [char]$code }
    return '#'
}

$failed = $false
foreach ($p in $platforms.Keys) {
    $settings = $platforms[$p]
    $build = Join-Path $PSScriptRoot "build/$p"
    $image = Join-Path $build $settings.Image
    if (-not (Test-Path $image)) { throw "$p is not built: run build.ps1 first" }
    $labels = @{}
    foreach ($line in Get-Content (Join-Path $build 'monitor.lbl')) {
        $_, $address, $name = $line -split ' '
        $labels[$name.TrimStart('.')] = $address.Substring(2)
    }
    $emulator = Emulator $settings.Emulator
    $work = Join-Path $build 'tests'
    New-Item -ItemType Directory -Force $work | Out-Null

    # Every session at once, each in an emulator of its own: most of each is VICE starting.
    $runs = foreach ($file in Get-ChildItem (Join-Path $PSScriptRoot "tests/$p") -Filter *.txt | Sort-Object Name) {
        if ($Session.Count -gt 0 -and $file.BaseName -notin $Session) { continue }
        $expected = @(Get-Content $file.FullName)
        # Typed in lower case, which the C64's keyboard sends as the capitals it shows, and
        # ended with a POKE from BASIC that the debug cartridge takes as the way out.
        $typed = ($expected | Where-Object { $_.StartsWith('.') } | ForEach-Object { $_.Substring(1).ToLowerInvariant() + '\\x0d' }) -join ''
        $typed += 'poke 55295,0\\x0d'
        $screen = Join-Path $work "$($file.BaseName).screen"
        Remove-Item $screen -ErrorAction SilentlyContinue
        $script = Join-Path $work "$($file.BaseName).mon"
        @(
            "tr exec `$$($labels[$settings.Start])"
            "command 1 `"keybuf $typed`""
            "tr exec `$$($labels[$settings.End])"
            "command 2 `"bsave \`"$($screen.Replace('\', '/'))\`" 0 $($settings.Screen)`""
        ) | Set-Content $script
        $arguments = @('-default', '-console', '-warp', '+sound', '-debugcart', '-limitcycles', '20000000',
                       '-autostartprgmode', '1', '-moncommands', $script, '-autostart', $image)
        [pscustomobject]@{
            File = $file; Expected = $expected; Screen = $screen
            Process = Start-Process $emulator -ArgumentList $arguments -PassThru -WindowStyle Hidden `
                -RedirectStandardOutput (Join-Path $work "$($file.BaseName).log")
        }
    }

    foreach ($run in $runs) {
        $run.Process.WaitForExit()
        $name = "$p/$($run.File.BaseName)"
        if ($run.Process.ExitCode -ne 0 -or -not (Test-Path $run.Screen)) {
            Write-Host "$name did not finish: see build/$p/tests/$($run.File.BaseName).log" -ForegroundColor Red
            $failed = $true
            continue
        }
        $bytes = [IO.File]::ReadAllBytes($run.Screen)
        $columns = $settings.Columns
        $shown = for ($row = 0; $row -lt $bytes.Length / $columns; $row++) {
            (-join ($bytes[($row * $columns)..($row * $columns + $columns - 1)] | ForEach-Object { Character $_ })).TrimEnd()
        }
        $shown = @($shown)
        while ($shown.Count -gt 0 -and $shown[-1] -eq '') { $shown = @($shown[0..($shown.Count - 2)]) }
        if ($Update) {
            [IO.File]::WriteAllText($run.File.FullName, (($shown -join "`n") + "`n"))
            Write-Host "$name written"
        } elseif (Compare-Object $run.Expected $shown -SyncWindow 0) {
            Write-Host "$name differs; the screen showed:" -ForegroundColor Red
            $shown | ForEach-Object { Write-Host "  $_" }
            $failed = $true
        } else {
            Write-Host "$name passed"
        }
    }
}
if ($failed) { exit 1 }
