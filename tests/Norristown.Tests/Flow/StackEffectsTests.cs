using Norristown.Flow;
using Norristown.Syntax;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Checks the stack effects worked out across the program: what each routine leaves on its
/// caller's stack once it returns, and that every stack tracker applies it after a call.
/// </summary>
public sealed class StackEffectsTests
{
    /// <summary>
    /// A routine that returns as it was called leaves nothing. One that pulls its own return
    /// address and returns through its caller's takes the caller's return address with it.
    /// </summary>
    [Theory]
    [InlineData(".proc p {\n    pha\n    pla\n    rts\n}\n", "balanced")]
    [InlineData(".proc p {\n    pla\n    pla\n    rts\n}\n", "leaves -2")]
    [InlineData(".proc p {\n    jmp p\n}\n", "never returns")]
    public void AReturnLeavesWhatTheStackHoldsAboveTheReturnAddress(string text, string effect)
    {
        Assert.Equal(effect, EffectOf(FlowFragment.Analyze("6502", text), "p").ToString());
    }

    /// <summary>
    /// A caller cannot tell which way a routine returned, so ways that leave different amounts
    /// leave something unknown, and so does a way nt65 cannot follow.
    /// </summary>
    [Theory]
    [InlineData(".proc p {\n    beq @out\n    pla\n@out:\n    rts\n}\n")]
    [InlineData(".proc p {\n    jmp (slot)\n    .next ?\n}\n.segment BSS\n.data slot: .addr\n")]
    public void WaysThatDisagreeLeaveSomethingUnknown(string text)
    {
        Assert.Equal(StackEffect.Unknown, EffectOf(FlowFragment.Analyze("6502", text), "p"));
    }

    /// <summary>
    /// A tail call hands its routine the stack as it stands, with the top bytes taken for the
    /// return address. Here each routine pulls one byte, so together they take two of the
    /// caller's bytes besides the return address the last one returns through.
    /// </summary>
    [Fact]
    public void ATailCallAddsWhatItsRoutineLeaves()
    {
        var analysis = FlowFragment.Analyze("6502", ".proc a {\n    pla\n    jmp b\n}\n.proc b {\n    pla\n    rts\n}\n");

        Assert.Equal(StackEffect.Leaving(-1), EffectOf(analysis, "b"));
        Assert.Equal(StackEffect.Leaving(-2), EffectOf(analysis, "a"));
    }

    /// <summary>
    /// An effect that depends on itself, as one that pushes and then calls itself does, would grow
    /// a byte every time it was worked out, so it is taken to be unknown.
    /// </summary>
    [Fact]
    public void AnEffectThatGrowsWithoutEndIsUnknown()
    {
        var analysis = FlowFragment.Analyze("6502", ".proc r {\n    pha\n    beq @out\n    jsr r\n@out:\n    rts\n}\n");

        Assert.Equal(StackEffect.Unknown, EffectOf(analysis, "r"));
    }

    /// <summary>
    /// After a call to a routine that takes a byte of its caller's, the push the caller made is no
    /// longer known to be there, so the register walk forgets the stack. The 65816 state analysis
    /// counts bytes, and takes the byte off the top.
    /// </summary>
    [Fact]
    public void EachTrackerAppliesTheEffectAfterACall()
    {
        const string Takes = ".proc takes1: a8, i8 {\n    pla\n    tax\n    pla\n    tay\n    pla\n    tya\n    pha\n    txa\n    pha\n    rts\n}\n";
        const string Caller = ".proc c: a8, i8 {\n    lda #0\n    pha\n    pha\n    jsr takes1\n    nop\n    pla\n    rts\n}\n";
        var analysis = FlowFragment.Analyze("65816", Takes + Caller);

        Assert.Equal(StackEffect.Leaving(-1), EffectOf(analysis, "takes1"));
        Assert.Null(RegistersAt(analysis, "nop").Stack);
        Assert.Equal(1, FlowFragment.StateAt(analysis, "nop").Stack?.Depth);
    }

    /// <summary>
    /// The 65816 state analysis runs on each file before the effects are worked out across the
    /// program. A file that called a routine in another file whose effect turned out not to be
    /// balanced is analyzed again with it.
    /// </summary>
    [Fact]
    public void AFileIsAnalyzedAgainWithTheEffectsOfAnotherFile()
    {
        var analysis = Analysis.Program(
            ("lib.nt65", ".module lib\n.cpu 65816\n.segment CODE\n"
                + ".export .proc takes1: a8, i8 {\n    pla\n    tax\n    pla\n    tay\n    pla\n    tya\n    pha\n    txa\n    pha\n    rts\n}\n"),
            ("main.nt65", ".module main\n.cpu 65816\n.segment CODE\n"
                + ".export .proc c: a8, i8 {\n    lda #0\n    pha\n    pha\n    jsr lib::takes1\n    nop\n    pla\n    rts\n}\n"));

        Assert.Equal(1, FlowFragment.StateAt(analysis, "nop").Stack?.Depth);
    }

    private static StackEffect EffectOf(ProgramAnalysis analysis, string routine)
    {
        var flow = analysis.FlowFor(Analysis.Path);
        Assert.NotNull(flow);
        return flow.Effects.Of(flow.Regions.Single(region => region.Routine.DisplayName == routine).Routine);
    }

    private static RegisterState RegistersAt(ProgramAnalysis analysis, string line)
    {
        var statement = analysis.File(Analysis.Path).Tree.Root.DescendantNodes()
            .OfType<LineSyntax>()
            .Select(node => node.Statement)
            .First(statement => statement.GetText().Trim() == line);
        var state = analysis.FlowFor(Analysis.Path)?.Registers?.Before(statement);
        Assert.NotNull(state);
        return state;
    }
}
