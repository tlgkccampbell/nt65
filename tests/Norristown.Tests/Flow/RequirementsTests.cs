using Norristown.Flow;
using Norristown.Semantics;
using Norristown.Syntax;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Which annotation each construct the analysis cannot follow needs written beside it, and
/// what the annotated forms do to the state. The fixtures hold one example of each; these
/// tests cover what a fixture cannot show, such as the same program on another CPU, or the
/// stack after a call.
/// </summary>
public sealed class RequirementsTests
{
    /// <summary>
    /// On the 65C02 no instruction depends on the processor state, so none of the annotations
    /// is required.
    /// </summary>
    [Fact]
    public void NothingIsRequiredOnThe65C02()
    {
        const string Text = """
            .module main
            .cpu 65c02
            .segment BSS
            .data vec: .byte[2]
            .proc p: a8, i8 {
                jmp (vec)
            }
            .proc q: a8, i8 {
                jmp q+3
            }
            .proc r: a8, i8 {
            @op:
                lda $0400
                sta @op+1
                nop
                rts
            }
            """;

        Assert.Empty(Analysis.Program(Analysis.Fragment, ("main.nt65", Text)).Problems());
    }

    /// <summary>
    /// On CPUs other than the 65816 a routine that runs off its end is likely a mistake rather
    /// than something the analysis cannot follow, so it is a warning, and <c>.fallthrough</c>
    /// says it was meant. The suggested fix is a <c>.fallthrough</c> naming the routine written
    /// next.
    /// </summary>
    [Fact]
    public void RunningOffTheEndWarnsOnThe6502()
    {
        const string Text = """
            .module main
            .cpu 6502
            .segment CODE
            .proc first: a8, i8 {
                lda #1
            }
            .proc second: a8, i8 {
                lda #2
                .fallthrough third
            }
            .proc third: a8, i8 {
                tax
                .next ?
            }
            """;

        var analysis = Analysis.Program(Analysis.Fragment, ("main.nt65", Text));

        var only = Assert.Single(analysis.Diagnostics);
        Assert.Equal(Severity.Warning, only.Severity);
        Assert.Equal("`first` runs off its end into whatever is written after it: a `.fallthrough` naming the routine "
            + "it runs into says so, or `.next ?` ends the path", only.Message);
        Assert.Equal(new DiagnosticFix(FixKind.Fallthrough, "second", only.Fix?.At), only.Fix);
        Assert.Equal(6, only.Fix?.At?.Line);
    }

    /// <summary>
    /// All the regions of a segment are laid out as one run of bytes, so the fix names the next
    /// routine written in the same segment, even when a region of another segment is written in
    /// between. An <c>.align</c> between the two routines breaks the run, and the fix then names
    /// no routine.
    /// </summary>
    [Fact]
    public void TheFixNamesTheRoutineNextInTheSegmentAcrossRegions()
    {
        const string Text = """
            .module main
            .cpu 6502
            .segment CODE
            .proc first {
                lda #1
            }
            .segment RODATA
            .data table: .byte 1, 2, 3
            .segment CODE
            .proc second {
                lda #2
            }
            .align 2
            .proc third {
                rts
            }
            """;

        var analysis = Analysis.Program(Analysis.Fragment, ("main.nt65", Text));

        Assert.Equal(2, analysis.Diagnostics.Count);
        Assert.Equal(new DiagnosticFix(FixKind.Fallthrough, "second", analysis.Diagnostics[0].Fix?.At), analysis.Diagnostics[0].Fix);
        Assert.Null(analysis.Diagnostics[1].Fix?.Text);
    }

    /// <summary>A routine returns past its inline data whatever the processor, so the data is checked on every CPU.</summary>
    [Fact]
    public void InlineDataIsCheckedOnEveryCpu()
    {
        const string Text = """
            .module main
            .cpu 6502
            .import print: proc(inline .strz)
            .segment CODE
            .proc p: a8, i8 {
                jsr print
                .strz "hi"
                jsr print
                .byte 1
                rts
            }
            """;

        Assert.Equal(
            [
                "main.nt65:8: `print` returns past one `.strz` written after each call, and none follows this one",
                "main.nt65:9: the instruction above runs into this data. `.next` on it says where flow goes instead",
            ],
            Analysis.Program(Analysis.Fragment, ("main.nt65", Text)).Problems());
    }

    /// <summary>
    /// A relative call (a <c>per</c> of the return address, then a branch) comes back with the
    /// exit state of the routine it branches to, and that routine's return has pulled the
    /// return address the call pushed.
    /// </summary>
    [Fact]
    public void ARelativeCallReturnsWithTheExitAndNothingPushed()
    {
        const string Text = """
            .proc wide: a16 {
                rts
            }
            .proc p: a8 -> a16 {
                phk
                per @back - 1
                brl far_wide
            @back:
                tax
                per @near - 1
                bra wide
            @near:
                tay
                rts
            }
            .proc far_wide: a8, far -> a16 {
                rep #$20
                rtl
            }
            """;
        var analysis = Program(Text);

        Assert.Empty(analysis.Problems());
        var back = StateAt(analysis, "tax");
        Assert.Equal(Width.Sixteen, back.Processor.A);
        Assert.Equal(0, back.Stack?.Depth);
        Assert.Equal(0, StateAt(analysis, "tay").Stack?.Depth);
    }

