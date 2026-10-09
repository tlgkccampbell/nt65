using Norristown.Flow;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Checks the height each stack tracker keeps: how many bytes the stack holds above the caller's
/// stack as it was before the call. A routine starts at the size of its return address, and a pull
/// of more than it pushed, such as a pull of its own return address, lowers the height below it.
/// </summary>
public sealed class StackHeightTests
{
    /// <summary>
    /// A routine that pulls its own return address and pushes one byte stands one byte above its
    /// caller's stack, which is what a return through that address would leave the caller.
    /// </summary>
    [Fact]
    public void PullingTheReturnAddressLowersTheHeight()
    {
        const string Text = ".proc p {\n    pla\n    pla\n    lda #0\n    pha\n    nop\n    rts\n}\n";

        Assert.Equal(2, RegistersAt(Text, "pla").Stack?.Height);
        Assert.Equal(1, RegistersAt(Text, "nop").Stack?.Height);
    }

    /// <summary>
    /// The height starts at the return address, which is three bytes for a far routine. The
    /// arguments of a routine that declares <c>pushed n</c> are its caller's, so they leave it there.
    /// </summary>
    [Theory]
    [InlineData(".proc p: a8, i8 {\n    nop\n    rts\n}\n", 2)]
    [InlineData(".proc p: a8, i8, far {\n    nop\n    rtl\n}\n", 3)]
    [InlineData(".proc p: a8, i8, pushed 2 {\n    nop\n    rts\n}\n", 2)]
    public void TheHeightStartsAtTheReturnAddress(string text, int height)
    {
        Assert.Equal(height, FlowFragment.StateAt(FlowFragment.Analyze("65816", text), "nop").Stack?.Height);
        Assert.Equal(height, RegistersAt(text, "nop", "65816").Stack?.Height);
    }

    /// <summary>
    /// The 65816 state analysis counts bytes, so a pull past the return address lowers the height
    /// by the width pulled.
    /// </summary>
    [Fact]
    public void TheStateAnalysisCountsAWidePull()
    {
        var state = FlowFragment.StateAt(
            FlowFragment.Analyze("65816", ".proc p: a16, i8 {\n    pla\n    sep #$20\n    pha\n    nop\n    .ensure a16\n    rts\n}\n"),
            "nop");

        Assert.Equal(1, state.Stack?.Height);
    }

    /// <summary>
    /// Two paths that pulled different amounts leave the height unknown where they meet, while the
    /// pushes both agree on are still followed.
    /// </summary>
    [Fact]
    public void PathsThatDisagreeLeaveTheHeightUnknown()
    {
        var state = RegistersAt(".proc p {\n    beq @a\n    pla\n@a:\n    nop\n    rts\n}\n", "nop");

        Assert.NotNull(state.Stack);
        Assert.Null(state.Stack.Height);
    }

    /// <summary>
    /// The stack the value sources follow keeps the same height as the others.
    /// </summary>
    [Fact]
    public void TheSourceStackKeepsTheHeight()
    {
        var entered = SourceStack.Entered(2);
        var pulled = entered.Pull(PushSize.OneByte, Width.Eight);

        Assert.Equal(1, pulled?.Height);
        Assert.Null(SourceStack.Merge(entered, pulled)?.Height);
        Assert.Null(entered.Pull(PushSize.Accumulator, Width.Unknown)?.Height);
    }

    /// <summary>Returns what the registers hold before the statement written as <paramref name="line"/>.</summary>
    private static RegisterState RegistersAt(string text, string line, string cpu = "6502")
    {
        var analysis = FlowFragment.Analyze(cpu, text);
        var model = analysis.File(Analysis.Path);
        var statement = model.Tree.Root.DescendantNodes()
            .OfType<LineSyntax>()
            .Select(node => node.Statement)
            .First(statement => statement.GetText().Trim() == line);
        var state = analysis.FlowFor(Analysis.Path)?.Registers?.Before(statement);
        Assert.NotNull(state);
        return state;
    }
}
