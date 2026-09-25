using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests that the program <see cref="GeneratedProject"/> builds compiles cleanly. The keystroke
/// benchmarks rely on that program and no gate runs them, so a small copy of it is checked here.
/// </summary>
public sealed class GeneratedProjectTests
{
    /// <summary>
    /// A program of one file, which the large-file benchmark extends, and a program of three
    /// files, shaped like the many-file benchmark's, have no diagnostics.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task TheGeneratedProgramHasNoDiagnostics(int files)
    {
        var workspace = new Workspace();
        for (var i = 0; i < files; i++)
            workspace.Open(new TextDocumentItem(GeneratedProject.Uri(i), "nt65", 1, GeneratedProject.Text(i, files)));

        var analysis = await workspace.AnalysisForAsync(Uris.ToPath(GeneratedProject.Uri(0)), TestTimeout.Token());
        Assert.Empty(analysis.Diagnostics);
    }
}
