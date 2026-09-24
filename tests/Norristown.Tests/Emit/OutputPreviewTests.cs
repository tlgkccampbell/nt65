using Norristown.Emit;
using Norristown.Syntax;
using Norristown.Tests.Fixtures;

namespace Norristown.Tests.Emit;

/// <summary>
/// Checks the output preview against what a build writes. The preview is what the editor shows
/// beside a source, and what <c>nt65 build --stdout</c> writes. The two must be the same text, or
/// the preview is showing something other than the program.
/// </summary>
public sealed class OutputPreviewTests
{
    /// <summary>
    /// The source line the preview gives for each output line is the one the line map beside the
    /// output records: both describe the same mapping, and an editor and a debugger have to agree
    /// about where a line came from. The fixture runner checks every fixture's output against the
    /// preview itself, since it has already analysed the program.
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
    /// A module a build writes no file for is shown as a line saying so, rather than as the
    /// header of a file that is not there.
    /// </summary>
    [Fact]
    public void AModuleThatWritesNothingIsShownAsNoFile()
    {
        var analysis = Compiler.Analyze(
            [new SourceFile("text.nt65", ".module text\n.export .charmap screen {\n    'A'..'Z' = $01\n}\n")],
            Norristown.Project.ProjectSettings.None);
        var preview = OutputPreview.Of(analysis, Norristown.Project.ProjectSettings.None, "text.nt65");
        Assert.NotNull(preview);
        Assert.StartsWith("; No file:", preview.Text, StringComparison.Ordinal);
        Assert.Equal([0], preview.SourceLines);
    }

    /// <summary>
    /// A file with errors shows what could be written, under a first line that says it is
    /// incomplete and why. That line gives how many errors the program has, then the first
    /// error's line and message.
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
            "nt65: this output is incomplete because the program has an error at line 4: `nowhere` is not declared",
            preview.Note);
        Assert.StartsWith($"; {preview.Note}\n", preview.Text, StringComparison.Ordinal);

        // The note is an output line that no source line produced, so it maps to no source line,
        // and every line after it still maps to the line it came from.
        Assert.Equal(0, preview.SourceLines[0]);
        Assert.Contains(4, preview.SourceLines);
        Assert.Equal(preview.Text.Split('\n').Length - 1, preview.SourceLines.Count);
    }

    /// <summary>A file that is not part of the program has no output to show.</summary>
    [Fact]
    public void AFileTheProgramDoesNotHoldHasNoOutput()
    {
        var analysis = Compiler.Analyze(
            [new SourceFile("main.nt65", ".module main\n")], Norristown.Project.ProjectSettings.None);
        Assert.Null(OutputPreview.Of(analysis, Norristown.Project.ProjectSettings.None, "other.nt65"));
    }
}
