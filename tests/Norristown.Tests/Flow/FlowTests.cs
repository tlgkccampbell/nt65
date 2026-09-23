using Norristown.Flow;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Where control goes inside a routine: its blocks, the edges between them, and the
/// diagnostics for a label that nothing reaches and for data that an instruction runs into.
/// On the 6502 and the 65C02 no instruction depends on the processor state, so no annotation
/// is required, and both diagnostics are warnings rather than errors.
/// </summary>
public sealed class FlowTests
{
    /// <summary>
    /// A branch cuts the run of statements in two, and the block after it is reached both by
    /// the branch and by falling past it.
    /// </summary>
    [Fact]
    public void ABranchCutsARoutineIntoBlocks()
    {
        var region = Region("""
            .proc p {
                lda flag
                beq @skip
                inc flag
            @skip:
                rts
            }

            .data flag: .byte 0
            """);

        Assert.True(region.IsEntered);
        Assert.Equal(["p", null, "@skip"], region.Blocks.Select(b => b.Label?.DisplayName));
        Assert.Equal([new FlowEdge(1, EdgeKind.FallThrough), new FlowEdge(2, EdgeKind.Taken)],
            region.Blocks[0].Successors);
        Assert.True(region.Blocks[2].IsFallenInto);
        Assert.All(region.Blocks, block => Assert.True(block.IsReached));
    }

    /// <summary>
    /// Flow does not fall through a <c>jmp</c>, so the statement after it is reached only if
    /// something names its label.
    /// </summary>
    [Fact]
    public void AJumpDoesNotRunIntoWhatFollowsIt()
    {
        var region = Region(".proc p {\n    jmp @out\n@skip:\n    nop\n@out:\n    rts\n}\n");

        Assert.False(Block(region, "@skip").IsFallenInto);
        Assert.Equal([new FlowEdge(Block(region, "@out").Index, EdgeKind.Taken)],
            region.Blocks[0].Successors);
    }

    /// <summary>
    /// The operand of an indirect jump does not say where the jump goes. A <c>.next</c> lists
    /// the targets, and each target it names gets an edge.
    /// </summary>
    [Fact]
    public void ANextGivesAnIndirectJumpItsEdges()
    {
        var region = Region("""
            .proc dispatch {
                lda cmd
                jmp (@table)
                .next @move, @fire

            @table: .addr @move, @fire

            @move:
                lda #1
                rts
            @fire:
                lda #2
                rts
            }

            .data cmd: .byte 0
            """);

        Assert.True(IsDeclared(region, "@move"));
        Assert.True(IsDeclared(region, "@fire"));
    }

    /// <summary>
    /// Every item of the table is a code label, so <c>.next table</c> means the same as a
    /// <c>.next</c> that lists those labels one by one.
    /// </summary>
    [Fact]
    public void ATableOfLabelsStandsForEveryOneOfThem()
    {
        var region = Region("""
            .proc dispatch {
                jmp (table)
                .next table

            .data table: .addr @move, @fire

            @move:  rts
            @fire:  rts
            }
            """);

        Assert.True(IsDeclared(region, "@move"));
        Assert.True(IsDeclared(region, "@fire"));
    }

    /// <summary>
    /// A <c>.next</c> that names a <c>.list</c> names every label in it, as naming a list does
    /// anywhere else.
    /// </summary>
    [Fact]
    public void AListStandsForEveryLabelInIt()
    {
        var region = Region("""
            .proc dispatch {
                jmp (ptr)
                .next handlers

            move:   rts
            fire:   rts
            }

            .list handlers {
                dispatch::move, dispatch::fire
            }

            .data ptr: .addr 0
            """);

        Assert.True(IsDeclared(region, "move"));
        Assert.True(IsDeclared(region, "fire"));
    }

    /// <summary>
    /// A <c>.next</c> on a call lists the routines it reaches, and the call still returns to
    /// the statement after it.
    /// </summary>
    [Fact]
    public void ACallWithANextStillReturns()
    {
        var region = Region("""
            .cpu 65c02

            .proc p {
                jsr (@table,x)
                .next ?
            @after:
                rts

            @table: .addr @after
            }
            """);

        Assert.True(Block(region, "@after").IsFallenInto);
    }

