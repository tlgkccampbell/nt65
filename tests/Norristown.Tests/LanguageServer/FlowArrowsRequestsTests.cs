using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests the <c>nt65/flowArrows</c> request, which lists the transfers of control inside the
/// routine at the caret with the column each arrow is drawn in.
/// </summary>
public sealed class FlowArrowsRequestsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    /// <summary>
    /// An arrow inside another goes nearer the code, and an arrow that overlaps no other shares the
    /// innermost column. The answer spans the routine from its opening line to its closing line.
    /// </summary>
    [Fact]
    public async Task ShorterArrowsGoNearerTheCode()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .segment CODE
            .export .proc main {
                ldx #8
            @outer:
                ldy #4
            @inner:
                d|ey
                bne @inner
                dex
                bne @outer
                bcc @done
                nop
            @done:
                rts
            }
            """);
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        var result = await FlowArrowsAsync(client, position, timeout);
        Assert.NotNull(result);
        Assert.Equal((2, 15, 2), (result.First, result.Last, result.Columns));
        Assert.Equal(
            [(8, 6, 0), (10, 4, 1), (11, 13, 0)],
            result.Arrows.Select(arrow => (arrow.From, arrow.To, arrow.Column)));
    }

    /// <summary>
    /// The arrows take at most four columns, and one that finds no free column has none.
    /// </summary>
    [Fact]
    public async Task ArrowsBeyondTheLastColumnHaveNone()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .segment CODE
            .export .proc main {
            @a:
            @b:
            @c:
            @d:
            @e:
                n|op
                bcc @e
                bne @d
                bvc @c
                bmi @b
                lda $10
                bne @a
                rts
            }
            """);
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        var result = await FlowArrowsAsync(client, position, timeout);
        Assert.NotNull(result);
        Assert.Equal(4, result.Columns);
        Assert.Equal(
            [(9, 7, 0), (10, 6, 1), (11, 5, 2), (12, 4, 3), (14, 3, null)],
            result.Arrows.Select(arrow => (arrow.From, arrow.To, arrow.Column)));
    }

    private static Task<FlowArrowsResult?> FlowArrowsAsync(TestClient client, Position position, CancellationToken timeout) =>
        client.RequestAsync<FlowArrowsResult?>("nt65/flowArrows",
            new TextDocumentPositionParams(new TextDocumentIdentifier(Uri), position), timeout);
}
