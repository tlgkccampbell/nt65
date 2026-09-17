using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// The rewrites offered at a selection, which nothing reported: each is asked for where a
/// programmer would ask for it, applied, and the file it leaves is compared with what they
/// would have written.
/// </summary>
public sealed class RefactorsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    private const string GfxUri = "file:///c:/work/gfx.nt65";

    private const string Gfx = ".module gfx\n.segment CODE\n.export .proc clear {\n    rts\n}\n"
        + ".export .proc fill {\n    rts\n}\n";

    /// <summary>A path written out in full is brought in, and every writing of it in the file is shortened.</summary>
    [Fact]
    public void APathIsBroughtInWithAUse()
    {
        const string Main = ".module main\n.segment CODE\n.proc main {\n    jsr gfx::clear\n    jsr gfx::clear\n    rts\n}\n";

        var action = Single(Main, "gfx::clear", "Bring in `gfx::clear` with `.use`");

        Assert.Equal("refactor.rewrite", action.Kind);
        Assert.Equal(
            ".module main\n.use gfx::clear\n.segment CODE\n.proc main {\n    jsr clear\n    jsr clear\n    rts\n}\n",
            Editing.Apply(Main, action.Edit.Changes[Uri]));
    }

    /// <summary>A name a <c>.use</c> brought in is written out in full, and the item that brought it goes.</summary>
    [Fact]
    public void ABroughtNameIsWrittenOutInFull()
    {
        const string Main = ".module main\n.use gfx::clear\n.segment CODE\n.proc main {\n    jsr clear\n    rts\n}\n";

        var action = Single(Main, "jsr clear", "Write `clear` as `gfx::clear`", "jsr ".Length);

        Assert.Equal(
            ".module main\n.segment CODE\n.proc main {\n    jsr gfx::clear\n    rts\n}\n",
            Editing.Apply(Main, action.Edit.Changes[Uri]));
    }

    /// <summary>The <c>.use</c> items are put in order, and one nothing names is not among them.</summary>
    [Fact]
    public void TheUseItemsAreOrderedAndWhatNothingNamesGoes()
    {
        const string Main = ".module main\n.use gfx::fill\n.use gfx::clear\n.segment CODE\n"
            + ".proc main {\n    jsr clear\n    rts\n}\n";

        var action = Single(Main, ".use gfx::fill", "Organize the `.use` items");

        Assert.Equal(
            ".module main\n.use gfx::clear\n.segment CODE\n.proc main {\n    jsr clear\n    rts\n}\n",
            Editing.Apply(Main, action.Edit.Changes[Uri]));
    }

    /// <summary>A declaration is exported where the caret is on it, and stops being exported the same way.</summary>
    [Fact]
    public void ADeclarationIsExportedAndUnexported()
    {
        const string Main = ".module main\n.segment CODE\n.proc start {\n    rts\n}\n";

        var exported = Single(Main, ".proc start", "Export `start` from `main`");
        var written = Editing.Apply(Main, exported.Edit.Changes[Uri]);
        Assert.Equal(".module main\n.segment CODE\n.export .proc start {\n    rts\n}\n", written);

        var stopped = Single(written, ".export .proc start", "Stop exporting `start`");
        Assert.Equal(Main, Editing.Apply(written, stopped.Edit.Changes[Uri]));
    }

    /// <summary>What a routine leaves is declared from what the analysis finds at its returns.</summary>
    [Fact]
    public void WhatARoutineLeavesIsDeclared()
    {
        const string Main = ".module main\n.cpu 65816\n.segment CODE\n.proc widen: a8, i8 {\n    rep #$20\n    rts\n}\n";

        var action = Single(Main, ".proc widen", "Declare what `widen` leaves: `-> a16, i8, native`");

        Assert.Equal(
            ".module main\n.cpu 65816\n.segment CODE\n.proc widen: a8, i8 -> a16, i8, native {\n    rep #$20\n    rts\n}\n",
            Editing.Apply(Main, action.Edit.Changes[Uri]));
    }

    /// <summary>A <c>rep</c> that only changes a width is written as the <c>.ensure</c> that says so, and back.</summary>
    [Fact]
    public void WidthsAreWrittenAsAnEnsureAndBack()
    {
        const string Main = ".module main\n.cpu 65816\n.segment CODE\n.proc widen: a8, i8 -> a16 {\n    rep #$20\n    rts\n}\n";

        var ensured = Single(Main, "rep #$20", "Write it as `.ensure a16`");
        var written = Editing.Apply(Main, ensured.Edit.Changes[Uri]);
        Assert.Equal(
            ".module main\n.cpu 65816\n.segment CODE\n.proc widen: a8, i8 -> a16 {\n    .ensure a16\n    rts\n}\n",
            written);

        var back = Single(written, ".ensure a16", "Write it out as `rep #$20`");
        Assert.Equal(Main, Editing.Apply(written, back.Edit.Changes[Uri]));
    }

    /// <summary>A number written in an operand is given a name at the top of the file.</summary>
    [Fact]
    public void ANumberIsGivenAName()
    {
        const string Main = ".module main\n.segment CODE\n.proc main {\n    lda $d020\n    rts\n}\n";

        var action = Single(Main, "$d020", "Give `$d020` a name");

        Assert.Equal("refactor.extract", action.Kind);
        Assert.Equal(
            ".module main\nVALUE = $d020\n.segment CODE\n.proc main {\n    lda VALUE\n    rts\n}\n",
            Editing.Apply(Main, action.Edit.Changes[Uri]));
    }

    /// <summary>A cheap local is given a name of its own, and a label only its routine names is made cheap.</summary>
    [Fact]
    public void ALabelIsGivenANameOrMadeCheap()
    {
        const string Main = ".module main\n.segment CODE\n.proc main {\n@loop:\n    jmp @loop\n}\n";

        var named = Single(Main, "@loop:", "Give `@loop` a name of its own");
        var written = Editing.Apply(Main, named.Edit.Changes[Uri]);
        Assert.Equal(".module main\n.segment CODE\n.proc main {\nloop:\n    jmp loop\n}\n", written);

        var cheap = Single(written, "loop:", "Make `loop` a cheap local, `@loop`");
        Assert.Equal(Main, Editing.Apply(written, cheap.Edit.Changes[Uri]));
    }

    /// <summary>A declaration a routine owns is put in a segment block of its own.</summary>
    [Fact]
    public void ADeclarationIsPutInASegmentBlock()
    {
        const string Main = ".module main\n.segment CODE\n.proc main {\n    lda table\n    rts\n"
            + "    .data table: .byte 1, 2\n}\n";

        var action = Single(Main, ".data table", "Put `table` in a `.segment RODATA` block");

        Assert.Equal(
            ".module main\n.segment CODE\n.proc main {\n    lda table\n    rts\n"
                + "    .segment RODATA {\n        .data table: .byte 1, 2\n    }\n}\n",
            Editing.Apply(Main, action.Edit.Changes[Uri]));
    }

    /// <summary>Selected instructions become a routine of their own, with a call left where they were.</summary>
    [Fact]
    public void SelectedLinesBecomeARoutine()
    {
        const string Main = ".module main\n.segment CODE\n.proc main {\n    lda #0\n    sta $0400\n    rts\n}\n";

        var action = Single(Main, new Range(new Position(3, 0), new Position(5, 0)), "Extract into a `.proc`");

        Assert.Equal("refactor.extract", action.Kind);
        Assert.Equal(
            ".module main\n.segment CODE\n.proc main {\n    jsr extracted\n    rts\n}\n"
                + "\n.proc extracted {\n    lda #0\n    sta $0400\n    rts\n}\n",
            Editing.Apply(Main, action.Edit.Changes[Uri]));
    }

    /// <summary>A selection that leaves its routine part way through is no routine of its own.</summary>
    [Fact]
    public void ASelectionThatReturnsIsNotExtracted()
    {
        const string Main = ".module main\n.segment CODE\n.proc main {\n    lda #0\n    rts\n}\n";

        Assert.DoesNotContain(
            Actions(Main, new Range(new Position(3, 0), new Position(5, 0))),
            action => action.Title == "Extract into a `.proc`");
    }

    /// <summary>ca65 in the selection is read as nt65, as far as one line at a time can say.</summary>
    [Fact]
    public void Ca65InTheSelectionIsReadAsNt65()
    {
        const string Main = ".module main\n.segment CODE\n.proc main {\n    rts\n}\n"
            + ".macpack longbranch\n.zeropage\nptr: .res 2\n.code\n.proc old\n    lda #$00\n.endproc\n";

        var action = Single(Main, new Range(new Position(5, 0), new Position(12, 0)), "Read the selection as nt65");

        Assert.Equal(
            ".module main\n.segment CODE\n.proc main {\n    rts\n}\n"
                + ".segment ZEROPAGE\n.data ptr: .byte[2]\n.segment CODE\n.proc old {\n    lda #$00\n}\n",
            Editing.Apply(Main, action.Edit.Changes[Uri]));
    }

    /// <summary>
    /// The rest of what one line at a time can say: a macro's parameters, a define as the
    /// constant or the function it stood for, a condition on a define, and ca65's operator
    /// words. What needs a decision is left as it was.
    /// </summary>
    [Fact]
    public void Ca65ThatIsOnlySpellingIsRewritten()
    {
        const string Main = ".module main\n.segment CODE\n.proc main {\n    rts\n}\n"
            + ".macro mov dest, src\n    lda src\n    sta dest\n.endmacro\n"
            + ".define LINES 25\n.define rgb(r, g) (r .bitor g)\n"
            + ".ifdef DEBUG\nTRACE = 1 .bitand 3\n.endif\n"
            + ":   bne :-\n";

        var action = Single(Main, new Range(new Position(5, 0), new Position(15, 0)), "Read the selection as nt65");

        Assert.Equal(
            ".module main\n.segment CODE\n.proc main {\n    rts\n}\n"
                + ".macro mov(dest, src) {\n    lda src\n    sta dest\n}\n"
                + "LINES = 25\n.func rgb(r, g) = (r | g)\n"
                + ".if .defined(DEBUG) {\nTRACE = 1 & 3\n}\n"
                + ":   bne :-\n",
            Editing.Apply(Main, action.Edit.Changes[Uri]));
    }

    /// <summary>The one action with <paramref name="title"/> offered where <paramref name="at"/> is written.</summary>
    private static CodeAction Single(string text, string at, string title, int offset = 0)
    {
        var start = text.IndexOf(at, StringComparison.Ordinal) + offset;
        var line = text[..start].Count(c => c == '\n');
        var character = start - (text[..start].LastIndexOf('\n') + 1);
        var caret = new Position(line, character);
        return Assert.Single(Actions(text, new Range(caret, caret)), action => action.Title == title);
    }

    /// <summary>The one action with <paramref name="title"/> offered over <paramref name="range"/>.</summary>
    private static CodeAction Single(string text, Range range, string title) =>
        Assert.Single(Actions(text, range), action => action.Title == title);

    private static IReadOnlyList<CodeAction> Actions(string text, Range range)
    {
        var workspace = new Workspace();
        workspace.Open(new TextDocumentItem(GfxUri, "nt65", 1, Gfx));
        var document = workspace.Open(new TextDocumentItem(Uri, "nt65", 1, text));
        var analysis = workspace.AnalysisFor(document.Tree.Path);
        return CodeActions.In(analysis, analysis.ModelFor(document.Tree.Path)!, range, ["refactor"]);
    }
}
