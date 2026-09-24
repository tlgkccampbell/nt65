namespace Norristown.Tests.Semantics;

/// <summary>
/// Checks <c>.assert</c> and <c>.error</c>. nt65 answers what it can itself, and leaves the rest
/// for ca65 and ld65 to answer once the addresses are known.
/// </summary>
public sealed class AssertionTests
{
    /// <summary>An assertion about something nt65 knows is answered by nt65.</summary>
    [Fact]
    public void AnAssertionAboutAConstantIsCheckedHere()
    {
        var program = Analysis.Program(("main.nt65", """
            .module main
            .struct Point {
            x:  .word
            y:  .word
            }
                .assert .sizeof(Point) == 4, "Point must be 4 bytes"
                .assert .sizeof(Point) == 8, "Point must be 8 bytes"
            """));

        Assert.Equal(["main.nt65:7: Point must be 8 bytes"], program.Problems());
    }

    /// <summary>
    /// A failed assertion is always an error. ca65's levels choose when a check runs, which
    /// nt65 decides for itself, so a level in the source is reported.
    /// </summary>
    [Fact]
    public void AnAssertionTakesNoLevel()
    {
        var program = Analysis.Program(("main.nt65", ".module main\n    .assert 0, warning, \"only a warning\"\n"));

        Assert.Equal(
            ["main.nt65:2: only a warning",
             "main.nt65:2: nt65's `.assert` takes no level: remove `warning`, since a failed assertion is always an "
                + "error"],
            program.Problems());
    }

    /// <summary>An assertion with no message still reports which line failed.</summary>
    [Fact]
    public void AnAssertionWithNoMessageStillReportsTheLine()
    {
        var program = Analysis.Program(("main.nt65", ".module main\n    .assert 1 == 2\n"));

        Assert.Equal(["main.nt65:2: this assertion does not hold"], program.Problems());
    }

    /// <summary>
    /// An assertion nt65 answered does not reach ca65. An assertion about an address, which only
    /// the linker can decide, is written out for ld65.
    /// </summary>
    [Fact]
    public void WhatNt65CannotAnswerIsPassedOn()
    {
        var main = Analysis.Outputs(("main.nt65", """
            .module main
            .segment CODE
                .assert 4 == 4, "checked here"
            .proc irq {
                rts
            }
                .assert irq >= $8000, "irq must be in ROM"
            """))["main.s"];

        Assert.DoesNotContain("checked here", main);
        Assert.Contains(".assert irq >= $8000, lderror, \"irq must be in ROM\"", main);
    }

    /// <summary>
    /// An <c>.error</c> the build reaches is reported with its own message, and nothing is written
    /// for ca65.
    /// </summary>
    [Fact]
    public void AnErrorTheBuildReachesIsReported()
    {
        var compilation = Compiler.Compile(
            [new SourceFile("main.nt65", ".module main\n    .error \"unsupported configuration\"\n")]);

        Assert.Equal(["unsupported configuration"], compilation.Diagnostics.Select(d => d.Message));
        Assert.Empty(compilation.Outputs);
    }

    /// <summary>An <c>.error</c> in a branch the build leaves out is not reached.</summary>
    [Fact]
    public void AnErrorInABranchThatIsNotTakenIsNotReported()
    {
        var program = Analysis.Program(("main.nt65", """
            .module main
            .if 0 {
                .error "unsupported configuration"
            }
            """));

        Assert.Empty(program.Problems());
    }
}
