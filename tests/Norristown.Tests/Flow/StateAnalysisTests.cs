using Norristown.Flow;
using Norristown.Semantics;
using Norristown.Syntax;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Checks how the 65816's widths and emulation flag flow through a routine. The tests cover what
/// each instruction does to them and what a call and a return are checked against. They also
/// check that the analysis reaches a fixed point.
/// </summary>
public sealed class StateAnalysisTests
{
    /// <summary>In emulation mode or native, `sep` leaves the widths it names at 8 bits.</summary>
    [Fact]
    public void ASepMakesTheWidthsEightWhateverTheMode()
    {
        var state = StateAt(".proc p: e?, a?, i? -> e?, a8, i8 {\n    sep #$30\n    lda #1\n    rts\n}\n", "lda #1");

        Assert.Equal(new ProcessorState(Width.Eight, Width.Eight, ProcessorMode.Unknown), state.Processor);
    }

    /// <summary>
    /// When the mode is not known, a `rep` might be a no-op (it widens nothing in emulation
    /// mode), so it leaves the widths unknown.
    /// </summary>
    [Fact]
    public void ARepInAnUnknownModeLeavesTheWidthsUnknown()
    {
        Assert.Contains("main.nt65:3: `lda #` needs the width of A, and it is not known here, because `rep #$20` widens nothing "
            + "in emulation mode, and the mode is not known: a `.state` before it declares which mode it is",
            Problems(".proc p: e? {\n    rep #$20\n    lda #1\n    sep #$20\n    rts\n}\n"));
    }

    /// <summary>In emulation mode the widths are pinned at 8 bits, and `rep` changes nothing.</summary>
    [Fact]
    public void ARepInEmulationModeChangesNothing()
    {
        var state = StateAt(".proc p: emu {\n    rep #$30\n    lda #1\n    rts\n}\n", "lda #1");

        Assert.Equal(new ProcessorState(Width.Eight, Width.Eight, ProcessorMode.Emulation), state.Processor);
    }

    /// <summary>
    /// A `rep` whose flag byte nt65 cannot evaluate might clear either width bit, so both
    /// widths become unknown.
    /// </summary>
    [Fact]
    public void ARepOfAnUnknownValueForgetsBothWidths()
    {
        var state = StateAt(".import flags: zp\n.proc p: a8, i8 {\n    rep #<flags\n    nop\n    .state a8, i8\n    rts\n}\n", "nop");

        Assert.Equal(Width.Unknown, state.Processor.A);
        Assert.Equal(Width.Unknown, state.Processor.Index);
    }

    [Theory]
    // `clc` then `xce` enters native mode; coming from emulation mode, both widths start at 8 bits.
    [InlineData("emu", "clc", "a8, i8, native")]
    [InlineData("a8, i8, native", "clc", "a8, i8, native")]
    [InlineData("e?", "clc", "a?, i?, native")]

    // `sec` then `xce` enters emulation mode, where both widths are 8 bits.
    [InlineData("native", "sec", "a8, i8, emu")]
    [InlineData("e?", "sec", "a8, i8, emu")]

    // After any other `xce` the carry it swapped in is unknown, so the mode is too.
    [InlineData("native", "nop", "a?, i?, e?")]
    public void AnXceTakesItsModeFromTheCarrySetJustBeforeIt(string entry, string before, string after)
    {
        var state = StateAt($".proc p: {entry} -> e?, a?, i? {{\n    {before}\n    xce\n    tax\n    rts\n}}\n", "tax");

        Assert.Equal(after, state.Processor.ToString());
    }

    /// <summary>
    /// The carry has to be set in the same block: with a label between them, something else
    /// may have run first.
    /// </summary>
    [Fact]
    public void AClcInAnotherBlockDoesNotDecideTheXce()
    {
        var state = StateAt(".proc p: emu -> e?, a?, i? {\n    clc\n@here:\n    xce\n    nop\n    rts\n}\n", "nop");

        Assert.Equal(ProcessorMode.Unknown, state.Processor.E);
    }

    /// <summary>
    /// `plp` restores the widths `php` saved, across a call and a label, because the saved
    /// status travels with the analysis stack rather than being paired with its push.
    /// </summary>
    [Fact]
    public void APlpRestoresWhatAPhpSavedAcrossACallAndALabel()
    {
        var state = StateAt("""
            .proc helper: a16 {
                rts
            }

            .proc p: a8, i8 {
                php
                rep #$20
                jsr helper
            @after:
                plp
                nop
                rts
            }
            """, "nop");

        Assert.Equal(Width.Eight, state.Processor.A);
        Assert.Equal(0, state.Stack?.Depth);
    }

