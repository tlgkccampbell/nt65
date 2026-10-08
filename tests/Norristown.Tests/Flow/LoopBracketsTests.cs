using Norristown.Flow;
using Norristown.Syntax;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Checks the loops that an editor draws as brackets in front of a routine's lines. Each loop is
/// written as its first line, its last line, its header, its latches and its trip count.
/// </summary>
public sealed class LoopBracketsTests
{
    /// <summary>Two counted loops, one inside the other, each have a bracket and a count of their own.</summary>
    [Fact]
    public void NestedCountedLoopsHaveTheirOwnCounts()
    {
        Assert.Equal(
            [
                "@row: .. bne @row header @row: latches [bne @row] x8",
                "@col: .. bne @col header @col: latches [bne @col] x16",
                "@wait: .. bpl @wait header @wait: latches [bpl @wait] x?",
            ],
            Brackets("""
                .proc clear {
                    ldy #8
                @row:
                    ldx #16
                @col:
                    sta $0400,y
                    dex
                    bne @col
                    dey
                    bne @row
                @wait:
                    bit $d011
                    bpl @wait
                    rts
                }
                """));
    }

    /// <summary>
    /// A loop entered by a jump to its test has the test as its header, which is not its first
    /// line. The bracket runs from the body to the test.
    /// </summary>
    [Fact]
    public void ARotatedLoopIsHeadedByItsTest()
    {
        Assert.Equal(
            ["@body: .. bne @body header @test: latches [sta $0400,x] x?"],
            Brackets("""
                .proc p {
                    jmp @test
                @body:
                    sta $0400,x
                @test:
                    dex
                    bne @body
                    rts
                }
                """));
    }

    /// <summary>Two branches back to one header make one loop with two latches, and it has no count.</summary>
    [Fact]
    public void TwoBranchesBackMakeOneLoop()
    {
        Assert.Equal(
            ["@top: .. bne @top header @top: latches [bcc @top, bne @top] x?"],
            Brackets("""
                .proc p {
                @top:
                    lda $10
                    bcc @top
                    dex
                    bne @top
                    rts
                }
                """));
    }

    /// <summary>A branch back that the flags prove never taken makes no loop.</summary>
    [Fact]
    public void ABranchBackProvedNeverTakenIsNoLoop()
    {
        Assert.Equal(
            [],
            Brackets("""
                .proc p {
                @top:
                    lda #0
                    bne @top
                    rts
                }
                """));
    }

    /// <summary>Only routines with loops are listed, each with its own text.</summary>
    [Fact]
    public void OnlyRoutinesWithLoopsAreListed()
    {
        var analysis = FlowFragment.Analyze("6502", """
            .proc p {
                rts
            }
            .proc q {
            @a:
                bcc @a
                rts
            }
            """);
        var model = analysis.File(Analysis.Path);
        var found = Assert.Single(LoopBrackets.In(analysis, model));
        Assert.StartsWith(".proc q", model.Tree.Text[found.Routine.Start..found.Routine.End], StringComparison.Ordinal);
    }

    /// <summary>Returns the loops of every routine in <paramref name="text"/>, described one to a string.</summary>
    private static IReadOnlyList<string> Brackets(string text)
    {
        var analysis = FlowFragment.Analyze("6502", text);
        var model = analysis.File(Analysis.Path);
        var tree = model.Tree;
        return [.. LoopBrackets.In(analysis, model).SelectMany(routine => routine.Loops).Select(loop =>
            $"{LineText(tree, loop.Top)} .. {LineText(tree, loop.Bottom)} header {LineText(tree, loop.Header)} "
            + $"latches [{string.Join(", ", loop.Latches.Select(latch => LineText(tree, latch)))}] x{loop.Trips?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}")];
    }

    /// <summary>Returns the text of the line holding <paramref name="span"/>, without its indentation.</summary>
    private static string LineText(SyntaxTree tree, TextSpan span)
    {
        var line = tree.GetLineIndex(span.Start);
        var start = tree.LineStarts[line];
        var end = line + 1 < tree.LineStarts.Length ? tree.LineStarts[line + 1] : tree.Text.Length;
        return tree.Text[start..end].Trim();
    }
}
