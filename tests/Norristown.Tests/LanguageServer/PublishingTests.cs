using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// What a keystroke publishes, and when. The promise to the person typing is that a squiggle
/// never flickers and never refers to text that has gone, and each rule that keeps that promise
/// is checked here. The delay between an edit and the diagnostics for the rest of the program is
/// controlled by the test, so none of this takes real time.
/// </summary>
public sealed class PublishingTests
{
    private const string GfxUri = "file:///c:/work/gfx.nt65";

    private const string MainUri = "file:///c:/work/main.nt65";

    /// <summary>A module that exports what main uses and keeps one name of its own that nothing uses.</summary>
    private const string Gfx = """
        .module gfx
        .export clear, SCREEN

        .const SCREEN = $0400
        .const rows   = 25
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
    /// Diagnostics for the edited file are published at once, and those for the rest of the
    /// program are published once typing has stopped. The edit here is in gfx and what it breaks
    /// is in main, which is the case the two timings exist for.
    /// </summary>
    [Fact]
    public async Task TheEditedFileHearsAtOnceAndTheRestOnceTheTypingStops()
    {
        var timeout = TestTimeout.Token();
        var held = new HeldDelay();
        await using var client = await OpenBothAsync(held, timeout);

        // `clear` is removed from the export line, which changes main's diagnostics and no others.
        await client.ChangeAsync(GfxUri, 2, new TextDocumentContentChangeEvent(
            Locate.Span(Gfx, ".export |clear, "), ""));

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
    /// During a run of keystrokes, what is published about a file never goes back to an older
    /// revision of it. A file that is still part of the program never has its diagnostics cleared
    /// and then published again, which is what makes a squiggle flicker.
    /// </summary>
    [Fact]
    public async Task ARunOfKeystrokesNeverGoesBackAndNeverEmptiesAFileThatIsStillThere()
    {
        var timeout = TestTimeout.Token();
        var held = new HeldDelay();
        await using var client = await OpenBothAsync(held, timeout);

        // `jsr clear` loses and regains its last letter, ten times over, so every other keystroke
        // leaves a name that nothing declares.
        var version = 1;
        var published = new List<PublishDiagnosticsParams>();
        var letter = Locate.Span(Main, "jsr clea|r");
        for (var i = 0; i < 10; i++)
        {
            var (range, typed) = i % 2 == 0
                ? (letter, "")
                : (new Range(letter.Start, letter.Start), "r");
            await client.ChangeAsync(MainUri, ++version, new TextDocumentContentChangeEvent(range, typed));
            published.Add(await client.NextDiagnosticsAsync(MainUri, timeout));
            while (held.Waiting > 0)
                await held.ReleaseAsync(timeout);
        }

        Assert.Equal(version, published[^1].Version);
        Assert.Equal([.. published.Select(one => one.Version).Order()], published.Select(one => one.Version));

        // gfx is part of the program throughout and nothing about it changes, so it is never
        // published with no diagnostics and then with its diagnostic again.
        var elsewhere = client.Pending().Where(one => one.Uri == GfxUri).ToList();
        Assert.DoesNotContain(elsewhere, one => one.Diagnostics.Count == 0);
    }

    /// <summary>
    /// The client is never asked to refetch semantic tokens for the edited document alone,
    /// because it refetches the document it is showing by itself. It is asked to refetch the
    /// others only when the edit reaches past its own file, since only then can what their names
    /// refer to have changed.
    /// </summary>
    [Fact]
    public async Task OnlyAnEditThatReachesPastItsOwnFileAsksForAnythingToBeFetchedAgain()
    {
        var timeout = TestTimeout.Token();
        var held = new HeldDelay();
        await using var client = await OpenBothAsync(held, timeout, refreshesTokens: true);

        // A keystroke in a routine body cannot change anything outside main.
        await client.ChangeAsync(MainUri, 2, new TextDocumentContentChangeEvent(
            Locate.Span(Main, "rts"), "nop"));
        await client.NextDiagnosticsAsync(MainUri, timeout);
        await held.ReleaseAsync(timeout);

        // A constant that main reads is changed in gfx, so something outside gfx has now changed.
        await client.ChangeAsync(GfxUri, 2, new TextDocumentContentChangeEvent(
            Locate.Span(Gfx, "$0400"), "$0800"));
        await client.NextDiagnosticsAsync(GfxUri, timeout);
        await held.ReleaseAsync(timeout);

        await client.NextTokensRefreshAsync(timeout);
        Assert.False(client.AskedForTokensRefresh, "a keystroke in a routine body asked for a fetch of its own");
    }

    /// <summary>
    /// A diagnostic that moves its end, changes its code or gains a tag is published again, even
    /// where its start and message stay the same, because the client shows each of those.
    /// </summary>
    [Fact]
    public void EveryFieldTheClientShowsTellsTwoPublishesApart()
    {
        var one = new Norristown.LanguageServer.Protocol.Diagnostic(
            new Range(new Position(1, 0), new Position(1, 4)), DiagnosticSeverity.Hint, "unused", "nt65",
            "`rows` is never used", null);
        string[] signatures =
        [
            Server.Signature([one]),
            Server.Signature([one with { Range = one.Range with { End = new Position(1, 8) } }]),
            Server.Signature([one with { Code = "unreachable" }]),
            Server.Signature([one with { Tags = [DiagnosticTag.Unnecessary] }]),
        ];

        Assert.Equal(signatures.Length, signatures.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Opens both files and waits for everything that opening them publishes.</summary>
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

            // Opening a file analyzes the whole program, so any token may have changed and the
            // client is asked to refetch them. None of these tests is about that, so that request
            // is consumed here.
            if (refreshesTokens)
                await client.NextTokensRefreshAsync(cancellation);
        }
        return client;
    }
}
