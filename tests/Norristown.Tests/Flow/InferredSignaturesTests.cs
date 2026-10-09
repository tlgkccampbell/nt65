using Norristown.Semantics;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Tests the parts of a routine's signature that are inferred where it declares nothing about
/// them. A routine's exit comes from its body and its entry from what its callers agree on. Where
/// callers disagree on a width the body depends on, that is an error at the routine. Where they
/// disagree on the direct page or the data bank, the two combine as two paths do where they meet.
/// A routine that never returns is inferred to, on every processor.
/// </summary>
public sealed class InferredSignaturesTests
{
    /// <summary>
    /// A routine that sets a width and returns hands that width back, so its caller's immediate
    /// is sized from it, and the routine is not held to returning the width it was entered with.
    /// </summary>
    [Fact]
    public void AnExitComesFromTheBody()
    {
        const string Text = ".proc narrow {\n    sep #$20\n    rts\n}\n.export .proc main: a16, native -> a8 {\n    jsr narrow\n    lda #1\n    rts\n}\n";

        var analysis = FlowFragment.Analyze("65816", Text);

        Assert.Empty(analysis.Problems());
        Assert.Equal(Width.Eight, FlowFragment.StateAt(analysis, "lda #1").Processor.A);
    }

    /// <summary>
    /// A routine whose callers all reach it with one width is entered with that width, so the
    /// immediates in its body are sized from it.
    /// </summary>
    [Fact]
    public void AnEntryComesFromCallersThatAgree()
    {
        const string Text = ".proc draw {\n    lda #$12\n    ldx #$34\n    rts\n}\n"
            + ".export .proc main: a8, i16, native {\n    jsr draw\n    jsr draw\n    rts\n}\n";

        var analysis = FlowFragment.Analyze("65816", Text);

        Assert.Empty(analysis.Problems());
        var state = FlowFragment.StateAt(analysis, "lda #$12").Processor;
        Assert.Equal((Width.Eight, Width.Sixteen), (state.A, state.Index));
    }

    /// <summary>
    /// The entry passes along a chain of calls and through routines that call each other, so a
    /// routine reached only through others still takes the width its callers' callers set.
    /// </summary>
    [Fact]
    public void AnEntryPassesAlongCallsAndCycles()
    {
        const string Text = ".proc even {\n    lda $10\n    beq @done\n    dec $10\n    jsr odd\n@done:\n    rts\n}\n"
            + ".proc odd {\n    lda #1\n    jsr even\n    rts\n}\n"
            + ".export .proc main: a16, native {\n    jsr even\n    rts\n}\n";

        var analysis = FlowFragment.Analyze("65816", Text);

        Assert.Empty(analysis.Problems());
        Assert.Equal(Width.Sixteen, FlowFragment.StateAt(analysis, "lda #1").Processor.A);
    }

    /// <summary>
    /// Callers that reach a routine with two widths, where its body has an immediate that depends
    /// on the width, are reported once at the routine, with a fix that declares either width.
    /// </summary>
    [Fact]
    public void CallersThatDisagreeOnAWidthTheBodyNeedsAreAnError()
    {
        const string Text = ".proc draw {\n    lda #$12\n    lda #$34\n    rts\n}\n"
            + ".export .proc main: a8, native {\n    jsr draw\n    rep #$20\n    jsr draw\n    sep #$20\n    rts\n}\n";

        var problem = Assert.Single(FlowFragment.Analyze("65816", Text).Diagnostics);

        Assert.Equal("callers-disagree", problem.Id);
        Assert.Equal(1, problem.Span.Line - FlowFragment.HeaderLines);
        Assert.Contains("`draw` is called with `a16` by some callers and `a8` by others", problem.Message, StringComparison.Ordinal);
        Assert.Equal(["a16", "a8"], new[] { problem.Fix?.Text, problem.Also?.Text });
        Assert.Equal(3, problem.Related.Count);
    }

    /// <summary>
    /// Callers that disagree on a width the body does not depend on are no mistake. The routine
    /// runs with either, and hands the width back as it found it.
    /// </summary>
    [Fact]
    public void CallersThatDisagreeOnAWidthTheBodyDoesNotNeedAreFine()
    {
        const string Text = ".proc bump {\n    inc $10\n    rts\n}\n"
            + ".export .proc main: a8, native {\n    jsr bump\n    rep #$20\n    jsr bump\n    lda #$1234\n    sep #$20\n    rts\n}\n";

        Assert.Empty(FlowFragment.Problems("65816", Text));
    }

    /// <summary>
    /// A routine whose address is taken may be called through it from anywhere, so it keeps the
    /// default entry, and an immediate in it needs a width it declares.
    /// </summary>
    [Fact]
    public void ARoutineWhoseAddressIsTakenKeepsTheDefaultEntry()
    {
        const string Text = ".proc draw {\n    lda #$12\n    rts\n}\n.export .proc main: a8, native {\n    jsr draw\n    rts\n}\n"
            + ".segment RODATA\n.data table: .addr draw\n";

        Assert.Contains(FlowFragment.Problems("65816", Text),
            problem => problem.Contains("`draw` declares `a*`, which assumes nothing about it", StringComparison.Ordinal));
    }