    /// <summary>
    /// A `plp` with no status saved by a `php` in the routine makes the widths unknown, until
    /// a `.state` declares them again.
    /// </summary>
    [Fact]
    public void APlpOfAStatusSavedElsewhereNeedsAState()
    {
        const string Text = """
            .segment BSS
            .data saved_p: .byte
            .segment CODE

            .proc p: a8, i8 {
                lda saved_p
                pha
                plp
                lda #1
                .state a8, i8
                lda #2
                rts
            }
            """;

        Assert.Equal(["main.nt65:9: `lda #` needs the width of A, and it is not known here, because `plp` pulls a status "
            + "that no `php` in this routine pushed: an `.ensure` after it sets it"], Problems(Text));
    }

    /// <summary>
    /// Once a routine pulls more than it pushed, reaching into what its caller pushed, the
    /// analysis no longer knows where the stack's base is. It goes on tracking later pushes
    /// relative to that unknown base.
    /// </summary>
    [Fact]
    public void PullingMoreThanWasPushedForgetsTheBase()
    {
        var state = StateAt(".proc p: a8, i8 {\n    php\n    pla\n    pla\n    nop\n    rts\n}\n", "nop");

        Assert.Equal(AnalysisStack.Unanchored, state.Stack);
    }

    /// <summary>
    /// A push of a register whose width is not known moves the stack by an unknown amount, so
    /// the stack is no longer tracked.
    /// </summary>
    [Fact]
    public void APushOfUnknownWidthForgetsTheStack()
    {
        var state = StateAt(".proc p: a? {\n    pha\n    nop\n    .state a8\n    rts\n}\n", "nop");

        Assert.Null(state.Stack);
    }

    /// <summary>
    /// An indirect call returns with the merge of what the routines its `.next` names return
    /// with, and must meet the entry state of every one of them.
    /// </summary>
    [Fact]
    public void AnIndirectCallReturnsWithTheMergeOfItsRoutinesExits()
    {
        const string Text = """
            .proc wide: a8 -> a16 {
                rep #$20
                rts
            }

            .proc narrow: a8, i16 -> a8, i16 {
                rts
            }

            .segment RODATA
            .data table: .addr wide, narrow
            .segment CODE

            .proc p: a8, i8 {
                jsr (table,x)
                .next wide, narrow
                nop
                .state a8, i8
                rts
            }
            """;
        Assert.Equal(["main.nt65:15: `jsr narrow` needs `i16`, but X and Y are 8-bit here"], Problems(Text));
        Assert.Equal(Width.Unknown, StateAt(Text, "nop").Processor.A);
    }

    /// <summary>
    /// A conditional branch to a routine is a tail call when taken, and continues when not.
    /// </summary>
    [Fact]
    public void ABranchToARoutineIsATailCallOrFallsThrough()
    {
        const string Text = """
            .proc wide: a16 {
                rts
            }

            .proc p: a8, i8 {
                lda #0
                beq wide
                lda #1
                rts
            }
            """;

        Assert.Equal(
            [
                "main.nt65:7: `beq wide` is a tail call: `p` declares it returns with `a8`, but A is 16-bit when `wide` returns",
                "main.nt65:7: `beq wide` needs `a16`, but A is 8-bit here",
            ],
            Problems(Text));
    }

    /// <summary>
    /// A jump to the routine's own entry is a tail call like any other: the state at the jump
    /// must match the declared entry state, rather than being silently merged into it.
    /// </summary>
    [Fact]
    public void AJumpToTheRoutinesOwnEntryIsChecked()
    {
        Assert.Equal(["main.nt65:3: `jmp p` needs `a8`, but A is 16-bit here"],
            Problems(".proc p: a8, i8 {\n    rep #$20\n    jmp p\n}\n"));
    }

