using System.Text.Json;
using System.Threading.Channels;
using Nerdbank.Streams;
using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;
using StreamJsonRpc;

// The protocol has a Range of its own, which is the one this client means.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// An editor, as far as the server can tell: the server running in this process over a pair
/// of streams, and the JSON-RPC connection an editor would hold. Everything crosses the
/// wire, so the protocol types and their JSON spelling are under test too.
/// </summary>
internal sealed class TestClient : IAsyncDisposable
{
    private readonly StringWriter logText = new();
    private readonly ServerLog log;
    private readonly JsonRpc rpc;
    private readonly Task server;
    private readonly Notifications notifications = new();

    private TestClient()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        log = new ServerLog(logText);
        server = Server.RunAsync(serverStream, serverStream, log);
        rpc = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, Server.CreateFormatter()));
        rpc.AddLocalRpcTarget(notifications);
        rpc.StartListening();
    }

    /// <summary>What the server said it can do.</summary>
    public InitializeResult Initialized { get; private set; } = null!;

    /// <summary>Everything the server wrote to its log.</summary>
    public string Log => logText.ToString();

    /// <summary>Connects and completes the initialize handshake.</summary>
    public static async Task<TestClient> StartAsync(CancellationToken cancellation, string name = "test-client")
    {
        var client = new TestClient();
        client.Initialized = await client.rpc.InvokeWithParameterObjectAsync<InitializeResult>("initialize",
            new { processId = (int?)null, clientInfo = new { name, version = "1.0" }, capabilities = new { } },
            cancellation);
        await client.rpc.NotifyWithParameterObjectAsync("initialized", new { });
        return client;
    }

    /// <summary>Opens a document at version 1.</summary>
    public Task OpenAsync(string uri, string text) =>
        rpc.NotifyWithParameterObjectAsync("textDocument/didOpen",
            new DidOpenTextDocumentParams(new TextDocumentItem(uri, "nt65", 1, text)));

    /// <summary>Sends edits, in order, as the revision <paramref name="version"/>.</summary>
    public Task ChangeAsync(string uri, int version, params TextDocumentContentChangeEvent[] changes) =>
        rpc.NotifyWithParameterObjectAsync("textDocument/didChange",
            new DidChangeTextDocumentParams(new VersionedTextDocumentIdentifier(uri, version), changes));

    /// <summary>Closes a document.</summary>
    public Task CloseAsync(string uri) =>
        rpc.NotifyWithParameterObjectAsync("textDocument/didClose",
            new DidCloseTextDocumentParams(new TextDocumentIdentifier(uri)));

    /// <summary>
    /// The next set of diagnostics the server publishes. Opening and changing a document each
    /// publish exactly once, so a test reads one set per edit it made.
    /// </summary>
    public async Task<PublishDiagnosticsParams> NextDiagnosticsAsync(CancellationToken cancellation) =>
        await notifications.Published.Reader.ReadAsync(cancellation);

    /// <summary>The next message the server logged to the client's window.</summary>
    public async Task<LogMessageParams> NextLogMessageAsync(CancellationToken cancellation) =>
        await notifications.Logged.Reader.ReadAsync(cancellation);

    /// <summary>The document's outline.</summary>
    public Task<IReadOnlyList<DocumentSymbol>> SymbolsAsync(string uri, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<IReadOnlyList<DocumentSymbol>>("textDocument/documentSymbol",
            new DocumentSymbolParams(new TextDocumentIdentifier(uri)), cancellation);

    /// <summary>The document's foldable ranges.</summary>
    public Task<IReadOnlyList<FoldingRange>> FoldingRangesAsync(string uri, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<IReadOnlyList<FoldingRange>>("textDocument/foldingRange",
            new FoldingRangeParams(new TextDocumentIdentifier(uri)), cancellation);

    /// <summary>What to show about the name at a place in the document.</summary>
    public Task<Hover?> HoverAsync(string uri, Position position, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<Hover?>("textDocument/hover",
            new TextDocumentPositionParams(new TextDocumentIdentifier(uri), position), cancellation);

    /// <summary>Where the name at a place is declared.</summary>
    public Task<Location?> DefinitionAsync(string uri, Position position, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<Location?>("textDocument/definition",
            new TextDocumentPositionParams(new TextDocumentIdentifier(uri), position), cancellation);

    /// <summary>Every place the name at a place is written.</summary>
    public Task<IReadOnlyList<Location>> ReferencesAsync(
        string uri, Position position, bool includeDeclaration, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<IReadOnlyList<Location>>("textDocument/references",
            new ReferenceParams(new TextDocumentIdentifier(uri), position, new ReferenceContext(includeDeclaration)),
            cancellation);

    /// <summary>The same places, as the client marks them.</summary>
    public Task<IReadOnlyList<DocumentHighlight>> HighlightsAsync(
        string uri, Position position, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<IReadOnlyList<DocumentHighlight>>("textDocument/documentHighlight",
            new TextDocumentPositionParams(new TextDocumentIdentifier(uri), position), cancellation);

    /// <summary>What a rename at a place would replace.</summary>
    public Task<Range?> PrepareRenameAsync(string uri, Position position, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<Range?>("textDocument/prepareRename",
            new TextDocumentPositionParams(new TextDocumentIdentifier(uri), position), cancellation);

    /// <summary>Renames the name at a place, everywhere it is written.</summary>
    public Task<WorkspaceEdit?> RenameAsync(
        string uri, Position position, string newName, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<WorkspaceEdit?>("textDocument/rename",
            new RenameParams(new TextDocumentIdentifier(uri), position, newName), cancellation);

    /// <summary>Shuts the server down the way an editor does, and waits for it to stop.</summary>
    public async ValueTask DisposeAsync()
    {
        await rpc.InvokeWithParameterObjectAsync<JsonElement>("shutdown", null, CancellationToken.None);
        await rpc.NotifyWithParameterObjectAsync("exit", null);
        await server.WaitAsync(TimeSpan.FromSeconds(10));
        rpc.Dispose();
        log.Dispose();
    }

    /// <summary>What the server sends without being asked.</summary>
    private sealed class Notifications
    {
        public Channel<PublishDiagnosticsParams> Published { get; } =
            Channel.CreateUnbounded<PublishDiagnosticsParams>();

        public Channel<LogMessageParams> Logged { get; } = Channel.CreateUnbounded<LogMessageParams>();

        [JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)]
        public void OnPublishDiagnostics(PublishDiagnosticsParams parameters) => Published.Writer.TryWrite(parameters);

        [JsonRpcMethod("window/logMessage", UseSingleObjectParameterDeserialization = true)]
        public void OnLogMessage(LogMessageParams parameters) => Logged.Writer.TryWrite(parameters);
    }
}
