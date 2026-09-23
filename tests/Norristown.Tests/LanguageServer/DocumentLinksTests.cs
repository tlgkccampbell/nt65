using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// The paths in a file that name another file, which are those an <c>.incbin</c> includes. Each
/// is resolved relative to the file it is written in, as the build resolves it.
/// </summary>
public sealed class DocumentLinksTests
{
    private const string Uri = "file:///c:/work/src/main.nt65";

    /// <summary>Every <c>.incbin</c> path is a link to the file it names.</summary>
    [Fact]
    public async Task EachIncbinPathLinksToTheFileBesideIt()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment RODATA
            .data tiles:  .incbin "art/tiles.bin"
            .data part:   .incbin "../shared/font.chr", 0, 8
            .data none:   .byte 1
            .export tiles, part, none
            """;
        await using var client = await OpenAsync(Source, timeout);

        var links = await LinksAsync(client, timeout);

        Assert.Equal(
            ["file:///c:/work/src/art/tiles.bin", "file:///c:/work/shared/font.chr"],
            links.Select(link => link.Target));

        // The link's range is the written path, quotes included, so a click anywhere on it follows it.
        var path = Locate.Span(Source, "\"art/tiles.bin\"");
        Assert.Equal(path.Start, links[0].Range.Start);
        Assert.Equal(path.End, links[0].Range.End);
    }

    /// <summary>An <c>.incbin</c> whose path is given by a constant's name, not a string, has no link.</summary>
    [Fact]
    public async Task ANameIsNotALink()
    {
        var timeout = TestTimeout.Token();
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

    private static Task<TestClient> OpenAsync(string text, CancellationToken timeout) =>
        TestClient.OpenedAsync(timeout, (Uri, text.ReplaceLineEndings("\n")));
}
