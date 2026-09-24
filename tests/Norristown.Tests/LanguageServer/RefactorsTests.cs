using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests the refactorings offered at a selection, as opposed to fixes for reported diagnostics.
/// Each is requested where a programmer would request it and then applied, and the resulting file
/// is compared with what the programmer would have written.
/// </summary>
public sealed class RefactorsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    private const string GfxUri = "file:///c:/work/gfx.nt65";

    private const string Gfx = ".module gfx\n.segment CODE\n.export .proc clear {\n    rts\n}\n"
        + ".export .proc fill {\n    rts\n}\n";

    /// <summary>
    /// A path spelled out in full is brought in with a <c>.use</c>, and every occurrence of it in
    /// the file is shortened.
    /// </summary>
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

    /// <summary>
    /// A name that a <c>.use</c> brought in is spelled out in full, and the <c>.use</c> item that
    /// brought it in is removed.
    /// </summary>
    [Fact]
    public void ABroughtNameIsSpelledOutInFull()
    {
        const string Main = ".module main\n.use gfx::clear\n.segment CODE\n.proc main {\n    jsr clear\n    rts\n}\n";

        var action = Single(Main, "jsr clear", "Write `clear` as `gfx::clear`", "jsr ".Length);

        Assert.Equal(
            ".module main\n.segment CODE\n.proc main {\n    jsr gfx::clear\n    rts\n}\n",
            Editing.Apply(Main, action.Edit.Changes[Uri]));
    }

    /// <summary>
    /// Laying out works on the whole expression: every call at its outer level is broken, one
    /// argument to a line, and each line that leaves a bracket open indents the next a step
    /// further, as the formatter does. The caret may be anywhere in the expression.
    /// </summary>
    [Fact]
    public void TheWholeExpressionIsLaidOut()
    {
        const string Main = ".module main\n.export X\nX = .select(1, 2, 3) + .select(4, 5, 6)\n";

        var action = Single(Main, "+", "Lay out the expression across lines");

        Assert.Equal(
            ".module main\n.export X\nX = .select(\n    1,\n    2,\n    3) + .select(\n        4,\n        5,\n        6)\n",
            Editing.Apply(Main, action.Edit.Changes[Uri]));
    }

    /// <summary>
    /// A <c>.switch</c> keeps its value beside its bracket and each set beside its result, and what
    /// the outer level holds is broken only where it does not fit within the line length, which
    /// gives it a level of its own. The caret in one of the sets lays out the whole expression.
    /// </summary>
    [Fact]
    public void WhatDoesNotFitIsBrokenAtALevelOfItsOwn()
    {
        const string Main = ".module main\n.export f\n.func f(m) = .switch(m, [1], 2, .select(1, .switch(m, [3], 4, [5], 6, 7), 8))\n";

        var action = Single(Main, "[1]", "Lay out the expression across lines", 1, lineLength: 40);

        Assert.Equal(
            ".module main\n.export f\n.func f(m) = .switch(m,\n    [1], 2,\n    .select(\n        1,\n"
                + "        .switch(m, [3], 4, [5], 6, 7),\n        8))\n",
            Editing.Apply(Main, action.Edit.Changes[Uri]));
    }

    /// <summary>
    /// An expression across lines joins back onto one from anywhere inside it, and neither action is
    /// offered where a comment would be lost. A set on its own is laid out as a call is.
    /// </summary>
    [Fact]
    public void AnExpressionJoinsBackUnlessACommentWouldBeLost()
    {
        const string Broken = ".module main\n.export X\nX = .switch(2,\n    [1, 4], 10,\n    [2, 3], 20,\n    30) + 1\n";
        var joined = Single(Broken, "3]", "Join the expression onto one line");
        Assert.Equal(".module main\n.export X\nX = .switch(2, [1, 4], 10, [2, 3], 20, 30) + 1\n",
            Editing.Apply(Broken, joined.Edit.Changes[Uri]));

        const string Commented = ".module main\n.export X\nX = .select(\n    1,  ; one\n    2, 3)\n";
        Assert.DoesNotContain(Actions(Commented, At(Commented, "2,")), action => action.Title.EndsWith("line", StringComparison.Ordinal)
            || action.Title.EndsWith("lines", StringComparison.Ordinal));

        const string Set = ".module main\n.export X\nX = 2 .in [1, 2]\n";
        var values = Single(Set, "1,", "Lay out the expression across lines");
        Assert.Equal(".module main\n.export X\nX = 2 .in [\n    1,\n    2]\n", Editing.Apply(Set, values.Edit.Changes[Uri]));
    }

    /// <summary>Organizing sorts the <c>.use</c> items and removes any that nothing names.</summary>
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

    /// <summary>
    /// With the caret on a declaration, the declaration can be exported, and with the caret on an
    /// exported declaration, it can stop being exported.
    /// </summary>
    [Fact]
    public void ADeclarationIsExportedAndUnexported()
    {
        const string Main = ".module main\n.segment CODE\n.proc start {\n    rts\n}\n";

        var exported = Single(Main, ".proc start", "Export `start` from `main`");
        var edited = Editing.Apply(Main, exported.Edit.Changes[Uri]);
        Assert.Equal(".module main\n.segment CODE\n.export .proc start {\n    rts\n}\n", edited);

        var stopped = Single(edited, ".export .proc start", "Stop exporting `start`");
        Assert.Equal(Main, Editing.Apply(edited, stopped.Edit.Changes[Uri]));
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

    /// <summary>
    /// A <c>rep</c> that only changes a width is converted to the <c>.ensure</c> that says so, and
    /// back.
    /// </summary>
    [Fact]
    public void WidthsAreConvertedToAnEnsureAndBack()
    {
        const string Main = ".module main\n.cpu 65816\n.segment CODE\n.proc widen: a8, i8 -> a16 {\n    rep #$20\n    rts\n}\n";

        var ensured = Single(Main, "rep #$20", "Rewrite as `.ensure a16`");
        var edited = Editing.Apply(Main, ensured.Edit.Changes[Uri]);
        Assert.Equal(
            ".module main\n.cpu 65816\n.segment CODE\n.proc widen: a8, i8 -> a16 {\n    .ensure a16\n    rts\n}\n",
            edited);

        var back = Single(edited, ".ensure a16", "Write it out as `rep #$20`");
        Assert.Equal(Main, Editing.Apply(edited, back.Edit.Changes[Uri]));
    }

    /// <summary>
    /// Only an immediate operand sets the flags. <c>rep FLAGS</c> is another instruction
    /// altogether, and converting it to the <c>.ensure</c> that its value happens to match would
    /// change what the line does.
    /// </summary>
    [Fact]
    public void OnlyAnImmediateRepIsConvertedToAnEnsure()
    {
        const string Main = ".module main\n.cpu 65816\nFLAGS = $20\n.segment CODE\n"
            + ".proc widen: a8, i8 -> a16 {\n    rep FLAGS\n    rts\n}\n";

        var caret = Locate.At(Main, "rep FLAGS");
        Assert.DoesNotContain(
            Actions(Main, new Range(caret, caret)),
            action => action.Title.StartsWith("Rewrite as `.ensure", StringComparison.Ordinal));
    }

    /// <summary>A number in an operand is given a name at the top of the file.</summary>
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
        var edited = Editing.Apply(Main, named.Edit.Changes[Uri]);
        Assert.Equal(".module main\n.segment CODE\n.proc main {\nloop:\n    jmp loop\n}\n", edited);

        var cheap = Single(edited, "loop:", "Make `loop` a cheap local, `@loop`");
        Assert.Equal(Main, Editing.Apply(edited, cheap.Edit.Changes[Uri]));
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

        var selection = new Range(Locate.At(Main, "    lda #0"), Locate.At(Main, "    rts"));
        var action = Single(Main, selection, "Extract into a `.proc`");

        Assert.Equal("refactor.extract", action.Kind);
        Assert.Equal(
            ".module main\n.segment CODE\n.proc main {\n    jsr extracted\n    rts\n}\n"
                + "\n.proc extracted {\n    lda #0\n    sta $0400\n    rts\n}\n",
            Editing.Apply(Main, action.Edit.Changes[Uri]));
    }

    /// <summary>
    /// The new routine is named after the label the selection starts with, which is the one
    /// word the file already has about those lines. The client is asked to rename it at the
    /// position where the name lands once the change has been applied.
    /// </summary>
    [Fact]
    public void AnExtractedRoutineIsNamedAfterItsLabelAndOfferedForRenaming()
    {
        const string Main = ".module main\n.segment CODE\n.proc main {\n@wait:\n    lda $d012\n    cmp #100\n    rts\n}\n";

        var selection = new Range(Locate.At(Main, "@wait:"), Locate.At(Main, "    rts"));
        var action = Single(Main, selection, "Extract into a `.proc`");

        var edited = Editing.Apply(Main, action.Edit.Changes[Uri]);
        Assert.Equal(
            ".module main\n.segment CODE\n.proc main {\n    jsr wait\n    rts\n}\n"
                + "\n.proc wait {\n@wait:\n    lda $d012\n    cmp #100\n    rts\n}\n",
            edited);

        Assert.NotNull(action.Command);
        Assert.Equal("nt65.rename", action.Command.Name);
        var line = (int)action.Command.Arguments![1];
        var character = (int)action.Command.Arguments[2];
        Assert.Equal(Uri, action.Command.Arguments[0]);
        Assert.Equal("wait {", edited.Split('\n')[line][character..]);
    }

    /// <summary>
    /// The rename is asked for at the position the client counts to, which treats a lone
    /// <c>\r</c> as a line break just as the server does everywhere else.
    /// </summary>
    [Fact]
    public void AnExtractedRoutineIsOfferedForRenamingInAFileWithCarriageReturnLineEnds()
    {
        const string Main = ".module main\r.segment CODE\r.proc main {\r@wait:\r    lda $d012\r    cmp #100\r    rts\r}\r";

        var action = Single(Main, new Range(new Position(3, 0), new Position(6, 0)), "Extract into a `.proc`");

        var edited = Editing.Apply(Main, action.Edit.Changes[Uri]);
        Assert.NotNull(action.Command);
        var line = (int)action.Command.Arguments![1];
        var character = (int)action.Command.Arguments[2];
        Assert.Equal("wait {", edited.Split(["\r\n", "\r", "\n"], StringSplitOptions.None)[line][character..]);
    }

    /// <summary>
    /// A selection that returns from its routine part way through cannot be extracted into a
    /// routine.
    /// </summary>
    [Fact]
    public void ASelectionThatReturnsIsNotExtracted()
    {
        const string Main = ".module main\n.segment CODE\n.proc main {\n    lda #0\n    rts\n}\n";

        Assert.DoesNotContain(
            Actions(Main, new Range(Locate.At(Main, "    lda #0"), Locate.At(Main, "}"))),
            action => action.Title == "Extract into a `.proc`");
    }

    /// <summary>
    /// A jump of any kind to a label the selection declares stays inside the new routine, and so
    /// does a software interrupt, which comes back to the instruction after it.
    /// </summary>
    [Fact]
    public void AJumpThatStaysInsideIsExtracted()
    {
        const string Main = ".module main\n.cpu 65c02\n.segment CODE\n.proc main {\n"
            + "@wait:\n    brk #0\n    lda $d012\n    bne @next\n    bra @wait\n@next:\n    jmp @wait\n}\n";

        var selection = new Range(Locate.At(Main, "@wait:"), Locate.At(Main, "}"));
        var action = Single(Main, selection, "Extract into a `.proc`");

        Assert.Contains("jsr wait", Editing.Apply(Main, action.Edit.Changes[Uri]), StringComparison.Ordinal);
    }

    /// <summary>
    /// A jump out of the selection to another routine, or through a pointer, would leave the new
    /// routine without coming back to its caller, so the selection is not extracted.
    /// </summary>
    [Theory]
    [InlineData("jmp other")]
    [InlineData("bra other")]
    [InlineData("jmp ($fffc)")]
    public void AJumpThatLeavesIsNotExtracted(string jump)
    {
        var main = ".module main\n.cpu 65c02\n.segment CODE\n.proc main {\n    lda #0\n    " + jump + "\n}\n"
            + ".proc other {\n    rts\n}\n";

        Assert.DoesNotContain(
            Actions(main, new Range(Locate.At(main, "    lda #0"), Locate.At(main, "}"))),
            action => action.Title == "Extract into a `.proc`");
    }

    /// <summary>ca65 source in the selection is rewritten as nt65, as far as a line-by-line translation can.</summary>
    [Fact]
    public void Ca65InTheSelectionIsReadAsNt65()
    {
        const string Main = ".module main\n.segment CODE\n.proc main {\n    rts\n}\n"
            + ".macpack longbranch\n.zeropage\nptr: .res 2\n.code\n.proc old\n    lda #$00\n.endproc\n";

        var selection = new Range(Locate.At(Main, ".macpack"), Locate.At(Main, ".endproc\n|"));
        var action = Single(Main, selection, "Read the selection as nt65");

        Assert.Equal(
            ".module main\n.segment CODE\n.proc main {\n    rts\n}\n"
                + ".segment ZEROPAGE\n.data ptr: .byte[2]\n.segment CODE\n.proc old {\n    lda #$00\n}\n",
            Editing.Apply(Main, action.Edit.Changes[Uri]));
    }

    /// <summary>
    /// A line-by-line translation also rewrites a macro's parameters, a define as the constant or
    /// the function it stood for, a condition on a define, and ca65's word operators such as
    /// <c>.bitor</c>. Anything that needs a decision, such as an unnamed label, is left as it was.
    /// </summary>
    [Fact]
    public void Ca65ThatIsOnlySpellingIsRewritten()
    {
        const string Main = ".module main\n.segment CODE\n.proc main {\n    rts\n}\n"
            + ".macro mov dest, src\n    lda src\n    sta dest\n.endmacro\n"
            + ".define LINES 25\n.define rgb(r, g) (r .bitor g)\n"
            + ".ifdef DEBUG\nTRACE = 1 .bitand 3\n.endif\n"
            + ":   bne :-\n";

        var selection = new Range(Locate.At(Main, ".macro mov"), Locate.At(Main, "bne :-\n|"));
        var action = Single(Main, selection, "Read the selection as nt65");

        Assert.Equal(
            ".module main\n.segment CODE\n.proc main {\n    rts\n}\n"
                + ".macro mov(dest, src) {\n    lda src\n    sta dest\n}\n"
                + "LINES = 25\n.func rgb(r, g) = (r | g)\n"
                + ".if .defined(DEBUG) {\nTRACE = 1 & 3\n}\n"
                + ":   bne :-\n",
            Editing.Apply(Main, action.Edit.Changes[Uri]));
    }

    /// <summary>
    /// Returns the one action titled <paramref name="title"/> that is offered at the caret where
    /// <paramref name="at"/> first appears in <paramref name="text"/>, moved by
    /// <paramref name="offset"/> characters.
    /// </summary>
    private static CodeAction Single(
        string text, string at, string title, int offset = 0, int lineLength = LineBreaks.DefaultLength) =>
        Assert.Single(Actions(text, At(text, at, offset), lineLength), action => action.Title == title);

    /// <summary>Returns an empty range at <paramref name="at"/> in the text, plus the offset.</summary>
    private static Range At(string text, string at, int offset = 0)
    {
        var start = text.IndexOf(at, StringComparison.Ordinal) + offset;
        var line = text[..start].Count(c => c == '\n');
        var character = start - (text[..start].LastIndexOf('\n') + 1);
        var caret = new Position(line, character);
        return new Range(caret, caret);
    }

    /// <summary>
    /// Returns the one action titled <paramref name="title"/> that is offered over
    /// <paramref name="range"/>.
    /// </summary>
    private static CodeAction Single(string text, Range range, string title) =>
        Assert.Single(Actions(text, range), action => action.Title == title);

    private static IReadOnlyList<CodeAction> Actions(string text, Range range, int lineLength = LineBreaks.DefaultLength)
    {
        var workspace = new Workspace();
        workspace.Open(new TextDocumentItem(GfxUri, "nt65", 1, Gfx));
        var document = workspace.Open(new TextDocumentItem(Uri, "nt65", 1, text));
        var analysis = workspace.AnalysisForAsync(document.Tree.Path, TestTimeout.Token()).GetAwaiter().GetResult();
        return CodeActions.In(analysis, analysis.ModelFor(document.Tree.Path)!, range, ["refactor"], lineLength);
    }
}
