using Norristown.Flow;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Where control goes inside a routine: its blocks, the edges between them, and what nt65
/// says about a label nothing reaches and about data an instruction runs into. On the 6502
/// and the 65C02 nothing consumes processor state, so no annotation is required and both
/// are warnings rather than errors.
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

    /// <summary>A jump leaves, so what follows it is reached only by being named.</summary>
    [Fact]
    public void AJumpDoesNotRunIntoWhatFollowsIt()
    {
        var region = Region(".proc p {\n    jmp @out\n@skip:\n    nop\n@out:\n    rts\n}\n");

        Assert.False(Block(region, "@skip").IsFallenInto);
        Assert.Equal([new FlowEdge(Block(region, "@out").Index, EdgeKind.Taken)],
            region.Blocks[0].Successors);
    }

    /// <summary>
    /// An indirect jump goes where the operand does not say. A <c>.next</c> answers that,
    /// and each named target gets an edge.
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
    /// Every item of the table is a code label, so <c>.next table</c> says what listing
    /// them one by one says.
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

    /// <summary>A list stands for its items here as it does anywhere else.</summary>
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
    /// An annotation under a macro call is about the last statement of its expansion, which
    /// falls out of reading the path in the order the bytes were written.
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
    /// A label nothing runs into and nothing names is dead. The check is complete for what
    /// is written, which is what pushes the programmer to write the label down.
    /// </summary>
    [Fact]
    public void ALabelNothingReachesIsReported()
    {
        var problems = Problems(".proc p {\n    rts\n@gone:\n    nop\n    rts\n}\n");

        Assert.Equal(
            ["main.nt65:5: `@gone` is never reached: nothing runs into it and nothing names it"],
            problems);
    }

    /// <summary>
    /// Anything that names the label answers the question, whether or not flow can be seen
    /// to get there: a branch, an annotation, an address taken.
    /// </summary>
    [Theory]
    [InlineData(".proc p {\n    beq @here\n    rts\n@here:\n    rts\n}\n")]
    [InlineData(".proc p {\n    jmp (ptr)\n    .next @here\n@here:\n    rts\n}\n\n.data ptr: .addr 0\n")]
    [InlineData(".proc p {\n    rts\nhere:\n    rts\n}\n\n.data table: .addr p::here\n.export table\n")]
    public void ALabelSomethingNamesIsNotReported(string text)
    {
        Assert.Empty(Problems(text));
    }

    /// <summary>An annotation names somewhere code is, so a constant is no target for one.</summary>
    [Theory]
    [InlineData(".proc p {\n    jmp (ptr)\n    .next N\n}\n\nN = 5\n\n.data ptr: .addr 0\n", ".next")]
    [InlineData(".proc p {\n    sta $0400\n    .patch N\n    rts\n}\n\nN = 5\n", ".patch")]
    public void AnAnnotationThatNamesSomethingThatIsNotCodeIsReported(string text, string directive)
    {
        Assert.Contains($"`N` is a constant, and `{directive}` names somewhere code is",
            string.Join("\n", Problems(text)), StringComparison.Ordinal);
    }

    [Fact]
    public void DataTheInstructionAboveRunsIntoIsReported()
    {
        var problems = Problems(".proc p {\n    lda #1\n    .byte $2c\n    rts\n}\n");

        Assert.Equal(
            ["main.nt65:5: the instruction above runs into this data. `.next` on it says where flow goes instead"],
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

        // The `.next` is what redirects the path: `@store` follows the data, and nothing
        // runs into the instruction between them.
        Assert.True(IsDeclared(region, "@store"));
        Assert.False(Block(region, "@set_two").IsFallenInto);
    }

    /// <summary>With its <c>.next</c> the skip trick is accepted, and without it is not.</summary>
    [Fact]
    public void TheBitSkipTrickWithoutItsNextIsReported()
    {
        var problems = Problems(
            ".proc set {\n    beq @two\n    lda #1\n    .byte $2c\n@two:\n    lda #2\n    rts\n}\n");

        Assert.Equal(
            ["main.nt65:6: the instruction above runs into this data. `.next` on it says where flow goes instead"],
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
        var p = Analysis.Program(("main.nt65", ".module main\n.segment CODE\n" + Text)).Flows.Single().Regions
            .Single(region => region.Routine.Name == "p");
        Assert.All(p.Blocks.Skip(1), block => Assert.False(block.IsFallenInto));
    }

    /// <summary>
    /// On the 6502, as on the 65816, an interrupt handler leaves by <c>rti</c> and is never
    /// called, and a routine that never returns does not return.
    /// </summary>
    [Fact]
    public void OnThe6502InterruptHandlersAndRoutinesThatNeverReturnAreChecked()
    {
        var problems = Problems(
            ".proc irq: interrupt {\n    rts\n}\n.proc stop: a8, noreturn {\n    rts\n}\n.proc p {\n    jsr irq\n    rts\n}\n");

        Assert.Equal(
            [
                "main.nt65:4: `irq` is an interrupt handler, and leaves by `rti` rather than `rts`",
                "main.nt65:7: `stop` never returns, as its `noreturn` says, and `rts` returns",
                "main.nt65:10: `irq` is an interrupt handler, which the processor enters and `rti` leaves: a call to it would not come back",
            ],
            problems);
    }

    /// <summary>What is wrong with <paramref name="text"/>, placed in the code segment on a line before it.</summary>
    private static IReadOnlyList<string> Problems(string text) =>
        Analysis.Program(("main.nt65", ".module main\n.segment CODE\n" + text)).Problems();

    /// <summary>The one region of the one routine in <paramref name="text"/>.</summary>
    private static FlowRegion Region(string text)
    {
        var analysis = Analysis.Program(("main.nt65", ".module main\n.segment CODE\n" + text));
        Assert.DoesNotContain(analysis.Problems(), problem => problem.Contains("error", StringComparison.Ordinal));
        return Assert.Single(analysis.Flows.Single().Regions);
    }

    private static BasicBlock Block(FlowRegion region, string label) =>
        region.Blocks.Single(block => block.Label?.DisplayName == label);

    /// <summary>Whether a <c>.next</c> is what gives the block at <paramref name="label"/> an edge.</summary>
    private static bool IsDeclared(FlowRegion region, string label) =>
        region.Blocks.SelectMany(block => block.Successors)
            .Any(edge => edge.To == Block(region, label).Index && edge.Kind == EdgeKind.Declared);
}
