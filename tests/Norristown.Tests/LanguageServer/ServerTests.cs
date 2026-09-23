using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>Drives the server in-process over a pair of streams, the way an editor would.</summary>
public sealed class ServerTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    /// <summary>A file with one syntax error on line 5 (0-based line 4).</summary>
    private const string Broken = ".module main\n.segment CODE\n.export .proc reset {\n    lda #0\n    lda #\n    rts\n}\n";

    private const string Fixed = ".module main\n.segment CODE\n.export .proc reset {\n    lda #0\n    lda #1\n    rts\n}\n";

    [Fact]
    public async Task InitializesLogsTheConnectionAndExits()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.StartAsync(timeout);

        Assert.Equal("Norristown Assembler", client.Initialized.ServerInfo.Name);
        Assert.Contains("connected: test-client 1.0", client.Log);

        var message = await client.NextLogMessageAsync(timeout);
        Assert.Equal("Norristown language server ready", message.Message);
    }

    /// <summary>The capabilities the server announces: incremental sync, the outline and folding.</summary>
    [Fact]
    public async Task AnnouncesWhatTheSyntaxLayerCanDo()
    {
        var timeout = TestTimeout.Token();
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
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, Broken);

        var published = await client.NextDiagnosticsAsync(timeout);
        Assert.Equal(Uri, published.Uri);
        Assert.Equal(1, published.Version);
        var diagnostic = Assert.Single(published.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("nt65", diagnostic.Source);
        Assert.Equal(4, diagnostic.Range.Start.Line);
        Assert.Equal(4, diagnostic.Range.End.Line);
        Assert.Null(diagnostic.RelatedInformation);
    }

    /// <summary>Typing the missing operand clears the error: the basic edit-and-republish loop.</summary>
    [Fact]
    public async Task EditingAwayAnErrorClearsIt()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, Broken);
        Assert.NotEmpty((await client.NextDiagnosticsAsync(timeout)).Diagnostics);

        // Insert `1` at the end of 0-based line 4, which is where the operand is missing.
        await client.ChangeAsync(Uri, 2, new TextDocumentContentChangeEvent(
            new Range(new Position(4, 9), new Position(4, 9)), "1"));

        var published = await client.NextDiagnosticsAsync(timeout);
        Assert.Equal(2, published.Version);
        Assert.Empty(published.Diagnostics);
    }

    [Fact]
    public async Task ClosingADocumentClearsItsDiagnostics()
    {
        var timeout = TestTimeout.Token();
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
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, ".module main\n.scope gfx {\n.proc init {\nrts\n}\nCOUNT = 4\n}\n");
        await client.NextDiagnosticsAsync(timeout);

        var scope = Assert.Single(await client.SymbolsAsync(Uri, timeout));
        Assert.Equal("gfx", scope.Name);
        Assert.Equal(SymbolKind.Namespace, scope.Kind);
        Assert.Equal(new Position(1, 7), scope.SelectionRange.Start);
        Assert.NotNull(scope.Children);
        Assert.Equal(["init", "COUNT"], scope.Children.Select(child => child.Name));
        Assert.Equal([SymbolKind.Function, SymbolKind.Constant], scope.Children.Select(child => child.Kind));

        Assert.Equal([new FoldingRange(1, 6), new FoldingRange(2, 4)],
            await client.FoldingRangesAsync(Uri, timeout));
    }

    /// <summary>A request for a document the client never opened is answered, not refused.</summary>
    [Fact]
    public async Task ADocumentThatIsNotOpenHasNoOutline()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.StartAsync(timeout);

        Assert.Empty(await client.SymbolsAsync(Uri, timeout));
        Assert.Empty(await client.FoldingRangesAsync(Uri, timeout));
    }

    /// <summary>Several edits in one notification apply in order, each to the text the previous one left.</summary>
    [Fact]
    public async Task ChangesInOneNotificationApplyInOrder()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, Fixed);
        Assert.Empty((await client.NextDiagnosticsAsync(timeout)).Diagnostics);

        // The second edit is past the end of what the first one wrote, so it is only where it
        // is meant to be once the first has been applied.
        await client.ChangeAsync(Uri, 2,
            new TextDocumentContentChangeEvent(new Range(new Position(5, 4), new Position(5, 7)), "jsr reset"),
            new TextDocumentContentChangeEvent(new Range(new Position(5, 13), new Position(5, 13)), "\n    rts"));

        Assert.Empty((await client.NextDiagnosticsAsync(timeout)).Diagnostics);
        var segment = Assert.Single(await client.SymbolsAsync(Uri, timeout));
        var proc = Assert.Single(segment.Children!);
        Assert.Equal("reset", proc.Name);
        Assert.Equal([new FoldingRange(1, 7), new FoldingRange(2, 7)], await client.FoldingRangesAsync(Uri, timeout));
    }
}