    /// <summary>
    /// An annotation after a macro call applies to the last statement the macro expands to.
    /// No special case is needed: the path is read in the order the bytes were emitted, so
    /// that statement is the one just before the annotation.
    /// </summary>
    [Fact]
    public void ANextUnderACallIsAboutTheLastStatementItExpandsTo()
    {
        var region = Region("""
            .macro go() {
                nop
                jmp (ptr)
            }

            .proc p {
                go!()
                .next @out
            @out:
                rts
            }

            .data ptr: .addr 0
            """);

        Assert.True(IsDeclared(region, "@out"));
    }

    /// <summary><c>.next ?</c> ends the path: nothing is claimed about where flow goes.</summary>
    [Fact]
    public void NextQuestionEndsThePath()
    {
        var region = Region(".proc p {\n    jmp (ptr)\n    .next ?\n@after:\n    rts\n}\n\n.data ptr: .addr 0\n");

        Assert.Empty(region.Blocks[0].Successors);
    }

    /// <summary>
    /// A label that nothing runs into and nothing names is reported as never reached. The
    /// check trusts only what the source says, so a programmer who knows of a path nt65
    /// cannot see has to write it down, for example with a <c>.next</c> that names the label.
    /// </summary>
    [Fact]
    public void ALabelNothingReachesIsReported()
    {
        var problems = Problems(".proc p {\n    rts\n@gone:\n    nop\n    rts\n}\n");

        Assert.Equal(
            ["main.nt65:5: `@gone` is never reached: no code falls into it and nothing refers to it"],
            problems);
    }

    /// <summary>
    /// Anything that names the label counts as reaching it, whether or not flow can be seen
    /// to get there: a branch, a <c>.next</c>, or taking its address in data.
    /// </summary>
    [Theory]
    [InlineData(".proc p {\n    beq @here\n    rts\n@here:\n    rts\n}\n")]
    [InlineData(".proc p {\n    jmp (ptr)\n    .next @here\n@here:\n    rts\n}\n\n.data ptr: .addr 0\n")]
    [InlineData(".proc p {\n    rts\nhere:\n    rts\n}\n\n.data table: .addr p::here\n.export table\n")]
    public void ALabelSomethingNamesIsNotReported(string text)
    {
        Assert.Empty(Problems(text));
    }

    /// <summary>
    /// <c>.next</c> and <c>.patch</c> name places in code, so naming a constant with either
    /// is reported.
    /// </summary>
    [Theory]
    [InlineData(".proc p {\n    jmp (ptr)\n    .next N\n}\n\nN = 5\n\n.data ptr: .addr 0\n", ".next")]
    [InlineData(".proc p {\n    sta $0400\n    .patch N\n    rts\n}\n\nN = 5\n", ".patch")]
    public void AnAnnotationThatNamesSomethingThatIsNotCodeIsReported(string text, string directive)
    {
        Assert.Contains($"`N` is a constant, and `{directive}` must name a code label, a routine, or a table of them",
            string.Join("\n", Problems(text)), StringComparison.Ordinal);
    }

    [Fact]
    public void DataTheInstructionAboveRunsIntoIsReported()
    {
        var problems = Problems(".proc p {\n    lda #1\n    .byte $2c\n    rts\n}\n");

        Assert.Equal(
            ["main.nt65:5: the instruction above falls through into this data: add a `.next` after the data saying where flow goes instead"],
            problems);
    }

    /// <summary>
    /// The <c>bit</c> skip trick: <c>$2c</c> is <c>bit abs</c>, which swallows the two bytes
    /// after it, so flow carries on past the instruction they spell.
    /// </summary>
    [Fact]
    public void TheBitSkipTrickIsAcceptedWithItsNext()
    {
        var region = Region("""
            .proc set {
                beq @set_two
            @set_one:
                lda #1
                .byte $2c
                .next @store
            @set_two:
                lda #2
            @store:
                sta value
                rts
            }

            .data value: .byte 0
            """);

        // The `.next` sends the path from the data straight to `@store`, so nothing runs into
        // `@set_two`, the instruction whose bytes `bit` swallows.
        Assert.True(IsDeclared(region, "@store"));
        Assert.False(Block(region, "@set_two").IsFallenInto);
    }

    /// <summary>
    /// Without its <c>.next</c>, the skip trick is reported as an instruction running into data.
    /// </summary>
    [Fact]
    public void TheBitSkipTrickWithoutItsNextIsReported()
    {
        var problems = Problems(
            ".proc set {\n    beq @two\n    lda #1\n    .byte $2c\n@two:\n    lda #2\n    rts\n}\n");

        Assert.Equal(
            ["main.nt65:6: the instruction above falls through into this data: add a `.next` after the data saying where flow goes instead"],
            problems);
    }

