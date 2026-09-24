using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;
using Norristown.Semantics;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests the fixes for diagnostics that suggest one, requested from the workspace directly rather
/// than through the protocol. Each fix is applied, the resulting file is compared with what the
/// programmer would have written, and that file is checked to have no diagnostics at all.
/// </summary>
public sealed class FixesTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    private const string Header = ".module main\n.cpu 65816\n.segment CODE\n";

    /// <summary>A body long enough that a branch over it cannot reach.</summary>
    private static readonly string Far = string.Concat(Enumerable.Repeat("    nop\n", 130));

    /// <summary>
    /// Gets a range covering the whole file, as a client sends when it asks for actions across all
    /// of it.
    /// </summary>
    private static Range Whole => new(new Position(0, 0), new Position(1000, 0));

    public static TheoryData<string, string, string> Fixes => new()
    {
        {
            "Branch with `jne`",
            $".export .proc main: a8, i8 {{\n    bne @done\n{Far}@done:\n    rts\n}}\n",
            $".export .proc main: a8, i8 {{\n    jne @done\n{Far}@done:\n    rts\n}}\n"
        },
        {
            "Leave with `rti`",
            ".proc irq: interrupt {\n    rts\n}\n",
            ".proc irq: interrupt {\n    rti\n}\n"
        },
        {
            "Change to `.strz`",
            ".data title: .asciiz \"hi\"\n",
            ".data title: .strz \"hi\"\n"
        },
        {
            "Change it to `counter`",
            ".data counter: .byte 0\n.export .proc main: a8, i8 {\n    lda countr\n    rts\n}\n",
            ".data counter: .byte 0\n.export .proc main: a8, i8 {\n    lda counter\n    rts\n}\n"
        },
        {
            "Drop the level: an assertion that fails is an error",
            ".assert 1 == 1, error, \"always\"\n",
            ".assert 1 == 1, \"always\"\n"
        },
        {
            "Declare it as `.byte[4]`",
            ".data buffer: .res 4\n.export .proc main: a8, i8 {\n    lda buffer\n    rts\n}\n",
            ".data buffer: .byte[4]\n.export .proc main: a8, i8 {\n    lda buffer\n    rts\n}\n"
        },
        {
            "Make `last` a member of the data",
            ".data table {\n    .word 1\nlast: .word 2\n}\n",
            ".data table {\n    .word 1\n.data last: .word 2\n}\n"
        },
        {
            "Make `here` a position, `@here`",
            ".data table {\n    .word here - table\nhere:\n    .word 2\n}\n",
            ".data table {\n    .word @here - table\n@here:\n    .word 2\n}\n"
        },
        {
            "Export it as `abs`",
            ".export marker: zp\n.data marker: .word 0\n",
            ".export marker: abs\n.data marker: .word 0\n"
        },
        {
            "Change to `(1 & 2) == 0`",
            "MASK = 1 & 2 == 0\n.export .proc main: a8, i8 {\n    lda #MASK\n    rts\n}\n",
            "MASK = (1 & 2) == 0\n.export .proc main: a8, i8 {\n    lda #MASK\n    rts\n}\n"
        },
        {
            "Change to `1 & (2 == 0)`",
            "MASK = 1 & 2 == 0\n.export .proc main: a8, i8 {\n    lda #MASK\n    rts\n}\n",
            "MASK = 1 & (2 == 0)\n.export .proc main: a8, i8 {\n    lda #MASK\n    rts\n}\n"
        },
        {
            "Declare it `a8`, which is what the routine assumes",
            ".export .proc main {\n    lda #1\n    rts\n}\n",
            ".export .proc main: a8 {\n    lda #1\n    rts\n}\n"
        },
        {
            "Remove `SPARE`",
            "SPARE = 1\n",
            ""
        },
    };

    [Theory]
    [MemberData(nameof(Fixes))]
    public void AFixAppliesWhatTheDiagnosticNames(string title, string body, string fixedBody)
    {
        var (analysis, model) = Analyzed(Header + body);

        var action = Assert.Single(CodeActions.In(analysis, model, Whole), action => action.Title == title);

        Assert.Equal("quickfix", action.Kind);
        Assert.Equal([Uri], action.Edit.Changes.Keys);
        Assert.Equal(Header + fixedBody, Editing.Apply(Header + body, action.Edit.Changes[Uri]));

        // What the fix leaves is a file with nothing wrong.
        var (after, _) = Analyzed(Header + fixedBody);
        Assert.Empty(after.Diagnostics.Select(diagnostic => diagnostic.Message));
    }

    /// <summary>
    /// A condition that tests a constant at file level is fixed by declaring the constant with
    /// <c>.config</c>, after which the condition may test it.
    /// </summary>
    [Fact]
    public void AConstantAConditionTestsBecomesASetting()
    {
        const string before = ".module main\n.export ROWS\nWIDE = 1\n.if WIDE {\n    ROWS = 2\n}\n";
        var (analysis, model) = Analyzed(before);

        var action = Assert.Single(CodeActions.In(analysis, model, Whole),
            action => action.Title == "Declare it with `.config`, as a setting");
        var after = Editing.Apply(before, action.Edit.Changes[Uri]);

        Assert.Equal(".module main\n.export ROWS\n.config WIDE = 1\n.if WIDE {\n    ROWS = 2\n}\n", after);
        Assert.Empty(Analyzed(after).Analysis.Diagnostics.Select(diagnostic => diagnostic.Message));
    }

    /// <summary>
    /// The fix that exports an unused name inserts the <c>.export</c> directly under the file's
    /// <c>.module</c>, which is also where the fix for a name another module cannot see inserts
    /// one.
    /// </summary>
    [Fact]
    public void ExportingWhatNothingNamesInsertsTheExportUnderTheModule()
    {
        var (analysis, model) = Analyzed(Header + "SPARE = 1\n");

        var action = Assert.Single(CodeActions.In(analysis, model, Whole),
            action => action.Title == "Export `SPARE` from `main`");

        Assert.Equal(
            ".module main\n.export SPARE\n.cpu 65816\n.segment CODE\nSPARE = 1\n",
            Editing.Apply(Header + "SPARE = 1\n", action.Edit.Changes[Uri]));
    }

    /// <summary>
    /// Where the analysis cannot work out a register width, only the programmer can say which it
    /// is, so both widths are offered and neither is marked preferred, since neither should be
    /// applied without asking.
    /// </summary>
    [Fact]
    public void AWidthThatIsNotKnownOffersEitherOne()
    {
        const string Body = ".proc other: a8, i8 -> ? {\n    rts\n}\n.export .proc main: a8, i8 {\n    jsr other\n    lda #1\n    rts\n}\n";
        var (analysis, model) = Analyzed(Header + Body);

        var actions = CodeActions.In(analysis, model, Whole)
            .Where(action => action.Title.StartsWith("Add `.ensure", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(
            ["Add `.ensure a8`", "Add `.ensure a16`"],
            actions.Select(action => action.Title));
        Assert.All(actions, action => Assert.False(action.IsPreferred));
        Assert.Equal(
            Header + ".proc other: a8, i8 -> ? {\n    rts\n}\n.export .proc main: a8, i8 {\n    jsr other\n    .ensure a8\n    lda #1\n    rts\n}\n",
            Editing.Apply(Header + Body, actions[0].Edit.Changes[Uri]));
    }

    /// <summary>
    /// A name brought in by <c>.use</c> and never used is shown faded, and a fix removes it from
    /// the <c>.use</c>.
    /// </summary>
    [Fact]
    public async Task AUseItemNothingNamesIsOfferedForRemoval()
    {
        const string Gfx = ".module gfx\n.segment CODE\n.export .proc clear {\n    rts\n}\n.export .proc fill {\n    rts\n}\n";
        const string Main = ".module main\n.use gfx::{clear, fill}\n.segment CODE\n.export .proc main {\n    jsr clear\n    rts\n}\n";
        var workspace = new Workspace();
        workspace.Open(new TextDocumentItem("file:///c:/work/gfx.nt65", "nt65", 1, Gfx));
        var document = workspace.Open(new TextDocumentItem(Uri, "nt65", 1, Main));
        var analysis = await workspace.AnalysisForAsync(document.Tree.Path, TestTimeout.Token());
        var model = analysis.ModelFor(document.Tree.Path)!;

        var brought = Assert.Single(analysis.DiagnosticsFor(document.Tree.Path));
        Assert.Equal("`fill` is brought in and nothing names it: the `.use` item may go", brought.Message);
        Assert.True(brought.IsUnnecessary);

        var action = Assert.Single(CodeActions.In(analysis, model, Whole), action => action.Kind == "quickfix");
        Assert.Equal("Remove the `.use` of `fill`", action.Title);
        Assert.Equal(
            ".module main\n.use gfx::clear\n.segment CODE\n.export .proc main {\n    jsr clear\n    rts\n}\n",
            Editing.Apply(Main, action.Edit.Changes[Uri]));
    }

    /// <summary>
    /// A missing bracket is inserted where the syntax tree holds a place for the missing token.
    /// The position comes from that token rather than from the end of the text, so on a line with
    /// a trailing comment the brace is inserted before the comment rather than after it.
    /// </summary>
    [Theory]
    [InlineData(".export .proc main: a8, i8\n    rts\n}\n", "Insert the missing `{`", ".export .proc main: a8, i8 {\n    rts\n}\n")]
    [InlineData(".export .proc main   ; note\n}\n", "Insert the missing `{`", ".export .proc main {   ; note\n}\n")]
    [InlineData("MASK = (1 + 2\n", "Insert the missing `)`", "MASK = (1 + 2)\n")]
    [InlineData(".export .proc main: a8, i8 {\n    lda [dp\n    rts\n}\n", "Insert the missing `]`",
        ".export .proc main: a8, i8 {\n    lda [dp]\n    rts\n}\n")]
    [InlineData(".use gfx::{clear\n", "Insert the missing `}`", ".use gfx::{clear}\n")]
    public void AMissingBracketIsInsertedWhereTheTreeHoldsItsPlace(string body, string title, string repaired)
    {
        var (analysis, model) = Analyzed(Header + body);

        var action = Assert.Single(CodeActions.In(analysis, model, Whole), action => action.Title == title);

        Assert.Equal("quickfix", action.Kind);
        Assert.Equal(Header + repaired, Editing.Apply(Header + body, action.Edit.Changes[Uri]));
    }

    /// <summary>
    /// Inserting the missing brace of a routine's body leaves a file with no diagnostics, which
    /// parses and means what it appears to mean.
    /// </summary>
    [Fact]
    public void InsertingTheMissingBraceLeavesAFileWithNothingWrong()
    {
        const string Body = ".export .proc main: a8, i8\n    rts\n}\n";
        var (analysis, model) = Analyzed(Header + Body);

        var action = Assert.Single(CodeActions.In(analysis, model, Whole), action => action.Title == "Insert the missing `{`");

        var repaired = Editing.Apply(Header + Body, action.Edit.Changes[Uri]);
        var (after, _) = Analyzed(repaired);
        Assert.Empty(after.Diagnostics.Select(diagnostic => diagnostic.Message));
    }

    /// <summary>
    /// A name that ca65 would read as an instruction has no mechanical fix, so the fix inserts no
    /// text. Instead it puts the caret on the name and starts a rename, because what the name
    /// should be is for the programmer to decide, not for the server to guess.
    /// </summary>
    [Fact]
    public void AMnemonicNameOffersARenameAndEditsNothing()
    {
        var (analysis, model) = Analyzed(Header + ".export .proc main: a8, i8 {\nlda:\n    bra lda\n}\n");

        var action = Assert.Single(CodeActions.In(analysis, model, Whole), action => action.Kind == "quickfix");

        Assert.Equal("Rename `lda`…", action.Title);
        Assert.Empty(action.Edit.Changes);
        var rename = Assert.IsType<Command>(action.Command);
        Assert.Equal("nt65.rename", rename.Name);
        Assert.Equal([Uri, 4, 0], rename.Arguments);
    }

    /// <summary>A client that asks for one kind of change is given that kind and no other.</summary>
    [Fact]
    public void OnlyTheKindsAskedForAreOffered()
    {
        var (analysis, model) = Analyzed(Header + ".export .proc main: a8, i8 {\n    jmp ($1234)\n}\n");

        Assert.DoesNotContain(CodeActions.In(analysis, model, Whole, ["refactor"]), action => action.Kind == "quickfix");
        Assert.NotEmpty(CodeActions.In(analysis, model, Whole, ["quickfix"]));
    }

    private static (ProgramAnalysis Analysis, SemanticModel Model) Analyzed(string text)
    {
        var workspace = new Workspace();
        var document = workspace.Open(new TextDocumentItem(Uri, "nt65", 1, text));
        var analysis = workspace.AnalysisForAsync(document.Tree.Path, TestTimeout.Token()).GetAwaiter().GetResult();
        return (analysis, analysis.ModelFor(document.Tree.Path)!);
    }
}
