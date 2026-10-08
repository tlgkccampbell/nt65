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

        await Assert.ThrowsAsync<InvalidOperationException>(() => live.AnalysisAsync(Inputs, TestTimeout.Token()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => live.AnalysisAsync(Inputs, TestTimeout.Token()));
        Assert.Equal(1, runs);

        live.Invalidate();
        await Assert.ThrowsAsync<InvalidOperationException>(() => live.AnalysisAsync(Inputs, TestTimeout.Token()));
        Assert.Equal(2, runs);
    }

    /// <summary>
    /// A request that needs the program-wide answers settles the analysis once, and from then on
    /// every request for the same files gets the settled analysis.
    /// </summary>
    [Fact]
    public async Task ASettledAnalysisIsWorkedOutOnceAndShared()
    {
        var settled = Compiler.Analyze([SyntaxTree.Parse("main.nt65", ".module main\n")], ProjectSettings.None);
        var unsettled = settled with { IsSettled = false };
        var settles = 0;
        var live = new LiveAnalysis((_, _, _, _) => unsettled, (_, _) =>
        {
            Interlocked.Increment(ref settles);
            return settled;
        });

        Assert.Same(unsettled, await live.AnalysisAsync(Inputs, TestTimeout.Token()));
        Assert.Equal(0, settles);
        Assert.Same(settled, await live.AnalysisAsync(Inputs, TestTimeout.Token(), settled: true));
        Assert.Same(settled, await live.AnalysisAsync(Inputs, TestTimeout.Token(), settled: true));
        Assert.Same(settled, await live.AnalysisAsync(Inputs, TestTimeout.Token()));
        Assert.Equal(1, settles);
    }

    /// <summary>
    /// Settling runs beside the next edit's analysis rather than before it, so a keystroke never
    /// waits for it, and the edit stops it.
    /// </summary>
    [Fact]
    public async Task AnEditStopsSettlingWithoutWaitingForIt()
    {
        var settled = Compiler.Analyze([SyntaxTree.Parse("main.nt65", ".module main\n")], ProjectSettings.None);
        var unsettled = settled with { IsSettled = false };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var live = new LiveAnalysis((_, _, _, _) => unsettled, (analysis, cancellation) =>
        {
            started.TrySetResult();
            cancellation.WaitHandle.WaitOne();
            cancellation.ThrowIfCancellationRequested();
            return analysis;
        });

        var settling = live.AnalysisAsync(Inputs, TestTimeout.Token(), settled: true);
        await started.Task.WaitAsync(TestTimeout.Token());
        live.Invalidate();

        Assert.Same(unsettled, await live.AnalysisAsync(Inputs, TestTimeout.Token()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => settling);
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

    private static (IReadOnlyCollection<SyntaxTree>, ProjectSettings) Inputs() => ([], ProjectSettings.None);
}
