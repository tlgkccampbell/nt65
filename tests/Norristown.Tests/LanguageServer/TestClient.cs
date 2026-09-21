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

    private TestClient(Delay? delay)
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        log = new ServerLog(logText);

        // The wait between an edit and what the rest of the program has to say is the server's
        // to decide and the test's to drive: the suite waits for no real time, and a test about
        // the wait itself passes one it lets go of when it is ready.
        server = Server.RunAsync(serverStream, serverStream, log, delay ?? Yield);
        rpc = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, Server.CreateFormatter()));
        rpc.AddLocalRpcTarget(notifications);
        rpc.StartListening();
    }

    /// <summary>What the server said it can do.</summary>
    public InitializeResult Initialized { get; private set; } = null!;

    /// <summary>Everything the server wrote to its log.</summary>
    public string Log => logText.ToString();

    /// <summary>Whether the server has asked for semantic tokens to be fetched again, without waiting for it to.</summary>
    public bool AskedForTokensRefresh => notifications.TokensRefreshed.Reader.Count > 0;

    /// <summary>Whether anything at all is waiting to be read, which is how a test says nothing came.</summary>
    public bool Quiet => notifications.Published.Reader.Count == 0;

    /// <summary>Connects and completes the initialize handshake.</summary>
    public static Task<TestClient> StartAsync(CancellationToken cancellation, string name = "test-client") =>
        StartAsync(null, null, cancellation, name);

    /// <summary>
    /// Connects with <paramref name="rootUri"/> as the folder the client opened, and
    /// <paramref name="configuration"/> as the configuration its settings choose.
    /// <paramref name="refreshesTokens"/> says the client can be asked to fetch semantic tokens again.
    /// </summary>
    public static Task<TestClient> StartAsync(
        string? rootUri, string? configuration, CancellationToken cancellation, string name = "test-client",
        bool refreshesTokens = false) =>
        StartAsync(
            Capable(refreshesTokens), cancellation, rootUri: rootUri, configuration: configuration, name: name);

    /// <summary>
    /// What a client of the kind nt65 is written for declares: an outline as a tree, edits
    /// against a named revision, snippets, and the folders it has open. It is what VS Code
    /// declares, so it is what most of the suite asks as.
    /// </summary>
    public static object Capable(bool refreshesTokens = false) => new
    {
        workspace = new
        {
            workspaceEdit = new { documentChanges = true },
            workspaceFolders = true,
            semanticTokens = new { refreshSupport = refreshesTokens },
        },
        textDocument = new
        {
            documentSymbol = new { hierarchicalDocumentSymbolSupport = true },
            completion = new { completionItem = new { snippetSupport = true } },
        },
    };

    /// <summary>
    /// Connects as a client that declares <paramref name="capabilities"/>, which is the object
    /// the protocol's own <c>capabilities</c> is, so that a test says what it can take in the
    /// spelling a real client would.
    /// </summary>
    public static async Task<TestClient> StartAsync(
        object capabilities, CancellationToken cancellation, string? rootUri = null, string? configuration = null,
        string name = "test-client", Delay? delay = null, int? processId = null)
    {
        var client = new TestClient(delay);
        client.Initialized = await client.rpc.InvokeWithParameterObjectAsync<InitializeResult>("initialize",
            new
            {
                processId,
                clientInfo = new { name, version = "1.0" },
                capabilities,
                rootUri,
                initializationOptions = new { configuration },
            },
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

    /// <summary>Says that files changed on disk.</summary>
    public Task ChangedOnDiskAsync(params string[] uris) =>
        rpc.NotifyWithParameterObjectAsync("workspace/didChangeWatchedFiles",
            new DidChangeWatchedFilesParams([.. uris.Select(uri => new FileEvent(uri, FileChangeType.Changed))]));

    /// <summary>Says that the folders the client has open have changed.</summary>
    public Task FoldersChangedAsync(IEnumerable<string> added, IEnumerable<string> removed) =>
        rpc.NotifyWithParameterObjectAsync("workspace/didChangeWorkspaceFolders",
            new DidChangeWorkspaceFoldersParams(new WorkspaceFoldersChangeEvent(
                [.. added.Select(uri => new WorkspaceFolder(uri, uri))],
                [.. removed.Select(uri => new WorkspaceFolder(uri, uri))])));

    /// <summary>Says that the client's <c>nt65</c> settings now choose <paramref name="configuration"/>.</summary>
    public Task ConfigureAsync(string? configuration) =>
        rpc.NotifyWithParameterObjectAsync("workspace/didChangeConfiguration",
            new { settings = new { nt65 = new { configuration } } });

    /// <summary>Sends any request, for the ones with no method of their own here.</summary>
    public Task<T> RequestAsync<T>(string method, object? parameters, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<T>(method, parameters, cancellation);

    /// <summary>
    /// The next set of diagnostics the server publishes, for whichever file it is about. The
    /// server publishes for every file of the program, so a test with more than one file in it
    /// asks for the one it is about by name.
    /// </summary>
    public async Task<PublishDiagnosticsParams> NextDiagnosticsAsync(CancellationToken cancellation) =>
        await notifications.Published.Reader.ReadAsync(cancellation);

    /// <summary>Everything published and not yet read, taken off without waiting for any more.</summary>
    public IReadOnlyList<PublishDiagnosticsParams> Pending()
    {
        var found = new List<PublishDiagnosticsParams>();
        while (notifications.Published.Reader.TryRead(out var published))
            found.Add(published);
        return found;
    }

    /// <summary>The next set published for <paramref name="uri"/>, passing over every other file's.</summary>
    public async Task<PublishDiagnosticsParams> NextDiagnosticsAsync(string uri, CancellationToken cancellation)
    {
        while (true)
        {
            var published = await NextDiagnosticsAsync(cancellation);
            if (published.Uri == uri)
                return published;
        }
    }

    /// <summary>Waits for the server to ask for semantic tokens to be fetched again.</summary>
    public async Task NextTokensRefreshAsync(CancellationToken cancellation) =>
        await notifications.TokensRefreshed.Reader.ReadAsync(cancellation);

    /// <summary>The next message the server logged to the client's window.</summary>
    public async Task<LogMessageParams> NextLogMessageAsync(CancellationToken cancellation) =>
        await notifications.Logged.Reader.ReadAsync(cancellation);

    /// <summary>The document's outline.</summary>
    public Task<IReadOnlyList<DocumentSymbol>> SymbolsAsync(string uri, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<IReadOnlyList<DocumentSymbol>>("textDocument/documentSymbol",
            new DocumentSymbolParams(new TextDocumentIdentifier(uri)), cancellation);

    /// <summary>The document's outline as a client that takes no tree gets it: one flat list.</summary>
    public Task<IReadOnlyList<SymbolInformation>> FlatSymbolsAsync(string uri, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<IReadOnlyList<SymbolInformation>>("textDocument/documentSymbol",
            new DocumentSymbolParams(new TextDocumentIdentifier(uri)), cancellation);

    /// <summary>The document's names, classified.</summary>
    public Task<SemanticTokens> SemanticTokensAsync(string uri, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<SemanticTokens>("textDocument/semanticTokens/full",
            new SemanticTokensParams(new TextDocumentIdentifier(uri)), cancellation);

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

    /// <summary>The wait the suite uses: no real time, and still not taken on the caller's own step.</summary>
    private static async Task Yield(TimeSpan quiet) => await Task.Yield();

    /// <summary>What the server sends without being asked.</summary>
    private sealed class Notifications
    {
        public Channel<PublishDiagnosticsParams> Published { get; } =
            Channel.CreateUnbounded<PublishDiagnosticsParams>();

        public Channel<LogMessageParams> Logged { get; } = Channel.CreateUnbounded<LogMessageParams>();

        public Channel<bool> TokensRefreshed { get; } = Channel.CreateUnbounded<bool>();

        [JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)]
        public void OnPublishDiagnostics(PublishDiagnosticsParams parameters) => Published.Writer.TryWrite(parameters);

        [JsonRpcMethod("window/logMessage", UseSingleObjectParameterDeserialization = true)]
        public void OnLogMessage(LogMessageParams parameters) => Logged.Writer.TryWrite(parameters);

        [JsonRpcMethod("workspace/semanticTokens/refresh")]
        public object? OnRefreshTokens()
        {
            TokensRefreshed.Writer.TryWrite(true);
            return null;
        }
    }
}
