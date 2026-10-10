using System.Text.Json;
using Norristown.LanguageServer.Protocol;
using static Norristown.Tests.LanguageServer.EditingWorkspace;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests the code lenses above each routine and inline scope, which report what one pass through
/// it costs and which registers it preserves.
/// </summary>
public sealed class CodeLensTests
{
    /// <summary>
    /// Above each routine, a lens shows the cycles that one pass through it costs. The cost is a
    /// range where the routine's paths have a longest one, and a lower bound followed by <c>+</c>
    /// where it loops. The lens also notes what the count leaves out.
    /// </summary>
    [Fact]
    public async Task ALensAboveEachRoutineShowsWhatOnePassThroughItCosts()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc straight {
                lda #0
                sta $10
                rts
            }
            .proc branching {
                lda $10
                beq @skip
                inx
            @skip:
                rts
            }
            .proc looping {
            @turn:
                lda $10
                bne @turn
                rts
            }
            .proc calling {
                jsr straight
                rts
            }
            .proc endless: noreturn {
            @turn:
                lda $10
                beq @turn
                jmp @turn
            }
            .proc unlaid {
                stz $10
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(
            [
                (2, "11 cycles"),
                // Not taken, the branch costs 2 and the pass 3 + 2 + 2 + 6 = 13. Taken, it costs 3,
                // or 4 across a page, and the pass 3 + 3 + 6 = 12 to 13.
                (7, "12-13 cycles"),
                (14, "11+ cycles, loops"),
                (20, "12 cycles, 23 with calls"),

                // No path leaves it, so there is no pass through it to put a cost on.
                (24, "never returns"),

                // `stz` is not a 6502 instruction, so the line is left out of the assembled code,
                // and a count of the rest would not be the routine's real cost. It gets no lens.
            ],
            Costs(lenses).Select(lens => (lens.Range.Start.Line, lens.Command.Title)));
    }

