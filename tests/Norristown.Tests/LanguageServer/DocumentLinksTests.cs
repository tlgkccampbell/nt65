using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// The paths a file writes that name another file: what an <c>.incbin</c> includes, resolved
/// beside the file that writes it, as the build resolves it.
/// </summary>
public sealed class DocumentLinksTests
{
    private const string Uri = "file:///c:/work/src/main.nt65";

    /// <summary>Every <c>.incbin</c> path is a link to the file it names.</summary>
    [Fact]
    public async Task EachIncbinPathLinksToTheFileBesideIt()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync("""
            .module main
            .segment RODATA
            .data tiles:  .incbin "art/tiles.bin"
            .data part:   .incbin "../shared/font.chr", 0, 8
            .data none:   .byte 1
            .export tiles, part, none
            """, timeout);

        var links = await LinksAsync(client, timeout);

        Assert.Equal(
            ["file:///c:/work/src/art/tiles.bin", "file:///c:/work/shared/font.chr"],
            links.Select(link => link.Target));

        // The link is the written path, quotes and all, so a click lands on it.
        Assert.Equal(new Position(2, 22), links[0].Range.Start);
        Assert.Equal(new Position(2, 37), links[0].Range.End);
    }

    /// <summary>A file with nothing to include has no links, and neither has a path a constant names.</summary>
    [Fact]
    public async Task ANameIsNotALink()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync("""
            .module main
            ART = "art/tiles.bin"
            .segment RODATA
            .data tiles: .incbin ART
            .export tiles
            """, timeout);

        Assert.Empty(await LinksAsync(client, timeout));
    }

    private static Task<IReadOnlyList<DocumentLink>> LinksAsync(TestClient client, CancellationToken timeout) =>
        client.RequestAsync<IReadOnlyList<DocumentLink>>("textDocument/documentLink",
            new { textDocument = new { uri = Uri } }, timeout);

    private static async Task<TestClient> OpenAsync(string text, CancellationToken timeout)
    {
        var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, text.ReplaceLineEndings("\n"));
        await client.NextDiagnosticsAsync(Uri, timeout);
        return client;
    }
}
