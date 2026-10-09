using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Tests the flags a signature gives: the values a routine needs on entry, the values it returns
/// with after <c>-&gt;</c>, and the flags it sets for its caller, written alone after <c>-&gt;</c>.
/// It also tests a <c>.state</c> and an <c>.ensure</c> that give a flag a value.
/// </summary>
public sealed class FlagSignatureTests
{
    private const string Header = ".module main\n.cpu 6502\n.segment CODE\n";

    /// <summary>
    /// A ROM routine's declared exit flags decide a branch after the call, so the branch needs no
    /// <c>.next</c>. So does a flag the routine's <c>keeps</c> promises, which comes back as the
    /// caller set it.
    /// </summary>
    [Theory]
    [InlineData(".proc SCROLL_UP = $E8EA: -> z = 1\n.export .proc main {\n    jsr SCROLL_UP\n    beq @x\n    .byte 1\n@x:\n    rts\n}\n")]
    [InlineData(".proc CHROUT = $FFD2: -> c = 0\n.export .proc main {\n    jsr CHROUT\n    bcc @x\n    .byte 1\n@x:\n    rts\n}\n")]
    [InlineData(".proc rom = $1234: keeps c\n.export .proc main {\n    sec\n    jsr rom\n    bcs @x\n    .byte 1\n@x:\n    rts\n}\n")]
    public void ADeclaredFlagDecidesABranchAfterTheCall(string text)
    {
        Assert.Empty(Problems(text));
    }

    /// <summary>
    /// A flag a signature does not name is not known after the call, so a branch on it may go
    /// either way.
    /// </summary>
    [Fact]
    public void AFlagTheSignatureDoesNotNameIsNotKnown()
    {
        Assert.Contains(
            Problems(".proc SCROLL_UP = $E8EA: -> z = 1\n.export .proc main {\n    jsr SCROLL_UP\n    bcc @x\n    .byte 1\n@x:\n    rts\n}\n"),
            problem => problem.Contains("falls into this data", StringComparison.Ordinal));
    }

    /// <summary>
    /// A routine that promises an exit flag is checked at each return, and the fix sets the flag
    /// with <c>.ensure</c>. The entry is empty where the routine needs nothing.
    /// </summary>
    [Fact]
    public void AnExitFlagIsCheckedAtEachReturn()
    {
        Assert.Empty(Problems(".export .proc p: -> c = 0 {\n    clc\n    rts\n}\n"));
        Assert.Empty(Problems(".export .proc p: -> z = 1 {\n    lda #0\n    rts\n}\n"));

        var diagnostic = Assert.Single(Diagnostics(".export .proc p: -> c = 0 {\n    lda $10\n    beq @x\n    clc\n@x:\n    rts\n}\n"));
        Assert.Equal("return-flag-mismatch", diagnostic.Id);
        Assert.Equal("`p` declares it returns with `c = 0`, but C is not known here", diagnostic.Message);
        Assert.Equal(new DiagnosticFix(FixKind.Ensure, "c = 0"), diagnostic.Fix);
    }

    /// <summary>
    /// A tail call hands on the exit flags of the routine it jumps to, which must keep the
    /// promise for the routine that jumps.
    /// </summary>
    [Fact]
    public void ATailCallHandsOnItsTargetsExitFlags()
    {
        Assert.Empty(Problems(".proc q: -> c = 0 {\n    clc\n    rts\n}\n.export .proc p: -> c = 0 {\n    jmp q\n}\n"));
        Assert.Equal(
            ["main.nt65:9: `p` declares it returns with `c = 1`, but `q`, which it hands control to, returns with C = 0"],
            Problems(".proc q: -> c = 0 {\n    clc\n    rts\n}\n.export .proc p: -> c = 1 {\n    jmp q\n}\n"));
    }

    /// <summary>
    /// A branch into another routine hands control to it where the branch is taken, so it is
    /// checked as a tail call is, against both that routine's entry flags and its exit flags.
    /// </summary>
    [Fact]
    public void ABranchIntoAnotherRoutineIsCheckedAsATailCall()
    {
        Assert.Empty(Problems(".proc q: c = 0 -> c = 0 {\n    rts\n}\n.export .proc p: -> c = 0 {\n    lda $10\n    bcc q\n    clc\n    rts\n}\n"));
        Assert.Equal(
            ["main.nt65:10: `p` declares it returns with `c = 1`, but `q`, which it hands control to, returns with C = 0"],
            Problems(".proc q: -> c = 0 {\n    clc\n    rts\n}\n.export .proc p: -> c = 1 {\n    lda $10\n    bcc q\n    sec\n    rts\n}\n"));
        Assert.Equal(
            ["main.nt65:9: `q` needs `c = 1`, but C is not known here"],
            Problems(".proc q: c = 1 {\n    rts\n}\n.export .proc p {\n    lda $10\n    beq q\n    rts\n}\n"));
    }

