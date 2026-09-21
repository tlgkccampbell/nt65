using Norristown.Emit;
using Norristown.Syntax;
using Norristown.Tests.Fixtures;

namespace Norristown.Tests.Emit;

/// <summary>
/// What the editor shows beside a source, and what <c>nt65 build --stdout</c> writes, against
/// what a build writes: they are one text, or the view is showing something that is not the
/// program.
/// </summary>
public sealed class OutputPreviewTests
{
    /// <summary>
    /// What the view says each source line became is what the map beside the output records:
    /// two readings of one thing, and an editor and a debugger have to agree about a line.
    /// Every fixture's output is checked against the view by the fixture runner itself, which
    /// has the program already read.
    /// </summary>
    [Fact]
    public void TheLinesShownAreTheLinesTheMapRecords()
    {
        var fixture = FixtureCase.Load(Repo.Path("tests", "fixtures", "macros"))[0];
        var analysis = Compiler.Analyze(
            [.. fixture.Sources.Select(SyntaxTree.Parse)], fixture.Project, fixture.BinaryLength);
        var output = Assert.Single(
            Compiler.Emit(analysis, fixture.Project).Outputs, o => o.Kind == OutputKind.Ca65);
        var preview = OutputPreview.Of(analysis, fixture.Project, output.Source);
        Assert.NotNull(preview);

        var map = LineMap.For(output);
        Assert.NotNull(map);
        var read = LineMap.Read(map.Text, out var problem);
        Assert.Null(problem);
        Assert.NotNull(read);
        for (var i = 0; i < preview.SourceLines.Count; i++)
        {
            var mapped = read.Lines.TryGetValue(i + 1, out var line) ? line.Line : (int?)null;
            Assert.Equal(preview.SourceLines[i] == 0 ? null : preview.SourceLines[i], mapped);
        }
        Assert.Contains(preview.SourceLines, source => source != 0);
    }

    /// <summary>
    /// A file with errors shows what could be written, under a first line saying that it is
    /// incomplete and why: the answer, then where the first of it is, then how many there are.
    /// </summary>
    [Fact]
    public void AFileWithErrorsIsShownUnderANoteSayingWhy()
    {
        var analysis = Compiler.Analyze(
            [new SourceFile("main.nt65", ".module main\n.segment CODE\n.export .proc main {\n    lda nowhere\n    rts\n}\n")],
            Norristown.Project.ProjectSettings.None);
        var preview = OutputPreview.Of(analysis, Norristown.Project.ProjectSettings.None, "main.nt65");
        Assert.NotNull(preview);
        Assert.Equal(
            "nt65: incomplete, because the program is wrong. line 4: `nowhere` is not declared",
            preview.Note);
        Assert.StartsWith($"; {preview.Note}\n", preview.Text, StringComparison.Ordinal);

        // The note is a line of the output that no line of the source wrote, so everything
        // after it still points at the line it came from.
        Assert.Equal(0, preview.SourceLines[0]);
        Assert.Contains(4, preview.SourceLines);
        Assert.Equal(preview.Text.Split('\n').Length - 1, preview.SourceLines.Count);
    }

    /// <summary>A file the program does not hold has no output to show.</summary>
    [Fact]
    public void AFileTheProgramDoesNotHoldHasNoOutput()
    {
        var analysis = Compiler.Analyze(
            [new SourceFile("main.nt65", ".module main\n")], Norristown.Project.ProjectSettings.None);
        Assert.Null(OutputPreview.Of(analysis, Norristown.Project.ProjectSettings.None, "other.nt65"));
    }
}
