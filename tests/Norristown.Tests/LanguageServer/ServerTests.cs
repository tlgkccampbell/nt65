using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>Drives the server in-process over a pair of streams, the way an editor would.</summary>
public sealed class ServerTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    /// <summary>A file with one syntax error on line 3 (0-based line 2).</summary>
    private const string Broken = ".proc reset {\n    lda #0\n    lda #\n    rts\n}\n";

    private const string Fixed = ".proc reset {\n    lda #0\n    lda #1\n    rts\n}\n";

    [Fact]
    public async Task InitializesLogsTheConnectionAndExits()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);

        Assert.Equal("Norristown Assembler", client.Initialized.ServerInfo.Name);
        Assert.Contains("connected: test-client 1.0", client.Log);

        var message = await client.NextLogMessageAsync(timeout);
        Assert.Equal("Norristown language server ready", message.Message);
    }

    /// <summary>Stage 3's capabilities: incremental sync, the outline and folding.</summary>
    [Fact]
    public async Task AnnouncesWhatTheSyntaxLayerCanDo()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);

        var capabilities = client.Initialized.Capabilities;
        Assert.True(capabilities.TextDocumentSync.OpenClose);
        Assert.Equal(TextDocumentSyncKind.Incremental, capabilities.TextDocumentSync.Change);
        Assert.True(capabilities.DocumentSymbolProvider);
        Assert.True(capabilities.FoldingRangeProvider);
    }

    [Fact]
    public async Task OpeningADocumentPublishesItsSyntaxDiagnostics()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, Broken);

        var published = await client.NextDiagnosticsAsync(timeout);
        Assert.Equal(Uri, published.Uri);
        Assert.Equal(1, published.Version);
        var diagnostic = Assert.Single(published.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("nt65", diagnostic.Source);
        Assert.Equal(2, diagnostic.Range.Start.Line);
        Assert.Equal(2, diagnostic.Range.End.Line);
        Assert.Null(diagnostic.RelatedInformation);
    }

    /// <summary>Typing the missing operand clears the error, which is the editor loop of §14.</summary>
    [Fact]
    public async Task EditingAwayAnErrorClearsIt()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, Broken);
        Assert.NotEmpty((await client.NextDiagnosticsAsync(timeout)).Diagnostics);

        // Insert `1` at the end of line 3, which is where the operand is missing.
        await client.ChangeAsync(Uri, 2, new TextDocumentContentChangeEvent(
            new Range(new Position(2, 9), new Position(2, 9)), "1"));

        var published = await client.NextDiagnosticsAsync(timeout);
        Assert.Equal(2, published.Version);
        Assert.Empty(published.Diagnostics);
    }

    [Fact]
    public async Task ClosingADocumentClearsItsDiagnostics()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, Broken);
        Assert.NotEmpty((await client.NextDiagnosticsAsync(timeout)).Diagnostics);

        await client.CloseAsync(Uri);
        var published = await client.NextDiagnosticsAsync(timeout);
        Assert.Empty(published.Diagnostics);
        Assert.Null(published.Version);
    }

    [Fact]
    public async Task OutlineAndFoldingComeFromTheOpenDocument()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, ".scope gfx {\n.proc init {\nrts\n}\nCOUNT = 4\n}\n");
        await client.NextDiagnosticsAsync(timeout);

        var scope = Assert.Single(await client.SymbolsAsync(Uri, timeout));
        Assert.Equal("gfx", scope.Name);
        Assert.Equal(SymbolKind.Namespace, scope.Kind);
        Assert.Equal(new Position(0, 7), scope.SelectionRange.Start);
        Assert.NotNull(scope.Children);
        Assert.Equal(["init", "COUNT"], scope.Children.Select(child => child.Name));
        Assert.Equal([SymbolKind.Function, SymbolKind.Constant], scope.Children.Select(child => child.Kind));

        Assert.Equal([new FoldingRange(0, 5), new FoldingRange(1, 3)],
            await client.FoldingRangesAsync(Uri, timeout));
    }

    /// <summary>A request for a document the client never opened is answered, not refused.</summary>
    [Fact]
    public async Task ADocumentThatIsNotOpenHasNoOutline()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);

        Assert.Empty(await client.SymbolsAsync(Uri, timeout));
        Assert.Empty(await client.FoldingRangesAsync(Uri, timeout));
    }

    /// <summary>Several edits in one notification apply in order, each to what the last left.</summary>
    [Fact]
    public async Task ChangesInOneNotificationApplyInOrder()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, Fixed);
        Assert.Empty((await client.NextDiagnosticsAsync(timeout)).Diagnostics);

        await client.ChangeAsync(Uri, 2,
            new TextDocumentContentChangeEvent(new Range(new Position(3, 4), new Position(3, 7)), "jsr wait"),
            new TextDocumentContentChangeEvent(new Range(new Position(4, 1), new Position(4, 1)), "\nrts\n"));

        Assert.Empty((await client.NextDiagnosticsAsync(timeout)).Diagnostics);
        var proc = Assert.Single(await client.SymbolsAsync(Uri, timeout));
        Assert.Equal("reset", proc.Name);
        Assert.Equal(new FoldingRange(0, 4), Assert.Single(await client.FoldingRangesAsync(Uri, timeout)));
    }
}
