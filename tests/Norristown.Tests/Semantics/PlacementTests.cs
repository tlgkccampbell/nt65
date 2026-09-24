using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.Tests.Semantics;

/// <summary>
/// Checks placement, which decides which modules share a translation unit and what a routine may
/// run into there. Within a unit nt65 lays out every byte, so a <c>.fallthrough</c> into the
/// routine after a <c>.place</c>, or into another module's, is checked against that layout. The
/// fixtures hold whole programs. These tests hold the cases that depend on what a build or an
/// edit leaves in between.
/// </summary>
public sealed class PlacementTests
{
    private const string Main = """
        .module main

        .segment CODE
        .export .proc first {
            lda #1
            .fallthrough second
        }

        .place platform

        .export .proc second {
            rts
        }
        """;

    private static readonly ProjectSettings Project = ProjectSettings.None with { Cpu = Processor.Cpu.Mos6502 };

    /// <summary>
    /// A placed module whose items are all under an <c>.if</c> the build leaves out places
    /// nothing, so the routine before its <c>.place</c> runs into the one after it. A placed
    /// routine the build keeps stands between them, and the same <c>.fallthrough</c> is then wrong.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void ARoutineRunsPastAPlaceOnlyWhereNothingIsPlacedThere(int kim, bool between)
    {
        var analysis = Analyze(Main, Platform(kim));

        var problems = analysis.Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => $"{d.Span.File}:{d.Span.Line}: {d.Id}");
        Assert.Equal(between ? ["main.nt65:6: fallthrough-not-adjacent"] : [], problems);
    }

    /// <summary>
    /// What a routine runs into across a <c>.place</c> depends on the whole translation unit, so
    /// an edit to the placed module alone is enough to change it, and the answer after the edit
    /// is the one analyzing the program from scratch gives.
    /// </summary>
    [Fact]
    public void AnEditToAPlacedModuleChecksWhatRunsPastItAgain()
    {
        var before = Analyze(Main, Platform(0));
        Assert.DoesNotContain(before.Diagnostics, d => d.Severity == Severity.Error);

        var trees = before.Program.Files
            .Where(file => file.Tree != before.Defines)
            .Select(file => file.Tree.Path == "platform.nt65" ? SyntaxTree.Parse("platform.nt65", Platform(1)) : file.Tree)
            .ToList();
        var after = Compiler.Analyze(trees, Project, before, TestContext.Current.CancellationToken);
        var fresh = Analyze(Main, Platform(1));

        Assert.Equal(1, after.Reanalyzed);
        Assert.Equal(fresh.Diagnostics.Select(d => d.ToString()), after.Diagnostics.Select(d => d.ToString()));
        Assert.Contains(after.Diagnostics, d => d.Id == "fallthrough-not-adjacent");
    }

    /// <summary>
    /// A module that another module places has no output of its own, and its private names are
    /// prefixed with its module's name, so that one output can hold two modules' names that are
    /// spelled alike in their sources.
    /// </summary>
    [Fact]
    public void APlacedModulesNamesAreItsOwnInTheOutputItShares()
    {
        var outputs = Compiler.Compile(
            [new SourceFile("main.nt65", Main.Replace("rts\n}", "rts\n}\nloop = 1", StringComparison.Ordinal)),
                new SourceFile("platform.nt65", ".module platform: placed\nloop = 2\n")],
            Project).Ca65.ToList();

        var output = Assert.Single(outputs);
        Assert.Equal("main.s", output.Path);
        Assert.Contains("\nloop = 1\n", output.Text, StringComparison.Ordinal);
        Assert.Contains("\nplatform__loop = 2\n", output.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(".import", output.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Across a <c>.place</c>, what a routine runs into is decided within the segment the placing
    /// file is in at that line: the routine before it runs into the placed module's first routine
    /// in that segment, and the placed module's last routine there runs into what the placing
    /// file writes next in it. What the placed module writes to other segments does not come
    /// between them, just as it does not in the single <c>.s</c> file from which ca65 lays out
    /// each segment in order.
    /// </summary>
    [Fact]
    public void AFallthroughAcrossAPlaceIsReadInThePlacingFilesSegment()
    {
        const string Placing = """
            .module main
            .segment CODE
            .export .proc before {
                lda #1
                .fallthrough part::enter
            }
            .place part
            .export .proc after {
                rts
            }
            """;
        const string Placed = """
            .module part: placed
            .segment CODE
            .export .proc enter {
                lda #2
                .fallthrough leave
            }
            .segment RODATA
            .data table: .byte 1, 2, 3
            .segment CODE
            .export .proc leave {
                lda #3
                .fallthrough main::after
            }
            """;

        var analysis = Analyze(Placing, Placed);

        Assert.DoesNotContain(analysis.Diagnostics, d => d.Severity == Severity.Error);
    }

    /// <summary>
    /// A routine runs only into what its own segment holds next, so a <c>.fallthrough</c> across
    /// a <c>.place</c> into a routine the placed module writes to another segment is an error that
    /// names both segments.
    /// </summary>
    [Fact]
    public void AFallthroughAcrossAPlaceIntoAnotherSegmentIsAnError()
    {
        const string Placing = """
            .module main
            .segment CODE
            .export .proc before {
                lda #1
                .fallthrough part::enter
            }
            .place part
            """;
        const string Placed = """
            .module part: placed
            .segment BANKED: abs
            .segment BANKED
            .export .proc enter {
                rts
            }
            """;

        var analysis = Analyze(Placing, Placed);

        var error = Assert.Single(analysis.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Equal("fallthrough-other-segment", error.Id);
        Assert.Equal(
            "`enter` is in segment \"BANKED\", and this routine ends in \"CODE\": a routine can only run into what "
                + "comes next in its own segment",
            error.Message);
    }

    /// <summary>
    /// Across translation units the linker decides the order of the bytes, so a <c>.fallthrough</c>
    /// into a module nothing places with this one is an error naming placement, while the same
    /// routine placed is accepted.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFallthroughIntoAnotherModuleNeedsOneTranslationUnit(bool placed)
    {
        var placing = """
            .module main
            .segment CODE
            .export .proc before {
                lda #1
                .fallthrough part::enter
            }
            """ + (placed ? "\n.place part\n" : "\n");
        var part = $$"""
            .module part{{(placed ? ": placed" : "")}}
            .segment CODE
            .export .proc enter {
                rts
            }
            """;

        var problems = Analyze(placing, part).Diagnostics
            .Where(d => d.Severity == Severity.Error)
            .Select(d => d.Id);

        Assert.Equal(placed ? [] : ["fallthrough-not-placed"], problems);
    }

    private static string Platform(int kim) => $$"""
        .module platform: placed

        .segment CODE

        .if {{kim}} {
            .export .proc check {
                rts
            }
        }
        """;

    private static ProgramAnalysis Analyze(string main, string platform) =>
        Compiler.Analyze([new SourceFile("main.nt65", main), new SourceFile("platform.nt65", platform)], Project);
}
