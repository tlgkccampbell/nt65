using Norristown.LanguageServer.Protocol;
using StreamJsonRpc;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// What an editor gets within one file: hover, go to definition, find references, highlights and
/// rename.
/// </summary>
public sealed class SymbolRequestsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    /// <summary>
    /// A file with zero-page data, a constant, a scope and two procs, so that every request has
    /// something of each kind to find.
    /// </summary>
    private const string Source = """
        .module main
        .segment ZEROPAGE {
        .data ptr:    .word
        }

        SCREEN = $0400
        .segment CODE
        .scope gfx {
            .proc init {
                lda #0
                sta ptr
            @loop:
                bne @loop
                rts
            }
        }

        .proc main {
            jsr gfx::init
            lda ptr
            lda #<SCREEN
            rts
        }
        """;

    [Fact]
    public async Task AnnouncesWhatTheNameLayerCanDo()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);

        var capabilities = client.Initialized.Capabilities;
        Assert.True(capabilities.HoverProvider);
        Assert.True(capabilities.DefinitionProvider);
        Assert.True(capabilities.ReferencesProvider);
        Assert.True(capabilities.DocumentHighlightProvider);
        Assert.True(capabilities.RenameProvider?.PrepareProvider);
    }

    /// <summary>Hover says what a symbol is, what it is worth and how wide an address it is.</summary>
    [Fact]
    public async Task HoverDescribesALabelAndAConstant()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var label = await client.HoverAsync(Uri, new Position(2, 6), timeout);
        Assert.NotNull(label);
        Assert.Contains("```nt65\n.data ptr:    .word\n```", label.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("address  zp (1 byte) in ZEROPAGE", label.Contents.Value, StringComparison.Ordinal);

        var constant = await client.HoverAsync(Uri, new Position(5, 0), timeout);
        Assert.NotNull(constant);
        Assert.Contains("```nt65\nSCREEN = $0400\n```", constant.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("value    $0400 (1024)\naddress  abs (2 bytes)", constant.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one name the output does not spell as the source does is one ca65 would read as an
    /// instruction, which the emitter writes with its module in front. Hover says so there and
    /// nowhere else: every other name keeps its spelling, and saying so would say nothing.
    /// </summary>
    [Fact]
    public async Task HoverSaysWhatTheOutputCallsANameCa65WouldMisread()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(
            Uri, ".module main\n.cpu 6502\n.segment CODE\n.data lda: .byte 0\n.data plain: .byte 0\n");
        await client.NextDiagnosticsAsync(timeout);

        var misread = await client.HoverAsync(Uri, new Position(3, 6), timeout);
        var plain = await client.HoverAsync(Uri, new Position(4, 6), timeout);

        Assert.Contains("`main__lda`", misread?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("in the output", misread?.Contents.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("in the output", plain?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// nt65 writes a number in hexadecimal, which is what an address or a mask is read as. A
    /// number that is also a count is worth the decimal beside it, and below ten the two are
    /// the same digit, so there is nothing to put beside it.
    /// </summary>
    [Fact]
    public async Task HoverPutsTheDecimalBesideAHexadecimalValue()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, ".module main\nWIDE = $0400\nSMALL = 4\n");
        await client.NextDiagnosticsAsync(timeout);

        var wide = await client.HoverAsync(Uri, new Position(1, 0), timeout);
        var small = await client.HoverAsync(Uri, new Position(2, 0), timeout);

        Assert.Contains("value    $0400 (1024)", wide?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("value    4\n", small?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>A name inside a scope hovers under the path another file would write.</summary>
    [Fact]
    public async Task HoverQualifiesAScopedName()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var hover = await client.HoverAsync(Uri, new Position(8, 11), timeout);
        Assert.NotNull(hover);
        Assert.Contains("```nt65\n.proc gfx::init\n```", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Equal(new Range(new Position(8, 10), new Position(8, 14)), hover.Range);
    }

    /// <summary>
    /// What a routine costs and what it hands back are shown wherever its name is written:
    /// what a call costs is the question asked at the call, not at the declaration, and the
    /// lens that says it above the declaration is nowhere near the call.
    /// </summary>
    [Fact]
    public async Task HoverOnACallSaysWhatTheRoutineCostsAndKeeps()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var hover = await client.HoverAsync(Uri, new Position(18, 13), timeout);

        Assert.NotNull(hover);
        Assert.Contains("```nt65\n.proc gfx::init\n```", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("cost       13+ cycles, loops\npreserves  X, Y, C", hover.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Hover over a type and its members says what an editor needs of a layout: the offset a
    /// member sits at, how much room it takes, and the type it stands for.
    /// </summary>
    [Fact]
    public async Task HoverOnALayoutShowsOffsetsAndSizes()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, """
            .module main
            .struct Point {
            x:      .word
            y:      .word
            }

            .struct Player {
            pos:    .type Point
            hp:     .byte
            }

            .data here:   .type Player[4]
            """);
        await client.NextDiagnosticsAsync(timeout);

        var member = await client.HoverAsync(Uri, new Position(8, 0), timeout);
        Assert.NotNull(member);
        Assert.Contains("```nt65\nmember Player::hp\n```", member.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("offset  4\nsize    1 byte", member.Contents.Value, StringComparison.Ordinal);

        var nested = await client.HoverAsync(Uri, new Position(7, 0), timeout);
        Assert.NotNull(nested);
        Assert.Contains("type    Point\nsize    4 bytes", nested.Contents.Value, StringComparison.Ordinal);

        // How much room it takes and how many of them there are read as one fact.
        var array = await client.HoverAsync(Uri, new Position(11, 6), timeout);
        Assert.NotNull(array);
        Assert.Contains("```nt65\n.data here:   .type Player[4]\n```", array.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("size  20 bytes x 4", array.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>A cheap local has no path, so hover names the routine it is private to.</summary>
    [Fact]
    public async Task HoverOnACheapLocalSaysWhereItLives()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var hover = await client.HoverAsync(Uri, new Position(11, 4), timeout);
        Assert.NotNull(hover);
        Assert.Contains("```nt65\n@loop:\n```", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("private to  init", hover.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HoverOnSomethingThatIsNeitherANameNorAnInstructionSaysNothing()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        Assert.Null(await client.HoverAsync(Uri, new Position(14, 4), timeout));
    }

    /// <summary>
    /// Away from a name, an instruction is shown how long it takes and how long the block
    /// around it takes. The count is an interval wherever it depends on something the
    /// program does not say.
    /// </summary>
    [Fact]
    public async Task HoverOnAnInstructionShowsWhatItCosts()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var hover = await client.HoverAsync(Uri, new Position(9, 8), timeout);
        Assert.NotNull(hover);
        Assert.Contains("```nt65\nlda #0  ; load accumulator\n```", hover.Contents.Value, StringComparison.Ordinal);

        // `lda #0` and `sta z:ptr` come to five, and the block ends at the label after them.
        Assert.Contains("cycles  2         block 5", hover.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>A branch costs two not taken and three taken, and one more when a taken
    /// branch crosses a page, so what it costs is an interval.</summary>
    [Fact]
    public async Task HoverOnABranchShowsTheWholeInterval()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var hover = await client.HoverAsync(Uri, new Position(12, 8), timeout);
        Assert.NotNull(hover);
        Assert.Contains("cycles  2-4       block 2-4", hover.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// On the 65816 an instruction is also shown the processor state that reaches it, which
    /// is what its immediate was sized by.
    /// </summary>
    [Fact]
    public async Task HoverOnA65816InstructionShowsTheStateReachingIt()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, ".module main\n.cpu 65816\n.segment CODE\n.proc p: a16, i8 {\n    php\n    lda #$1234\n    plp\n    rts\n}\n");
        Assert.Empty((await client.NextDiagnosticsAsync(timeout)).Diagnostics);

        var hover = await client.HoverAsync(Uri, new Position(5, 5), timeout);
        Assert.NotNull(hover);
        Assert.Contains("cycles  3", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("state   a16, i8, native", hover.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// An instruction is read under the name its datasheet gives it, written where the
    /// language writes a comment. A reader who already knows what `pea` stands for is not the
    /// one asking.
    /// </summary>
    [Fact]
    public async Task HoverNamesTheInstructionOnTheHeadline()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var load = await client.HoverAsync(Uri, new Position(9, 8), timeout);
        Assert.Contains("```nt65\nlda #0  ; load accumulator\n```", load?.Contents.Value, StringComparison.Ordinal);

        await using var wide = await TestClient.StartAsync(timeout);
        await wide.OpenAsync(
            Uri, ".module main\n.cpu 65816\n.segment CODE\n.proc p: a8, i8 {\n    pea $1234\n    pld\n    rts\n}\n");
        await wide.NextDiagnosticsAsync(timeout);

        var push = await wide.HoverAsync(Uri, new Position(4, 4), timeout);
        Assert.Contains(
            "```nt65\npea $1234  ; push effective absolute address\n```",
            push?.Contents.Value,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Which flags an instruction writes is the fact people misremember, so it is beside the
    /// instruction rather than in a datasheet on the desk.
    /// </summary>
    [Fact]
    public async Task HoverListsTheFlagsAnInstructionWrites()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(
            Uri, ".module main\n.segment CODE\n.proc main {\n    lda #1\n    adc #2\n    sta $10\n    rts\n}\n");
        Assert.Empty((await client.NextDiagnosticsAsync(timeout)).Diagnostics);

        var load = await client.HoverAsync(Uri, new Position(3, 4), timeout);
        var add = await client.HoverAsync(Uri, new Position(4, 4), timeout);
        var store = await client.HoverAsync(Uri, new Position(5, 4), timeout);

        // The line's own facts stand apart from what the registers hold, and the grid is
        // fenced as a language of its own so that an editor can tell one row from another.
        Assert.Contains("```nt65-hover\ncycles  2         block 13\nflags   N Z\n\nA       as entered\n",
            load?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("flags   N V Z C\n", add?.Contents.Value, StringComparison.Ordinal);

        // A store writes no flag at all, and a row saying none would say nothing.
        Assert.DoesNotContain("flags", store?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// A count that is an interval says what its top would be paid for. The reason is always
    /// something the processor decides as it runs and the program does not say, and a reader
    /// left to work out which of them it is has been told half an answer.
    /// </summary>
    [Fact]
    public async Task HoverSaysWhyACountIsAnInterval()
    {
        var timeout = TestContext.Current.CancellationToken;
        const string Indexed = """
            .module main
            .segment CODE
            .data table: .byte[300]
            .proc main {
                ldx #4
            @loop:
                lda table,x
                dex
                bne @loop
                rts
            }
            .export table
            """;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, Indexed.ReplaceLineEndings("\n"));
        await client.NextDiagnosticsAsync(timeout);

        var read = await client.HoverAsync(Uri, new Position(6, 4), timeout);
        var branch = await client.HoverAsync(Uri, new Position(8, 4), timeout);

        Assert.Contains(
            "cycles  4-5       block 8-11    +1 when the read crosses a page\n",
            read?.Contents.Value,
            StringComparison.Ordinal);
        Assert.Contains(
            "cycles  2-4       block 8-11    +1 when taken, +1 when that crosses a page\n",
            branch?.Contents.Value,
            StringComparison.Ordinal);

        // On the 65816 a direct operand costs one more where the low byte of D is not zero,
        // and a routine that says nothing about D does not say whether it is.
        await using var wide = await TestClient.StartAsync(timeout);
        await wide.OpenAsync(Uri, ".module main\n.cpu 65816\n.segment CODE\n.proc p: a8, i8 {\n    lda $10\n    rts\n}\n");
        await wide.NextDiagnosticsAsync(timeout);

        var direct = await wide.HoverAsync(Uri, new Position(4, 4), timeout);

        Assert.Contains(
            "cycles  3-4       block 9-10    +1 when the low byte of D is not zero\n",
            direct?.Contents.Value,
            StringComparison.Ordinal);
    }

    /// <summary>An <c>.ensure</c> shows what the analysis found it has to write.</summary>
    [Fact]
    public async Task HoverOnAnEnsureShowsWhatItWrites()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, ".module main\n.cpu 65816\n.segment CODE\n.proc p: a8 -> a16, i8 {\n    .ensure a16, i8\n    rts\n}\n");
        Assert.Empty((await client.NextDiagnosticsAsync(timeout)).Diagnostics);

        var hover = await client.HoverAsync(Uri, new Position(4, 5), timeout);
        Assert.NotNull(hover);
        Assert.Contains("writes  rep #$20 and sep #$10", hover.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefinitionGoesToTheDeclaration()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        // `ptr` used on the `lda ptr` line, declared on line 2.
        var definition = await client.DefinitionAsync(Uri, new Position(19, 8), timeout);
        Assert.NotNull(definition);
        Assert.Equal(Uri, definition.Uri);
        Assert.Equal(new Range(new Position(2, 6), new Position(2, 9)), definition.Range);
    }

    /// <summary>Each part of a path finds its own declaration.</summary>
    [Fact]
    public async Task DefinitionFollowsEachPartOfAPath()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var scope = await client.DefinitionAsync(Uri, new Position(18, 8), timeout);
        Assert.Equal(new Range(new Position(7, 7), new Position(7, 10)), scope?.Range);

        var routine = await client.DefinitionAsync(Uri, new Position(18, 13), timeout);
        Assert.Equal(new Range(new Position(8, 10), new Position(8, 14)), routine?.Range);
    }

    [Fact]
    public async Task ReferencesFindEveryUse()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var all = await client.ReferencesAsync(Uri, new Position(2, 6), includeDeclaration: true, timeout);
        Assert.Equal([2, 10, 19], all.Select(location => location.Range.Start.Line));

        var uses = await client.ReferencesAsync(Uri, new Position(2, 6), includeDeclaration: false, timeout);
        Assert.Equal([10, 19], uses.Select(location => location.Range.Start.Line));
    }

    /// <summary>A cheap local is private to its proc, so its uses are the ones inside it.</summary>
    [Fact]
    public async Task ReferencesToACheapLocalStayInItsProc()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var all = await client.ReferencesAsync(Uri, new Position(11, 4), includeDeclaration: true, timeout);
        Assert.Equal([11, 12], all.Select(location => location.Range.Start.Line));
    }

    [Fact]
    public async Task HighlightsMarkTheDeclarationAsAWrite()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var highlights = await client.HighlightsAsync(Uri, new Position(19, 8), timeout);
        Assert.Equal(
            [DocumentHighlightKind.Write, DocumentHighlightKind.Read, DocumentHighlightKind.Read],
            highlights.Select(highlight => highlight.Kind));
    }

    [Fact]
    public async Task RenameRewritesEveryOccurrence()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        // What a rename replaces is the name under the caret, which is what the client shows
        // the programmer to edit.
        Assert.Equal(new Range(new Position(19, 8), new Position(19, 11)),
            await client.PrepareRenameAsync(Uri, new Position(19, 8), timeout));

        var edit = await client.RenameAsync(Uri, new Position(19, 8), "pointer", timeout);
        Assert.NotNull(edit);
        var edits = edit.Changes[Uri];
        Assert.Equal([2, 10, 19], edits.Select(e => e.Range.Start.Line));
        Assert.All(edits, e => Assert.Equal("pointer", e.NewText));
    }

    /// <summary>Renaming a cheap local touches its proc and keeps it a cheap local.</summary>
    [Fact]
    public async Task ACheapLocalIsRenamedWithItsAt()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var edit = await client.RenameAsync(Uri, new Position(11, 4), "@again", timeout);
        Assert.NotNull(edit);
        Assert.Equal([11, 12], edit.Changes[Uri].Select(e => e.Range.Start.Line));

        var refused = await Assert.ThrowsAsync<RemoteInvocationException>(() =>
            client.RenameAsync(Uri, new Position(11, 4), "again", timeout));
        Assert.Contains("must start with `@`", refused.Message);
    }

    [Theory]
    [InlineData("x", "is a register name")]
    [InlineData("2fast", "is not a name")]
    [InlineData("SCREEN", "is already declared in this scope")]
    public async Task ARenameThatWouldNotCompileIsRefused(string newName, string reason)
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var refused = await Assert.ThrowsAsync<RemoteInvocationException>(() =>
            client.RenameAsync(Uri, new Position(2, 6), newName, timeout));
        Assert.Contains(reason, refused.Message);
    }

    /// <summary>Names are resolved as the document is edited, not as it was opened.</summary>
    [Fact]
    public async Task AnEditChangesWhatANameMeans()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, ".module main\nCOUNT = 1\n.segment CODE\n.proc main {\n    lda #COUNT\n    rts\n}\n");
        Assert.Empty((await client.NextDiagnosticsAsync(timeout)).Diagnostics);

        // Rename the declaration alone, and the use no longer resolves.
        await client.ChangeAsync(Uri, 2, new TextDocumentContentChangeEvent(
            new Range(new Position(1, 0), new Position(1, 5)), "TOTAL"));

        var published = await client.NextDiagnosticsAsync(timeout);
        var diagnostic = Assert.Single(published.Diagnostics);
        Assert.Equal("`COUNT` is not declared", diagnostic.Message);
        Assert.Equal(4, diagnostic.Range.Start.Line);

        var hover = await client.HoverAsync(Uri, new Position(1, 0), timeout);
        Assert.Contains("TOTAL = 1", hover?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// A branch the build leaves out is not a problem, so it is published as a hint the
    /// client renders faded rather than as anything that belongs in a problem list.
    /// </summary>
    [Fact]
    public async Task ABranchTheBuildLeavesOutIsPublishedAsFaded()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, ".module main\n.if 0 {\nBROKEN = nowhere\n}\nON = 1\n.export ON\n");

        var dimmed = Assert.Single((await client.NextDiagnosticsAsync(timeout)).Diagnostics);
        Assert.Equal(DiagnosticSeverity.Hint, dimmed.Severity);
        Assert.Equal([DiagnosticTag.Unnecessary], dimmed.Tags);
        // The `.if` through its closing brace, and not the line after it.
        Assert.Equal(1, dimmed.Range.Start.Line);
        Assert.Equal(3, dimmed.Range.End.Line);
    }

    /// <summary>A duplicate declaration points at the one that got there first.</summary>
    [Fact]
    public async Task ADuplicateCarriesRelatedInformation()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, ".module main\nSIZE = 1\nSIZE = 2\n");

        var diagnostic = Assert.Single((await client.NextDiagnosticsAsync(timeout)).Diagnostics);
        Assert.Equal("`SIZE` is already declared in this scope", diagnostic.Message);
        var related = Assert.Single(diagnostic.RelatedInformation!);
        Assert.Equal(1, related.Location.Range.Start.Line);
    }

    /// <summary>A request about a document the client never opened is answered, not refused.</summary>
    [Fact]
    public async Task ADocumentThatIsNotOpenAnswersEmpty()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);

        Assert.Null(await client.HoverAsync(Uri, new Position(1, 0), timeout));
        Assert.Null(await client.DefinitionAsync(Uri, new Position(1, 0), timeout));
        Assert.Empty(await client.ReferencesAsync(Uri, new Position(1, 0), true, timeout));
        Assert.Empty(await client.HighlightsAsync(Uri, new Position(1, 0), timeout));
        Assert.Null(await client.PrepareRenameAsync(Uri, new Position(1, 0), timeout));
        Assert.Null(await client.RenameAsync(Uri, new Position(1, 0), "x", timeout));
    }

    private static async Task<TestClient> OpenAsync(CancellationToken cancellation)
    {
        var client = await TestClient.StartAsync(cancellation);
        await client.OpenAsync(Uri, Source);
        Assert.Empty((await client.NextDiagnosticsAsync(cancellation)).Diagnostics);
        return client;
    }
}
