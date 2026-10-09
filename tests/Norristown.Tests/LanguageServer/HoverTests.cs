using static Norristown.Tests.LanguageServer.EditingWorkspace;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests what hover shows about the processor state. It shows which registers a routine or scope
/// preserves, the state inferred for a routine, what each register holds at a line, and what the
/// routine has pushed.
/// </summary>
public sealed class HoverTests
{
    /// <summary>
    /// An editor can be set to hide lenses, so which registers a routine or an inline
    /// <c>.scope</c> block preserves is shown on hover as well as in the lens above the line.
    /// </summary>
    [Fact]
    public async Task HoverOnARoutineShowsTheStateInferredForIt()
    {
        var timeout = TestTimeout.Token();
        const string Source = ".module main\n.cpu 65816\n.segment CODE\n.proc draw {\n    lda #$12\n    rep #$20\n    rts\n}\n"
            + ".export .proc main: a8, i16 -> a16 {\n    jsr draw\n    rts\n}\n";
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source));

        var hover = await client.HoverAsync(MainUri, Locate.At(Source, ".proc dr|aw"), timeout);

        Assert.Matches(@"inferred +a8, i16 -> a16", hover?.Contents.Value);
    }

    /// <summary>
    /// The inferred row shows the program bank a routine was inferred to run in only where it is
    /// not the bank its segment declares. <c>FAST</c> is in bank $00 and mirrored at $80.
    /// <c>reset</c> jumps to <c>fast</c> through the mirror, so <c>fast</c> and <c>helper</c>,
    /// which only <c>fast</c> calls, run in bank $80. <c>either</c> is also called from
    /// <c>slow</c>, which runs in bank $00, so its bank is not known. <c>home</c> is called only
    /// from <c>slow</c>, so it runs in its own bank and its row says nothing about the bank.
    /// </summary>
    [Fact]
    public async Task TheInferredRowShowsABankOnlyWhereItIsNotTheHomeBank()
    {
        var timeout = TestTimeout.Token();
        using var root = new TempFolder("nt65-hover-");
        root.Write("nt65.json", """
            { "cpu": "65816", "files": ["*.nt65"],
              "segments": { "STUBS": { "size": "abs", "bank": 0 }, "FAST": { "size": "abs", "bank": 0, "mirrors": ["$80"] } } }
            """);
        const string Source = """
            .module main
            .export reset
            .segment STUBS
            .proc reset: emu, dp?, dbr?, noreturn {
                clc
                xce
                jml ($80 << 16) | .loword(fast)
            }
            .segment FAST
            .proc fast: noreturn {
                jsr helper
                jsr either
            @forever:
                bra @forever
            }
            .proc helper: a8 {
                rts
            }
            .proc either: a8 {
                rts
            }
            .proc home: a8 {
                rts
            }
            .proc slow: a8, native, noreturn {
                jsr either
                jsr home
            @forever:
                bra @forever
            }
            """;
        var source = Source.ReplaceLineEndings("\n");
        root.Write("main.nt65", source);
        var uri = new Uri(root.PathOf("main.nt65")).AbsoluteUri;
        await using var client = await TestClient.StartAsync(new Uri(root.FullName).AbsoluteUri, null, timeout);
        await client.OpenAsync(uri, source);

        async Task<string> Inferred(string at) =>
            (await client.HoverAsync(uri, Locate.At(source, at), timeout))?.Contents.Value ?? "";

        Assert.Matches(@"inferred +.*pbr = \$80", await Inferred(".proc fa|st"));
        Assert.Matches(@"inferred +.*pbr = \$80", await Inferred(".proc hel|per"));
        Assert.Matches(@"inferred +.*pbr\?", await Inferred(".proc eit|her"));
        Assert.DoesNotContain("pbr", await Inferred(".proc ho|me"), StringComparison.Ordinal);
    }

    /// <summary>
    /// An editor can be set to hide lenses, so which registers a routine or an inline
    /// <c>.scope</c> block preserves is shown on hover as well as in the lens above the line.
    /// </summary>
    [Fact]
    public async Task HoverOnARoutineAndOnAScopeShowsWhatItPreserves()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc main {
                lda #1
                .scope {
                    pha
                    ldx #2
                    stx $10
                    pla
                }
                sta $11
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var routine = await client.HoverAsync(MainUri, Locate.At(Source, ".proc m|ain"), timeout);
        var scope = await client.HoverAsync(MainUri, Locate.At(Source, ".s|cope"), timeout);

        Assert.Contains("```nt65\n.proc main\n```", routine?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("preserves  Y, C, V", routine?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("```nt65\n.scope\n```", scope?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("preserves  A, Y, C, V", scope?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Hover on a line shows what each register holds there, beside what the line costs. A
    /// register may hold the value another register had on entry, which is how a 6502 saves X
    /// (by copying it to A), and naming that register makes the save readable.
    /// </summary>
    [Fact]
    public async Task HoverShowsWhatTheRegistersHoldAtTheLine()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc main {
                txa
                ldy #0
                sty $10
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var entry = await client.HoverAsync(MainUri, Locate.At(Source, "txa"), timeout);
        var after = await client.HoverAsync(MainUri, Locate.At(Source, "sty $10"), timeout);

        Assert.Contains(
            "A       as entered\nX       as entered\nY       as entered\nC       as entered\n"
                + "Z       as entered\nN       as entered\nV       as entered\n```",
            entry?.Contents.Value,
            StringComparison.Ordinal);
        Assert.Contains(
            "A       X as entered\nX       as entered\nY       new\nC       as entered\n"
                + "Z       new\nN       new\nV       as entered\n```",
            after?.Contents.Value,
            StringComparison.Ordinal);

        // The routine pushes nothing, and an empty stack is shown by leaving the stack group out.
        Assert.DoesNotContain("stack", after?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a register may hold is a set. Where two paths meet, it may hold its entry value on
    /// one path and a newly loaded value on the other. Reducing that to "unknown" would hide a
    /// save that is still valid on one path, so both descriptions are shown, joined by "or".
    /// </summary>
    [Fact]
    public async Task HoverShowsWhatTwoPathsLeaveInARegister()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc main {
                ldx $10
                beq @skip
                lda #1
            @skip:
                sta $11
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var hover = await client.HoverAsync(MainUri, Locate.At(Source, "sta $11"), timeout);

        // One path falls through the `lda` and the other branches over it.
        Assert.Contains(
            "A       as entered, or new\nX       new\nY       as entered\nC       as entered",
            hover?.Contents.Value,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Hover lists what the routine has pushed, top of the stack first, with what each push
    /// saved, because what a <c>pla</c> is about to get back is what a reader wants to know.
    /// </summary>
    [Fact]
    public async Task HoverListsWhatTheRoutineHasPushed()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc main {
                txa
                pha
                php
                sta $10
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var hover = await client.HoverAsync(MainUri, Locate.At(Source, "sta $10"), timeout);

        // The `php` is on top, holding the flags, of which `txa` set Z and N. Under it is the
        // accumulator, which `txa` filled with X.
        Assert.Contains(
            "V       as entered\n\nstack   C, V as entered; Z, N new\n        X as entered\n```",
            hover?.Contents.Value,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Where the analysis has lost track of the stack, hover says so. Leaving the stack group out
    /// means the stack is empty, and a stack nothing is known about is not an empty one.
    /// </summary>
    [Fact]
    public async Task HoverShowsWhereTheStackIsNotKnown()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc main {
                txs
                sta $10
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var hover = await client.HoverAsync(MainUri, Locate.At(Source, "sta $10"), timeout);

        Assert.Contains("\nstack   unknown", hover?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reader needs the top of the stack, which is what the routine is about to pull back, so
    /// a deep stack is cut off with a count of the entries not shown rather than listed in full.
    /// </summary>
    [Fact]
    public async Task HoverCountsThePushesItDoesNotList()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc main {
                pha
                pha
                pha
                pha
                pha
                pha
                pha
                sta $10
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var hover = await client.HoverAsync(MainUri, Locate.At(Source, "sta $10"), timeout);

        Assert.Contains("        A as entered\n        and 1 more\n```", hover?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>.frame</c> declares the bytes it covers as one structure the routine has pushed, so
    /// hover shows them as one row no matter how many pushes built them.
    /// </summary>
    [Fact]
    public async Task HoverReadsAFrameAsOnePush()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .cpu 65816
            .struct Locals {
            a:      .word
            b:      .word
            }
            .segment CODE
            .proc p: a16, i8 {
                pea $0000
                pea $1234
                .frame vars: Locals
                sta $10
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var hover = await client.HoverAsync(MainUri, Locate.At(Source, "sta $10"), timeout);

        Assert.Contains("\nstack   frame vars\n```", hover?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// On the 65816 the processor-state analysis supplies two facts that the stack tracking alone
    /// cannot know. These are the status that a <c>php</c> saved and the width of each pushed
    /// register, which decides whether a later pull restores the value at all.
    /// </summary>
    [Fact]
    public async Task HoverNamesA65816PushAndShowsHowWideItWas()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .cpu 65816
            .segment CODE
            .proc p: a8, i8 {
                pha
                php
                phx
                sta $10
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var hover = await client.HoverAsync(MainUri, Locate.At(Source, "sta $10"), timeout);

        Assert.Contains(
            "stack   X as entered, 8-bit\n        status a8, i8\n        A as entered, 8-bit\n```",
            hover?.Contents.Value,
            StringComparison.Ordinal);
    }
}
