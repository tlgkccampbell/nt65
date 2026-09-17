using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;
using Norristown.Semantics;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// The fixes for the diagnostics that name one, asked of the workspace directly rather than
/// over the wire: each is applied, the file it leaves is compared with what the programmer
/// would have written, and what that file says is checked to be nothing at all.
/// </summary>
public sealed class FixesTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    private const string Header = ".module main\n.cpu 65816\n.segment CODE\n";

    /// <summary>A body long enough that a branch over it cannot reach.</summary>
    private static readonly string Far = string.Concat(Enumerable.Repeat("    nop\n", 130));

    public static TheoryData<string, string, string> Fixes => new()
    {
        {
            "Branch with `jne`",
            $".proc main: a8, i8 {{\n    bne @done\n{Far}@done:\n    rts\n}}\n",
            $".proc main: a8, i8 {{\n    jne @done\n{Far}@done:\n    rts\n}}\n"
        },
        {
            "Leave with `rti`",
            ".proc irq: interrupt {\n    rts\n}\n",
            ".proc irq: interrupt {\n    rti\n}\n"
        },
        {
            "Write it as `.strz`",
            ".data title: .asciiz \"hi\"\n",
            ".data title: .strz \"hi\"\n"
        },
        {
            "Change it to `counter`",
            ".data counter: .byte 0\n.proc main: a8, i8 {\n    lda countr\n    rts\n}\n",
            ".data counter: .byte 0\n.proc main: a8, i8 {\n    lda counter\n    rts\n}\n"
        },
        {
            "Drop the level: an assertion that fails is an error",
            ".assert 1 == 1, error, \"always\"\n",
            ".assert 1 == 1, \"always\"\n"
        },
        {
            "Declare it as `.byte[4]`",
            ".data buffer: .res 4\n.proc main: a8, i8 {\n    lda buffer\n    rts\n}\n",
            ".data buffer: .byte[4]\n.proc main: a8, i8 {\n    lda buffer\n    rts\n}\n"
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
            "Write it as `(1 & 2) == 0`",
            "MASK = 1 & 2 == 0\n.proc main: a8, i8 {\n    lda #MASK\n    rts\n}\n",
            "MASK = (1 & 2) == 0\n.proc main: a8, i8 {\n    lda #MASK\n    rts\n}\n"
        },
        {
            "Write it as `1 & (2 == 0)`",
            "MASK = 1 & 2 == 0\n.proc main: a8, i8 {\n    lda #MASK\n    rts\n}\n",
            "MASK = 1 & (2 == 0)\n.proc main: a8, i8 {\n    lda #MASK\n    rts\n}\n"
        },
        {
            "Declare it `a8`, which is what the routine assumes",
            ".proc main {\n    lda #1\n    rts\n}\n",
            ".proc main: a8 {\n    lda #1\n    rts\n}\n"
        },
        {
            "Remove `SPARE`",
            "SPARE = 1\n",
            ""
        },
    };

    [Theory]
    [MemberData(nameof(Fixes))]
    public void AFixWritesWhatTheDiagnosticNames(string title, string body, string fixedBody)
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
    /// An export is written where the file's own exports go, under its <c>.module</c>, which is
    /// where the fix for a name another module cannot see writes one too.
    /// </summary>
    [Fact]
    public void ExportingWhatNothingNamesWritesTheExportUnderTheModule()
    {
        var (analysis, model) = Analyzed(Header + "SPARE = 1\n");

        var action = Assert.Single(CodeActions.In(analysis, model, Whole),
            action => action.Title == "Export `SPARE` from `main`");

        Assert.Equal(
            ".module main\n.export SPARE\n.cpu 65816\n.segment CODE\nSPARE = 1\n",
            Editing.Apply(Header + "SPARE = 1\n", action.Edit.Changes[Uri]));
    }

    /// <summary>
    /// A width the analysis cannot work out is the programmer's to say, so both widths are
    /// offered and neither is the one to apply without asking.
    /// </summary>
    [Fact]
    public void AWidthThatIsNotKnownOffersEitherOne()
    {
        const string Body = ".proc other: a8, i8 -> ? {\n    rts\n}\n.proc main: a8, i8 {\n    jsr other\n    lda #1\n    rts\n}\n";
        var (analysis, model) = Analyzed(Header + Body);

        var actions = CodeActions.In(analysis, model, Whole)
            .Where(action => action.Title.StartsWith("Say the width here", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(
            ["Say the width here with `.ensure a8`", "Say the width here with `.ensure a16`"],
            actions.Select(action => action.Title));
        Assert.All(actions, action => Assert.False(action.IsPreferred));
        Assert.Equal(
            Header + ".proc other: a8, i8 -> ? {\n    rts\n}\n.proc main: a8, i8 {\n    jsr other\n    .ensure a8\n    lda #1\n    rts\n}\n",
            Editing.Apply(Header + Body, actions[0].Edit.Changes[Uri]));
    }

    /// <summary>A name brought in and never written is faded, and the item that brought it may go.</summary>
    [Fact]
    public void AUseItemNothingNamesIsOfferedForRemoval()
    {
        const string Gfx = ".module gfx\n.segment CODE\n.export .proc clear {\n    rts\n}\n.export .proc fill {\n    rts\n}\n";
        const string Main = ".module main\n.use gfx::{clear, fill}\n.segment CODE\n.proc main {\n    jsr clear\n    rts\n}\n";
        var workspace = new Workspace();
        workspace.Open(new TextDocumentItem("file:///c:/work/gfx.nt65", "nt65", 1, Gfx));
        var document = workspace.Open(new TextDocumentItem(Uri, "nt65", 1, Main));
        var analysis = workspace.AnalysisFor(document.Tree.Path);
        var model = analysis.ModelFor(document.Tree.Path)!;

        var brought = Assert.Single(analysis.DiagnosticsFor(document.Tree.Path));
        Assert.Equal("`fill` is brought in and nothing names it: the `.use` item may go", brought.Message);
        Assert.True(brought.IsUnnecessary);

        var action = Assert.Single(CodeActions.In(analysis, model, Whole), action => action.Kind == "quickfix");
        Assert.Equal("Remove the `.use` of `fill`", action.Title);
        Assert.Equal(
            ".module main\n.use gfx::clear\n.segment CODE\n.proc main {\n    jsr clear\n    rts\n}\n",
            Editing.Apply(Main, action.Edit.Changes[Uri]));
    }

    /// <summary>A client that asks for one kind of change is given that kind and no other.</summary>
    [Fact]
    public void OnlyTheKindsAskedForAreOffered()
    {
        var (analysis, model) = Analyzed(Header + ".proc main: a8, i8 {\n    jmp ($1234)\n}\n");

        Assert.DoesNotContain(CodeActions.In(analysis, model, Whole, ["refactor"]), action => action.Kind == "quickfix");
        Assert.NotEmpty(CodeActions.In(analysis, model, Whole, ["quickfix"]));
    }

    /// <summary>The whole file, which is what a client asks about when it asks about all of it.</summary>
    private static Range Whole => new(new Position(0, 0), new Position(1000, 0));

    private static (ProgramAnalysis Analysis, SemanticModel Model) Analyzed(string text)
    {
        var workspace = new Workspace();
        var document = workspace.Open(new TextDocumentItem(Uri, "nt65", 1, text));
        var analysis = workspace.AnalysisFor(document.Tree.Path);
        return (analysis, analysis.ModelFor(document.Tree.Path)!);
    }
}
