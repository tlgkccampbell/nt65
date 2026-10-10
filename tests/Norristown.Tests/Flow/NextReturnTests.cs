using Norristown.Flow;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Checks <c>.next .return</c>, which says a jump goes back to the routine's caller as the
/// routine's own return would. That is how a routine that pulls its return address and jumps back
/// through it returns, often leaving bytes on its caller's stack or taking some.
/// </summary>
public sealed class NextReturnTests
{
    private const string Slot = ".segment BSS\n.data slot: .addr\n.segment CODE\n";

    /// <summary>
    /// The jump pulls nothing, so the caller is left everything above its own stack: here the
    /// return address is pulled and one byte pushed.
    /// </summary>
    [Fact]
    public void AReturnLeavesWhatTheStackHolds()
    {
        var analysis = FlowFragment.Analyze("6502",
            Slot + ".proc p {\n    pla\n    pla\n    lda #0\n    pha\n    jmp (slot)\n    .next .return\n}\n");

        Assert.Equal(StackEffect.Leaving(1), EffectOf(analysis, "p"));
        Assert.Empty(analysis.Problems());
    }

    /// <summary>A count is a promise, used as written and checked against what nt65 counts.</summary>
    [Fact]
    public void AWrittenCountIsCheckedAgainstTheCount()
    {
        const string Body = ".proc p {\n    pla\n    pla\n    lda #0\n    pha\n    jmp (slot)\n    .next .return {0}\n}\n";

        Assert.Empty(FlowFragment.Problems("6502", Slot + Body.Replace("{0}", "1")));
        var wrong = FlowFragment.Analyze("6502", Slot + Body.Replace("{0}", "3"));
        Assert.Equal(StackEffect.Leaving(3), EffectOf(wrong, "p"));
        var diagnostic = Assert.Single(wrong.Diagnostics);
        Assert.Equal(
            "`.next .return 3` promises the caller's stack holds 3 more bytes than before the call, but nt65 counts 1 here",
            diagnostic.Message);
        Assert.Equal("1", diagnostic.Fix?.Text);
    }

    /// <summary>
    /// <c>?</c> says what the return leaves is not known, and a count must be a constant.
    /// </summary>
    [Fact]
    public void AnUnknownCountAndAWrongOne()
    {
        Assert.Equal(StackEffect.Unknown, EffectOf(
            FlowFragment.Analyze("6502", Slot + ".proc p {\n    jmp (slot)\n    .next .return ?\n}\n"), "p"));
        Assert.Equal(
            ["main.nt65:6: the count of `.next .return` must be a constant number of bytes"],
            FlowFragment.Problems("6502", Slot + ".proc p {\n    jmp (slot)\n    .next .return slot\n}\n"));
    }

    /// <summary>
    /// Labels listed after <c>.return</c> are followed as tail calls. One that never returns adds
    /// nothing, and one that returns balanced leaves a different amount, which a caller cannot tell
    /// apart from the return.
    /// </summary>
    [Theory]
    [InlineData(".proc away: noreturn {\n    jmp away\n}\n", "leaves 1")]
    [InlineData(".proc away {\n    rts\n}\n", "unknown")]
    public void ListedLabelsAreFollowedAsTailCalls(string away, string effect)
    {
        var analysis = FlowFragment.Analyze("6502",
            Slot + ".proc p {\n    pla\n    pla\n    lda #0\n    pha\n    jmp (slot)\n    .next .return, away\n}\n" + away);

        Assert.Equal(effect, EffectOf(analysis, "p").ToString());
    }

    /// <summary>
    /// Each of the caller's tracked stacks holds what the routine left once the call returns, so a
    /// pull after the call takes that byte, and the pull after it takes back what the caller saved.
    /// </summary>
    [Theory]
    [InlineData("65816", ".proc p: a8, i8 {", ".proc c: a8, i8 {")]
    [InlineData("6502", ".proc p {", ".proc c {")]
    public void ACallerSeesWhatTheReturnLeft(string cpu, string callee, string caller)
    {
        var analysis = FlowFragment.Analyze(cpu,
            Slot + callee + "\n    pla\n    pla\n    lda #0\n    pha\n    jmp (slot)\n    .next .return\n}\n"
                + caller + "\n    lda #1\n    pha\n    jsr p\n    nop\n    pla\n    pla\n    sta slot\n    rts\n}\n");

        if (cpu == "65816")
            Assert.Equal(2, FlowFragment.StateAt(analysis, "nop").Stack?.Depth);
        Assert.Equal(2, FlowFragment.RegistersAt(analysis, "nop").Stack?.Depth);
        Assert.Equal(2, FlowFragment.RegistersAt(analysis, "sta slot").Stack?.Height);
        Assert.Equal(["A: lda #1 via pla"], FlowFragment.SourcesAt(analysis, "sta slot"));
    }

    /// <summary>
    /// A <c>.next .return</c> is a return, so a <c>keeps</c> promise and the exit state are
    /// checked there, and a routine that never returns cannot have one.
    /// </summary>
    [Fact]
    public void AReturnIsCheckedAsAReturn()
    {
        Assert.Contains(
            "main.nt65:6: `p` promises `keeps x`, but X is not the same as on entry here",
            string.Join("\n", FlowFragment.Problems("6502", Slot + ".proc p: keeps x {\n    ldx #0\n    jmp (slot)\n    .next .return\n}\n")));
        Assert.Contains(
            "`.next .return`:",
            string.Join("\n", FlowFragment.Problems("65816", Slot + ".proc p: a8, i8 {\n    rep #$20\n    jmp (slot)\n    .next .return\n}\n")));
        Assert.Contains(
            "`p` is declared `noreturn`, but `.next .return` returns from it",
            string.Join("\n", FlowFragment.Problems("6502", Slot + ".proc p: noreturn {\n    jmp (slot)\n    .next .return\n}\n")));
    }

    /// <summary>
    /// A branch goes to a label or on, a call comes back, and a return already goes back to the
    /// caller, so <c>.next .return</c> stands under none of them.
    /// </summary>
    [Theory]
    [InlineData("    beq @out\n    .next .return\n@out:\n    rts\n", "`.next .return` cannot follow `beq @out`")]
    [InlineData("    jsr (table,x)\n    .next .return\n    rts\n", "`.next .return` cannot follow `jsr (table,x)`, because a call comes back")]
    [InlineData("    rts\n    .next .return\n", "`rts` already returns to its caller")]
    public void AReturnStandsOnlyUnderAJump(string body, string problem)
    {
        var problems = FlowFragment.Problems("65816",
            ".segment RODATA\n.data table: .addr q\n.segment CODE\n.proc q {\n    rts\n}\n.proc p {\n" + body + "}\n");

        Assert.Contains(problems, found => found.Contains(problem, StringComparison.Ordinal));
    }

    private static StackEffect EffectOf(ProgramAnalysis analysis, string routine) =>
        FlowFragment.EffectOf(analysis, routine);
}
