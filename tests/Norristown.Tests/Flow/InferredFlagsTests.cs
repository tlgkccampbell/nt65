using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Tests the flags a routine with a body is inferred to return with: those it hands back as its
/// caller left them, and the values it gives others. A call uses them, across files too, and a
/// routine that names any flag after <c>-&gt;</c> promises only those, so relying on the rest draws
/// <c>unpromised-flag</c>.
/// </summary>
public sealed class InferredFlagsTests
{
    private const string Header = ".module main\n.cpu 6502\n.segment CODE\n";

    /// <summary>
    /// A routine that sets a flag, keeps one, or passes on what a routine it calls returns,
    /// decides a branch after the call to it, so the data after the branch is never run into.
    /// </summary>
    [Theory]
    [InlineData(".proc q {\n    clc\n    rts\n}\n.export .proc main {\n    jsr q\n    bcc @x\n    .byte 1\n@x:\n    rts\n}\n")]
    [InlineData(".proc q {\n    rts\n}\n.export .proc main {\n    sec\n    jsr q\n    bcs @x\n    .byte 1\n@x:\n    rts\n}\n")]
    [InlineData(".proc q {\n    clc\n    rts\n}\n.proc r {\n    jsr q\n    rts\n}\n.export .proc main {\n    jsr r\n    bcc @x\n    .byte 1\n@x:\n    rts\n}\n")]
    [InlineData(".proc q {\n    clv\n    rts\n}\n.proc r {\n    jmp q\n}\n.export .proc main {\n    jsr r\n    bvc @x\n    .byte 1\n@x:\n    rts\n}\n")]
    public void AnInferredFlagDecidesABranchAfterTheCall(string text)
    {
        Assert.Empty(Problems(text));
    }

    /// <summary>
    /// A flag set on one path and not another, or left by a routine nt65 cannot follow, is not
    /// known after the call.
    /// </summary>
    [Theory]
    [InlineData(".proc q {\n    lda $10\n    beq @x\n    clc\n@x:\n    rts\n}\n.export .proc main {\n    jsr q\n    bcc @y\n    .byte 1\n@y:\n    rts\n}\n")]
    [InlineData(".proc q {\n    clc\n    jmp ($10)\n    .next ?\n}\n.export .proc main {\n    jsr q\n    bcc @y\n    .byte 1\n@y:\n    rts\n}\n")]
    public void AFlagNotSetOnEveryPathIsNotKnown(string text)
    {
        Assert.Contains(Problems(text), problem => problem.Contains("falls into this data", StringComparison.Ordinal));
    }

    /// <summary>
    /// The answer of a routine in another file is used too. The file that calls it is analyzed
    /// again once the routine's answer is known, whichever file comes first.
    /// </summary>
    [Fact]
    public void ARoutineInAnotherFileIsInferredToo()
    {
        const string Lib = ".module lib\n.cpu 6502\n.segment CODE\n.export .proc q {\n    clc\n    rts\n}\n";
        const string Main = ".module main\n.cpu 6502\n.use lib::q\n.segment CODE\n"
            + ".export .proc main {\n    jsr q\n    bcc @x\n    .byte 1\n@x:\n    rts\n}\n";

        Assert.Empty(Analysis.Program(Analysis.Fragment, ("main.nt65", Main), ("lib.nt65", Lib)).Problems());
        Assert.Empty(Analysis.Program(Analysis.Fragment, ("lib.nt65", Lib), ("main.nt65", Main)).Problems());
    }

    /// <summary>
    /// A routine that needs D clear on entry and keeps it can be called twice in a row. Where it
    /// names other flags after <c>-&gt;</c>, D is not promised, and the second call relies on it,
    /// until D is named too.
    /// </summary>
    [Fact]
    public void AKeptFlagIsPromisedOnlyWhereTheExitNamesIt()
    {
        const string Main = ".export .proc main {\n    cld\n    clc\n    jsr next_row\n    jsr next_row\n    rts\n}\n";
        static string NextRow(string exit) => $".proc next_row: d = 0, c = 0 -> {exit} {{\n    lda $10\n    adc #40\n    sta $10\n"
            + "    bcc @done\n    inc $11\n    clc\n@done:\n    rts\n}\n";

        var diagnostic = Assert.Single(Diagnostics(NextRow("c = 0") + Main));
        Assert.Equal("unpromised-flag", diagnostic.Id);
        Assert.Equal(Severity.Warning, diagnostic.Severity);
        Assert.Equal(
            "this call relies on `next_row` returning with `d = 0`, which its `-> c = 0` does not promise",
            diagnostic.Message);
        Assert.Equal(new DiagnosticFix(FixKind.Exit, "d = 0", diagnostic.Fix?.At), diagnostic.Fix);

        Assert.Empty(Diagnostics(NextRow("c = 0, d = 0") + Main));
    }

    /// <summary>
    /// A routine that names no flag after <c>-&gt;</c> passes on what the routines it calls return
    /// without promising, and does not promote it. Relying on it through such a routine is
    /// reported, naming the routine that declined.
    /// </summary>
    [Fact]
    public void AnUnpromisedFlagIsNotPromotedThroughARoutineThatDeclaresNone()
    {
        const string Routines = ".proc clear: -> z = 1 {\n    lda #0\n    clc\n    rts\n}\n.proc b {\n    jsr clear\n    rts\n}\n";

        var diagnostic = Assert.Single(Diagnostics(Routines + ".export .proc main {\n    jsr b\n    bcc @x\n    .byte 1\n@x:\n    rts\n}\n"));
        Assert.Equal(
            "this branch relies on `clear` returning with `c = 0`, which its `-> z = 1` does not promise",
            diagnostic.Message);
        Assert.Equal(new DiagnosticFix(FixKind.Exit, "c = 0", diagnostic.Fix?.At), diagnostic.Fix);

        Assert.Empty(Diagnostics(Routines + ".export .proc main {\n    jsr b\n    beq @x\n    .byte 1\n@x:\n    rts\n}\n"));
    }

    /// <summary>
    /// An <c>.ensure</c> goes by what a routine promises, so it writes the instruction where the
    /// flag is set only by a body that does not promise it, and not where it is promised.
    /// </summary>
    [Fact]
    public void AnEnsureDoesNotRelyOnAnUnpromisedFlag()
    {
        const string Main = ".export .proc main {\n    jsr q\n    .ensure c = 0\n    rts\n}\n";

        Assert.DoesNotContain("    clc", Output(".proc q {\n    clc\n    rts\n}\n" + Main).Split("main__main:")[1], StringComparison.Ordinal);
        Assert.Contains("    clc", Output(".proc q: -> z = 1 {\n    lda #0\n    clc\n    rts\n}\n" + Main).Split("main__main:")[1], StringComparison.Ordinal);
    }

    /// <summary>Returns the warnings and errors nt65 reports for <paramref name="text"/>, each with its line.</summary>
    private static IReadOnlyList<string> Problems(string text) =>
        Analysis.Program(Analysis.Fragment, ("main.nt65", Header + text)).Problems();

    /// <summary>Returns the warnings and errors nt65 reports for <paramref name="text"/>.</summary>
    private static IReadOnlyList<Diagnostic> Diagnostics(string text) =>
        Analysis.Program(Analysis.Fragment, ("main.nt65", Header + text)).Diagnostics;

    /// <summary>Returns the ca65 source nt65 writes for <paramref name="text"/>.</summary>
    private static string Output(string text) => Analysis.Outputs(("main.nt65", Header + text))["main.s"];
}
