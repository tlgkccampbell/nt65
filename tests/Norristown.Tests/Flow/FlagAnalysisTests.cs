using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Tests the flag analysis, which follows N, Z, C and V through each routine from what the
/// instructions themselves set. A conditional branch whose flag it knows goes one way only: one
/// always taken is a jump, and one never taken transfers nothing.
/// </summary>
public sealed class FlagAnalysisTests
{
    private const string Header = ".module main\n.cpu 6502\n.segment CODE\n";

    /// <summary>
    /// A routine that ends in a branch the flags decide needs no <c>.next</c>, because nt65 knows
    /// the branch is always taken. These are the compo's own cases that need nothing but the code
    /// just before the branch.
    /// </summary>
    [Theory]
    [InlineData(".proc p {\n    lda #1\n    bne p\n}\n")]
    [InlineData(".proc p {\n    ldx #24\n    bne p\n}\n")]
    [InlineData(".proc p {\n    lsr $10\n    bcc p\n    bcs q\n}\n")]
    [InlineData(".proc p {\n    lda $10\n    bpl p\n@halt:\n    bmi @halt\n}\n")]
    [InlineData(".proc p {\n    dec $10\n    bmi @done\n    rts\n@done:\n    bne @done\n}\n")]
    [InlineData(".proc p {\n    clv\n    bvc q\n}\n")]
    public void ABranchTheFlagsDecideNeedsNoNext(string routine)
    {
        Assert.Empty(Problems(routine + ".proc q {\n    rts\n}\n"));
    }

    /// <summary>
    /// A flag is known only where every path agrees on it. One nothing in the routine sets is not
    /// known at the entry, a call to a routine with no body and no signature leaves every flag
    /// unknown, and a label another routine names may be entered with anything. A software
    /// interrupt's handler may return any flags, by editing those pushed on the stack.
    /// </summary>
    [Theory]
    [InlineData(".proc p {\n    lda #0\n    brk #$40\n    bne p\n}\n")]
    [InlineData(".proc p {\n    bvc p\n}\n")]
    [InlineData(".proc p {\n    rol a\n    bne p\n}\n")]
    [InlineData(".proc rom = $1234\n.proc p {\n    lda #1\n    jsr rom\n    bne p\n}\n")]
    [InlineData(".proc p {\n    lda $10\n    beq @x\n    lda #1\n@x:\n    bne p\n}\n")]
    [InlineData(".proc p {\n    lda #1\nentry:\n    bne p\n}\n.proc r {\n    jmp p::entry\n}\n")]
    public void AFlagNotKnownOnEveryPathLeavesTheBranchBothWays(string routine)
    {
        Assert.Contains(Problems(routine + ".proc q {\n    rts\n}\n"), problem => problem.Contains("runs off", StringComparison.Ordinal));
    }

    /// <summary>
    /// On the 65816, <c>rep</c> and <c>sep</c> set the flags their mask names.
    /// </summary>
    [Fact]
    public void RepAndSepSetTheFlagsTheyName()
    {
        var program = Analysis.Program(Analysis.Fragment, ("main.nt65", ".module main\n.cpu 65816\n.segment CODE\n"
            + ".proc p: a8, i8, native {\n    sep #$01\n    bcs q\n}\n.proc q: a8, i8 {\n    rts\n}\n"));

        Assert.Empty(program.Problems());
    }

    /// <summary>
    /// A <c>sep</c> whose mask is not known may set N and leave Z as it was, so a branch that
    /// learns N is 1 learns nothing about Z, and the code that needs Z to be 1 is reached.
    /// </summary>
    [Fact]
    public void ASepOfAnUnknownMaskLeavesNAndZApart()
    {
        var program = Analysis.Program(Analysis.Fragment, ("main.nt65", ".module main\n.cpu 65816\n.import mask\n.segment CODE\n"
            + ".proc p: a8, i8 {\n    sep #<mask\n    .ensure a8, i8\n    bmi @negative\n    rts\n@negative:\n    beq @zero\n"
            + "    rts\n@zero:\n    lda #1\n    rts\n}\n"));

        Assert.Empty(program.Problems());
    }

    /// <summary>
    /// Code after a branch that is always taken is never reached, and the message says why the
    /// branch is always taken.
    /// </summary>
    [Fact]
    public void CodeAfterABranchAlwaysTakenIsNeverReached()
    {
        Assert.Equal(
            ["main.nt65:7: this code is never reached: `bne @x` above is always taken, because Z is 0 here, and nothing "
                + "branches or jumps here"],
            Problems(".proc p {\n    lda #1\n    bne @x\n    nop\n@x:\n    rts\n}\n"));
    }

    /// <summary>
    /// A <c>.next</c> that says a branch is always taken is accepted where the flags agree, and is
    /// an error where they show the branch is never taken.
    /// </summary>
    [Fact]
    public void ANextIsCheckedAgainstTheFlags()
    {
        Assert.Empty(Problems(".proc p {\n    lda #1\n    bne p\n    .next p\n}\n"));
        Assert.Contains(
            "main.nt65:7: `.next p` says `bne p` is always taken, but Z is 1 here, so it is never taken",
            Problems(".proc p {\n    lda #0\n    bne p\n    .next p\n}\n"));
    }

    /// <summary>
    /// A line of a macro body serves every call, so code there is reported as never reached only
    /// where no call reaches it. It is then reported at each call, which is the side that can
    /// change.
    /// </summary>
    [Fact]
    public void AMacroLineIsUnreachedOnlyWhereNoCallReachesIt()
    {
        const string Macro = ".macro skip_if_plus(v: const) {\n    lda #v\n    bpl @skip\n    nop\n@skip:\n}\n";
        const string Message = ": this code is never reached: `bpl @skip` above is always taken, because N is 0 here, and "
            + "nothing branches or jumps here";

        Assert.Empty(Problems(Macro + ".proc p {\n    skip_if_plus!(1)\n    skip_if_plus!($80)\n    rts\n}\n"));
        Assert.Equal(
            ["main.nt65:11" + Message, "main.nt65:12" + Message],
            Problems(Macro + ".proc p {\n    skip_if_plus!(1)\n    skip_if_plus!(2)\n    rts\n}\n"));
    }

    /// <summary>Returns the problems nt65 finds in <paramref name="text"/>, after a 6502 header.</summary>
    private static IReadOnlyList<string> Problems(string text) =>
        Analysis.Program(Analysis.Fragment, ("main.nt65", Header + text)).Problems();
}
