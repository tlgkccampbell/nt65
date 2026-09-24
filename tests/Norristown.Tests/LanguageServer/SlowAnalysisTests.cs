using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// An analysis runs outside the workspace's lock, and requests that ask about the same files share
/// it. A request that needs no analysis is answered while one runs, and a request the client
/// cancels stops waiting at once. The analysis stops too, once nothing else is waiting for it.
/// </summary>
public sealed class SlowAnalysisTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    private const string Source = ".module main\n.segment CODE\n.export .proc reset {\n    ldx #0\n    rts\n}\n";

    private static readonly string Path = Uris.ToPath(Uri);

    /// <summary>
    /// A request that needs only the file's syntax is answered while the publish of the file's
    /// diagnostics waits for its analysis. A hover issued meanwhile waits for that same analysis
    /// rather than starting another.
    /// </summary>
    [Fact]
    public async Task AQueryDuringASlowPublishIsAnsweredWithoutWaitingForIt()
    {
        var timeout = TestTimeout.Token();
        var held = new HeldAnalysis();
        await using var client = await TestClient.StartAsync(TestClient.Capable(), timeout, analyzer: held.Analyze);
        await client.OpenAsync(Uri, Source);
        await held.Started.Task.WaitAsync(timeout);

        var hover = client.HoverAsync(Uri, new Position(2, 16), timeout);
        Assert.NotEmpty(await client.FoldingRangesAsync(Uri, timeout));
        Assert.NotEmpty(await client.SymbolsAsync(Uri, timeout));
        Assert.False(hover.IsCompleted);
        Assert.True(client.Quiet);

        held.Release();
        Assert.NotNull(await hover);
        Assert.Empty((await client.NextDiagnosticsAsync(Uri, timeout)).Diagnostics);
        Assert.Equal(1, held.Calls);
    }

    /// <summary>
    /// A request the client cancels while the analysis it needs is running is answered at once.
    /// The publish that is waiting for the same analysis still gets it.
    /// </summary>
    [Fact]
    public async Task ACancelledRequestStopsWaitingForAnAnalysisThePublishStillNeeds()
    {
        var timeout = TestTimeout.Token();
        var held = new HeldAnalysis();
        await using var client = await TestClient.StartAsync(TestClient.Capable(), timeout, analyzer: held.Analyze);
        await client.OpenAsync(Uri, Source);
        await held.Started.Task.WaitAsync(timeout);

        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(timeout);
        var hover = client.HoverAsync(Uri, new Position(2, 16), giveUp.Token);
        await giveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hover);
        Assert.False(held.Cancelled.Task.IsCompleted);

        held.Release();
        Assert.Empty((await client.NextDiagnosticsAsync(Uri, timeout)).Diagnostics);
    }

    /// <summary>
    /// Cancelling the only request waiting for an analysis cancels the analysis, and the next
    /// request starts a new one rather than waiting for the cancelled one.
    /// </summary>
    [Fact]
    public async Task ACancelledRequestCancelsItsAnalysis()
    {
        var timeout = TestTimeout.Token();
        var held = new HeldAnalysis();
        var workspace = new Workspace(held.Analyze);
        workspace.Open(new TextDocumentItem(Uri, "nt65", 1, Source));

        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(timeout);
        var asked = workspace.AnalysisForAsync(Path, giveUp.Token);
        await held.Started.Task.WaitAsync(timeout);
        await giveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asked);
        await held.Cancelled.Task.WaitAsync(timeout);

        held.Release();
        Assert.NotNull((await workspace.AnalysisForAsync(Path, timeout)).ModelFor(Path));
        Assert.Equal(2, held.Calls);
    }

    /// <summary>
    /// An analysis two requests wait for keeps running when one of them is cancelled, and the
    /// other gets it.
    /// </summary>
    [Fact]
    public async Task AnAnalysisAnotherRequestWaitsForIsNotCancelled()
    {
        var timeout = TestTimeout.Token();
        var held = new HeldAnalysis();
        var workspace = new Workspace(held.Analyze);
        workspace.Open(new TextDocumentItem(Uri, "nt65", 1, Source));

        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(timeout);
        var first = workspace.AnalysisForAsync(Path, giveUp.Token);
        var second = workspace.AnalysisForAsync(Path, timeout);
        await held.Started.Task.WaitAsync(timeout);
        await giveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(held.Cancelled.Task.IsCompleted);

        held.Release();
        Assert.NotNull((await second).ModelFor(Path));
        Assert.Equal(1, held.Calls);
    }

    /// <summary>
    /// An edit made while an analysis runs starts a new analysis of the edited text. The new one
    /// waits for the running one and starts from it, because that one is of the text before the
    /// edit.
    /// </summary>
    [Fact]
    public async Task AnAnalysisOfAnEditStartsFromTheAnalysisBeforeIt()
    {
        var timeout = TestTimeout.Token();
        var held = new HeldAnalysis();
        var workspace = new Workspace(held.Analyze);
        workspace.Open(new TextDocumentItem(Uri, "nt65", 1, Source));
        var before = workspace.AnalysisForAsync(Path, timeout);
        await held.Started.Task.WaitAsync(timeout);

        var edited = workspace.Change(new VersionedTextDocumentIdentifier(Uri, 2),
            [new TextDocumentContentChangeEvent(Locate.Span(Source, "ldx #|0"), "1")]);
        var after = workspace.AnalysisForAsync(Path, timeout);
        held.Release();

        var first = await before;
        var second = await after;
        Assert.Same(edited!.Tree, second.ModelFor(Path)!.Tree);
        Assert.Equal([null, first], held.Previous);
        Assert.Null(second.WholeProgram);
    }
}
