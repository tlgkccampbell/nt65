using System.Globalization;
using System.Text;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Builds a 65816 program of many modules, each shaped like a module of a real program. Each
/// module exports constants and data and declares a macro. Its routines call the module before
/// it and use that module's constants and macro, which it brings in with <c>.use</c>. Every
/// module also uses the first module's <c>M000_LIMIT</c>, as a real program uses its shared
/// definitions. The program is used to measure what an edit costs and to replay edits against.
/// </summary>
internal static class GeneratedProject
{
    /// <summary>Returns the URI of file <paramref name="index"/>.</summary>
    public static string Uri(int index) => $"file:///c:/generated/mod{index:D3}.nt65";

    /// <summary>
    /// Returns the text of file <paramref name="index"/> in a program of <paramref name="count"/> files.
    /// </summary>
    public static string Text(int index, int count)
    {
        var i = index.ToString("D3", CultureInfo.InvariantCulture);
        var before = ((index + count - 1) % count).ToString("D3", CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $$"""
            .module mod{{i}}
            .cpu 65816
            {{Uses(index, count)}}

            .export m{{i}}_init, m{{i}}_step, m{{i}}_fill, M{{i}}_SIZE, M{{i}}_LIMIT, m{{i}}_table, m{{i}}_put

            .const M{{i}}_SIZE = {{16 + index % 32}}
            .const M{{i}}_LIMIT = M{{before}}_SIZE * 2

            .segment BSS
            .data m{{i}}_state:   .byte[M{{i}}_SIZE]
            .data m{{i}}_count:   .word

            .segment RODATA
            .data m{{i}}_table:   .byte 1, 2, 3, 4, 5, 6, 7, 8

            .macro m{{i}}_put(dest: operand, value) {
                lda #<value
                sta dest
                lda #>value
                sta dest+1
            }

            .segment CODE
            .proc m{{i}}_init: a8, i8 {
                ldx #0
            @loop:
                lda m{{i}}_table,x
                sta m{{i}}_state,x
                inx
                cpx #8
                bne @loop
                jsr m{{before}}_step
                m{{before}}_put!(m{{i}}_count, M{{i}}_LIMIT)
                rts
            }

            .proc m{{i}}_step: a8, i8 {
                lda m{{i}}_count
                and #M000_LIMIT
                clc
                adc #1
                sta m{{i}}_count
                cmp #M{{i}}_SIZE
                bcc @done
                lda #0
                sta m{{i}}_count
            @done:
                rts
            }

            .proc m{{i}}_fill: a8, i8 -> a8, i8 {
                rep #$20
                lda #$1234
                ldx #M{{before}}_SIZE
            @again:
                sta m{{i}}_state,x
                dex
                bne @again
                sep #$20
                jsr m{{before}}_fill
                rts
            }

            """);
        return text.ToString().ReplaceLineEndings("\n");
    }

    /// <summary>
    /// Returns the <c>.use</c> lines by which file <paramref name="index"/> brings in names from
    /// the file before it and from the first file.
    /// </summary>
    private static string Uses(int index, int count)
    {
        var before = (index + count - 1) % count;
        var b = before.ToString("D3", CultureInfo.InvariantCulture);
        var uses = new List<string>();
        if (before != index)
            uses.Add($".use mod{b}::{{M{b}_SIZE, m{b}_step, m{b}_put, m{b}_fill}}");
        if (index != 0)
            uses.Add(".use mod000::M000_LIMIT");
        return string.Join("\n", uses);
    }
}
