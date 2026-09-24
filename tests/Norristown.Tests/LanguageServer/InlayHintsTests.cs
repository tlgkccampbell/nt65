using System.Text.Json;
using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests inlay hints, the few words drawn into a line to say what the line itself does not. There
/// are five kinds, each with a setting of its own. Which lines get no hint matters as much as
/// which do, because a note on every line would make the source a dashboard rather than a listing.
/// </summary>
public sealed class InlayHintsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    /// <summary>
    /// A 65816 file with a case of every kind in it. It has widths that change and widths that do
    /// not, an <c>.ensure</c> and a <c>.state</c> that already state the widths themselves,
    /// values a declaration leaves implied, a branch that cannot reach its target, and calls
    /// whose arguments are and are not worth labelling with the parameter's name.
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
    /// Each kind's setting is read from where the editor's settings put it, and a setting the
    /// editor's settings do not mention keeps its default. Cycle counts default to off, and every
    /// other kind defaults to on.
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

        // Turning one setting off leaves the rest at their defaults.
        Assert.Equal(
            HintSettings.Default with { ImpliedValues = false },
            Settings("""{ "inlayHints": { "impliedValues": false } }"""));
    }

    /// <summary>
    /// The hints shown to an editor with no hint settings, each drawn into its line. Every line
    /// not listed here gets no hint, and that half of the design is the easiest to lose. No hint
    /// appears on the <c>.ensure</c> or the <c>.state</c>, which already state the widths, or on a
    /// line whose state did not change. None appears for an argument spelled the same as its
    /// parameter or for a call that takes one argument, and none at all for a call that uses named
    /// arguments.
    /// </summary>
    [Fact]
    public async Task WhatTheDefaultsShow()
    {
        var timeout = TestTimeout.Token();
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

        // No hint is longer than twelve characters, because past that it stops reading as a note
        // and starts pushing the rest of its line off the screen. Every hint also has a tooltip.
        var hints = await client.InlayHintsAsync(Uri, 0, 200, timeout);
        Assert.All(hints, hint => Assert.True(hint.Label.Length <= 12, hint.Label));
        Assert.All(hints, hint => Assert.NotNull(hint.Tooltip));
    }

    /// <summary>
    /// Each hint's tooltip explains it in a sentence, naming the declaration that decided it where
    /// there is one, because a hint a few characters long cannot say that itself.
    /// </summary>
    [Fact]
    public async Task AHintExplainsWhatItMeansWhenItIsPointedAt()
    {
        var timeout = TestTimeout.Token();
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
    /// Cycle-count hints are off until asked for, because every instruction has a count, and a
    /// column of numbers down the listing is what this feature most risks becoming. The command
    /// toggles them for the session, and the server asks the editor to refresh its hints.
    /// </summary>
    [Fact]
    public async Task ACountIsOffUntilItIsAskedFor()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.StartAsync(
            TestClient.Capable(refreshesHints: true), timeout);
        await client.OpenAsync(Uri, Source);
        Assert.Empty((await client.NextDiagnosticsAsync(Uri, timeout)).Diagnostics);

        Assert.DoesNotContain(await ShownAsync(client, timeout), line => line.StartsWith("34:", StringComparison.Ordinal));
        Assert.True(await client.ToggleCycleHintsAsync(timeout));
        await client.NextHintsRefreshAsync(timeout);

        var counted = await ShownAsync(client, timeout);
        Assert.Contains("34: sei 2", counted);

        // A label shows the cycle count of the block it starts, which is the run of lines under
        // it that always execute together.
        Assert.Contains("50: done: block 3", counted);

        // Where two hints would land on one line the state change is shown, and the cycle count
        // it displaced is given in its tooltip.
        Assert.Contains("37: rep #$30 a16 i16", counted);
        var hints = await client.InlayHintsAsync(Uri, 37, 37, timeout);
        Assert.Contains("**3** — This line takes 3 cycles.", hints.Single().Tooltip!.Value, StringComparison.Ordinal);

        Assert.False(await client.ToggleCycleHintsAsync(timeout));
        Assert.DoesNotContain(await ShownAsync(client, timeout), line => line.StartsWith("34:", StringComparison.Ordinal));
    }

    /// <summary>With every kind's setting off, no hints are drawn at all.</summary>
    [Fact]
    public async Task EverythingOffIsNothingDrawn()
    {
        var timeout = TestTimeout.Token();
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

    /// <summary>Hints are worked out only for the lines the editor asks about, not for the whole file.</summary>
    [Fact]
    public async Task OnlyTheLinesAskedAboutAreWorkedOut()
    {
        var timeout = TestTimeout.Token();
        await using var client = await StartAsync(null, timeout);

        var hints = await client.InlayHintsAsync(Uri, 36, 38, timeout);
        Assert.Equal([36, 37], hints.Select(hint => hint.Position.Line));
    }

    /// <summary>
    /// The example program appears as an editor with no hint settings shows it. This is the real check
    /// that the defaults are right: a file opened for the first time should still look like the
    /// file, not be buried under hints.
    /// </summary>
    [Fact]
    public async Task TheExampleWithTheDefaultsStillLooksLikeTheFile()
    {
        var timeout = TestTimeout.Token();
        var folder = Repo.Path("examples", "lorom-template");
        var file = Path.Combine(folder, "src", "main.nt65");
        var text = File.ReadAllText(file).ReplaceLineEndings("\n");
        var uri = new Uri(file).AbsoluteUri;
        await using var client = await TestClient.StartAsync(
            TestClient.Capable(), timeout, rootUri: new Uri(folder).AbsoluteUri);
        await client.OpenAsync(uri, text);
        await client.NextDiagnosticsAsync(uri, timeout);

        // There are seventeen hints in a hundred and seventy-four lines. They fall on the lines
        // that change the processor state where nothing else in the source says so, the calls
        // that return a width other than the one they were called with, the one constant worked
        // out from the settings, and the store before the loop head. At that store, the widths
        // from the first pass and from the loop's own back edge meet, and A's width becomes
        // unknown. Nothing else in the file gets a hint, because its data is declared and not
        // laid out, and every branch reaches its target.
        Assert.Equal(
            [
                "23: PPURES_BITS = .select(USE_PSEUDOHIRES, SUB_HIRES, 0) | .select(USE_INTERLACE, INTERLACE, 0) = 0",
                "50: plb dbr = $80",
                "52: sep #$20 a8",
                "57: plb dbr?",
                "74: plb dbr = $81",
                "83: jsl spc_boot_apu → a8",
                "86: jsl load_bg_tiles → a16           ; fill pattern table",
                "87: jsl draw_bg → a8                 ; fill nametable",
                "88: jsl load_player_tiles → a16",
                "91: sep #$20 a8",
                "133: rep #$30 a16",
                "136: sta player_xlo a?",
                "139: jsl move_player → a8 i8",
                "142: rep #$30 a16 i16",
                "144: jsl draw_player_sprite → a8",
                "150: jsl ppu_clear_oam → a16",
                "157: sep #$20 a8",
            ],
            Shown(text, await client.InlayHintsAsync(uri, 0, 400, timeout)));
    }

    private static HintSettings Settings(string json) =>
        HintSettings.Of(JsonDocument.Parse(json).RootElement);

    /// <summary>
    /// Returns the hints for the test source up to line <paramref name="last"/>, each formatted as
    /// <c>line: the line with its hints drawn in</c>.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ShownAsync(
        TestClient client, CancellationToken cancellation, int last = 200) =>
        Shown(Source, await client.InlayHintsAsync(Uri, 0, last, cancellation));

    /// <summary>
    /// Returns the <paramref name="hints"/> already fetched for <paramref name="text"/>, each
    /// formatted as <c>line: the line with its hints drawn in</c>.
    /// </summary>
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

    /// <summary>Returns one line with its hints drawn into it, as a reader sees it.</summary>
    private static string Drawn(string line, IEnumerable<InlayHint> hints)
    {
        var drawn = line;
        foreach (var hint in hints.OrderByDescending(hint => hint.Position.Character))
        {
            drawn = drawn[..hint.Position.Character]
                + (hint.PaddingLeft == true ? " " : "") + hint.Label + (hint.PaddingRight == true ? " " : "")
                + drawn[hint.Position.Character..];
        }
        return drawn.TrimStart();
    }

    private static async Task<TestClient> StartAsync(object? inlayHints, CancellationToken cancellation)
    {
        var client = await TestClient.StartAsync(TestClient.Capable(), cancellation, inlayHints: inlayHints);
        await client.OpenAsync(Uri, Source);
        Assert.Empty((await client.NextDiagnosticsAsync(Uri, cancellation)).Diagnostics);
        return client;
    }
}
