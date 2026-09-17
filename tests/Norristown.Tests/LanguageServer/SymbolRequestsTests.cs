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
        Assert.Contains("**data declaration** `ptr`", label.Contents.Value);
        Assert.Contains("address size: `zp` (1 byte)", label.Contents.Value);
        Assert.Contains("segment: `ZEROPAGE`", label.Contents.Value);

        var constant = await client.HoverAsync(Uri, new Position(5, 0), timeout);
        Assert.NotNull(constant);
        Assert.Contains("**constant** `SCREEN`", constant.Contents.Value);
        Assert.Contains("value: `$0400`", constant.Contents.Value);
        Assert.Contains("address size: `abs` (2 bytes)", constant.Contents.Value);
    }

    /// <summary>A name inside a scope hovers under the path another file would write.</summary>
    [Fact]
    public async Task HoverQualifiesAScopedName()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var hover = await client.HoverAsync(Uri, new Position(8, 11), timeout);
        Assert.NotNull(hover);
        Assert.Contains("**routine** `gfx::init`", hover.Contents.Value);
        Assert.Equal(new Range(new Position(8, 10), new Position(8, 14)), hover.Range);
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
        Assert.Contains("**member** `Player::hp`", member.Contents.Value);
        Assert.Contains("offset: `4`", member.Contents.Value);
        Assert.Contains("size: `1` byte", member.Contents.Value);

        var nested = await client.HoverAsync(Uri, new Position(7, 0), timeout);
        Assert.NotNull(nested);
        Assert.Contains("type: `Point`", nested.Contents.Value);
        Assert.Contains("size: `4` bytes", nested.Contents.Value);

        var array = await client.HoverAsync(Uri, new Position(11, 6), timeout);
        Assert.NotNull(array);
        Assert.Contains("**data declaration** `here`", array.Contents.Value);
        Assert.Contains("size: `20` bytes", array.Contents.Value);
        Assert.Contains("count: `4`", array.Contents.Value);
    }

    /// <summary>A cheap local has no path, so hover names the routine it is private to.</summary>
    [Fact]
    public async Task HoverOnACheapLocalSaysWhereItLives()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var hover = await client.HoverAsync(Uri, new Position(11, 4), timeout);
        Assert.NotNull(hover);
        Assert.Contains("**label** `@loop`", hover.Contents.Value);
        Assert.Contains("private to: `init`", hover.Contents.Value);
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
        Assert.Contains("**2 cycles**", hover.Contents.Value, StringComparison.Ordinal);

        // `lda #0` and `sta z:ptr` come to five, and the block ends at the label after them.
        Assert.Contains("this block: 5 cycles", hover.Contents.Value, StringComparison.Ordinal);
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
        Assert.Contains("**2-4 cycles**", hover.Contents.Value, StringComparison.Ordinal);
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
        Assert.Contains("**3 cycles**", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("state here: `a16, i8, native`, 1 pushed", hover.Contents.Value, StringComparison.Ordinal);
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
        Assert.Contains("writes `rep #$20`", hover.Contents.Value, StringComparison.Ordinal);
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
    [InlineData("lda", "is a reserved word")]
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
        Assert.Contains("`TOTAL`", hover?.Contents.Value);
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
