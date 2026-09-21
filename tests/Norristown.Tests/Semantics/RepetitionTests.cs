namespace Norristown.Tests.Semantics;

/// <summary>
/// <c>.repeat</c> and <c>.each</c>: how many turns a body stands for, what its name is worth
/// on each of them, and what is written out.
/// </summary>
public sealed class RepetitionTests
{
    /// <summary>
    /// The example: the index counts from zero, so this is the eight single bits. Every turn
    /// came out as the same line in terms of the index, so the output says it once, with ca65's
    /// own counter, which counts from zero too.
    /// </summary>
    [Fact]
    public void RepeatCountsFromZero()
    {
        var main = Output(".data bits: .byte[] {\n.repeat 8, i {\n    1 << i\n}\n}\n");

        Assert.Contains(".repeat 8, i\n", main);
        Assert.Equal([".byte 1 << i"], Lines(main, ".byte"));
        Assert.Contains(".endrepeat\n", main);
    }

    /// <summary>A repetition inside another sees both names, and each counts with its own.</summary>
    [Fact]
    public void RepetitionsNest()
    {
        var main = Output(".data grid: .byte[] {\n.repeat 3, row {\n.repeat 3, col {\n    row * 3 + col\n}\n}\n}\n");

        Assert.Contains(".repeat 3, row\n", main);
        Assert.Contains(".repeat 3, col\n", main);
        Assert.Equal([".byte (row * 3) + col"], Lines(main, ".byte"));
    }

    /// <summary>
    /// The example: an RTS dispatch table. The items are labels, so the name stands for the
    /// label itself rather than for a number, which a label does not have.
    /// </summary>
    [Fact]
    public void EachWalksAListOfLabels()
    {
        var main = Output("""
            .list handlers {
                move, fire
            }

            .proc move {
                rts
            }
            .proc fire {
                rts
            }

            .data table: .addr[] {
                .each handlers, h {
                    h - 1
                }
            }
            """);

        Assert.Contains(".addr move - 1", main);
        Assert.Contains(".addr fire - 1", main);
    }

    /// <summary>A name bound to a label is that label, down to how wide an address it is.</summary>
    [Fact]
    public void ABoundLabelKeepsItsAddressSize()
    {
        var main = Output("""
            .list pointers {
                ptr
            }

            .segment ZEROPAGE
            .data ptr:    .word

            .segment CODE
            .proc main {
            .each pointers, p {
                lda p
            }
            }
            """);

        Assert.Contains("lda z:ptr", main);
    }

    /// <summary>An enum is walked in the order its members are written.</summary>
    [Fact]
    public void EachWalksAnEnum()
    {
        var main = Output(".enum Cmd {\nmove\nfire\nwait\n}\n\n.data values: .byte[] {\n.each Cmd, c {\n    c\n}\n}\n");

        Assert.Equal([".byte $00", ".byte $01", ".byte $02"],
            Lines(main, ".byte").Select(line => line.Split(';')[0].Trim()));
    }

    /// <summary>
    /// A turn decides how much room a line takes, not just what it says, so each one is
    /// sized on its own.
    /// </summary>
    [Fact]
    public void ATurnDecidesHowMuchRoomALineTakes()
    {
        var main = Output(".data room {\n.repeat 3, n {\n    .res n + 1\n}\n}\n");

        Assert.Equal(3, Lines(main, ".res").Count);
        Assert.Equal(["$00 + 1", "$01 + 1", "$02 + 1"],
            Lines(main, ".res").Select(line => line[".res".Length..].Split(';')[0].Trim()));
    }

    /// <summary>A count nt65 cannot work out, and a walk over something that is neither.</summary>
    [Theory]
    [InlineData(".data here: .byte 0\n.data t: .byte[] {\n.repeat here, i {\n    i\n}\n}\n", "a `.repeat` count is a constant, and this is not one")]
    [InlineData("SIZE = -1\n.data t: .byte[] {\n.repeat SIZE, i {\n    i\n}\n}\n", "a `.repeat` count cannot be negative, and this one is -1")]
    [InlineData("SIZE = 4\n.data t: .byte[] {\n.each SIZE, h {\n    h\n}\n}\n", "`.each` walks a list or an enum, and this is neither")]
    public void WhatIsWrongWithARepetitionIsReported(string text, string message)
    {
        Assert.Contains(message, Analysis.Program(("main.nt65", ".module main\n.segment RODATA\n" + text)).Problems().Single());
    }

    /// <summary>
    /// A block whose opener is no repetition at all — a <c>.repeat</c> written after a label,
    /// which the parser refuses — stands for no turns, and says nothing further about itself.
    /// </summary>
    [Fact]
    public void ALineThatOpensNoRepetitionStandsForNoTurns()
    {
        var program = Analysis.Program(("main.nt65", ".module main\n.segment RODATA\nfoo: .repeat 3 {\n}\n"));

        Assert.Equal(["main.nt65:3: `.repeat` may not follow a label"], program.Problems());
    }

    /// <summary>
    /// What a repetition declares is its own on every turn, as a macro expansion's is, so each
    /// turn's label gets a name of its own in the output.
    /// </summary>
    [Fact]
    public void ANameDeclaredInsideARepetitionIsOneNamePerTurn()
    {
        var main = Output(".proc p {\n.repeat 2, i {\n@wait:\n    dex\n    bne @wait\n}\n    rts\n}\n");

        Assert.Contains("p__wait:", main);
        Assert.Contains("bne p__wait\n", main);
        Assert.Contains("p__wait_2:", main);
        Assert.Contains("bne p__wait_2\n", main);
    }

    /// <summary>
    /// A repetition whose turns came out as the same lines is said once, however many of them
    /// there are: the showcase is a table of the numbers themselves.
    /// </summary>
    [Fact]
    public void ARepetitionSaidOnceIsThreeLinesHoweverManyTurnsItRuns()
    {
        var main = Output(".data ramp: .byte[] {\n.repeat 256, i {\n    i\n}\n}\n");

        Assert.Contains(".repeat 256, i\n", main);
        Assert.Equal([".byte i"], Lines(main, ".byte"));
    }

    /// <summary>
    /// A repetition is unrolled first, and only what came out the same is said once: turns that
    /// differ in a decision nt65 made — here how much room each line takes — are all written out,
    /// because a ca65 <c>.repeat</c> could not say them.
    /// </summary>
    [Fact]
    public void TurnsThatCameOutDifferentlyAreAllWrittenOut()
    {
        var main = Output(".data room {\n.repeat 3, n {\n    .res n + 1\n}\n}\n");

        Assert.DoesNotContain(".repeat", main);
        Assert.DoesNotContain(".endrep", main);
    }

    /// <summary>Two turns read no better said once, so the threshold is three.</summary>
    [Fact]
    public void TwoTurnsAreWrittenOut()
    {
        var main = Output(".proc p {\n.repeat 2 {\n    nop\n}\n    rts\n}\n");

        Assert.DoesNotContain(".repeat", main);
        Assert.Equal(["nop", "nop"], Lines(main, "nop"));
    }

    /// <summary>The output for <paramref name="text"/>, which is placed in the code segment.</summary>
    private static string Output(string text) => Analysis.Outputs(("main.nt65", ".module main\n.segment CODE\n" + text))["main.s"];

    private static IReadOnlyList<string> Lines(string output, string directive) =>
        [.. output.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith(directive, StringComparison.Ordinal))];
}
