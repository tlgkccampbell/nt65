# Packs the screen and its colors into build/screens, which src/screens.nt65 includes: each as
# the bytes the C64 holds, in a .bin, and those bytes packed in the LZ4 block format, in a .lz4.
# build.ps1 runs it before nt65, which measures every file an .incbin names, and test.ps1 checks
# the .bin files against what the demo unpacked.
#
# screens/title.txt is the screen as 25 lines of up to 40 characters, each a letter, a digit,
# a space or one of the signs the C64's screen codes share with ASCII, or `#` for a solid block.
# screens/colors.txt is the colors as 25 lines of 40 hex digits, a color to each place.
[CmdletBinding()]
param([string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) 'build/screens'))

$ErrorActionPreference = 'Stop'
$example = Split-Path -Parent $PSScriptRoot

# The packer is greedy: at each place it takes the longest match, and the nearest of the longest.
# A sequence's token holds its literals' count and its match's count less 4, a nibble each, and
# a nibble of 15 goes on in the bytes after it, each added, up to one that is not 255. The
# format's rules for the end of a block hold, so that any LZ4 decoder reads what this writes:
# the last 5 bytes are literals, and no match starts in the last 12.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;

public static class Lz4Block
{
    public static byte[] Pack(byte[] input)
    {
        var output = new List<byte>();
        int n = input.Length, anchor = 0, i = 0;
        while (i < n - 12)
        {
            int bestLength = 0, bestOffset = 0;
            for (int j = Math.Max(0, i - 65535); j < i; j++)
            {
                int length = 0;
                while (i + length < n - 5 && input[j + length] == input[i + length])
                    length++;
                if (length >= 4 && length >= bestLength)
                {
                    bestLength = length;
                    bestOffset = i - j;
                }
            }
            if (bestLength == 0)
            {
                i++;
                continue;
            }
            int token = Literals(output, input, anchor, i - anchor);
            output.Add((byte)bestOffset);
            output.Add((byte)(bestOffset >> 8));
            output[token] |= (byte)Math.Min(bestLength - 4, 15);
            Extend(output, bestLength - 4);
            i += bestLength;
            anchor = i;
        }
        Literals(output, input, anchor, n - anchor);
        return output.ToArray();
    }

    // Writes a token with the literals' count, and the literals, and returns where the token is.
    private static int Literals(List<byte> output, byte[] input, int start, int count)
    {
        int token = output.Count;
        output.Add((byte)(Math.Min(count, 15) << 4));
        Extend(output, count);
        for (int k = 0; k < count; k++)
            output.Add(input[start + k]);
        return token;
    }

    // Writes the bytes that carry on a count too large for its nibble.
    private static void Extend(List<byte> output, int count)
    {
        if (count < 15)
            return;
        for (count -= 15; count >= 255; count -= 255)
            output.Add(255);
        output.Add((byte)count);
    }
}
'@

# Reads a file of 25 lines, each made 40 bytes long by `$convert`, which takes a line and gives
# its bytes.
function Read-Screen([string]$path, [scriptblock]$convert) {
    $lines = @(Get-Content $path)
    if ($lines.Count -ne 25) { throw "$path has $($lines.Count) lines, not 25" }
    $bytes = foreach ($line in $lines) {
        if ($line.Length -gt 40) { throw "$path has a line longer than 40: $line" }
        & $convert $line
    }
    [byte[]]$bytes
}

# A character as the screen code that shows it, in the uppercase and graphics characters.
function Screen-Code([char]$c) {
    if ($c -eq '#') { return 0xA0 }                    # a reversed space, which is solid
    if ($c -ge 'A' -and $c -le 'Z') { return [int]$c - 0x40 }
    if ($c -ge ' ' -and $c -le '?') { return [int]$c }
    if ($c -eq '@') { return 0 }
    throw "no screen code for `"$c`""
}

$screen = Read-Screen (Join-Path $example 'screens/title.txt') {
    param($line)
    foreach ($c in $line.PadRight(40).ToCharArray()) { Screen-Code $c }
}
$colors = Read-Screen (Join-Path $example 'screens/colors.txt') {
    param($line)
    if ($line.Length -ne 40) { throw "a line of colors is not 40 hex digits: $line" }
    foreach ($c in $line.ToCharArray()) { [Convert]::ToByte([string]$c, 16) }
}

New-Item -ItemType Directory -Force $Out | Out-Null
foreach ($file in @{ Name = 'title'; Bytes = $screen }, @{ Name = 'colors'; Bytes = $colors }) {
    [IO.File]::WriteAllBytes((Join-Path $Out "$($file.Name).bin"), $file.Bytes)
    [IO.File]::WriteAllBytes((Join-Path $Out "$($file.Name).lz4"), [Lz4Block]::Pack($file.Bytes))
}
