using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests the comment above a declaration, which is shown on hover and beside a completion. The
/// language has no special doc-comment syntax. The <c>;</c> comment lines directly above a
/// declaration, each on a line of its own, are taken as its documentation.
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

        .const ROWS = 25       ; how many rows there are
        .proc scroll {
            jsr clear
            rts
        }

        .export clear, scroll, ROWS
        """;

    /// <summary>The comment above a declaration is shown wherever the name appears.</summary>
    [Fact]
    public async Task HoverShowsTheCommentAboveTheDeclaration()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(Source, timeout);

        // On `clear` where it is declared, and on the `jsr clear` that calls it.
        var declaration = await client.HoverAsync(Uri, Locate.At(Source, ".proc |clear"), timeout);
        var use = await client.HoverAsync(Uri, Locate.At(Source, "jsr |clear"), timeout);

        Assert.NotNull(declaration);
        Assert.Contains("Clears the screen.\nThe border is left alone.", declaration.Contents.Value);
        Assert.NotNull(use);
        Assert.Contains("Clears the screen.\nThe border is left alone.", use.Contents.Value);
    }

    /// <summary>A blank line ends the comment, and a comment that shares a line with code documents nothing.</summary>
    [Fact]
    public async Task ABlankLineOrALineOfCodeEndsTheComment()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(Source, timeout);

        var hover = await client.HoverAsync(Uri, Locate.At(Source, ".proc |scroll"), timeout);

        Assert.NotNull(hover);
        Assert.DoesNotContain("Not this one's", hover.Contents.Value);
        Assert.DoesNotContain("how many rows", hover.Contents.Value);
    }

    /// <summary>
    /// Every instance of a family is declared on the family's line, so each shows the family's
    /// comment.
    /// </summary>
    [Fact]
    public async Task AFamilysInstancesShowTheFamilysComment()
    {
        var timeout = TestTimeout.Token();
        const string Text = """
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
            """;
        await using var client = await OpenAsync(Text, timeout);

        var hover = await client.HoverAsync(Uri, Locate.At(Text, "jsr play::|pulse2"), timeout);

        Assert.NotNull(hover);
        Assert.Contains("routine play::pulse2", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("Starts the channel it is named after.", hover.Contents.Value);
    }

    /// <summary>
    /// A completion item carries the comment too, but only when the item the caret is on is
    /// resolved. Each name can carry a paragraph, and sending them all with a list of hundreds
    /// would be mostly prose nobody is reading.
    /// </summary>
    [Fact]
    public async Task ACompletionIncludesTheComment()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(Source, timeout);

        // A fresh line inside `scroll`, where a name may go.
        var at = Locate.At(Source, "    jsr clear");
        var edited = Source.Replace("    jsr clear", "    lda cl\n    jsr clear", StringComparison.Ordinal);
        await client.ChangeAsync(Uri, 2, new TextDocumentContentChangeEvent(new Range(at, at), "    lda cl\n"));
        var items = await client.RequestAsync<IReadOnlyList<CompletionItem>>("textDocument/completion",
            new { textDocument = new { uri = Uri }, position = Locate.At(edited, "lda cl|") }, timeout);

        // Nothing in the list carries prose; the one the caret lands on is resolved.
        Assert.All(items, item => Assert.Null(item.Documentation));

        var clear = await client.ResolveAsync(Assert.Single(items, item => item.Label == "clear"), timeout);
        Assert.NotNull(clear.Documentation);
        Assert.Equal("markdown", clear.Documentation.Kind);
        Assert.Equal("Clears the screen.\nThe border is left alone.", clear.Documentation.Value);

        // A name whose declaration has no comment above it resolves with no documentation.
        var rows = Assert.Single(items, item => item.Label == "ROWS");
        Assert.Null((await client.ResolveAsync(rows, timeout)).Documentation);
    }

    private static Task<TestClient> OpenAsync(string text, CancellationToken timeout) =>
        TestClient.OpenedAsync(timeout, (Uri, text.ReplaceLineEndings("\n")));
}
