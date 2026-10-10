namespace Norristown.Tests.Semantics;

/// <summary>
/// Checks where a problem with a line that a macro or function body emits is reported. A body
/// serves every call, so the problem is reported at the call in the file's own text, with the body
/// line as a note and without a fix that would change the body.
/// </summary>
public sealed class BodyLineReportTests
{
    /// <summary>
    /// A <c>.next</c> that is not needed in a body declared in the same file is reported at the
    /// call, with the body line as a note, and offers no fix.
    /// </summary>
    [Fact]
    public void AnAnnotationProblemInASameFileBodyIsReportedAtTheCall()
    {
        const string Text = ".module main\n.cpu 6502\n.segment CODE\n.macro m() {\n    jmp @out\n    .next @out\n@out:\n}\n"
            + ".export .proc p {\n    m!()\n    rts\n}\n";

        var only = Assert.Single(Analysis.Program(("main.nt65", Text)).Diagnostics);

        Assert.Equal("next-successors-known", only.Id);
        Assert.Equal(10, only.Span.Line);
        var note = Assert.Single(only.Related);
        Assert.Equal(6, note.Span.Line);
        Assert.Equal("in the macro body", note.Message);
        Assert.Null(only.Fix);
    }

    /// <summary>
    /// A return in a body that breaks the stack, a <c>keeps</c> promise or a flag promise of the
    /// routine that calls the macro is reported at the call, with the return as a note, and offers
    /// no fix.
    /// </summary>
    /// <param name="signature">The routine's signature, written after its name.</param>
    /// <param name="body">The macro body's first line, which comes before its <c>rts</c>.</param>
    /// <param name="id">The diagnostic expected.</param>
    [Theory]
    [InlineData("", "pla", "return-beneath-entry")]
    [InlineData(": keeps a", "lda #0", "keeps-broken")]
    [InlineData(": -> c", "nop", "return-flag-not-set")]
    [InlineData(": -> c = 1", "clc", "return-flag-mismatch")]
    public void AReturnProblemInABodyIsReportedAtTheCall(string signature, string body, string id)
    {
        var text = $".module main\n.cpu 6502\n.segment CODE\n.macro m() {{\n    {body}\n    rts\n}}\n"
            + $".export .proc p{signature} {{\n    m!()\n}}\n";

        var only = Assert.Single(Analysis.Program(("main.nt65", text)).Diagnostics);

        Assert.Equal(id, only.Id);
        Assert.Equal(9, only.Span.Line);
        var note = Assert.Single(only.Related);
        Assert.Equal(6, note.Span.Line);
        Assert.Equal("in the macro body", note.Message);
        Assert.Null(only.Fix);
    }

    /// <summary>
    /// A problem that evaluating a <c>.func</c> body meets with what a call gave it is reported at
    /// the call, with the body's text as a note.
    /// </summary>
    [Fact]
    public void AProblemInAFunctionBodyIsReportedAtTheCall()
    {
        const string Text = ".module main\n.cpu 6502\n.func half(n) = 100 / n\n.segment CODE\n"
            + ".export .proc p {\n    lda #half(0)\n    rts\n}\n";

        var only = Assert.Single(Analysis.Program(("main.nt65", Text)).Diagnostics);

        Assert.Equal("division-by-zero", only.Id);
        Assert.Equal(new Span("main.nt65", 6, 10, 17), only.Span);
        var note = Assert.Single(only.Related);
        Assert.Equal(3, note.Span.Line);
        Assert.Equal("in the function body", note.Message);
    }
}
