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
    /// Every fixture, built once: the preview of each of its files is the file the build wrote,
    /// and the lines it says each source line became are the ones the map beside it records.
    /// </summary>
    [Fact]
    public void ThePreviewIsWhatTheBuildWrites()
    {
        var seen = 0;
        foreach (var fixture in FixtureCase.All())
        {
            var analysis = Compiler.Analyze(
                [.. fixture.Sources.Select(SyntaxTree.Parse)], fixture.Project, fixture.BinaryLength);
            var compilation = Compiler.Emit(analysis, fixture.Project);
            if (compilation.Outputs.Count == 0)
                continue;
            foreach (var output in compilation.Outputs.Where(output => output.Kind == OutputKind.Ca65))
            {
                var preview = OutputPreview.Of(analysis, fixture.Project, output.Source);
                Assert.NotNull(preview);
                Assert.Null(preview.Note);
                Assert.Equal(output.Path, preview.Path);
                Assert.Equal(output.Text, preview.Text);
                Assert.Equal(output.LineSources, preview.SourceLines);

                // The map is written from the same lines, so what it says and what the view
                // says are the same thing said twice.
                if (LineMap.For(output) is { } map)
                {
                    var read = LineMap.Read(map.Text, out var problem);
                    Assert.Null(problem);
                    Assert.NotNull(read);
                    for (var i = 0; i < preview.SourceLines.Count; i++)
                    {
                        var mapped = read.Lines.TryGetValue(i + 1, out var line) ? line.Line : (int?)null;
                        Assert.Equal(preview.SourceLines[i] == 0 ? null : preview.SourceLines[i], mapped);
                    }
                }
                seen++;
            }
        }
        Assert.True(seen > 20, $"the fixtures wrote {seen} files");
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
