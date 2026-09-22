using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.Tests.Semantics;

/// <summary>
/// Placement: which modules share a translation unit, and what a routine may run into there.
/// Within a unit nt65 lays out every byte, so a <c>.next</c> into the routine after a
/// <c>.place</c>, or into another module's, is checked against that layout; the fixtures hold
/// the programs, and these the cases that turn on what a build or an edit leaves in between.
/// </summary>
public sealed class PlacementTests
{
    private const string Main = """
        .module main

        .segment CODE
        .export .proc first {
            lda #1
            .next second
        }

        .place platform

        .export .proc second {
            rts
        }
        """;

    private static readonly ProjectSettings Project = ProjectSettings.None with { Cpu = Processor.Cpu.Mos6502 };

    /// <summary>
    /// A placed module whose items are all under an <c>.if</c> the build leaves out places
    /// nothing, so the routine before its <c>.place</c> runs into the one after it; one the
    /// build takes stands between them, and the same <c>.next</c> is then wrong.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void ARoutineRunsPastAPlaceOnlyWhereNothingIsPlacedThere(int kim, bool between)
    {
        var analysis = Analyze(Main, Platform(kim));

        var problems = analysis.Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => $"{d.Span.File}:{d.Span.Line}: {d.Id}");
        Assert.Equal(between ? ["main.nt65:6: next-routine-not-adjacent"] : [], problems);
    }

    /// <summary>
    /// What a routine runs into across a <c>.place</c> is the translation unit's to say, so an
    /// edit to the placed module alone is enough to change it, and the answer after the edit is
    /// the one analyzing the program from nothing gives.
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
        var after = Compiler.Analyze(trees, Project, before);
        var fresh = Analyze(Main, Platform(1));

        Assert.Equal(1, after.Reanalyzed);
        Assert.Equal(fresh.Diagnostics.Select(d => d.ToString()), after.Diagnostics.Select(d => d.ToString()));
        Assert.Contains(after.Diagnostics, d => d.Id == "next-routine-not-adjacent");
    }

    /// <summary>
    /// A module another places has no output of its own, and its private names are written with
    /// its module in front, so that one output can hold two modules' names that are spelled
    /// alike in their sources.
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
