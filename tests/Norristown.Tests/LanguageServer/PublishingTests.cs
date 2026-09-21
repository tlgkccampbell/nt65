using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// What a keystroke publishes, and when. The promise to the person typing is that a squiggle
/// never flickers and is never about text that is gone, and each of the rules that keeps it is
/// checked here. The wait between an edit and what the rest of the program has to say is held
/// by the test, so none of this costs real time.
/// </summary>
public sealed class PublishingTests
{
    private const string GfxUri = "file:///c:/work/gfx.nt65";

    private const string MainUri = "file:///c:/work/main.nt65";

    /// <summary>Exports what main uses, and keeps one name of its own that nothing uses.</summary>
    private const string Gfx = """
        .module gfx
        .export clear, SCREEN

        SCREEN = $0400
        rows   = 25
        .segment CODE
        .proc clear {
            rts
        }
        """;

    private const string Main = """
        .module main
        .use gfx::{clear, SCREEN}
        .export main
        .segment CODE
        .proc main {
            jsr clear
            lda #<SCREEN
            rts
        }
        """;

    /// <summary>
    /// The file the caret is in hears at once; the rest of the program hears once the typing
    /// has stopped. The edit here is in gfx and what it breaks is in main, which is the case
    /// the two timings exist for.
    /// </summary>
    [Fact]
    public async Task TheEditedFileHearsAtOnceAndTheRestOnceTheTypingStops()
    {
        var timeout = TestContext.Current.CancellationToken;
        var held = new HeldDelay();
        await using var client = await OpenBothAsync(held, timeout);

        // `clear` comes off the export line, which is news to main and to nothing else.
        await client.ChangeAsync(GfxUri, 2, new TextDocumentContentChangeEvent(
            new Range(new Position(1, 8), new Position(1, 15)), ""));

        var edited = await client.NextDiagnosticsAsync(timeout);
        Assert.Equal(GfxUri, edited.Uri);
        Assert.Equal(2, edited.Version);
        Assert.True(client.Quiet, "the rest of the program heard before the typing had stopped");

        await held.ReleaseAsync(timeout);
        var rest = await client.NextDiagnosticsAsync(timeout);
        Assert.Equal(MainUri, rest.Uri);
        Assert.Contains(rest.Diagnostics, d => d.Message.Contains("clear", StringComparison.Ordinal));
    }

    /// <summary>
    /// A run of keystrokes: what is published about a file never goes back to an older revision
    /// of it, and a file that is still part of the program is never emptied and then filled in
    /// again, which is what a flickering squiggle is.
    /// </summary>
    [Fact]
    public async Task ARunOfKeystrokesNeverGoesBackAndNeverEmptiesAFileThatIsStillThere()
    {
        var timeout = TestContext.Current.CancellationToken;
        var held = new HeldDelay();
        await using var client = await OpenBothAsync(held, timeout);

        // `jsr clear` loses and regains its last letter, ten times over: every other keystroke
        // is a name nothing declares.
        var version = 1;
        var published = new List<PublishDiagnosticsParams>();
        for (var i = 0; i < 10; i++)
        {
            var (range, written) = i % 2 == 0
                ? (new Range(new Position(5, 12), new Position(5, 13)), "")
                : (new Range(new Position(5, 12), new Position(5, 12)), "r");
            await client.ChangeAsync(MainUri, ++version, new TextDocumentContentChangeEvent(range, written));
            published.Add(await client.NextDiagnosticsAsync(MainUri, timeout));
            while (held.Waiting > 0)
                await held.ReleaseAsync(timeout);
        }

        Assert.Equal(version, published[^1].Version);
        Assert.Equal([.. published.Select(one => one.Version).Order()], published.Select(one => one.Version));

        // gfx is part of the program throughout, and nothing about it changed, so it is never
        // told that it has no problems and then told again that it has one.
        var elsewhere = client.Pending().Where(one => one.Uri == GfxUri).ToList();
        Assert.DoesNotContain(elsewhere, one => one.Diagnostics.Count == 0);
    }

    /// <summary>
    /// The edited document is never asked to fetch its colours again — the client asks about
    /// the document it is showing by itself — and the other files are asked only when the edit
    /// reached past the file it was made in, which is when what their names refer to can have
    /// moved.
    /// </summary>
    [Fact]
    public async Task OnlyAnEditThatReachesPastItsOwnFileAsksForAnythingToBeFetchedAgain()
    {
        var timeout = TestContext.Current.CancellationToken;
        var held = new HeldDelay();
        await using var client = await OpenBothAsync(held, timeout, refreshesTokens: true);

        // A keystroke in a routine body: nothing outside main can have moved.
        await client.ChangeAsync(MainUri, 2, new TextDocumentContentChangeEvent(
            new Range(new Position(7, 4), new Position(7, 7)), "nop"));
        await client.NextDiagnosticsAsync(MainUri, timeout);
        await held.ReleaseAsync(timeout);

        // A constant main reads, changed in gfx: now it has.
        await client.ChangeAsync(GfxUri, 2, new TextDocumentContentChangeEvent(
            new Range(new Position(3, 9), new Position(3, 14)), "$0800"));
        await client.NextDiagnosticsAsync(GfxUri, timeout);
        await held.ReleaseAsync(timeout);

        await client.NextTokensRefreshAsync(timeout);
        Assert.False(client.AskedForTokensRefresh, "a keystroke in a routine body asked for a fetch of its own");
    }

    /// <summary>Opens both files and lets everything the opening published arrive.</summary>
    private static async Task<TestClient> OpenBothAsync(
        HeldDelay held, CancellationToken cancellation, bool refreshesTokens = false)
    {
        var client = await TestClient.StartAsync(
            TestClient.Capable(refreshesTokens), cancellation, delay: held.Wait);
        foreach (var (uri, text) in new[] { (GfxUri, Gfx), (MainUri, Main) })
        {
            await client.OpenAsync(uri, text);
            Assert.Equal(uri, (await client.NextDiagnosticsAsync(cancellation)).Uri);
            await held.ReleaseAsync(cancellation);

            // Opening a file reads the whole program, so everything about it may have moved
            // and the client is asked to fetch it again. That is not what any of these tests
            // is about, so it is taken off here.
            if (refreshesTokens)
                await client.NextTokensRefreshAsync(cancellation);
        }
        return client;
    }
}
