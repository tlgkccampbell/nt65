using System.Text.Json;
using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// The few words drawn in a line that the line does not say. There are five kinds, each with a
/// switch of its own, and what is not hinted matters as much as what is: a note on every line
/// is a dashboard rather than a listing.
/// </summary>
public sealed class InlayHintsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    /// <summary>
    /// A 65816 file with something of every kind in it: widths that change and widths that do
    /// not, an <c>.ensure</c> and a <c>.state</c> that say their own, values a declaration
    /// leaves out, a branch that cannot reach, and calls whose arguments are and are not worth
    /// naming the parameter of.
    /// </summary>
    private const string Source = """
        .module main
        .cpu 65816

        .export .enum Speed {
            slow = 1
            fast
        }

        .export .struct Point {
        x:  .word
        y:  .word
        }

        .export WIDE  = 8 * 4
        .export PLAIN = 7
        count = 3

        .macro fill(count: expr, with: expr) {
            ldx #count
            lda #with
        }

        .macro one(only: expr) {
            lda #only
        }

        .segment CODE

        .proc narrow: a16, keeps x, y -> a8 {
            sep #$20
            rts
        }

        .export .proc reset: emu, noreturn {
            sei
            clc
            xce
            rep #$30
            ldx #$1fff
            .ensure a16, i16
            nop
            jsr narrow
            nop
            fill!(16, 0)
            fill!(count, 0)
            fill!(count = 1, with = 2)
            one!(5)
            jeq done
            jmp done
            .res 200
        done:
            .state a8, i16
            stp
        }
        """;

    /// <summary>
    /// Every switch is read from where the editor's settings put it, and one the editor does
    /// not mention keeps its default: the counts off, everything else on.
    /// </summary>
    [Fact]
    public void EveryKindHasASwitchOfItsOwn()
    {
        Assert.Equal(new HintSettings(true, true, true, true, false), HintSettings.Default);
        Assert.Equal(HintSettings.Default, HintSettings.Of(null));
        Assert.Equal(HintSettings.Default, Settings("""{ "inlayHints": {} }"""));
        Assert.Equal(
            new HintSettings(false, false, false, false, true),
            Settings("""
                {
                  "inlayHints": {
                    "stateChanges": false,
                    "longBranches": false,
                    "impliedValues": false,
                    "parameterNames": false,
                    "cycles": true
                  }
                }
                """));

        // One switch thrown leaves the rest where they were.
        Assert.Equal(
            HintSettings.Default with { ImpliedValues = false },
            Settings("""{ "inlayHints": { "impliedValues": false } }"""));
    }

    /// <summary>
    /// What an editor that has said nothing about hints is shown, written into the line it is
    /// drawn in. Everything not listed here is a line that gets none, which is the half of the
    /// design that is easiest to lose: no hint on the <c>.ensure</c> or the <c>.state</c> that
    /// say their own, none on a line whose state did not change, none for an argument that
    /// names the parameter it is for, none for a call that takes one argument, and none at all
    /// for a call written with named arguments.
    /// </summary>
    [Fact]
    public async Task WhatTheDefaultsShow()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await StartAsync(null, timeout);

        Assert.Equal(
            [
                "5: fast = 2",
                "9: x:  .word +0",
                "10: y:  .word +2",
                "13: .export WIDE  = 8 * 4 = $20",
                "29: sep #$20 a8",
                "36: xce native",
                "37: rep #$30 a16 i16",
                "41: jsr narrow → a8",
                "43: fill!(count: 16, with: 0)",
                "44: fill!(count, with: 0)",
                "47: jeq done long",
            ],
            await ShownAsync(client, timeout));

        // Twelve characters is as long as a hint gets: past that it stops reading as a note in
        // the margin and starts pushing the line it is about off the screen.
        var hints = await client.InlayHintsAsync(Uri, 0, 200, timeout);
        Assert.All(hints, hint => Assert.True(hint.Label.Length <= 12, hint.Label));
        Assert.All(hints, hint => Assert.NotNull(hint.Tooltip));
    }

    /// <summary>
    /// What a hint means is in a sentence under it, with the declaration that decided it where
    /// there is one, because four characters cannot say it.
    /// </summary>
    [Fact]
    public async Task AHintSaysWhatItMeansWhenItIsPointedAt()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await StartAsync(null, timeout);

        var hints = await client.InlayHintsAsync(Uri, 0, 200, timeout);
        Assert.Equal(
            "What reaches the next line is `a8, i16, native`, and what reached this one was "
                + "`a16, i16, native`.",
            hints.Single(hint => hint.Position.Line == 41).Tooltip!.Value);
        Assert.Equal(
            "`jeq` cannot reach its target in the two-byte form, so it is written as a `bne` over "
                + "a `jmp`: 5 bytes and 3-5 cycles.",
            hints.Single(hint => hint.Position.Line == 47).Tooltip!.Value);
        Assert.Equal(
            "`WIDE` works out to `$20`, which is 32.",
            hints.Single(hint => hint.Position.Line == 13).Tooltip!.Value);
    }

    /// <summary>
    /// The counts are off until they are asked for, because every instruction has one and a
    /// column of numbers down a listing is the thing this feature is most able to become. The
    /// command turns them on for the session, and the editor is asked to fetch what it holds.
    /// </summary>
    [Fact]
    public async Task ACountIsOffUntilItIsAskedFor()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(
            TestClient.Capable(refreshesHints: true), timeout);
        await client.OpenAsync(Uri, Source);
        Assert.Empty((await client.NextDiagnosticsAsync(Uri, timeout)).Diagnostics);

        Assert.DoesNotContain(await ShownAsync(client, timeout), line => line.StartsWith("34:", StringComparison.Ordinal));
        Assert.True(await client.ToggleCycleHintsAsync(timeout));
        await client.NextHintsRefreshAsync(timeout);

        var counted = await ShownAsync(client, timeout);
        Assert.Contains("34: sei 2", counted);

        // A label carries the count of the block it opens, which is the run of lines under it
        // that always run together.
        Assert.Contains("50: done: block 3", counted);

        // Where two hints would land on one line the state change wins, and what it stood in
        // front of is under its sentence.
        Assert.Contains("37: rep #$30 a16 i16", counted);
        var hints = await client.InlayHintsAsync(Uri, 37, 37, timeout);
        Assert.Contains("**3** — This line takes 3 cycles.", hints.Single().Tooltip!.Value, StringComparison.Ordinal);

        Assert.False(await client.ToggleCycleHintsAsync(timeout));
        Assert.DoesNotContain(await ShownAsync(client, timeout), line => line.StartsWith("34:", StringComparison.Ordinal));
    }

    /// <summary>A file whose every switch is off is the file, which is what the default is for.</summary>
    [Fact]
    public async Task EverythingOffIsNothingDrawn()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await StartAsync(
            new
            {
                stateChanges = false,
                longBranches = false,
                impliedValues = false,
                parameterNames = false,
                cycles = false,
            },
            timeout);

        Assert.Empty(await client.InlayHintsAsync(Uri, 0, 200, timeout));
    }

    /// <summary>Only the lines the editor is showing are worked out, and not the file.</summary>
    [Fact]
    public async Task OnlyTheLinesAskedAboutAreWorkedOut()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await StartAsync(null, timeout);

        var hints = await client.InlayHintsAsync(Uri, 36, 38, timeout);
        Assert.Equal([36, 37], hints.Select(hint => hint.Position.Line));
    }

    /// <summary>
    /// The example program hinted as an editor that has said nothing shows it, which is the
    /// one reading that says whether the defaults are right: a file opened for the first time
    /// should look like the file.
    /// </summary>
    [Fact]
    public async Task TheExampleWithTheDefaultsStillLooksLikeTheFile()
    {
        var timeout = TestContext.Current.CancellationToken;
        var folder = Repo.Path("examples", "snes-hello");
        var file = Path.Combine(folder, "src", "main.nt65");
        var text = File.ReadAllText(file).ReplaceLineEndings("\n");
        var uri = new Uri(file).AbsoluteUri;
        await using var client = await TestClient.StartAsync(
            TestClient.Capable(), timeout, rootUri: new Uri(folder).AbsoluteUri);
        await client.OpenAsync(uri, text);
        await client.NextDiagnosticsAsync(uri, timeout);

        // Twenty hints in a hundred and seventy lines, every one on a line that moves the
        // processor state and says so nowhere else, and nothing at all on the other hundred
        // and fifty. Nothing else in the file earns one: its data is declared and not laid
        // out, its constants are written as literals, and every branch reaches.
        Assert.Equal(
            [
                "27: xce                         ; native, both widths 8 native",
                "28: rep #$38                    ; a16, i16, binary mode a16 i16",
                "32: tcd                         ; D = $0000 dp = $0000",
                "34: plb                         ; B = $00 dbr = $00",
                "35: sep #$20 a8",
                "45: rep #$20 a16",
                "52: sep #$20 a8",
                "77: rep #$20 a16",
                "82: sep #$20 a8",
                "92: rep #$20 a16",
                "114: sep #$20 a8",
                "120: rep #$20 a16",
                "131: sep #$20 a8",
                "139: rep #$30 a16 i16",
                "144: plb dbr = $00",
                "146: tcd dp = $0000",
                "147: sep #$20 a8",
                "158: rep #$20 a16",
                "160: plb dbr?",
                "161: pld dp?",
            ],
            Shown(text, await client.InlayHintsAsync(uri, 0, 400, timeout)));
    }

    private static HintSettings Settings(string json) =>
        HintSettings.Of(JsonDocument.Parse(json).RootElement);

    /// <summary>Hints for the lines the editor is showing, as <c>line: the line with them in it</c>.</summary>
    private static async Task<IReadOnlyList<string>> ShownAsync(
        TestClient client, CancellationToken cancellation, int last = 200) =>
        Shown(Source, await client.InlayHintsAsync(Uri, 0, last, cancellation));

    /// <summary>The same, for text the test holds rather than asks the client for.</summary>
    private static IReadOnlyList<string> Shown(string text, IReadOnlyList<InlayHint> hints)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        return
        [
            .. hints
                .GroupBy(hint => hint.Position.Line)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key}: {Drawn(lines[group.Key], group)}"),
        ];
    }

    /// <summary>One line with its hints written into it, which is what a reader sees.</summary>
    private static string Drawn(string line, IEnumerable<InlayHint> hints)
    {
        var written = line;
        foreach (var hint in hints.OrderByDescending(hint => hint.Position.Character))
        {
            written = written[..hint.Position.Character]
                + (hint.PaddingLeft == true ? " " : "") + hint.Label + (hint.PaddingRight == true ? " " : "")
                + written[hint.Position.Character..];
        }
        return written.TrimStart();
    }

    private static async Task<TestClient> StartAsync(object? inlayHints, CancellationToken cancellation)
    {
        var client = await TestClient.StartAsync(TestClient.Capable(), cancellation, inlayHints: inlayHints);
        await client.OpenAsync(Uri, Source);
        Assert.Empty((await client.NextDiagnosticsAsync(Uri, cancellation)).Diagnostics);
        return client;
    }
}
