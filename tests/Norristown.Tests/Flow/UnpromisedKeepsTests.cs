using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Tests the <c>unpromised-keep</c> warning. A declared <c>keeps</c> is a contract, so a call
/// that relies on the routine keeping a register the list leaves out is warned about at the call,
/// even though the routine's body does keep it today.
/// </summary>
public sealed class UnpromisedKeepsTests
{
    private const string Header = ".module main\n.cpu 6502\n.segment CODE\n";

    // A routine that keeps X and Y, and promises only X.
    private const string PrintDigit = ".proc print_digit: keeps x {\n    lda #'0'\n    sta $10\n    rts\n}\n";

    /// <summary>
    /// Using a register after the call that was set before it relies on the routine keeping it.
    /// The warning is at the call, and points at the use.
    /// </summary>
    [Fact]
    public void AUseAfterTheCallReliesOnTheKeep()
    {
        var diagnostic = Assert.Single(Diagnostics(PrintDigit + ".export .proc main {\n    ldy #1\n    jsr print_digit\n    sty $11\n    rts\n}\n"));

        Assert.Equal("unpromised-keep", diagnostic.Id);
        Assert.Equal(Severity.Warning, diagnostic.Severity);
        Assert.Equal(11, diagnostic.Span.Line);
        Assert.Equal(
            "this call relies on `print_digit` keeping Y, which it does but does not promise (it declares `keeps x`)",
            diagnostic.Message);
        Assert.Equal(12, Assert.Single(diagnostic.Related).Span.Line);
        Assert.Equal(new DiagnosticFix(FixKind.Keeps, "y", diagnostic.Fix?.At), diagnostic.Fix);
    }

    /// <summary>
    /// Nothing relies on the keep where the register is given a new value first, where the routine
    /// declares no <c>keeps</c>, or where it has no body and keeps only what it declares.
    /// </summary>
    [Theory]
    [InlineData(PrintDigit + ".export .proc main {\n    ldy #1\n    jsr print_digit\n    ldy #2\n    sty $11\n    rts\n}\n")]
    [InlineData(".proc print_digit {\n    lda #'0'\n    sta $10\n    rts\n}\n.export .proc main {\n    ldy #1\n    jsr print_digit\n    sty $11\n    rts\n}\n")]
    [InlineData(".proc print_digit = $ffd2: keeps x\n.export .proc main {\n    jsr print_digit\n    rts\n}\n")]
    public void NothingReliesOnMoreThanIsPromised(string text)
    {
        Assert.Empty(Diagnostics(text));
    }

    /// <summary>
    /// A routine whose own <c>keeps</c> promise depends on a routine it calls relies on that
    /// routine too, whether it returns after the call or hands control over in a tail call.
    /// </summary>
    [Theory]
    [InlineData(".export .proc main: keeps y {\n    jsr print_digit\n    rts\n}\n", "call")]
    [InlineData(".export .proc main: keeps y {\n    jmp print_digit\n}\n", "tail call")]
    public void AKeepsPromiseCanRelyOnACallee(string main, string how)
    {
        var diagnostic = Assert.Single(Diagnostics(PrintDigit + main));

        Assert.Equal(
            $"this {how} relies on `print_digit` keeping Y, which it does but does not promise (it declares `keeps x`)",
            diagnostic.Message);
    }

    /// <summary>
    /// A shift through memory leaves the accumulator alone, so a use of A after one still relies
    /// on the call before it.
    /// </summary>
    [Fact]
    public void AShiftThroughMemoryLeavesTheRelianceOnA()
    {
        var diagnostic = Assert.Single(Diagnostics(".proc bump: keeps x {\n    inc $10\n    rts\n}\n"
            + ".export .proc main {\n    lda #1\n    jsr bump\n    asl $12\n    sta $11\n    rts\n}\n"));

        Assert.Equal(
            "this call relies on `bump` keeping A, which it does but does not promise (it declares `keeps x`)",
            diagnostic.Message);
    }

    /// <summary>A deliberate reliance can be allowed where it is made.</summary>
    [Fact]
    public void ADeliberateRelianceCanBeAllowed()
    {
        Assert.Empty(Diagnostics(PrintDigit + ".export .proc main {\n    ldy #1\n"
            + "    .allow \"unpromised-keep\", \"Y survives print_digit on every platform we ship\"\n"
            + "    jsr print_digit\n    sty $11\n    rts\n}\n"));
    }

    /// <summary>Returns the warnings and errors nt65 reports for <paramref name="text"/>.</summary>
    private static IReadOnlyList<Diagnostic> Diagnostics(string text) =>
        Analysis.Program(("main.nt65", Header + text)).Diagnostics;
}
