using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

/// <summary>
/// The formatter's single layout: what a block holds indented once, cheap locals at their
/// routine's margin, a run of named data lines on one column, and nothing after the last token
/// of a line.
/// </summary>
public sealed class FormatterTests
{
    /// <summary>A block indents what it holds, and its <c>}</c> goes back to the opener's margin.</summary>
    [Fact]
    public void ABlockIndentsWhatItHolds()
    {
        Assert.Equal("""
            .module main
            .segment CODE
            .proc main {
                lda #1
                .scope {
                    rts
                }
            }
            """, Formatted("""
            .module main
                .segment CODE
              .proc main {
            lda #1
                    .scope {
              rts
                        }
                  }
            """));
    }

    /// <summary>
    /// A <c>.segment NAME</c> region opens a block with no brace, holding the rest of the file,
    /// so what follows one is written where the region line is.
    /// </summary>
    [Fact]
    public void ARegionIndentsNothing()
    {
        Assert.Equal("""
            .module main
            .segment CODE
            .proc main {
                rts
            }
            .segment RODATA
            .data one: .byte 1
            """, Formatted("""
            .module main
            .segment CODE
                .proc main {
                rts
                }
                .segment RODATA
                    .data one: .byte 1
            """));
    }

    /// <summary>
    /// A cheap local names a place in the routine around it, not another instruction, so it is
    /// written at the margin of whatever holds the instructions it points into.
    /// </summary>
    [Fact]
    public void ACheapLocalSitsAtItsRoutinesMargin()
    {
        Assert.Equal("""
            .module main
            .segment CODE
            .proc main {
                ldx #4
            @loop:
                dex
                .if DEBUG {
                @again:
                    bne @again
                }
                bne @loop
                rts
            }
            """, Formatted("""
            .module main
            .segment CODE
            .proc main {
                    ldx #4
                @loop:
                    dex
                .if DEBUG {
                    @again:
                        bne @again
                }
                    bne @loop
                    rts
            }
            """));
    }

    /// <summary>
    /// A run of named data lines lines up on its directives, one space past the longest name in
    /// the run. Members of a layout are named data lines too, at the indent the block gives them.
    /// </summary>
    [Fact]
    public void ARunOfNamedDataLinesLinesUp()
    {
        Assert.Equal("""
            .module main
            .struct Player {
                pos:  .word
                hp:   .byte
                name: .res 8
            }
            .segment BSS
            .data one:    .byte
            .data longer: .word
            """, Formatted("""
            .module main
            .struct Player {
            pos: .word
            hp:            .byte
            name:.res 8
            }
            .segment BSS
            .data one: .byte
            .data longer:                 .word
            """));
    }

    /// <summary>
    /// A comment line does not end a run, so a comment about the next declaration can sit inside
    /// the run; an empty line, or any other line that is not a named data line, ends it.
    /// </summary>
    [Fact]
    public void ACommentCarriesARunThroughAndAnEmptyLineEndsIt()
    {
        Assert.Equal("""
            .module main
            .segment BSS
            .data one:    .byte
            ; What the long one is for.
            .data longer: .word

            .data short: .byte
            """, Formatted("""
            .module main
            .segment BSS
            .data one: .byte
              ; What the long one is for.
            .data longer: .word

            .data short:           .byte
            """));
    }

    /// <summary>
    /// Only a declaration's <c>:</c> is lined up. Any other <c>:</c> is an address-size prefix
    /// or the start of a signature, and what follows it is never a directive, so no run lines it
    /// up.
    /// </summary>
    [Fact]
    public void OnlyADeclarationsColonLinesUp()
    {
        const string written = """
            .module main
            .import scratch: zp
            .segment CODE
            .proc main: a8, i8 {
                lda z:scratch
                rts
            }
            """;
        Assert.Equal(written, Formatted(written));
    }

    /// <summary>Nothing follows a line's last token, and an empty line is empty.</summary>
    [Fact]
    public void NothingIsLeftAtTheEndOfALine()
    {
        Assert.Equal(".module main\n\n.segment CODE   ; here\n",
            Formatted(".module main   \n   \t \n.segment CODE   ; here  \t\n"));
    }

    /// <summary>
    /// A file whose braces do not balance still formats: the block layer recovers, so what is
    /// half-typed is laid out as far as it was understood rather than left alone.
    /// </summary>
    [Fact]
    public void AFileWithAMissingBraceStillFormats()
    {
        Assert.Equal("""
            .module main
            .segment CODE
            .proc main {
                rts
            .proc other {
                rts
            }
            """, Formatted("""
            .module main
            .segment CODE
            .proc main {
            rts
            .proc other {
            rts
            }
            """));
    }

    /// <summary>
    /// Formatting a range moves the lines in it and nothing else, but the column a run lines up
    /// on is still worked out from the whole run, so formatting one of its lines lines it up with
    /// the others.
    /// </summary>
    [Fact]
    public void ARangeMovesOnlyItsOwnLines()
    {
        var tree = SyntaxTree.Parse("main.nt65", """
            .module main
            .segment BSS
            .data one: .byte
            .data longer: .word
                 .data third: .byte

            """.ReplaceLineEndings("\n"));

        var changes = Formatter.Changes(tree, 4, 4);
        var change = Assert.Single(changes);
        Assert.Equal(".data third:  .byte", change.NewText);
        Assert.Equal(tree.LineStarts[4], change.Start);
    }

    /// <summary>Formatting text that is already formatted produces no changes.</summary>
    [Fact]
    public void FormattingWhatIsFormattedChangesNothing()
    {
        var once = Formatted("""
            .module main
            .struct Pair {
            lo: .byte
            hi:      .byte
            }
            .segment CODE
                .proc main {
            @loop:
                    bne @loop
            }
            """);
        var tree = SyntaxTree.Parse("main.nt65", once);
        Assert.Empty(Formatter.Changes(tree, 0, tree.Lines.Length - 1));
        Assert.Equal(once, Formatter.Format(tree));
    }

    /// <summary>Every line keeps its own break, whatever the file is written with.</summary>
    [Fact]
    public void LineBreaksAreLeftAsTheyAre()
    {
        Assert.Equal(".module main\r\n.segment CODE\r\n.proc main {\r\n    rts\r\n}\r\n",
            Formatter.Format(SyntaxTree.Parse("main.nt65", ".module main\r\n.segment CODE\r\n.proc main {\r\nrts\r\n}\r\n")));
    }

    private static string Formatted(string text) =>
        Formatter.Format(SyntaxTree.Parse("main.nt65", text.ReplaceLineEndings("\n")));
}