    /// <summary>A table after a <c>rts</c> is not run into: the return ended the path.</summary>
    [Fact]
    public void DataAfterAReturnIsNotReported()
    {
        Assert.Empty(Problems(".proc p {\n    lda @table\n    rts\n@table: .byte 1\n    .byte 2\n}\n"));
    }

    /// <summary>
    /// A call to a routine that never returns is where the path ends, on every CPU: what follows
    /// it is not run into, and a routine that ends with one does not run off its end.
    /// </summary>
    [Fact]
    public void ACallToARoutineThatNeverReturnsEndsThePath()
    {
        const string Text = """
            .proc halt: a8, noreturn {
                jmp halt
            }

            .proc p {
                jsr halt
                .byte 1
            }
            """;

        Assert.Empty(Problems(Text));
        var p = Analysis.Program(Analysis.Fragment, ("main.nt65", ".module main\n.segment CODE\n" + Text)).Flows.Single().Regions
            .Single(region => region.Routine.Name == "p");
        Assert.All(p.Blocks.Skip(1), block => Assert.False(block.IsFallenInto));
    }

    /// <summary>
    /// On the 6502, as on the 65816, an interrupt handler must leave by <c>rti</c> and must
    /// not be called, and a <c>noreturn</c> routine must not return.
    /// </summary>
    [Fact]
    public void OnThe6502InterruptHandlersAndRoutinesThatNeverReturnAreChecked()
    {
        var problems = Problems(
            ".proc irq: interrupt {\n    rts\n}\n.proc stop: a8, noreturn {\n    rts\n}\n.proc p {\n    jsr irq\n    rts\n}\n");

        Assert.Equal(
            [
                "main.nt65:4: `irq` is an interrupt handler and must return with `rti`, not `rts`",
                "main.nt65:7: `stop` is declared `noreturn`, but `rts` returns from it",
                "main.nt65:10: `irq` is an interrupt handler and cannot be called: it returns with `rti`, which would not return to the caller",
            ],
            problems);
    }

    /// <summary>
    /// Checks that a loop is counted only when nothing but the decrements just before its
    /// branch writes the counter, even when another write comes from the same macro line.
    /// Two expansions of one macro share a source position, so the decrement from the first
    /// expansion must not pass for the one from the second.
    /// </summary>
    [Theory]
    [InlineData("    step!()\n", true)]
    [InlineData("    step!()\n    step!()\n", true)]
    [InlineData("    step!()\n    nop\n    step!()\n", false)]
    public void AnotherExpansionOfTheDecrementStopsTheLoopBeingCounted(string body, bool counted)
    {
        var region = Region(
            ".macro step() {\n    dex\n}\n.proc p {\n    ldx #4\n@loop:\n" + body + "    bne @loop\n    rts\n}\n");

        Assert.Equal(counted, region.Cost.Most is not null);
    }

    /// <summary>
    /// The problems reported for <paramref name="text"/>, compiled after two lines that declare
    /// the module and select the code segment.
    /// </summary>
    private static IReadOnlyList<string> Problems(string text) =>
        Analysis.Program(Analysis.Fragment, ("main.nt65", ".module main\n.segment CODE\n" + text)).Problems();

    /// <summary>The one region of the one routine in <paramref name="text"/>.</summary>
    private static FlowRegion Region(string text)
    {
        var analysis = Analysis.Program(Analysis.Fragment, ("main.nt65", ".module main\n.segment CODE\n" + text));
        Assert.DoesNotContain(analysis.Problems(), problem => problem.Contains("error", StringComparison.Ordinal));
        return Assert.Single(analysis.Flows.Single().Regions);
    }

    private static BasicBlock Block(FlowRegion region, string label) =>
        region.Blocks.Single(block => block.Label?.DisplayName == label);

    /// <summary>
    /// Whether some block has a declared edge, one that comes from a <c>.next</c>, to the block
    /// at <paramref name="label"/>.
    /// </summary>
    private static bool IsDeclared(FlowRegion region, string label) =>
        region.Blocks.SelectMany(block => block.Successors)
            .Any(edge => edge.To == Block(region, label).Index && edge.Kind == EdgeKind.Declared);
}