    /// <summary>
    /// A return followed by a <c>.next</c> is a jump to the address pushed, and pulls that
    /// address off the stack.
    /// </summary>
    [Fact]
    public void AReturnUsedAsAJumpPullsTheAddress()
    {
        const string Text = """
            .proc p: a8, i8 {
                pea @there - 1
                rts
                .next @there
            @there:
                tax
                rts
            }
            """;
        var analysis = Program(Text);

        Assert.Empty(analysis.Problems());
        Assert.Equal(0, StateAt(analysis, "tax").Stack?.Depth);
    }

    /// <summary>
    /// A <c>.next</c> that names a list or a table of routines names every routine in it, so an
    /// indirect call through it is checked against each routine's entry state and returns with
    /// the merge of their exit states.
    /// </summary>
    [Theory]
    [InlineData(".list handlers {\n    wide\n    wider\n}\n")]
    [InlineData(".segment RODATA\n.data handlers: .addr wide, wider\n")]
    [InlineData(".segment RODATA\n.data handlers: .faraddr wide, wider\n")]
    [InlineData(".segment RODATA\n.data handlers: .addr[] {\n    wide\n    .if 1 {\n        wider\n    }\n}\n")]
    public void ANextNamingRoutinesInAListCallsEachOfThem(string handlers)
    {
        var text = """
            .proc wide: a8 -> a16 {
                rep #$20
                rts
            }
            .proc wider: a8 -> a16, i16 {
                rep #$30
                rts
            }
            .proc p: a16 -> a16, i? {
                jsr (handlers,x)
                .next handlers
                tax
                rts
            }

            """ + handlers;
        var analysis = Program(text);

        Assert.Equal(
            [
                "main.nt65:13: `jsr wide` needs `a8`, and A is 16-bit here",
                "main.nt65:13: `jsr wider` needs `a8`, and A is 16-bit here",
            ],
            analysis.Problems());
        Assert.Equal("a16, i?, native", StateAt(analysis, "tax").Processor.ToString());
    }

    /// <summary>
    /// A nested segment block is part of its routine: a jump into it and back carries the
    /// state both ways, as a jump within one stream of bytes does.
    /// </summary>
    [Fact]
    public void AJumpIntoANestedSegmentBlockCarriesTheState()
    {
        const string Text = """
            .proc p: a16, i8 {
                jmp @away
            @back:
                lda #$1234
                rts
                .segment DATA {
            @away:
                    lda #$5678
                    jmp @back
                }
            }
            """;
        var analysis = Program(Text);

        Assert.Empty(analysis.Problems());
        Assert.Equal("a16, i8, native", StateAt(analysis, "lda #$5678").Processor.ToString());
    }

    /// <summary>
    /// Code in a nested segment block that runs off the end of the block runs into whatever
    /// that segment holds next. That is no more part of the routine than what follows the
    /// routine's own end, so it needs a <c>.next</c> just the same.
    /// </summary>
    [Fact]
    public void ANestedSegmentBlockThatRunsOffItsEndNeedsANext()
    {
        const string Text = """
            .proc p: a8, i8 {
                jmp @away
                .segment DATA {
            @away:
                    nop
                }
            }
            """;

        Assert.Equal(
            ["main.nt65:8: `p` runs off the end of a segment block into whatever that segment holds next: "
                + "a `.next` says where flow goes, or `.next ?` ends the path"],
            Program(Text).Problems());
    }

    /// <summary>
    /// A jump to a label inside another routine is checked only against the state the label's
    /// <c>.state</c> declares. `p` leaves by the jump, so it returns the way `owner` does, which
    /// is what the `-&gt;` in its signature declares.
    /// </summary>
    [Fact]
    public void AJumpIntoAnotherRoutineMeetsItsDeclaration()
    {
        const string Text = """
            .proc owner: i16 {
                rts
            inner:
                .state a8, i?, native
                rts
                .next ?
            }
            .proc p: a8, i8 -> i16 {
                jmp owner::inner
            }
            """;

        Assert.Empty(Program(Text).Problems());
    }

    /// <summary>
    /// A jump into another routine hands the jumping routine's caller whatever that routine
    /// returns with, as a tail call to it would, so the jumping routine's declared exit state
    /// has to match.
    /// </summary>
    [Fact]
    public void AJumpIntoAnotherRoutineReturnsTheWayItDoes()
    {
        const string Text = """
            .proc owner: i16 {
                rts
            inner:
                .state a8, i?, native
                rts
                .next ?
            }
            .proc p: a8, i8 {
                jmp owner::inner
            }
            """;

        Assert.Equal(
            ["main.nt65:12: `jmp inner` leaves `p`: `p` returns with `i8`, and X and Y are 16-bit "
                + "when `owner` returns"],
            Program(Text).Problems());
    }

    private static ProgramAnalysis Program(string text) => Analysis.Program(Analysis.Fragment, ("main.nt65", ".module main\n.cpu 65816\n.segment CODE\n" + text));

    /// <summary>The state reaching the first statement written as <paramref name="line"/>.</summary>
    private static FlowState StateAt(ProgramAnalysis analysis, string line)
    {
        var model = analysis.File("main.nt65");
        var statement = model.Tree.Root.DescendantNodes()
            .OfType<LineSyntax>()
            .Select(node => node.Statement)
            .First(statement => statement.GetText().Trim() == line);
        var state = analysis.StatesFor("main.nt65")?.Before(statement);
        Assert.NotNull(state);
        return state;
    }
}
