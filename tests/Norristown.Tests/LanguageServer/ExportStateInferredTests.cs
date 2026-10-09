using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests the hint on an exported routine whose bytes depend on a part of its entry that is
/// inferred from its callers in the program. A caller outside nt65 is not checked against such a
/// part, so the hint's fix declares it.
/// </summary>
public sealed class ExportStateInferredTests
{
    private const string Uri = "file:///c:/work/main.nt65";
    private const string Header = ".module main\n.cpu 65816\n.segment CODE\n";

    // A caller in the program that enters `ext` with an 8-bit A in native mode.
    private const string Caller = ".export .proc main: a8, i8, native {\n    jsr ext\n    rts\n}\n";

    /// <summary>
    /// An immediate sized by an inferred width is reported, and the fix declares the width.
    /// </summary>
    [Fact]
    public void AnImmediateSizedByAnInferredWidthIsReported()
    {
        const string Body = Caller + ".export .proc ext {\n    lda #$12\n    rts\n}\n";

        var (analysis, path) = Analyzed(Body);
        var hint = Assert.Single(analysis.SuggestionsFor(path), hint => hint.Id == "export-state-inferred");

        Assert.Equal(
            "`ext` is exported, and its bytes depend on `a8`, which is inferred from its callers here: a caller outside "
                + "nt65 is not checked against it, so the signature can declare it",
            hint.Message);
        Assert.Equal(
            Body.Replace(".proc ext {", ".proc ext: a8 {", StringComparison.Ordinal),
            Applied(analysis, path, hint, "Declare `a8` in the signature of `ext`"));
    }

    /// <summary>
    /// A width set in the routine before the immediate makes the immediate's size the routine's
    /// own, so nothing is reported. Neither is a routine that declares the width, nor one with no
    /// width-dependent byte at all.
    /// </summary>
    [Theory]
    [InlineData(".export .proc ext {\n    sep #$20\n    lda #$12\n    rts\n}\n")]
    [InlineData(".export .proc ext: a8 {\n    lda #$12\n    rts\n}\n")]
    [InlineData(".export .proc ext {\n    lda $12\n    rts\n}\n")]
    public void BytesTheRoutineDecidesAreNotReported(string routine)
    {
        var (analysis, path) = Analyzed(Caller + routine);

        Assert.Empty(analysis.Diagnostics);
        Assert.DoesNotContain(analysis.SuggestionsFor(path), hint => hint.Id == "export-state-inferred");
    }

    /// <summary>
    /// A `rep` widens A only in native mode, so an immediate after it depends on the inferred
    /// mode, and the fix declares `native`.
    /// </summary>
    [Fact]
    public void AnImmediateAfterRepDependsOnTheInferredMode()
    {
        const string Body = Caller + ".export .proc ext {\n    rep #$20\n    lda #$1234\n    sep #$20\n    rts\n}\n";

        var (analysis, path) = Analyzed(Body);
        var hint = Assert.Single(analysis.SuggestionsFor(path), hint => hint.Id == "export-state-inferred");

        Assert.Contains("depend on `native`,", hint.Message, StringComparison.Ordinal);
        Assert.Equal(
            Body.Replace(".proc ext {", ".proc ext: native {", StringComparison.Ordinal),
            Applied(analysis, path, hint, "Declare `native` in the signature of `ext`"));
    }

    /// <summary>
    /// A <c>d:</c> operand is written through the direct page the routine is entered with, so an
    /// inferred one is reported, and the fix declares it.
    /// </summary>
    [Fact]
    public void ADirectPageOperandDependsOnTheInferredDirectPage()
    {
        const string Body = ".export .proc main: a8, i8, native, dp = $2100 {\n    jsr ext\n    rts\n}\n"
            + ".export .proc ext {\n    sta d:$2105\n    rts\n}\n";

        var (analysis, path) = Analyzed(Body);
        var hint = Assert.Single(analysis.SuggestionsFor(path), hint => hint.Id == "export-state-inferred");

        Assert.Contains("depend on `dp = $2100`,", hint.Message, StringComparison.Ordinal);
        Assert.Equal(
            Body.Replace(".proc ext {", ".proc ext: dp = $2100 {", StringComparison.Ordinal),
            Applied(analysis, path, hint, "Declare `dp = $2100` in the signature of `ext`"));
    }

    /// <summary>
    /// A routine the program does not export has every caller in sight, so nothing is reported.
    /// </summary>
    [Fact]
    public void ARoutineNotExportedIsNotReported()
    {
        var (analysis, path) = Analyzed(Caller + ".proc ext {\n    lda #$12\n    rts\n}\n");

        Assert.Empty(analysis.Diagnostics);
        Assert.DoesNotContain(analysis.SuggestionsFor(path), hint => hint.Id == "export-state-inferred");
    }

    /// <summary>
    /// On a CPU with one width, an immediate's size depends on nothing a caller brings, so an
    /// exported 6502 routine is never reported.
    /// </summary>
    [Fact]
    public void ARoutineOnACpuWithOneWidthIsNotReported()
    {
        var (analysis, path) = Analyzed(
            ".export .proc main {\n    jsr ext\n    rts\n}\n.export .proc ext {\n    lda #$12\n    rts\n}\n",
            ".module main\n.cpu 6502\n.segment CODE\n");

        Assert.Empty(analysis.Diagnostics);
        Assert.DoesNotContain(analysis.SuggestionsFor(path), hint => hint.Id == "export-state-inferred");
    }

    /// <summary>Returns the text of the program after the fix titled <paramref name="title"/> is applied.</summary>
    private static string Applied(ProgramAnalysis analysis, string path, Diagnostic hint, string title)
    {
        Assert.Empty(analysis.Diagnostics);
        var model = analysis.ModelFor(path)!;
        var line = hint.Span.LineIndex;
        var action = Assert.Single(
            CodeActions.In(analysis, model, new Range(new Position(line, 0), new Position(line, 0))),
            action => action.Title == title);
        var applied = Editing.Apply(Header + model.Tree.Text[Header.Length..], action.Edit!.Changes[Uri]);
        Assert.StartsWith(Header, applied, StringComparison.Ordinal);
        return applied[Header.Length..];
    }

    private static (ProgramAnalysis Analysis, string Path) Analyzed(string body, string header = Header)
    {
        var workspace = new Workspace();
        var document = workspace.Open(new TextDocumentItem(Uri, "nt65", 1, header + body));
        var analysis = workspace.AnalysisForAsync(document.Tree.Path, TestTimeout.Token()).GetAwaiter().GetResult();
        return (analysis, document.Tree.Path);
    }
}
