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
            .cpu 65c02
            .bss {
            vec: .res 2
            }
            .proc p {
                jmp (vec)
            }
            .proc q {
                jmp q+3
            }
            .proc r {
            @op:
                lda $0400
                sta @op+1
                nop
            }
            """;

        Assert.Empty(Analysis.Program(("main.nt65", Text)).Problems());
    }

    /// <summary>A routine returns past its inline data whatever the processor, so the data is checked on every CPU.</summary>
    [Fact]
    public void InlineDataIsCheckedOnEveryCpu()
    {
        const string Text = """
            .cpu 6502
            .import print: proc(inline .asciiz)
            .proc p {
                jsr print
                .asciiz "hi"
                jsr print
                .byte 1
                rts
            }
            """;

        Assert.Equal(
            [
                "main.nt65:6: `print` returns past one `.asciiz` written after each call, and none follows this one",
                "main.nt65:7: the instruction above runs into this data. `.next` on it says where flow goes instead",
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
            .proc p {
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
            .proc p {
                jmp owner::inner
            }
            """;

        Assert.Empty(Program(Text).Problems());
    }

    private static ProgramAnalysis Program(string text) => Analysis.Program(("main.nt65", ".cpu 65816\n" + text));

    /// <summary>The state reaching the first statement written as <paramref name="line"/>.</summary>
    private static FlowState StateAt(ProgramAnalysis analysis, string line)
    {
        var model = analysis.File("main.nt65");
        var statement = model.Tree.Root.DescendantNodes()
            .Select(node => node.Statement)
            .OfType<SyntaxNode>()
            .First(statement => statement.GetText().Trim() == line);
        var state = analysis.StatesFor("main.nt65")?.Before(statement);
        Assert.NotNull(state);
        return state;
    }
}
