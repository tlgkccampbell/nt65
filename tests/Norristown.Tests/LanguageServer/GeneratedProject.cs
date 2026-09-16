using System.Globalization;
using System.Text;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// A 65816 program of many files, each shaped like a module of a real one: constants and data
/// it exports, a macro, and routines that call the file before it and use its constants and
/// macro. It exists to measure what an edit costs, and to replay edits against.
/// </summary>
internal static class GeneratedProject
{
    /// <summary>The URI of file <paramref name="index"/>.</summary>
    public static string Uri(int index) => $"file:///c:/generated/mod{index:D3}.nt65";

    /// <summary>The text of file <paramref name="index"/> of a program of <paramref name="count"/> files.</summary>
    public static string Text(int index, int count)
    {
        var i = index.ToString("D3", CultureInfo.InvariantCulture);
        var before = ((index + count - 1) % count).ToString("D3", CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $$"""
            .cpu 65816

            .export m{{i}}_init, m{{i}}_step, m{{i}}_fill, M{{i}}_SIZE, m{{i}}_table, m{{i}}_put

            M{{i}}_SIZE = {{16 + index % 32}}
            M{{i}}_LIMIT = M{{before}}_SIZE * 2

            .bss {
            m{{i}}_state:   .res M{{i}}_SIZE
            m{{i}}_count:   .res 2
            }

            .rodata {
            m{{i}}_table:   .byte 1, 2, 3, 4, 5, 6, 7, 8
            }

            .macro m{{i}}_put(dest: operand, value) {
                lda #<value
                sta dest
                lda #>value
                sta dest+1
            }

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
}
