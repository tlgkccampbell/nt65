using Norristown.Flow;
using Norristown.Syntax;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Checks the transfers of control that an editor draws as arrows inside the routine holding the
/// caret. Each arrow is written as the line it starts on and the line it ends on.
/// </summary>
public sealed class FlowArrowsTests
{
    /// <summary>A branch back to a loop's label and a branch forward past some lines each make an arrow.</summary>
    [Fact]
    public void BranchesBackAndForwardAreArrows()
    {
        Assert.Equal(
            ["bpl @loop -> @loop:", "beq @plain -> @plain:"],
            Arrows("""
                .proc fill {
                    ldx #39
                @loop:
                    sta $0400,x
                    dex
                    bpl @loop
                    lda $10
                    beq @plain
                    lda #1
                @plain:
                    rts
                }
                """, "dex"));
    }

    /// <summary>Calls, returns and falling through to the next line transfer nothing an arrow shows.</summary>
    [Fact]
    public void CallsAndFallThroughsAreNotArrows()
    {
        Assert.Equal(
            [],
            Arrows("""
                .proc q {
                    rts
                }
                .proc p {
                    jsr q
                @next:
                    nop
                    rts
                }
                """, "nop"));
    }

    /// <summary>
    /// A branch the flags prove always taken and one they prove never taken are both drawn, and
    /// both are marked proved. The never-taken branch still points at its label.
    /// </summary>
    [Fact]
    public void ProvedBranchesAreMarked()
    {
        Assert.Equal(
            ["bne @fancy -> @fancy: proved", "bne @done -> @done: proved"],
            Arrows("""
                .proc p {
                    lda #1
                    bne @fancy
                    rts
                @fancy:
                    lda #0
                    bne @done
                    nop
                @done:
                    rts
                }
                """, "rts"));
    }

    /// <summary>The lines after an always-taken branch are reached by nothing, and an arrow from them says so.</summary>
    [Fact]
    public void AnArrowFromALineNothingReachesIsMarked()
    {
        Assert.Equal(
            ["bne @x -> @x: proved", "jmp @y -> @y: unreached"],
            Arrows("""
                .proc p {
                    lda #1
                    bne @x
                    jmp @y
                @x:
                    nop
                @y:
                    rts
                }
                """, "nop"));
    }

    /// <summary>A <c>.next</c> under an indirect jump gives an arrow to each label it names.</summary>
    [Fact]
    public void ANextDrawsAnArrowToEachLabel()
    {
        Assert.Equal(
            ["jmp (@table) -> @move: next", "jmp (@table) -> @fire: next"],
            Arrows("""
                .proc dispatch {
                    lda cmd
                    jmp (@table)
                    .next @move, @fire

                @table: .addr @move, @fire

                @move:
                    lda #1
                    rts
                @fire:
                    lda #2
                    rts
                }

                .data cmd: .byte 0
                """, "lda cmd"));
    }

    /// <summary>
    /// A tail call, a branch to another routine, and a <c>.next</c> that names routines or
    /// <c>?</c> leave the routine, and none of them is an arrow.
    /// </summary>
    [Fact]
    public void TransfersToOtherRoutinesAreNotArrows()
    {
        Assert.Equal(
            ["beq @second -> @second:"],
            Arrows("""
                .proc other {
                    rts
                }
                .proc fancy {
                    rts
                }
                .proc p {
                    bcs other
                    lda $10
                    beq @second
                    jmp (vector)
                    .next fancy, other
                @second:
                    jmp (vector)
                    .next ?
                @third:
                    jmp other
                }
                .data vector: .addr 0
                """, "lda $10"));
    }

    /// <summary>A branch inside a macro's body is drawn from the line that calls the macro, unless it stays on that line.</summary>
    [Fact]
    public void AMacroIsDrawnOnItsCall()
    {
        Assert.Equal(
            ["skip!(@done) -> @done:"],
            Arrows("""
                .macro skip(to) {
                    beq to
                @wait:
                    dex
                    bne @wait
                }
                .proc p {
                    skip!(@done)
                    nop
                @done:
                    rts
                }
                """, "nop"));
    }

    /// <summary>
    /// The caret may rest on any line of a routine, its opening and closing lines too, and the
    /// innermost routine holding it is the one drawn. Outside every routine there is no answer.
    /// </summary>
    [Fact]
    public void TheRoutineHoldingTheCaretIsDrawn()
    {
        const string Text = """
            .proc p {
            @a:
                bcc @a
            }
            .proc q {
            @b:
                bcs @b
            }
            .data d: .byte 0
            """;
        var analysis = FlowFragment.Analyze("6502", Text);
        var model = analysis.File(Analysis.Path);
        var text = model.Tree.Text;
        Assert.Equal(["bcc @a -> @a:"], Described(model.Tree, FlowArrows.At(analysis, model, text.IndexOf(".proc p", StringComparison.Ordinal))));
        Assert.Equal(["bcs @b -> @b:"], Described(model.Tree, FlowArrows.At(analysis, model, text.IndexOf("}\n.data", StringComparison.Ordinal))));
        Assert.Null(FlowArrows.At(analysis, model, text.IndexOf(".data", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Returns the arrows of the routine holding the first statement whose text is
    /// <paramref name="line"/>, or null where there is no answer.
    /// </summary>
    private static IReadOnlyList<string>? Arrows(string text, string line)
    {
        var analysis = FlowFragment.Analyze("6502", text);
        var model = analysis.File(Analysis.Path);
        var statement = FlowFragment.Statement(model, line);
        return Described(model.Tree, FlowArrows.At(analysis, model, statement.Span.Start));
    }

    /// <summary>
    /// Returns each arrow as the text of the line it starts on, the text of the line it ends on,
    /// and a word for each mark it carries.
    /// </summary>
    private static IReadOnlyList<string>? Described(SyntaxTree tree, FlowArrows? found)
    {
        if (found is null)
            return null;
        return [.. found.Arrows.Select(arrow =>
        {
            var to = FlowFragment.LineText(tree, arrow.To);
            var marks = string.Concat(
                arrow.IsDeclared ? " next" : "",
                arrow.IsProved ? " proved" : "",
                arrow.IsReached ? "" : " unreached");
            return $"{FlowFragment.LineText(tree, arrow.From)} -> {to}{marks}";
        })];
    }
}
