using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

public sealed class OutlineTests
{
    /// <summary>Every declaration Stage 3 names, nested three deep and with a broken line in it.</summary>
    private const string Sample = """
        PPU_CTRL = $2000

        .segment "ZEROPAGE": zp
        .zeropage {
        ptr: .res 2
        }

        .proc reset: a8, i16 {
            ldx #0
        @loop:
            sta $0200,x
            .scope {
            @loop:
                nop
            }
            rts
        }

        .segment "RODATA" {
        table: .byte 1, 2, 3
        }

        .scope gfx {
            .proc init {
                rts
            }
        }

        .proc chrout = $ffd2: a8
        """;

    [Fact]
    public void MacrosAreNamedWithTheirParameters()
    {
        var tree = SyntaxTree.Parse("main.nt65", """
            .macro set16(dest: operand, value) {
                lda #<value
            @done:
                sta dest
            }

            .macro add16(a: operand, b: operand): a8 {
                clc
            }
            """);
        Assert.Equal("""
            Macro set16 1-5 [(dest: operand, value)]
              Label @done 3-3
            Macro add16 7-9 [(a: operand, b: operand): a8]

            """.ReplaceLineEndings("\n"), SyntaxDump.Symbols(tree));
    }

    [Fact]
    public void OutlineNestsDeclarationsTheWayBlocksDo()
    {
        var tree = SyntaxTree.Parse("main.nt65", Sample);
        Assert.Equal("""
            Constant PPU_CTRL 1-1 [$2000]
            Segment .zeropage 4-6
              Label ptr 5-5 [.res 2]
            Proc reset 8-17 [: a8, i16]
              Label @loop 10-10
              Scope .scope 12-15
                Label @loop 13-13
            Segment RODATA 19-21 [.segment]
              Label table 20-20 [.byte 1, 2, 3]
            Scope gfx 23-27
              Proc init 24-26
            Proc chrout 29-29 [= $ffd2: a8]

            """.ReplaceLineEndings("\n"), SyntaxDump.Symbols(tree));
    }

    [Fact]
    public void EveryBlockOfMoreThanOneLineFolds()
    {
        var tree = SyntaxTree.Parse("main.nt65", Sample);
        Assert.Equal("""
            4-6
            8-17
            12-15
            19-21
            23-27
            24-26

            """.ReplaceLineEndings("\n"), SyntaxDump.FoldingRanges(tree));
    }

    /// <summary>
    /// A block Stage 3 does not name — an `.if`, a macro body — is not a level of the
    /// outline, so what it declares stands where the block does rather than disappearing.
    /// </summary>
    [Fact]
    public void ABlockWithNoNameOfItsOwnGivesUpItsContents()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".proc p {\n.if DEBUG {\nmark: nop\n}\n}\n");
        Assert.Equal("Proc p 1-5\n  Label mark 3-3 [nop]\n", SyntaxDump.Symbols(tree));
    }

    /// <summary>
    /// The outline is built from whatever parsed, so a file being typed still has one. An
    /// item ends at its last token, which is why the block left open by the missing <c>}</c>
    /// stops at line 2 rather than running to the blank line the recovery closed it on.
    /// </summary>
    [Fact]
    public void AHalfTypedFileStillOutlines()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".proc p {\nlda #\n\n.proc q {\nrts\n}\n");
        Assert.NotEmpty(tree.Diagnostics);
        Assert.Equal("Proc p 1-2\nProc q 4-6\n", SyntaxDump.Symbols(tree));
    }

    /// <summary>A name span is inside the item's span, which is what an editor's reveal needs.</summary>
    [Fact]
    public void NameSpansNameTheDeclaration()
    {
        var tree = SyntaxTree.Parse("main.nt65", Sample);
        void Check(IReadOnlyList<OutlineItem> items)
        {
            foreach (var item in items)
            {
                Assert.Equal(item.Name, tree.Text.Substring(item.NameSpan.Start, item.NameSpan.Length));
                Assert.InRange(item.NameSpan.Start, item.Span.Start, item.Span.End);
                Assert.InRange(item.NameSpan.End, item.NameSpan.Start, item.Span.End);
                Check(item.Children);
            }
        }

        // A segment block's name is the text inside the quotes, so it is checked on its own.
        var items = Outline.Build(tree);
        Check([.. items.Where(i => i.Kind != OutlineKind.Segment)]);
        Assert.Equal("\"RODATA\"", tree.Text.Substring(items[3].NameSpan.Start, items[3].NameSpan.Length));
    }
}
