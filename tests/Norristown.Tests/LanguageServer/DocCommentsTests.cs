using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// The comment above a declaration, shown on hover and beside a completion. There is no
/// doc-comment syntax of its own: the <c>;</c> lines directly above it, each on a line of
/// its own, are what the author had to say about it.
/// </summary>
public sealed class DocCommentsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    private const string Source = """
        .module main
        .segment CODE

        ; Clears the screen.
        ; The border is left alone.
        .proc clear {
            rts
        }

        ; Not this one's.

        ROWS = 25       ; how many rows there are
        .proc scroll {
            jsr clear
            rts
        }

        .export clear, scroll, ROWS
        """;

    /// <summary>The comment above a declaration is shown wherever the name is written.</summary>
    [Fact]
    public async Task HoverShowsTheCommentAboveTheDeclaration()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(Source, timeout);

        // On `clear` where it is declared, and on the `jsr clear` that calls it.
        var declaration = await client.HoverAsync(Uri, new Position(5, 6), timeout);
        var use = await client.HoverAsync(Uri, new Position(13, 8), timeout);

        Assert.NotNull(declaration);
        Assert.Contains("Clears the screen.\nThe border is left alone.", declaration.Contents.Value);
        Assert.NotNull(use);
        Assert.Contains("Clears the screen.\nThe border is left alone.", use.Contents.Value);
    }

    /// <summary>A blank line between ends the comment, and a comment on a line of code is nobody's.</summary>
    [Fact]
    public async Task ABlankLineOrALineOfCodeEndsTheComment()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(Source, timeout);

        var hover = await client.HoverAsync(Uri, new Position(12, 6), timeout);

        Assert.NotNull(hover);
        Assert.DoesNotContain("Not this one's", hover.Contents.Value);
        Assert.DoesNotContain("how many rows", hover.Contents.Value);
    }

    /// <summary>Every instance of a family is declared on the family's line, so each shows its comment.</summary>
    [Fact]
    public async Task AFamilysInstancesShowTheFamilysComment()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync("""
            .module main
            .enum Channel {
                pulse1
                pulse2
            }

            .segment CODE
            .scope play {
                ; Starts the channel it is named after.
                .multiproc Channel, ch {
                    rts
                }
            }

            .proc main {
                jsr play::pulse2
                rts
            }

            .export main
            """, timeout);

        var hover = await client.HoverAsync(Uri, new Position(15, 14), timeout);

        Assert.NotNull(hover);
        Assert.Contains("routine play::pulse2", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("Starts the channel it is named after.", hover.Contents.Value);
    }

    /// <summary>
    /// A completion says what each name is for too, fetched for the one item the caret is on:
    /// a file's names carry a paragraph each, and a list of hundreds would be mostly prose
    /// nobody is reading.
    /// </summary>
    [Fact]
    public async Task ACompletionCarriesTheComment()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(Source, timeout);

        // A fresh line inside `scroll`, where a name may be written.
        await client.ChangeAsync(Uri, 2, new TextDocumentContentChangeEvent(
            new Range(new Position(13, 0), new Position(13, 0)), "    lda cl\n"));
        var items = await client.RequestAsync<IReadOnlyList<CompletionItem>>("textDocument/completion",
            new { textDocument = new { uri = Uri }, position = new Position(13, 10) }, timeout);

        // Nothing in the list carries prose; the one the caret lands on is resolved.
        Assert.All(items, item => Assert.Null(item.Documentation));

        var clear = await client.ResolveAsync(Assert.Single(items, item => item.Label == "clear"), timeout);
        Assert.NotNull(clear.Documentation);
        Assert.Equal("markdown", clear.Documentation.Kind);
        Assert.Equal("Clears the screen.\nThe border is left alone.", clear.Documentation.Value);

        // A name whose declaration has no comment above it resolves to itself.
        var rows = Assert.Single(items, item => item.Label == "ROWS");
        Assert.Null((await client.ResolveAsync(rows, timeout)).Documentation);
    }

    private static async Task<TestClient> OpenAsync(string text, CancellationToken timeout)
    {
        var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, text.ReplaceLineEndings("\n"));
        await client.NextDiagnosticsAsync(Uri, timeout);
        return client;
    }
}