    /// <summary>
    /// The cost of a routine includes what it calls, worked out through the call graph. A call
    /// costs the call instruction plus the callee, as does a tail jump or a <c>.fallthrough</c>
    /// into another routine. nt65 cannot count a routine with no body, a call to an address at
    /// which no routine is declared, or a routine calling itself. Each of these is left out and
    /// named, and the rest is still counted, as a lower bound with no upper bound. The lens names
    /// two at most, then says how many more.
    /// </summary>
    [Fact]
    public async Task ALensShowsWhatARoutineCostsWithWhatItCalls()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .cpu 65c02
            .segment CODE
            .proc CHROUT = $ffd2
            .proc RESET = $fffc: noreturn
            .proc into_leaf {
                inx
                .fallthrough leaf
            }
            .proc leaf {
                lda #0
                rts
            }
            .proc middle {
                jsr leaf
                jsr leaf
                rts
            }
            .proc onward {
                jsr middle
                rts
            }
            .proc tail {
                jmp leaf
            }
            .proc recurse {
                jsr recurse
                rts
            }
            .proc hands_off {
                jsr leaf
                jmp endless
            }
            .proc may_return {
                lda $10
                beq @die
                rts
            @die:
                jmp endless
            }
            .proc into_endless: noreturn {
                inx
                .fallthrough endless
            }
            .proc endless {
            @turn:
                jmp @turn
            }
            .proc external {
                jsr CHROUT
                rts
            }
            .proc through {
                jsr $1234
                rts
            }
            .proc two {
                jsr external
                jmp through
            }
            .proc three {
                jsr external
                jsr recurse
                jmp through
            }
            .proc quit: noreturn {
                jmp RESET
            }
            .proc bail: noreturn {
                jsr CHROUT
                jmp RESET
            }
            .data vector: .addr leaf
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(
            [
                "2 cycles, 10 with calls",
                "8 cycles",
                "18 cycles, 34 with calls",
                "12 cycles, 46 with calls",
                "3 cycles, 11 with calls",
                "12 cycles, 12+ with calls, excluding recursion",

                // It jumps to a routine that never returns, so the count is the cost of getting
                // there, and the jump is not treated as a call the count could not follow.
                "9 cycles, 17 with calls, then never returns",

                // One of its paths returns, so it is not marked as never returning. That path
                // costs 3 + 2 + 6 = 11, and the one taken to the jump 3 + 3 + 3 = 9 to 10.
                "9-11 cycles",

                // It runs on into a routine that never returns, so it never returns either.
                "2 cycles, then never returns",
                "never returns",

                // What is left out is named no matter how many calls away it is, and named once
                // no matter how many paths reach it.
                "12 cycles, 12+ with calls, excluding CHROUT",
                "12 cycles, 12+ with calls, excluding jsr $1234",
                "9 cycles, 33+ with calls, excluding CHROUT and jsr $1234",
                "15 cycles, 51+ with calls, excluding CHROUT, recursion and 1 more",

                // A routine with no body that is declared never to return ends the pass, as a
                // routine with a body does, and is not something the count leaves out.
                "3 cycles, then never returns",
                "9 cycles, 9+ with calls, excluding CHROUT, then never returns",
            ],
            Costs(lenses).Select(lens => lens.Command.Title));
    }

    /// <summary>
    /// A call to a label inside a routine that declares <c>noreturn</c> ends the pass, as a call
    /// to the routine itself does, so the caller never returns and the call is not left out of
    /// the count.
    /// </summary>
    [Fact]
    public async Task ALensEndsThePassAtACallIntoARoutineThatNeverReturns()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc stop: noreturn {
                lda #0
                resume:
                jmp resume
            }
            .proc caller {
                lda $10
                jsr stop::resume
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(
            [
                (2, "never returns"),

                // A zero-page `lda` costs 3 and the `jsr` 6, and nothing after the call is counted.
                (7, "9 cycles, then never returns"),
            ],
            Costs(lenses).Select(lens => (lens.Range.Start.Line, lens.Command.Title)));
    }

    /// <summary>
    /// The routines a <c>.next</c> names under a call through a pointer are alternatives, and one
    /// pass runs only one of them. So the cost with calls adds the cheapest of them at least and
    /// the dearest at most, never their sum. A tail jump through a pointer with a <c>.next</c>
    /// composes the same way as a tail jump to a routine named in its operand.
    /// </summary>
    [Fact]
    public async Task ALensAddsOneOfTheRoutinesADispatchCanCall()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .cpu 65816
            .segment CODE
            .proc dispatch {
                jsr (table,x)
                .next w1, w2
                rts
            }
            .proc tail {
                jmp (vector)
                .next w1, w2
            }
            .proc either {
                dex
                beq @other
                jmp (vector)
                .next w1
            @other:
                jmp (vector)
                .next w2
            }
            .proc w1 {
                nop
                rts
            }
            .proc w2 {
                nop
                nop
                rts
            }
            .data table: .addr w1, w2
            .data vector: .addr w1
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(
            [
                // The call costs 8 and the return 6. With w1 at 8 cycles and w2 at 10, one pass
                // costs 14 + 8 = 22 at least and 14 + 10 = 24 at most.
                "14 cycles, 22-24 with calls",

                // The indirect jump costs 5, and control comes back from w1 or w2 to the caller.
                "5 cycles, 13-15 with calls",

                // The way to w1 falls through the branch for 2 + 2 + 5 = 9 and adds 8. The way to
                // w2 takes it for 2 + 3 + 5 = 10, or 11 across a page, and adds 10.
                "9-11 cycles, 17-21 with calls",
                "8 cycles",
                "10 cycles",
            ],
            Costs(lenses).Select(lens => lens.Command.Title));
    }

    /// <summary>
    /// The lens only has room to name what a cost with calls leaves out; the hover lists each
    /// thing with why nt65 cannot count it.
    /// </summary>
    [Fact]
    public async Task TheHoverShowsWhyEachThingACostWithCallsLeavesOutIsLeftOut()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .cpu 65816
            .segment CODE
            .proc CHROUT = $ffd2
            .proc copy: a16, i16 {
                jsr move
                jsr CHROUT
                rts
            }
            .proc move: a16, i16 {
                mvn #$7e, #$7e
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);
        var hover = await client.HoverAsync(MainUri, Locate.At(Source, ".proc |copy"), timeout);

        Assert.Equal(
            [
                "18 cycles, 18+ with calls, excluding move and CHROUT",
                "not counted: a block move takes 7 cycles per byte, and moves one byte more than the 16-bit accumulator holds, which nt65 does not know here",
            ],
            Costs(lenses).Select(lens => lens.Command.Title));
        Assert.Contains(
            """
            cost       18 cycles, 18+ with calls
            excluding  move: a block move takes 7 cycles per byte, and moves one byte more than the 16-bit accumulator holds, which nt65 does not know here
                       CHROUT: no code in the program
            """.ReplaceLineEndings("\n"),
            hover?.Contents.Value,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A block move takes seven cycles for every byte it moves, and moves one byte more than the
    /// 16-bit accumulator holds. Where the accumulator is not known, the routine containing the
    /// move has no count, and the lens says so rather than being left out, because a missing lens
    /// reads as though the analysis failed. Where an immediate load of a 16-bit A gives it, the
    /// move is counted.
    /// </summary>
    [Fact]
    public async Task ALensCountsABlockMoveOnlyFromAKnownAccumulator()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .cpu 65816
            .segment CODE
            .proc copy: a16, i16 {
                mvn #$7e, #$7e
                rts
            }
            .proc counted: a16, i16 {
                lda #$00ff
                ldx #$2000
                ldy #$3000
                mvn #$7e, #$7e
                rts
            }
            .proc narrow: a8, i16 {
                lda #$ff
                mvn #$7e, #$7e
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        const string Unknown = "not counted: a block move takes 7 cycles per byte, and moves one byte more than the "
            + "16-bit accumulator holds, which nt65 does not know here";
        Assert.Equal(
            [
                Unknown,

                // A 16-bit immediate load takes 2 + 1 = 3 cycles, so the three loads are 9. The
                // move runs $00FF + 1 = 256 times at 7 cycles, which is 1792, and `rts` is 6, so
                // the pass is 9 + 1792 + 6 = 1807.
                "1807 cycles",

                // An 8-bit load leaves the accumulator's high byte as it was, so the count of
                // bytes is still not known.
                Unknown,
            ],
            Costs(lenses).Select(lens => lens.Command.Title));
    }

    /// <summary>
    /// The text after a call to a routine that returns past it is never run, so it costs nothing
    /// and the routine still has a count, rather than none and no reason for it.
    /// </summary>
    [Fact]
    public async Task InlineDataAfterACallTakesNoTime()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .cpu 6502
            .import print: proc(inline .strz)
            .segment CODE
            .proc greet {
                jsr print
                .strz "hi"
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(["12 cycles, 12+ with calls, excluding print"], Costs(lenses).Select(lens => lens.Command.Title));
    }

    /// <summary>
    /// An inline <c>.scope</c> is part of its routine, and its lens gives what one pass through
    /// the scope costs; a <c>.scope</c> at file level holds declarations and no code, and gets
    /// no lens.
    /// </summary>
    [Fact]
    public async Task ALensAboveAnInlineScopeShowsWhatThatPartOfTheRoutineCosts()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc init {
                lda #0
                .scope {
                    ldx #4
                    stx $10
                }
                rts
            }
            .scope loose {
                .proc other {
                    rts
                }
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(
            [(2, "13 cycles"), (4, "5 cycles"), (11, "6 cycles")],
            Costs(lenses).Select(lens => (lens.Range.Start.Line, lens.Command.Title)));
    }

    /// <summary>
    /// A routine that promises registers with <c>keeps</c> shows the promise apart from the
    /// registers the analysis only found unchanged, in the lens and in the hover. A caller may
    /// rely only on the promise. <c>lda #0</c> changes A, Z and N, so <c>declared</c> returns X,
    /// Y, C and V unchanged, of which it promises only X.
    /// </summary>
    [Fact]
    public async Task ALensSetsAPromiseApartFromWhatIsInferred()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc declared: keeps x {
                lda #0
                rts
            }
            .proc exact: keeps x, y, c, v {
                lda #0
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);
        var hover = await client.HoverAsync(MainUri, Locate.At(Source, ".proc decl|ared"), timeout);

        Assert.Equal(
            ["keeps X · also preserves Y, C, V (inferred)", "keeps X, Y, C, V"],
            lenses.Where(lens => lens.Command.Title.StartsWith("keeps", StringComparison.Ordinal))
                .Select(lens => lens.Command.Title));
        Assert.Contains("preserves  keeps X · also Y, C, V (inferred)", hover?.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Which registers a routine preserves, in a lens beside its cost. A routine whose calls
    /// nt65 cannot follow shows <c>preserves ?</c> rather than no lens, because a missing lens
    /// would read as though the routine were safe to call.
    /// </summary>
    [Fact]
    public async Task ALensShowsWhichRegistersARoutineHandsBack()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc quiet {
                rts
            }
            .proc counts {
                lda #0
                ldx #1
                rts
            }
            .proc saves {
                pha
                lda #0
                pla
                rts
            }
            .proc rom = $FFD2
            .proc asks {
                jsr rom
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(
            ["preserves A, X, Y, C, Z, N, V", "preserves Y, C, V", "preserves A, X, Y, C, V", "preserves ?"],
            lenses.Where(lens => lens.Command.Title.Contains("preserves", StringComparison.Ordinal))
                .Select(lens => lens.Command.Title));
    }

    /// <summary>
    /// Which registers a routine uses as its caller left them, in a lens between its cost and what
    /// it preserves. A routine that reads nothing says so, and one whose calls nt65 cannot follow
    /// ends the list with <c>?</c>. One that calls such code with none of the registers holding
    /// what its caller left names the registers it does not read, since that code may use what the
    /// caller left elsewhere. The hover says the same.
    /// </summary>
    [Fact]
    public async Task ALensShowsWhichRegistersARoutineReads()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc quiet {
                rts
            }
            .proc add {
                adc $10
                sta $10
                rts
            }
            .proc rom = $FFD2
            .proc asks {
                stx $10
                jsr rom
                rts
            }
            .proc cleared {
                lda #0
                ldx #0
                ldy #0
                clc
                clv
                jsr rom
                rts
            }
            .proc stored {
                sta $10
                lda #0
                ldx #0
                ldy #0
                clc
                clv
                jsr rom
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(
            [
                (2, "reads none"), (5, "reads A, C"), (11, "reads X, ?"), (16, "reads none of A, X, Y, C, Z, N, V"),
                (25, "reads A · none of X, Y, C, Z, N, V"),
            ],
            lenses.Where(lens => lens.Command.Title.StartsWith("reads ", StringComparison.Ordinal))
                .Select(lens => (lens.Range.Start.Line, lens.Command.Title)));
        var hover = await client.HoverAsync(MainUri, Locate.At(Source, ".proc |cleared"), timeout);
        Assert.Contains("reads      none of A, X, Y, C, Z, N, V\n", hover!.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Clicking a routine's read or preserved registers declares them in its signature. A lens
    /// whose list nt65 could not complete, or whose registers the signature already declares,
    /// does nothing when clicked.
    /// </summary>
    [Fact]
    public async Task ClickingARegistersLensDeclaresThem()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc add {
                adc $10
                sta $10
                rts
            }
            .proc declared: reads a, c, keeps x, y {
                adc $10
                sta $10
                rts
            }
            .proc rom = $FFD2
            .proc asks {
                stx $10
                jsr rom
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        // Each clickable lens carries the edit, which inserts the item into the signature.
        string Clicked(int line, string title)
        {
            var lens = Assert.Single(lenses, lens => lens.Range.Start.Line == line && lens.Command.Title == title);
            if (lens.Command.Name.Length == 0)
                return "";
            Assert.Equal("nt65.applyEdit", lens.Command.Name);
            return Assert.IsType<JsonElement>(Assert.Single(lens.Command.Arguments!))
                .GetProperty("changes").GetProperty(MainUri)[0].GetProperty("newText").GetString()!;
        }
        Assert.Equal(": reads a, c", Clicked(2, "reads A, C"));
        Assert.Equal(": keeps x, y", Clicked(2, "preserves X, Y"));
        Assert.Equal("", Clicked(7, "reads A, C"));
        Assert.Equal("", Clicked(7, "keeps X, Y"));
        Assert.Equal("", Clicked(13, "reads X, ?"));
    }

    /// <summary>
    /// A loop that counts a register down from an immediate value has a known number of iterations,
    /// so its cost is a range with an upper bound rather than a minimum with <c>+</c>. A loop of
    /// any other shape still shows only the minimum, because a loop counted wrongly is worse
    /// than one not counted.
    /// </summary>
    [Fact]
    public async Task ALoopCountingARegisterDownFromAnImmediateIsCounted()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc counted {
                ldx #16
            @turn:
                sta $0200,x
                dex
                bne @turn
                rts
            }
            .proc past_zero {
                ldy #3
            @turn:
                sty $10
                dey
                bpl @turn
                rts
            }
            .proc touched {
                ldx #16
            @turn:
                ldx $10
                dex
                bne @turn
                rts
            }
            .proc from_memory {
                ldx $10
            @turn:
                sta $0200,x
                dex
                bne @turn
                rts
            }
            .proc calling {
                ldx #4
            @turn:
                jsr leaf
                dex
                bne @turn
                rts
            }
            .proc leaf {
                rts
            }
            .proc by_twos {
                ldx #4
            @turn:
                sta $0200,x
                dex
                dex
                bpl @turn
                rts
            }
            .proc uneven {
                ldx #5
            @turn:
                sta $0200,x
                dex
                dex
                bne @turn
                rts
            }
            .proc starts_negative {
                ldx #200
            @turn:
                sta $0200,x
                dex
                bpl @turn
                rts
            }
            .proc branching {
                ldx #10
            @turn:
                lda $10
                beq @skip
                nop
            @skip:
                dex
                bne @turn
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(
            [
                // 16 iterations of a 9-11 cycle block, with the branch taken all but the last time.
                "167-182 cycles",

                // `bpl` runs one iteration past zero, so `ldy #3` is four iterations.
                "39-42 cycles",

                // The loop reloads X from memory, so the count no longer follows from the immediate.
                "15+ cycles, loops",

                // The count does not start at an immediate.
                "18+ cycles, loops",

                // The call splits the loop body into two blocks, and the loop is still counted.
                // The call is made once per iteration, so the callee's cost is counted four times.
                "51-54 cycles, 75-78 with calls",
                "6 cycles",

                // Two `dex` per iteration step through an array of words, so `ldx #4` is three
                // iterations of `bpl`.
                "43-45 cycles",

                // Stepping by two from five skips zero, so `bne` never sees it and the loop is not counted.
                "19+ cycles, loops",

                // `bpl` tests the sign bit, and 199 has it set after the first `dex`, so the flag
                // analysis proves the branch is never taken and the body runs once. The branch
                // costs 2 not taken, so the pass is 2 + 5 + 2 + 2 + 6 = 17.
                "17 cycles",

                // Short of the branch back, a turn costs 3 + 2 + 2 + 2 = 9 not taking the inner
                // branch and 3 + 3 + 2 = 8 to 9 taking it. Ten turns with the branch back taken
                // nine times, at 3 to 4, cost 2 + 80 + 27 + 2 + 6 = 117 at least and
                // 2 + 90 + 36 + 2 + 6 = 136 at most.
                "117-136 cycles",
            ],
            Costs(lenses).Select(lens => lens.Command.Title));
    }

    /// <summary>
    /// A count that starts at zero wraps round before the first test, so a `bne` loop runs the
    /// register's whole range, 256 times for an 8-bit register. A loop that a jump enters at its
    /// test runs the test as many times as the count says and the body one time fewer.
    /// </summary>
    [Fact]
    public async Task ALoopFromZeroAndALoopEnteredAtItsTestAreCounted()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc wraps {
                ldx #0
            @turn:
                sta $0200,x
                dex
                bne @turn
                rts
            }
            .proc test_first {
                ldx #4
                jmp @test
            @turn:
                sta $0200,x
            @test:
                dex
                bne @turn
                rts
            }
            .proc test_first_from_memory {
                ldx $10
                jmp @test
            @turn:
                sta $0200,x
            @test:
                dex
                bne @turn
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(
            [
                // A turn is `sta $0200,x` at 5 and `dex` at 2. The 256 turns take the branch back
                // 255 times at 3 to 4 and fall out once at 2, so the loop is 7 × 256 + 3 × 255 + 2
                // = 2559 at least and 7 × 256 + 4 × 255 + 2 = 2814 at most. `ldx #0` adds 2 and
                // `rts` 6.
                "2567-2822 cycles",

                // The test runs 4 times, the branch taken 3 times into a 5-cycle body, so the loop
                // is 2 × 4 + (3 + 5) × 3 + 2 = 34 at least and 2 × 4 + (4 + 5) × 3 + 2 = 37 at
                // most. `ldx #4` adds 2, `jmp` 3 and `rts` 6.
                "45-48 cycles",

                // The count does not start at an immediate, so the loop keeps its `+`. The fewest
                // is `ldx $10` at 3, `jmp` at 3, the test at 2 + 2 and `rts` at 6.
                "16+ cycles, loops",
            ],
            Costs(lenses).Select(lens => lens.Command.Title));
    }

    /// <summary>
    /// On the 65816 a count from zero runs the index register's whole range, which is 65536 times
    /// for a 16-bit index and 256 times for an 8-bit one.
    /// </summary>
    [Fact]
    public async Task ALoopFromZeroOnThe65816RunsTheIndexRegistersWholeRange()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .cpu 65816
            .segment CODE
            .proc wide: native, a8, i16 {
                ldx #0
            @turn:
                dex
                bne @turn
                rts
            }
            .proc narrow: native, a8, i8 {
                ldx #0
            @turn:
                dex
                bne @turn
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(
            [
                // In native mode a branch costs 2 not taken and 3 taken, wherever it goes. A turn
                // is `dex` at 2, so 65536 turns with the branch back taken 65535 times are
                // 2 × 65536 + 3 × 65535 + 2 = 327679. A 16-bit `ldx #0` adds 3 and `rts` 6.
                "327688 cycles",

                // With an 8-bit index the loop runs 256 times, 2 × 256 + 3 × 255 + 2 = 1279, and an
                // 8-bit `ldx #0` adds 2 and `rts` 6.
                "1287 cycles",
            ],
            Costs(lenses).Select(lens => lens.Command.Title));
    }

    /// <summary>
    /// An inline <c>.scope</c> gets its own lens of which registers it preserves, as a routine
    /// does: a block that saves a register and restores it preserves that register, even where
    /// the routine around it does not.
    /// </summary>
    [Fact]
    public async Task ALensAboveAnInlineScopeShowsWhatThatPartOfTheRoutinePreserves()
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

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        // The routine clobbers A and X; the block restores A, so the only register it clobbers is X.
        Assert.Equal(
            [(2, "preserves Y, C, V"), (4, "preserves A, Y, C, V")],
            lenses.Where(lens => lens.Command.Title.Contains("preserves", StringComparison.Ordinal))
                .Select(lens => (lens.Range.Start.Line, lens.Command.Title)));
    }

    /// <summary>
    /// A scope that something branches into past its start shows no registers it preserves,
    /// neither in a lens nor on hover. A pass from the top restores A, but a path that enters at
    /// <c>mid</c> pulls a byte the scope never pushed, so no one answer holds for the scope.
    /// </summary>
    [Fact]
    public async Task AScopeBranchedIntoPastItsStartShowsNoRegisters()
    {
        var timeout = TestTimeout.Token();
        const string Source = """
            .module main
            .segment CODE
            .proc main {
                beq body::mid
                .scope body {
                    pha
                    ldx #2
                mid:
                    pla
                    bne @out
                }
            @out:
                rts
            }
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);
        var scope = await client.HoverAsync(MainUri, Locate.At(Source, ".s|cope"), timeout);

        Assert.Equal(
            [2],
            lenses.Where(lens => lens.Command.Title.Contains("preserves", StringComparison.Ordinal))
                .Select(lens => lens.Range.Start.Line));
        Assert.DoesNotContain("preserves", scope?.Contents.Value ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the lenses that give what a pass costs, which are what these tests check. The lenses
    /// that say which registers are read and preserved are left out, because they have tests of
    /// their own.
    /// </summary>
    private static IEnumerable<CodeLens> Costs(IEnumerable<CodeLens> lenses) =>
        lenses.Where(lens => !lens.Command.Title.Contains("preserves", StringComparison.Ordinal)
            && !lens.Command.Title.StartsWith("reads ", StringComparison.Ordinal));
}
