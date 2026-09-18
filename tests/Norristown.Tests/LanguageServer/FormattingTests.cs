using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// The editor's <c>Format Document</c> and <c>Format Selection</c>: the same layout the command
/// line writes, as edits the client applies, and no analysis behind it.
/// </summary>
public sealed class FormattingTests
{
    private const string Uri = "file:///c:/work/src/main.nt65";

    private const string Crooked = """
        .module main
        .segment BSS
        .data one: .byte
        .data longer: .word
        .segment CODE
        .proc main {
        ldx #1
        @loop:
        dex
        bne @loop
        rts
        }
        """;

    /// <summary>The whole file comes back as one edit per line that moves, and no other.</summary>
    [Fact]
    public async Task FormattingAFileMovesEveryLineThatIsInTheWrongPlace()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(Crooked, timeout);

        var edits = await client.RequestAsync<IReadOnlyList<TextEdit>>("textDocument/formatting",
            new { textDocument = new { uri = Uri }, options = new { tabSize = 8, insertSpaces = false } }, timeout);

        Assert.Equal(
            [
                (2, ".data one:    .byte"),
                (6, "    ldx #1"),
                (8, "    dex"),
                (9, "    bne @loop"),
                (10, "    rts"),
            ],
            edits.Select(edit => (edit.Range.Start.Line, edit.NewText)));

        // Each edit replaces the line it is for, up to but not including its break, so what the
        // client applies never touches the line endings of the file.
        Assert.All(edits, edit => Assert.Equal(edit.Range.Start.Line, edit.Range.End.Line));
        Assert.All(edits, edit => Assert.Equal(0, edit.Range.Start.Character));

        // What the client would lay out with is not what nt65 lays out with: there is one layout.
        Assert.DoesNotContain(edits, edit => edit.NewText.Contains('\t'));
    }

    /// <summary>
    /// Formatting a selection moves the lines in it and leaves the rest where they are, and the
    /// column a run of data lines sits on is still the whole run's.
    /// </summary>
    [Fact]
    public async Task FormattingASelectionMovesOnlyWhatIsSelected()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(Crooked, timeout);

        var edits = await client.RequestAsync<IReadOnlyList<TextEdit>>("textDocument/rangeFormatting",
            new
            {
                textDocument = new { uri = Uri },
                range = new { start = new { line = 2, character = 0 }, end = new { line = 6, character = 6 } },
            },
            timeout);

        Assert.Equal([(2, ".data one:    .byte"), (6, "    ldx #1")],
            edits.Select(edit => (edit.Range.Start.Line, edit.NewText)));
    }

    /// <summary>
    /// A file with a mistake in it formats all the same: what a line is written at is what its
    /// own braces say, and nothing here waits on an analysis.
    /// </summary>
    [Fact]
    public async Task AFileThatDoesNotCompileStillFormats()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync("""
            .module main
            .segment CODE
            .proc main {
            lda nowhere
            rts
            }
            """, timeout);

        var edits = await client.RequestAsync<IReadOnlyList<TextEdit>>("textDocument/formatting",
            new { textDocument = new { uri = Uri } }, timeout);

        Assert.Equal(["    lda nowhere", "    rts"], edits.Select(edit => edit.NewText));
    }

    /// <summary>A file the server does not have is no edits, rather than an error.</summary>
    [Fact]
    public async Task AFileTheServerDoesNotHaveIsNoEdits()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);

        Assert.Empty(await client.RequestAsync<IReadOnlyList<TextEdit>>("textDocument/formatting",
            new { textDocument = new { uri = "file:///c:/work/src/nothing.nt65" } }, timeout));
    }

    private static async Task<TestClient> OpenAsync(string text, CancellationToken timeout)
    {
        var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, text.ReplaceLineEndings("\n"));
        await client.NextDiagnosticsAsync(Uri, timeout);
        return client;
    }
}
