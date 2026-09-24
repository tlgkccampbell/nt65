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
                (7, "11-15 cycles"),
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

                // One of its paths returns, so it is not marked as never returning.
                "8-13 cycles",

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
                "not counted: a block move takes 7 cycles per byte, and the number of bytes is in A",
            ],
            Costs(lenses).Select(lens => lens.Command.Title));
        Assert.Contains(
            """
            cost       18 cycles, 18+ with calls
            excluding  move: a block move takes 7 cycles per byte, and the number of bytes is in A
                       CHROUT: no code in the program
            """.ReplaceLineEndings("\n"),
            hover?.Contents.Value,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A block move takes seven cycles for every byte it moves, and the number of bytes is in A
    /// when it runs, so the routine containing it has no count. The lens says so rather than
    /// being left out, because a missing lens reads as though the analysis failed.
    /// </summary>
    [Fact]
    public async Task ALensShowsWhyARoutineWithABlockMoveHasNoCount()
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
            """;
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, Source.ReplaceLineEndings("\n")));

        var lenses = await client.RequestAsync<IReadOnlyList<CodeLens>>("textDocument/codeLens",
            new CodeLensParams(new TextDocumentIdentifier(MainUri)), timeout);

        Assert.Equal(
            ["not counted: a block move takes 7 cycles per byte, and the number of bytes is in A"],
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
            ["preserves A, X, Y, C", "preserves Y, C", "preserves A, X, Y, C", "preserves ?"],
            lenses.Where(lens => lens.Command.Title.Contains("preserves", StringComparison.Ordinal))
                .Select(lens => lens.Command.Title));
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

                // `bpl` tests the sign bit, and 200 has it set before the loop starts, so the
                // loop is not counted.
                "17+ cycles, loops",
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
            [(2, "preserves Y, C"), (4, "preserves A, Y, C")],
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
    /// Returns the lenses that give what a pass costs, which are what these tests check. The lens
    /// that says which registers are preserved is left out, because it has tests of its own.
    /// </summary>
    private static IEnumerable<CodeLens> Costs(IEnumerable<CodeLens> lenses) =>
        lenses.Where(lens => !lens.Command.Title.Contains("preserves", StringComparison.Ordinal));
}