    /// <summary>
    /// A macro's body is analyzed as the code it expands to, so what is wrong with it is
    /// wrong only for this call, and is reported there with the body's line beside it.
    /// </summary>
    [Fact]
    public void AnUnknownWidthInAnExpansionIsReportedAtTheCall()
    {
        const string Text = """
            .macro load(value) {
                lda #value
            }

            .proc p: a? {
                load!(1)
                sep #$20
                rts
            }
            """;
        var analysis = Analysis.Program(Analysis.Fragment, ("main.nt65", ".module main\n.cpu 65816\n.segment CODE\n" + Text));

        var problem = Assert.Single(analysis.Diagnostics);
        Assert.Equal(9, problem.Span.Line);
        Assert.Equal("`lda #` needs the width of A, and it is not known here, because `p` declares `a?` at entry: "
            + "an `.ensure` sets it", problem.Message);
        Assert.Equal(5, Assert.Single(problem.Related).Span.Line);
    }

    /// <summary>
    /// Nothing is reported where two paths disagree, only where the disagreement is used. A
    /// constant `rep` or `sep` makes the width known again, no matter what state arrived.
    /// </summary>
    [Fact]
    public void AMergeReportsNothing()
    {
        const string Text = """
            .proc p: a16, i8 {
                lda #1
                beq @done
                sep #$20
            @done:
                rep #$20
                rts
            }
            """;

        Assert.Empty(Problems(Text));
    }

    /// <summary>
    /// A label that a `.state` declares is an entry point. When a path in the routine also
    /// reaches it, the state along that path is checked against the declaration.
    /// </summary>
    [Fact]
    public void AStateDeclaresALabelAndIsCheckedWhereItIsReached()
    {
        const string Text = """
            .proc p: a8, i8 {
                bra @entry
                rts
            @entry:
                .state a16
                lda #$1234
                sep #$20
                rts
            }
            """;

        Assert.Equal(["main.nt65:5: `.state a16` does not match: A is 8-bit here"], Problems(Text));
    }

    /// <summary>
    /// Measures how quickly the analysis reaches a fixed point, rather than assuming it. Blocks
    /// are taken in the order their bytes are written, and a block is walked again only when what
    /// reaches it changes. So a routine without loops reaches its fixed point in one walk per
    /// block, and a loop costs its blocks one more walk for each part of the state its back edge
    /// makes unknown.
    /// </summary>
    [Fact]
    public void TheAnalysisConvergesInAFewWalksPerBlock()
    {
        Assert.Equal(1, MaximumWalks(".proc p: a8, i8 {\n    rep #$20\n    lda #1\n    sep #$20\n    rts\n}\n"));

        // In a loop whose body does not change the state, the state along its back edge is what
        // the head already has, so the head is not walked again.
        Assert.Equal(1, MaximumWalks("""
            .proc p: a8, i8 {
                ldx #8
            @loop:
                dex
                bne @loop
                rts
            }
            """));

        // In a loop that changes a width, the back edge tells the loop head that A may be either
        // width, and the head is walked once more with that.
        Assert.Equal(2, MaximumWalks("""
            .proc p: a8, i8 {
            @loop:
                rep #$20
                bne @loop
                sep #$20
                rts
            }
            """));

        // With two nested loops, each pushing, the stack depth at each loop head becomes unknown
        // on the first round, and nothing is left to change after that.
        Assert.Equal(2, MaximumWalks("""
            .proc p: a8, i8 {
                php
            @outer:
                pha
            @inner:
                pha
                dex
                bne @inner
                dey
                bne @outer
                plp
                .state a8, i8
                rts
            }
            """));

        // A bound of "at most two walks per block" does not hold once one part of the state
        // becomes unknown only because another did. The first round loses the stack. With no
        // saved status to find, the second round's `plp` loses the index width too, which the
        // loop head learns only on a third walk. Each part can change at most twice, so the
        // bound is one walk more than the number of parts, not two.
        Assert.Equal(3, MaximumWalks("""
            .proc p: a8, i8 {
                php
            @loop:
                plp
                rep #$20
                php
                bne @loop
                plp
                .state a8, i8
                rts
            }
            """));
    }

    [Theory]
    // The idioms that load D and B from constants.
    [InlineData(".proc p: a16, i8 {\n    lda #$2100\n    tcd\n    nop\n    .state dp?\n    rts\n}\n", "a16, i8, native, dp = $2100")]
    [InlineData(".proc p: a8, i8 {\n    pea $2100\n    pld\n    nop\n    .state dp?\n    rts\n}\n", "a8, i8, native, dp = $2100")]
    [InlineData(".proc p: a8, i8 {\n    lda #$7e\n    pha\n    plb\n    nop\n    .state dbr?\n    rts\n}\n", "a8, i8, native, dbr = $7e")]