    /// <summary>
    /// A <c>.next .return</c> returns with the flags its statement leaves, so it is checked
    /// against the exit flags as <c>rts</c> is.
    /// </summary>
    [Fact]
    public void ANextReturnIsCheckedAsAReturn()
    {
        Assert.Empty(Problems(".export .proc p: -> c = 1 {\n    sec\n    jmp ($20)\n    .next .return\n}\n"));
        Assert.Equal(
            ["main.nt65:6: `p` declares it returns with `c = 1`, but C is 0 here"],
            Problems(".export .proc p: -> c = 1 {\n    clc\n    jmp ($20)\n    .next .return\n}\n"));
    }

    /// <summary>
    /// A flag a routine needs on entry is checked at each call, and is known inside the routine.
    /// This is the guide's example of a routine that adds to a pointer.
    /// </summary>
    [Fact]
    public void AnEntryFlagIsCheckedAtEachCall()
    {
        const string NextRow = ".proc next_row: d = 0, c = 0 -> c = 0 {\n    lda $10\n    adc #40\n    sta $10\n"
            + "    bcc @done\n    inc $11\n    clc\n@done:\n    rts\n}\n";

        Assert.Empty(Problems(NextRow + ".export .proc main {\n    cld\n    clc\n    jsr next_row\n    rts\n}\n"));

        var diagnostic = Assert.Single(Diagnostics(NextRow + ".export .proc main {\n    cld\n    jsr next_row\n    rts\n}\n"));
        Assert.Equal("call-flag-mismatch", diagnostic.Id);
        Assert.Equal("`next_row` needs `c = 0`, but C is not known here", diagnostic.Message);
        Assert.Equal(new DiagnosticFix(FixKind.Ensure, "c = 0"), diagnostic.Fix);
    }

    /// <summary>
    /// A flag named alone after <c>-&gt;</c> is a result, which every path must set after the
    /// routine's entry. A path that leaves the caller's flag in place is an error.
    /// </summary>
    [Fact]
    public void AResultFlagMustBeSetOnEveryPath()
    {
        Assert.Empty(Problems(".export .proc find_slot: reads x -> c {\n    lda $1000,x\n    cmp #0\n    rts\n}\n"));

        var diagnostic = Assert.Single(Diagnostics(".export .proc find_slot: reads x -> c {\n    lda $1000,x\n    beq @empty\n    cmp #1\n@empty:\n    rts\n}\n"));
        Assert.Equal("return-flag-not-set", diagnostic.Id);
        Assert.Equal(
            "`find_slot` declares `-> c`, but on this path nothing sets C before the return",
            diagnostic.Message);
    }

    /// <summary>The items that cannot mean anything are reported where they are written.</summary>
    [Theory]
    [InlineData(".export .proc p: keeps c -> c = 0 {\n    clc\n    rts\n}\n",
        "`keeps c` says C comes back as the caller left it, and `c = 0` after `->` says otherwise: declare one or the other")]
    [InlineData(".export .proc p: c {\n    rts\n}\n",
        "`c` on its own belongs after `->`, where it says the routine sets C: to say the routine uses its caller's C, write `reads c`")]
    [InlineData(".export .proc p: -> c = 2 {\n    rts\n}\n", "`c = 2` gives a flag a value, which must be the constant 0 or 1")]
    [InlineData(".export .proc p: m = 0 {\n    rts\n}\n", "`m` is not an item: the accumulator's width is written `a8` or `a16`")]
    [InlineData(".export .proc p {\n    .ensure z = 1\n    rts\n}\n",
        "`.ensure` cannot set `z = 1`: it sets only widths, `c`, `d` and `i` to 0 or 1, and `v` to 0")]
    public void AFlagItemThatCannotMeanAnythingIsReported(string text, string message)
    {
        Assert.Contains(message, Diagnostics(text).Select(diagnostic => diagnostic.Message));
    }

    /// <summary>
    /// A <c>.state</c> declares a flag where nothing says what it is, as after a ROM call, and is
    /// checked where the flags prove the other value.
    /// </summary>
    [Fact]
    public void AStateDeclaresAFlagAndIsCheckedWhereItIsKnown()
    {
        Assert.Empty(Problems(".proc rom = $1234\n.export .proc main {\n    jsr rom\n    .state c = 1\n    bcs @x\n    .byte 1\n@x:\n    rts\n}\n"));

        var diagnostic = Assert.Single(Diagnostics(".export .proc main {\n    clc\n    .state c = 1\n    rts\n}\n"));
        Assert.Equal("`.state c = 1` does not match: C is 0 here", diagnostic.Message);
        Assert.Equal(new DiagnosticFix(FixKind.StateItem, "c = 0"), diagnostic.Fix);
    }

