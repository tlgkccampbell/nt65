using Norristown.Flow;
using Norristown.Semantics;
using Norristown.Syntax;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// What each construct the analysis cannot follow needs written beside it, and what the
/// annotated forms do to the state. The fixtures hold one of each; these are the cases a
/// fixture cannot show, such as the same program on another CPU, or the stack after a call.
/// </summary>
public sealed class RequirementsTests
{
    /// <summary>On the 65C02 nothing consumes processor state, so none of the annotations is required.</summary>
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

        Assert.Empty(Analysis.Program(("main.nt65", Text)).Problems());
    }

    /// <summary>
    /// Off the 65816 a routine that runs off its end is likely a mistake rather than something
    /// the analysis cannot follow, so it is a warning, and <c>.next</c> says it was meant.
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
                .next third
            }
            .proc third: a8, i8 {
                tax
                .next ?
            }
            """;

        var analysis = Analysis.Program(("main.nt65", Text));

        var only = Assert.Single(analysis.Diagnostics);
        Assert.Equal(Severity.Warning, only.Severity);
        Assert.Equal("`first` runs off its end into whatever is written after it: `.next` naming the routine it runs "
            + "into says so, or `.next ?` ends the path", only.Message);
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
            Analysis.Program(("main.nt65", Text)).Problems());
    }

    /// <summary>
    /// A relative call comes back with the routine's exit, and the return address it pushed
    /// has been pulled by the routine's return.
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

    /// <summary>A return with a <c>.next</c> is a jump to the address pushed, and pulls it.</summary>
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
    /// A list or a table of routines stands for every routine in it, so an indirect call through
    /// one is checked against each entry and returns with the merge of their exits.
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
    /// Code in a nested segment block that runs off its end runs into whatever that segment
    /// holds next, which is no more the routine's than the end of the routine is.
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
                + "`.next` says where flow goes, or `.next ?` ends the path"],
            Program(Text).Problems());
    }

    /// <summary>Only the state a declaration gives is checked at a jump into another routine.</summary>
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
            .proc p: a8, i8 {
                jmp owner::inner
            }
            """;

        Assert.Empty(Program(Text).Problems());
    }

    private static ProgramAnalysis Program(string text) => Analysis.Program(("main.nt65", ".module main\n.cpu 65816\n.segment CODE\n" + text));

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
