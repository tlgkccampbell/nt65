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
            "this call relies on `print_digit` keeping Y, which its `keeps x` does not promise",
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
    /// A routine that declares no <c>keeps</c> passes on what the routines it calls keep without
    /// promising, without promoting it. Relying on that through it is reported at the call, naming
    /// the routine that declined, and the fix adds the register to that routine's <c>keeps</c>.
    /// What it keeps by its own code, or through a promise, may be relied on.
    /// </summary>
    [Fact]
    public void AKeepNobodyPromisedIsNotPromotedThroughARoutineThatDeclaresNone()
    {
        const string Routines = ".proc c: keeps x {\n    inc $10\n    rts\n}\n.proc b {\n    jsr c\n    rts\n}\n";

        var diagnostic = Assert.Single(Diagnostics(Routines + ".export .proc main {\n    ldy #1\n    jsr b\n    sty $11\n    rts\n}\n"));
        Assert.Equal(
            "this call relies on `b` keeping Y, which depends on `c`, whose `keeps x` does not promise it",
            diagnostic.Message);
        Assert.Equal(new DiagnosticFix(FixKind.Keeps, "y", diagnostic.Fix?.At), diagnostic.Fix);
        Assert.Equal(4, diagnostic.Fix?.At?.Line);

        Assert.Empty(Diagnostics(Routines + ".export .proc main {\n    ldx #1\n    jsr b\n    stx $11\n    rts\n}\n"));
        Assert.Empty(Diagnostics(".proc b {\n    lda #1\n    rts\n}\n.export .proc main {\n    ldy #1\n    jsr b\n    sty $11\n    rts\n}\n"));
    }

    /// <summary>
    /// A routine whose own <c>keeps</c> promise depends on a routine it calls relies on that
    /// routine too, whether it returns after the call or hands control over in a tail call, made
    /// by a jump or by a branch.
    /// </summary>
    [Theory]
    [InlineData(".export .proc main: keeps y {\n    jsr print_digit\n    rts\n}\n", "call")]
    [InlineData(".export .proc main: keeps y {\n    jmp print_digit\n}\n", "tail call")]
    [InlineData(".export .proc main: keeps y {\n    lda $10\n    beq print_digit\n    rts\n}\n", "tail call")]
    public void AKeepsPromiseCanRelyOnACallee(string main, string how)
    {
        var diagnostic = Assert.Single(Diagnostics(PrintDigit + main));

        Assert.Equal(
            $"this {how} relies on `print_digit` keeping Y, which its `keeps x` does not promise",
            diagnostic.Message);
    }

    /// <summary>
    /// A flag is kept as a register is. A branch after the call on a flag set before it relies
    /// on the routine keeping that flag, and promising it with <c>keeps</c> answers the warning.
    /// A flag cannot be saved around the call alone, so the only fix is the promise.
    /// </summary>
    [Fact]
    public void ABranchOnAFlagSetBeforeTheCallReliesOnTheKeep()
    {
        const string Main = ".export .proc main {\n    cmp #1\n    jsr store\n    beq @done\n    inc $11\n@done:\n    rts\n}\n";

        var diagnostic = Assert.Single(Diagnostics(".proc store: keeps x {\n    sta $10\n    rts\n}\n" + Main));
        Assert.Equal(
            "this call relies on `store` keeping Z, which its `keeps x` does not promise",
            diagnostic.Message);
        Assert.Equal(new DiagnosticFix(FixKind.Keeps, "z", diagnostic.Fix?.At), diagnostic.Fix);
        Assert.Null(diagnostic.Also);

        Assert.Empty(Diagnostics(".proc store: keeps x, z {\n    sta $10\n    rts\n}\n" + Main));
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
            "this call relies on `bump` keeping A, which its `keeps x` does not promise",
            diagnostic.Message);
    }

    /// <summary>
    /// The second fix saves the register around the call where that is safe: for A on any CPU,
    /// and for Y where the CPU has <c>phy</c>.
    /// </summary>
    [Theory]
    [InlineData("6502", ".proc bump: keeps x {\n    inc $10\n    rts\n}\n.export .proc main {\n    lda #1\n    jsr bump\n    ora #2\n    sta $11\n    rts\n}\n", "pha")]
    [InlineData("65C02", PrintDigit + ".export .proc main {\n    ldy #1\n    jsr print_digit\n    tya\n    sta $11\n    rts\n}\n", "phy")]
    public void TheRegisterCanBeSavedAroundTheCall(string cpu, string text, string push)
    {
        var diagnostic = Assert.Single(Analysis.Program(("main.nt65", Header.Replace("6502", cpu, StringComparison.Ordinal) + text)).Diagnostics);

        Assert.Equal(new DiagnosticFix(FixKind.SaveAround, push), diagnostic.Also);
    }

    /// <summary>
    /// The register is not saved where that is not safe. X and Y need <c>phx</c> and <c>phy</c>,
    /// which the 6502 lacks; the carry cannot be saved without every other flag; a label on the
    /// call lets a branch reach it past the save; and the pull sets N and Z, so code that reads
    /// either before setting both rules it out.
    /// </summary>
    [Theory]
    [InlineData(PrintDigit + ".export .proc main {\n    ldy #1\n    jsr print_digit\n    tya\n    sta $11\n    rts\n}\n")]
    [InlineData(".proc setc: keeps x {\n    lda #1\n    rts\n}\n.export .proc main {\n    sec\n    jsr setc\n    adc #1\n    sta $11\n    rts\n}\n")]
    [InlineData(".proc bump: keeps x {\n    inc $10\n    rts\n}\n.export .proc main {\n    lda #1\n@again: jsr bump\n    ora #2\n    sta $11\n    bne @again\n    rts\n}\n")]
    [InlineData(".proc bump: keeps x {\n    inc $10\n    rts\n}\n.export .proc main {\n    lda #1\n    jsr bump\n    beq @zero\n    sta $11\n@zero:\n    rts\n}\n")]
    public void TheRegisterIsNotSavedWhereThatIsNotSafe(string text)
    {
        Assert.All(Diagnostics(text), diagnostic => Assert.Null(diagnostic.Also));
        Assert.NotEmpty(Diagnostics(text));
    }

    /// <summary>A deliberate reliance can be allowed where it is made.</summary>
    [Fact]
    public void ADeliberateRelianceCanBeAllowed()
    {
        Assert.Empty(Diagnostics(PrintDigit + ".export .proc main {\n    ldy #1\n"
            + "    .allow \"unpromised-keep\", \"Y survives print_digit on every platform we ship\"\n"
            + "    jsr print_digit\n    sty $11\n    rts\n}\n"));
    }

    /// <summary>
    /// A return that hands back a register this routine promises to keep relies on the called
    /// routine keeping it, and a <c>.next .return</c> is such a return as much as <c>rts</c> is.
    /// </summary>
    [Theory]
    [InlineData("    rts\n")]
    [InlineData("    jmp ($20)\n    .next .return\n")]
    public void AReturnReliesOnTheKeepItPromises(string exit)
    {
        var diagnostic = Assert.Single(Diagnostics(PrintDigit + ".export .proc main: keeps y {\n    jsr print_digit\n" + exit + "}\n"));

        Assert.Equal("unpromised-keep", diagnostic.Id);
        Assert.Contains("`main` returns it here, promising `keeps`", diagnostic.Related.Select(related => related.Message));
    }

    /// <summary>
    /// A call through a table to any of several routines relies on each of them keeping what the
    /// code after it uses. Each routine that does not promise the register is warned about.
    /// </summary>
    [Fact]
    public void ACallWithSeveralTargetsReliesOnEachOfThem()
    {
        const string Text = ".module main\n.cpu 65816\n.segment CODE\n"
            + ".proc b: a8, i8, native, keeps x {\n    inc $10\n    rts\n}\n.proc b2: a8, i8, native, keeps x {\n    inc $11\n    rts\n}\n"
            + ".export .proc main: a8, i8, native {\n    lda #1\n    ldx #0\n    jsr ($2000,x)\n    .next b, b2\n    sta $12\n    rts\n}\n";

        var diagnostics = Analysis.Program(("main.nt65", Text)).Diagnostics;

        Assert.Equal(
            [
                "this call relies on `b2` keeping A, which its `keeps x` does not promise",
                "this call relies on `b` keeping A, which its `keeps x` does not promise",
            ],
            diagnostics.Select(diagnostic => diagnostic.Message).Order());
    }

    /// <summary>
    /// A tail call through a vector to any of several routines relies on each of them keeping
    /// what this routine promises to keep.
    /// </summary>
    [Fact]
    public void ATailCallWithSeveralTargetsReliesOnEachOfThem()
    {
        const string Routines = ".proc b: keeps x {\n    inc $10\n    rts\n}\n.proc b2: keeps x, y {\n    inc $11\n    rts\n}\n";

        var diagnostic = Assert.Single(Diagnostics(Routines + ".export .proc main: keeps y {\n    jmp ($20)\n    .next b, b2\n}\n"));

        Assert.Equal("this tail call relies on `b` keeping Y, which its `keeps x` does not promise", diagnostic.Message);
    }

    /// <summary>Returns the warnings and errors nt65 reports for <paramref name="text"/>.</summary>
    private static IReadOnlyList<Diagnostic> Diagnostics(string text) =>
        Analysis.Program(("main.nt65", Header + text)).Diagnostics;
}