    /// <summary>
    /// An <c>.ensure</c> that names a flag emits the instruction that sets it, only where the
    /// flags do not already prove it. On the 65816 a flag rides along with a <c>rep</c> or a
    /// <c>sep</c> the widths need anyway.
    /// </summary>
    [Fact]
    public void AnEnsureSetsAFlagOnlyWhereItIsNotAlreadySo()
    {
        Assert.Contains("    clc\n", Output("6502", ".export .proc p {\n    lda $10\n    .ensure c = 0\n    adc #1\n    rts\n}\n"));
        Assert.Single(
            Output("6502", ".export .proc p {\n    clc\n    lda $10\n    .ensure c = 0\n    adc #1\n    rts\n}\n").Split('\n'),
            line => line.Trim() == "clc");
        Assert.Contains("    sed\n    cli\n", Output("6502", ".export .proc p {\n    .ensure d = 1, i = 0\n    rts\n}\n"));

        var wide = Output("65816", ".export .proc p: a8 -> a16 {\n    .ensure a16, c = 0\n    rts\n}\n");
        Assert.Contains("rep #$21", wide, StringComparison.Ordinal);
        Assert.DoesNotContain("clc", wide, StringComparison.Ordinal);
    }

    /// <summary>A signature shows the flags it gives, on both sides of the arrow.</summary>
    [Fact]
    public void ASignatureShowsItsFlags()
    {
        var analysis = Analysis.Program(Analysis.Fragment, ("main.nt65", Header + ".proc q = $1234: c = 0 -> z = 1, n\n"));
        var q = Assert.Single(analysis.File("main.nt65").Symbols, symbol => symbol.Name == "q");
        Assert.Equal("a*, i*, native, near, c = 0 -> z = 1, n", q.Signature!.ToString());
    }

    /// <summary>
    /// A run of flag letters gives each flag it names one value. A signature shows the flags by
    /// value, the zeros first, each run in the order the status register holds the flags.
    /// </summary>
    [Fact]
    public void ARunOfFlagLettersGivesEachTheSameValue()
    {
        const string Rom = ".proc q = $1234: -> cz = 0, n = 1\n";
        Assert.Empty(Problems(Rom + ".export .proc main {\n    jsr q\n    bcc @x\n    .byte 1\n@x:\n    bne @y\n    .byte 1\n@y:\n    rts\n}\n"));

        var analysis = Analysis.Program(Analysis.Fragment, ("main.nt65", Header + Rom));
        var q = Assert.Single(analysis.File("main.nt65").Symbols, symbol => symbol.Name == "q");
        Assert.Equal("a*, i*, native, near -> zc = 0, n = 1", q.Signature!.ToString());

        Assert.Contains("    clc\n    cld\n", Output("6502", ".export .proc p {\n    .ensure dc = 0\n    rts\n}\n"));
    }

    /// <summary>
    /// A letter twice in a run, or a flag in two of a list's own items, is a mistake. A
    /// <c>.state</c> that gives a run the wrong value is fixed flag by flag.
    /// </summary>
    [Fact]
    public void ARunIsCheckedFlagByFlag()
    {
        Assert.Contains("`cc` is not a processor-state item", Diagnostics(".export .proc p: -> cc = 0 {\n    rts\n}\n").Select(d => d.Message));
        Assert.Contains(
            "`cz = 0` and `c = 1` both describe the same part of the state",
            Diagnostics(".proc q = $1234: -> cz = 0, c = 1\n").Select(d => d.Message));

        var diagnostic = Assert.Single(Diagnostics(".export .proc main {\n    clc\n    .state cz = 1\n    rts\n}\n"));
        Assert.Equal("`.state cz = 1` does not match: C is 0 here", diagnostic.Message);
        Assert.Equal(new DiagnosticFix(FixKind.StateItem, "c = 0, z = 1"), diagnostic.Fix);
    }

    /// <summary>Returns the warnings and errors nt65 reports for <paramref name="text"/>, each with its line.</summary>
    private static IReadOnlyList<string> Problems(string text) =>
        Analysis.Program(Analysis.Fragment, ("main.nt65", Header + text)).Problems();

    /// <summary>Returns the warnings and errors nt65 reports for <paramref name="text"/>.</summary>
    private static IReadOnlyList<Diagnostic> Diagnostics(string text) =>
        Analysis.Program(Analysis.Fragment, ("main.nt65", Header + text)).Diagnostics;

    /// <summary>Returns the ca65 source nt65 writes for <paramref name="text"/> on <paramref name="cpu"/>.</summary>
    private static string Output(string cpu, string text) =>
        Analysis.Outputs(("main.nt65", Header.Replace("6502", cpu, StringComparison.Ordinal) + text))["main.s"];
}
