# Runs the monitor's sessions in emulators and checks what each leaves on the screen. Build
# first with build.ps1. A session is tests/<platform>/<name>.txt: the screen as it should be, in
# which the lines that start with the monitor's `.` prompt are what is typed. -Update writes
# each screen as it came out instead. Exits 1 if any session differs or does not finish.
[CmdletBinding()]
param(
    [string[]]$Platform = @(),
    [string[]]$Session = @(),
    [string]$Vice,
    [string]$Mame,
    [switch]$Update
)

$ErrorActionPreference = 'Stop'

# Each platform's emulator, the file it runs, and the routines where a session starts typing
# and where it ends. VICE saves the C64's screen memory, which is read here; MAME's script reads
# the IIGS's screen itself.
$platforms = [ordered]@{
    c64      = @{ Emulator = 'x64sc'; Given = $Vice; Image = 'monitor.prg'
                  Start = 'monitor__main'; End = 'platform__exit'; Screen = '0400 07e7'; Columns = 40 }
    apple2gs = @{ Emulator = 'mame'; Given = $Mame; Image = 'monitor.bin'; Load = 0x2000
                  Start = 'monitor__main'; End = 'platform__exit' }
}

# An emulator from the path, or from the folder or program given.
function Emulator([string]$name, [string]$given) {
    if (-not $given) { return (Get-Command $name).Source }
    if (Test-Path $given -PathType Container) { return (Get-ChildItem $given -Filter "$name*.exe" -Recurse | Select-Object -First 1).FullName }
    return (Resolve-Path $given).Path
}

# A C64 screen code as the character it shows: $00 to $1F are `@`, the capitals and a few
# signs, $20 to $3F are ASCII, and bit 7 is the cursor's reverse video.
function Character([byte]$code) {
    $code = $code -band 0x7F
    if ($code -lt 0x20) { return [char]($code + 0x40) }
    if ($code -lt 0x40) { return [char]$code }
    return '#'
}

# Starts VICE on a session: it types the lines with its monitor's `keybuf` as soon as the
# program reaches the start, saves screen memory when it reaches the end, and leaves through
# the debug cartridge, with a POKE typed after the `X` that ends every session.
function Start-Vice($settings, $emulator, $image, $labels, $lines, $screen, $work, $name) {
    # Typed in lower case, which the C64's keyboard sends as the capitals it shows.
    $typed = ($lines | ForEach-Object { $_.ToLowerInvariant() + '\\x0d' }) -join ''
    $typed += 'poke 55295,0\\x0d'
    $script = Join-Path $work "$name.mon"
    @(
        "tr exec `$$($labels[$settings.Start])"
        "command 1 `"keybuf $typed`""
        "tr exec `$$($labels[$settings.End])"
        "command 2 `"bsave \`"$($screen.Replace('\', '/'))\`" 0 $($settings.Screen)`""
    ) | Set-Content $script
    $arguments = @('-default', '-console', '-warp', '+sound', '-debugcart', '-limitcycles', '20000000',
                   '-autostartprgmode', '1', '-moncommands', $script, '-autostart', $image)
    Start-Process $emulator -ArgumentList $arguments -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $work "$name.log")
}

# Starts MAME on a session, with a script that sets what mame-session.lua is to do and runs it.
function Start-Mame($settings, $emulator, $image, $labels, $lines, $screen, $work, $name) {
    $typed = ($lines | ForEach-Object { $_ + '\r' }) -join ''
    $script = Join-Path $work "$name.lua"
    @(
        'session = {'
        "    image = [[$image]],"
        "    load = 0x$('{0:X4}' -f $settings.Load),"
        "    start = 0x$($labels[$settings.Start]),"
        "    finish = 0x$($labels[$settings.End]),"
        "    typed = `"$typed`","
        "    screen = [[$screen]],"
        '}'
        # MAME runs the script in an environment of its own, which the other has to share.
        "assert(loadfile([[$(Join-Path $PSScriptRoot 'mame-session.lua')]], 't', _ENV))()"
    ) | Set-Content $script
    # The ROMs are looked for beside MAME. What MAME writes of its own goes in the work folder,
    # and a session stops after a minute of the IIGS's time.
    $roms = Join-Path (Split-Path $emulator) 'roms'
    $arguments = @('apple2gs', '-rompath', "`"$roms`"", '-video', 'none', '-sound', 'none', '-nothrottle',
                   '-skip_gameinfo', '-nonvram_save', '-seconds_to_run', '60', '-debug', '-debugger', 'none',
                   '-autoboot_script', "`"$script`"")
    Start-Process $emulator -ArgumentList $arguments -PassThru -WindowStyle Hidden -WorkingDirectory $work `
        -RedirectStandardOutput (Join-Path $work "$name.log")
}

# The screen a session left, a row to a line.
function Read-Screen($settings, $screen) {
    if ($settings.Emulator -eq 'mame') { return @(Get-Content $screen) }
    $bytes = [IO.File]::ReadAllBytes($screen)
    $columns = $settings.Columns
    for ($row = 0; $row -lt $bytes.Length / $columns; $row++) {
        (-join ($bytes[($row * $columns)..($row * $columns + $columns - 1)] | ForEach-Object { Character $_ })).TrimEnd()
    }
}

# Every session of every platform at once, each in an emulator of its own: most of each is the
# emulator starting.
$runs = foreach ($p in $platforms.Keys) {
    if ($Platform.Count -gt 0 -and $p -notin $Platform) { continue }
    $settings = $platforms[$p]
    $build = Join-Path $PSScriptRoot "build/$p"
    $image = Join-Path $build $settings.Image
    if (-not (Test-Path $image)) { throw "$p is not built: run build.ps1 first" }
    $labels = @{}
    foreach ($line in Get-Content (Join-Path $build 'monitor.lbl')) {
        $_, $address, $name = $line -split ' '
        $labels[$name.TrimStart('.')] = $address.Substring(2)
    }
    $emulator = Emulator $settings.Emulator $settings.Given
    $work = Join-Path $build 'tests'
    New-Item -ItemType Directory -Force $work | Out-Null
    $start = if ($settings.Emulator -eq 'mame') { 'Start-Mame' } else { 'Start-Vice' }

    foreach ($file in Get-ChildItem (Join-Path $PSScriptRoot "tests/$p") -Filter *.txt | Sort-Object Name) {
        if ($Session.Count -gt 0 -and $file.BaseName -notin $Session) { continue }
        $expected = @(Get-Content $file.FullName)
        $lines = @($expected | Where-Object { $_.StartsWith('.') } | ForEach-Object { $_.Substring(1) })
        $screen = Join-Path $work "$($file.BaseName).screen"
        Remove-Item $screen -ErrorAction SilentlyContinue
        [pscustomobject]@{
            Platform = $p; Settings = $settings; File = $file; Expected = $expected; Screen = $screen
            Process = & $start $settings $emulator $image $labels $lines $screen $work $file.BaseName
        }
    }
}

$failed = $false
foreach ($run in $runs) {
    $run.Process.WaitForExit()
    $name = "$($run.Platform)/$($run.File.BaseName)"
    if ($run.Process.ExitCode -ne 0 -or -not (Test-Path $run.Screen)) {
        Write-Host "$name did not finish: see build/$($run.Platform)/tests/$($run.File.BaseName).log" -ForegroundColor Red
        $failed = $true
        continue
    }
    $shown = @(Read-Screen $run.Settings $run.Screen)
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
if ($failed) { exit 1 }
