using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests the suggestions an editor shows where code could be smaller, faster or clearer: a tail
/// call in place of a call and a return, a <c>rep</c> or <c>sep</c> that sets a width already set,
/// and a constant used as an address that could be declared as data.
/// The tests cover where each is made, where it is held back because the change would not be
/// safe, and that no build reports one.
/// </summary>
public sealed class SuggestionsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    private const string Header = ".module main\n.cpu 65816\n.segment CODE\n";

    private static Range Whole => new(new Position(0, 0), new Position(1000, 0));

    /// <summary>
    /// A suggestion is shown in the editor as a hint and offered as a fix, and the program's own
    /// diagnostics, which a build reports, do not include it.
    /// </summary>
    [Fact]
    public void ASuggestionIsAHintNoBuildReports()
    {
        var (analysis, path) = Analyzed(".proc helper {\n    rts\n}\n.export .proc main: native {\n    jsr helper\n    rts\n}\n");

        var suggestion = Assert.Single(analysis.SuggestionsFor(path));
        Assert.Equal("tail-call", suggestion.Id);
        Assert.Equal("`jsr helper` then `rts` can be `jmp helper`, which saves 9 cycles and a byte", suggestion.Message);
        Assert.Empty(analysis.Diagnostics);

        var shown = Assert.Single(Lsp.ToDiagnostics(analysis.SuggestionsFor(path), null, analysis.Configuration));
        Assert.Equal(DiagnosticSeverity.Hint, shown.Severity);
    }

    /// <summary>
    /// A return that another path reaches through its label has to stay for that path, so only
    /// the call becomes a jump, and the saving is in cycles alone.
    /// </summary>
    [Fact]
    public void AReturnAnotherPathReachesIsKept()
    {
        const string Body = ".proc helper {\n    rts\n}\n"
            + ".export .proc main {\n    beq @done\n    jsr helper\n@done:\n    rts\n}\n";
        var (analysis, path) = Analyzed(Body);

        Assert.Equal(
            "`jsr helper` then `rts` can be `jmp helper`, which saves 9 cycles",
            Assert.Single(analysis.SuggestionsFor(path)).Message);
        var model = analysis.ModelFor(path)!;
        var action = Assert.Single(CodeActions.In(analysis, model, Whole), action => action.Title == "Jump with `jmp` as a tail call");
        Assert.Equal(
            Header + Body.Replace("jsr helper", "jmp helper", StringComparison.Ordinal),
            Editing.Apply(Header + Body, action.Edit!.Changes[Uri]));
    }

    /// <summary>
    /// No tail call is suggested where the routine called depends on the depth of the stack it
    /// is entered with. A routine that pops its own return address leaves two levels at once, and
    /// after a jump it would leave three. That holds for a routine that branches into such code
    /// too, and for one that takes arguments on the stack.
    /// </summary>
    [Theory]
    [InlineData(".proc helper {\n    pla\n    pla\n    rts\n}\n")]
    [InlineData(".proc helper {\n    beq popper::out\n    rts\n}\n.proc popper {\n    nop\nout:\n    .state ?\n    pla\n    pla\n    rts\n}\n")]
    [InlineData(".proc helper {\n    tsx\n    rts\n}\n")]
    [InlineData(".proc helper: pushed 2 {\n    rts\n}\n")]
    public void NoTailCallIsSuggestedToARoutineThatReadsItsCallersStack(string helper)
    {
        var (analysis, path) = Analyzed(helper + ".export .proc main {\n    jsr helper\n    rts\n}\n");

        Assert.DoesNotContain(analysis.SuggestionsFor(path), suggestion => suggestion.Id == "tail-call");
    }

    /// <summary>
    /// No suggestion is made in a routine with a branch this build leaves out, because another
    /// build takes that branch and may need what the suggestion would remove.
    /// </summary>
    [Fact]
    public void NoSuggestionIsMadeInARoutineWithAnOmittedBranch()
    {
        var (analysis, path) = Analyzed(".proc helper {\n    rts\n}\n"
            + ".export .proc main: a8, i8 {\n    sep #$20\n    jsr helper\n    .if 0 {\n        nop\n    }\n    rts\n}\n");

        Assert.Empty(analysis.SuggestionsFor(path));
    }

    /// <summary>
    /// A <c>rep</c> or <c>sep</c> is suggested for removal only where the width it sets is
    /// already set on every path to it.
    /// </summary>
    [Fact]
    public void AWidthThatPathsDisagreeOnIsNotAlreadySet()
    {
        var (analysis, path) = Analyzed(
            ".export .proc main: a8, i8 {\n    beq @wide\n    rep #$20\n@wide:\n    sep #$20\n    rts\n}\n");

        Assert.Empty(analysis.SuggestionsFor(path));
    }

    /// <summary>
    /// A line of a macro body serves every call, and another call may need it, so no suggestion is
    /// made on it.
    /// </summary>
    [Fact]
    public void NoSuggestionIsMadeInAMacroBody()
    {
        var (analysis, path) = Analyzed(
            ".macro narrow {\n    sep #$20\n}\n.export .proc main: a8, i8 {\n    narrow!\n    rts\n}\n");

        Assert.Empty(analysis.SuggestionsFor(path));
    }

    /// <summary>
    /// A constant that an instruction reaches memory through is suggested as data, with a fix for
    /// each of <c>.data</c> and <c>.mmio</c>. A constant used only as a number, or as the address of
    /// code a call names, is not.
    /// </summary>
    [Fact]
    public void AConstantUsedAsAnAddressIsSuggestedAsData()
    {
        const string Body = ".export .const BORDER = $D020  ; the frame\n.const LIMIT = 4\n.const CHROUT = $FFD2\n"
            + ".export .proc main {\n    lda #LIMIT\n    sta BORDER\n    jsr CHROUT\n    rts\n}\n";
        var (analysis, path) = Analyzed(Body);

        var suggestion = Assert.Single(analysis.SuggestionsFor(path), found => found.Id == "constant-used-as-address");
        Assert.Equal(
            "`BORDER` is used as an address: declare it as data, or with `.mmio` if it is a hardware register",
            suggestion.Message);
        Assert.Equal(4, suggestion.Span.Line);

        var model = analysis.ModelFor(path)!;
        var actions = CodeActions.In(analysis, model, Whole);
        var data = Assert.Single(actions, action => action.Title == "Declare it as data with `.data`");
        var mmio = Assert.Single(actions, action => action.Title == "Declare it as a hardware register with `.mmio`");
        Assert.StartsWith(
            Header + ".export .data BORDER: .byte = $D020  ; the frame\n",
            Editing.Apply(Header + Body, data.Edit!.Changes[Uri]), StringComparison.Ordinal);
        Assert.StartsWith(
            Header + ".export .mmio BORDER: .byte = $D020  ; the frame\n",
            Editing.Apply(Header + Body, mmio.Edit!.Changes[Uri]), StringComparison.Ordinal);
    }

    /// <summary>
    /// A constant that another file uses as an address is still suggested as data after an edit
    /// to the file that declares it. The other file is not analyzed again, so it still refers to
    /// the constant as it was declared before the edit.
    /// </summary>
    [Fact]
    public void AConstantAnotherFileUsesAsAnAddressIsStillSuggestedAfterAnEditBesideIt()
    {
        const string HardwareUri = "file:///c:/work/hw.nt65";
        const string Hardware = ".module hw\n.export BORDER\n.const BORDER = $D020\n.segment CODE\n"
            + ".export .proc flash {\n    lda #0\n    rts\n}\n";
        const string Main = ".module main\n.use hw::{BORDER, flash}\n.segment CODE\n"
            + ".export .proc main {\n    jsr flash\n    sta BORDER\n    rts\n}\n";
        var workspace = new Workspace();
        var hardware = workspace.Open(new TextDocumentItem(HardwareUri, "nt65", 1, Hardware));
        workspace.Open(new TextDocumentItem(Uri, "nt65", 1, Main));
        var path = hardware.Tree.Path;
        Assert.Contains(Suggested(), found => found.Id == "constant-used-as-address");

        // `lda #0` becomes `lda #1`, which leaves main's view of `hw` as it was.
        workspace.Change(new VersionedTextDocumentIdentifier(HardwareUri, 2), [new TextDocumentContentChangeEvent(
            new Range(new Position(5, 9), new Position(5, 10)), "1")]);
        var edited = Suggested();
        Assert.Contains(edited, found => found.Id == "constant-used-as-address");

        IReadOnlyList<Diagnostic> Suggested()
        {
            var analysis = workspace.AnalysisForAsync(path, TestTimeout.Token()).GetAwaiter().GetResult();
            return analysis.SuggestionsFor(path);
        }
    }

    private static (ProgramAnalysis Analysis, string Path) Analyzed(string body)
    {
        var workspace = new Workspace();
        var document = workspace.Open(new TextDocumentItem(Uri, "nt65", 1, Header + body));
        var analysis = workspace.AnalysisForAsync(document.Tree.Path, TestTimeout.Token()).GetAwaiter().GetResult();
        return (analysis, document.Tree.Path);
    }
}
