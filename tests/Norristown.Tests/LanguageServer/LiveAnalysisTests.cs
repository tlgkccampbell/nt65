using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;
using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.Tests.LanguageServer;

/// <summary>Tests how the analysis of a program is shared between the requests that ask for it.</summary>
public sealed class LiveAnalysisTests
{
    /// <summary>
    /// An analysis that fails is the answer for the files as they stand. Every request shares that
    /// failure rather than starting another analysis that would fail the same way, and a change to
    /// a file is what starts a new one.
    /// </summary>
    [Fact]
    public async Task AFailedAnalysisIsNotRunAgainUntilAFileChanges()
    {
        var runs = 0;
        var live = new LiveAnalysis((_, _, _, _) =>
        {
            Interlocked.Increment(ref runs);
            throw new InvalidOperationException("a bug in the analysis");
        });
        (IReadOnlyCollection<SyntaxTree>, ProjectSettings) Inputs() => ([], ProjectSettings.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => live.AnalysisAsync(Inputs, TestTimeout.Token()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => live.AnalysisAsync(Inputs, TestTimeout.Token()));
        Assert.Equal(1, runs);

        live.Invalidate();
        await Assert.ThrowsAsync<InvalidOperationException>(() => live.AnalysisAsync(Inputs, TestTimeout.Token()));
        Assert.Equal(2, runs);
    }

    /// <summary>
    /// A workspace tells whoever asked of an analysis that fails, once for each time it runs,
    /// since no request says anything about why it failed.
    /// </summary>
    [Fact]
    public async Task AWorkspaceReportsAFailedAnalysis()
    {
        var failures = new List<Exception>();
        var workspace = new Workspace((_, _, _, _) => throw new InvalidOperationException("a bug"), failures.Add);

        var document = workspace.Open(new TextDocumentItem("file:///c:/work/main.nt65", "nt65", 1, ".module main\n"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => workspace.AnalysisForAsync(document.Tree.Path, TestTimeout.Token()));

        Assert.IsType<InvalidOperationException>(Assert.Single(failures));
    }
}
