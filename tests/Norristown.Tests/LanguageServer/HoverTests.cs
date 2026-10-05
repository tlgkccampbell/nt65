using static Norristown.Tests.LanguageServer.EditingWorkspace;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests what hover shows about the processor state. It shows which registers a routine or scope
/// preserves, what each register holds at a line, and what the routine has pushed.
/// </summary>
public sealed class HoverTests
{
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
        Assert.Contains("preserves  Y, C", routine?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("```nt65\n.scope\n```", scope?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("preserves  A, Y, C", scope?.Contents.Value, StringComparison.Ordinal);
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
            "A       as entered\nX       as entered\nY       as entered\nC       as entered\n```",
            entry?.Contents.Value,
            StringComparison.Ordinal);
        Assert.Contains(
            "A       X as entered\nX       as entered\nY       new\nC       as entered\n```",
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

        // The `php` is on top; under it is the accumulator, which `txa` filled with X.
        Assert.Contains(
            "C       as entered\n\nstack   C as entered\n        X as entered\n```",
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