    // With A 8 bits wide, `lda #c` then `tcd` transfers a high byte that is not known.
    [InlineData(".proc p: a8, i8 {\n    lda #$21\n    tcd\n    nop\n    .state dp?\n    rts\n}\n", "a8, i8, native, dp?")]

    // Any instruction between the load and the transfer loses the known value.
    [InlineData(".proc p: a16, i8 {\n    lda #$2100\n    tay\n    tcd\n    nop\n    .state dp?\n    rts\n}\n", "a16, i8, native, dp?")]

    // Pulling a value that was not pushed as a constant loads an unknown value.
    [InlineData(".proc p: a8, i8 {\n    pha\n    plb\n    nop\n    .state dbr?\n    rts\n}\n", "a8, i8, native, dbr?")]

    // Two paths that pushed different constants agree on the depth, not on the value.
    [InlineData(".proc p: a8, i8 {\n    beq @a\n    pea 1\n    bra @b\n@a:\n    pea 2\n@b:\n    pld\n    nop\n    .state dp?\n    rts\n}\n", "a8, i8, native, dp?")]
    public void DAndBAreLoadedOnlyByTheIdiomsThatLoadThemFromConstants(string text, string state)
    {
        Assert.Equal(state, StateAt(text, "nop").Processor.ToString());
    }

    /// <summary>
    /// An <c>operand</c> argument without braces is an expression, and the whole of
    /// it is what the instruction is given: <c>pea slot</c> with <c>slot</c> bound to
    /// <c>BASE + 2</c> pushes that address, not the base it starts from.
    /// </summary>
    [Fact]
    public void AnUnbracedOperandArgumentIsTheWholeExpression()
    {
        var state = StateAt("BASE = $2000\n.macro pushed(slot: operand) {\n    pea slot\n}\n"
            + ".proc main: a8, i8 {\n    pushed!(BASE + 2)\n    pld\n    nop\n    .state dp?\n    rts\n}\n", "nop");

        Assert.Equal("a8, i8, native, dp = $2002", state.Processor.ToString());
    }

    /// <summary>
    /// A label that a `.state` declares starts from what its routine declares about D and B:
    /// in a routine that declares nothing about them the label's `.state` need not either, but
    /// in a routine that declares them the label's `.state` must as well.
    /// </summary>
    [Fact]
    public void ADeclaredLabelStartsFromWhatTheRoutineDeclaresOfDAndB()
    {
        Assert.Empty(Problems(".proc p: a8, i8 {\n    rts\n@entry:\n    .state a8, i8, native\n    rts\n}\n"));
        Assert.Contains("main.nt65:5: `rts`: `p` declares it returns with `dp = $2100`, but D is not known here",
            Problems(".proc p: dp = $2100 {\n    rts\n@entry:\n    .state a8, i8, native\n    rts\n}\n"));
    }

    private static int MaximumWalks(string text)
    {
        var analysis = Analysis.Program(Analysis.Fragment, ("main.nt65", ".module main\n.cpu 65816\n.segment CODE\n" + text));
        return Assert.Single(analysis.States).MaximumWalks;
    }

    private static IReadOnlyList<string> Problems(string text) =>
        Analysis.Program(Analysis.Fragment, ("main.nt65", ".module main\n.cpu 65816\n.segment CODE\n" + text)).Problems()
            .Select(problem => Renumbered(problem))
            .ToList();

    /// <summary>
    /// Returns the problem with its line number counted from the start of the test's own text,
    /// not counting the <c>.module</c>, <c>.cpu</c> and <c>.segment</c> lines every test is
    /// compiled after.
    /// </summary>
    private static string Renumbered(string problem)
    {
        var parts = problem.Split(':', 3);
        return $"{parts[0]}:{int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) - 3}:{parts[2]}";
    }

    private static FlowState StateAt(string text, string line) =>
        StateAt(Analysis.Program(Analysis.Fragment, ("main.nt65", ".module main\n.cpu 65816\n.segment CODE\n" + text)), line);

    /// <summary>
    /// Returns the state reaching the first statement whose text is <paramref name="line"/>.
    /// </summary>
    private static FlowState StateAt(ProgramAnalysis analysis, string line)
    {
        var model = analysis.File("main.nt65");
        var statement = model.Tree.Root.DescendantNodes()
            .OfType<LineSyntax>()
            .Select(node => node.Statement)
            .First(statement => statement.GetText().Trim() == line);
        var state = analysis.StatesFor("main.nt65")?.Before(statement);
        Assert.NotNull(state);
        return state;
    }
}