    /// <summary>
    /// A routine whose address is taken, or that nothing calls, may be entered in either mode, so
    /// a <c>rep</c> in it widens nothing nt65 can rely on until the routine declares its mode.
    /// </summary>
    [Theory]
    [InlineData(".proc handler {\n    rep #$20\n    lda #$1234\n    sep #$20\n    rts\n}\n.segment RODATA\n.data vectors: .addr handler\n")]
    [InlineData(".export .proc handler {\n    rep #$20\n    lda #$1234\n    sep #$20\n    rts\n}\n")]
    public void ARoutineSomeOfWhoseCallersCannotBeSeenAssumesNothingAboutTheMode(string text)
    {
        Assert.Equal(
            ["main.nt65:3: `lda #` needs the width of A, and it is not known here, because `rep #$20` widens nothing in "
                + "emulation mode, and the mode is not known: a `.state` before it declares which mode it is"],
            FlowFragment.Problems("65816", text));
        Assert.Empty(FlowFragment.Problems("65816", text.Replace(".proc handler {", ".proc handler: native {", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A routine whose address is taken and that is also called hands back the mode it was
    /// entered with, so its caller keeps the mode it knew across the call.
    /// </summary>
    [Fact]
    public void ACallerKeepsItsModeAcrossARoutineWhoseAddressIsTaken()
    {
        const string Text = ".proc bump {\n    inc $10\n    rts\n}\n"
            + ".export .proc main: a8, native {\n    jsr bump\n    rep #$20\n    lda #$1234\n    sep #$20\n    rts\n}\n"
            + ".segment RODATA\n.data table: .addr bump\n";

        Assert.Empty(FlowFragment.Problems("65816", Text));
    }

    /// <summary>
    /// An exported routine is entered with what its callers in the program agree on, in its own
    /// module or another. Code outside nt65 that calls it is not checked, and the routine
    /// declares its entry where that code needs one.
    /// </summary>
    [Theory]
    [InlineData(".export .proc draw {\n    lda #$12\n    rts\n}\n")]
    [InlineData(".export draw\n.proc draw {\n    lda #$12\n    rts\n}\n")]
    public void AnExportedRoutineIsEnteredWithWhatItsCallersAgreeOn(string draw)
    {
        var lib = ".module lib\n.cpu 65816\n.segment CODE\n" + draw;
        const string Main = ".module main\n.cpu 65816\n.use lib::draw\n.segment CODE\n"
            + ".export .proc main: a16, native {\n    jsr draw\n    rts\n}\n";

        Assert.Empty(Analysis.Program(Analysis.Fragment, ("main.nt65", Main), ("lib.nt65", lib)).Problems());
        Assert.Empty(Analysis.Program(Analysis.Fragment, ("lib.nt65", lib), ("main.nt65", Main)).Problems());
    }

    /// <summary>
    /// Callers that agree on the direct page give it to the routine, so a <c>d:</c> operand in it
    /// can be laid out. Callers that disagree leave it unknown, and the operand is reported, naming
    /// them. That is not reported at the routine, since a routine may well be meant to run with
    /// several.
    /// </summary>
    [Theory]
    [InlineData("$0000", false)]
    [InlineData("$0100", true)]
    public void TheDirectPageComesFromCallersThatAgree(string other, bool reported)
    {
        var text = ".proc poke {\n    lda d:$0010\n    rts\n}\n"
            + ".export .proc first: a8, dp = $0000, native {\n    jsr poke\n    rts\n}\n"
            + $".export .proc second: a8, dp = {other}, native {{\n    jsr poke\n    rts\n}}\n";

        var problems = FlowFragment.Problems("65816", text);

        Assert.Equal(reported, problems.Any(problem => problem.Contains(
            "D is not known here, because `poke` is called with `dp = $0000` and `dp = $0100`", StringComparison.Ordinal)));
        Assert.DoesNotContain(problems, problem => problem.Contains("callers-disagree", StringComparison.Ordinal));
    }

    /// <summary>Callers in different data banks enter the routine in one of their banks.</summary>
    [Fact]
    public void TheDataBankIsOneOfTheCallers()
    {
        const string Text = ".proc peek {\n    lda $10\n    rts\n}\n"
            + ".export .proc first: a8, dbr = $7e, native {\n    jsr peek\n    rts\n}\n"
            + ".export .proc second: a8, dbr = $7f, native {\n    jsr peek\n    rts\n}\n";

        var analysis = FlowFragment.Analyze("65816", Text);

        Assert.Empty(analysis.Problems());
        Assert.Equal([0x7e, 0x7f], FlowFragment.StateAt(analysis, "lda $10").Processor.B.Values);
    }

    /// <summary>
    /// A routine no path returns from never returns, on every processor, so nothing after a call
    /// to it is run into and no <c>.next</c> is needed there.
    /// </summary>
    [Theory]
    [InlineData("6502")]
    [InlineData("65816")]
    public void ARoutineThatNeverReturnsIsInferred(string cpu)
    {
        const string Text = ".proc fatal {\n@spin:\n    jmp @spin\n}\n.export .proc main: native {\n    jsr fatal\n    .byte 1\n}\n";

        Assert.Empty(FlowFragment.Problems(cpu, Text));
    }
}
