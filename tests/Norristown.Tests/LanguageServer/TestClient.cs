using System.Text.Json;
using System.Threading.Channels;
using Nerdbank.Streams;
using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;
using StreamJsonRpc;

// The protocol defines its own Range type, and this client uses that one.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Represents an editor from the server's point of view. The server runs in this process over
/// a pair of streams, and this client holds the JSON-RPC connection an editor would hold.
/// Every message crosses the wire, so the protocol types and their JSON form are under test too.
/// </summary>
internal sealed class TestClient : IAsyncDisposable
{
    private readonly StringWriter logText = new();
    private readonly ServerLog log;
    private readonly JsonRpc rpc;
    private readonly Task server;
    private readonly Notifications notifications = new();

    private TestClient(Delay? delay, Analyzer? analyzer)
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        log = new ServerLog(logText);

        // The server decides when to wait between an edit and publishing the rest of the
        // program's diagnostics, but the test supplies the wait. By default the wait takes no
        // real time. A test about the wait itself passes a wait that it releases when it is ready.
        server = Server.RunAsync(serverStream, serverStream, log, delay ?? Yield, analyzer);
        rpc = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, Server.CreateFormatter()));
        rpc.AddLocalRpcTarget(notifications);
        rpc.StartListening();
    }

    /// <summary>Gets the result of the initialize handshake, which declares what the server can do.</summary>
    public InitializeResult Initialized { get; private set; } = null!;

    /// <summary>Gets everything the server has written to its log.</summary>
    public string Log => logText.ToString();

    /// <summary>
    /// Gets a value indicating whether the server has asked for semantic tokens to be fetched
    /// again. Reading it does not wait for the request.
    /// </summary>
    public bool AskedForTokensRefresh => notifications.TokensRefreshed.Reader.Count > 0;

    /// <summary>
    /// Gets a value indicating whether no published diagnostics are waiting to be read. A test
    /// uses it to check that nothing was published.
    /// </summary>
    public bool Quiet => notifications.Published.Reader.Count == 0;

    /// <summary>Connects as the default client and completes the initialize handshake.</summary>
    public static Task<TestClient> StartAsync(CancellationToken cancellation, string name = "test-client") =>
        StartAsync(null, null, cancellation, name);

    /// <summary>
    /// Connects, opens each of <paramref name="files"/> in order, and waits for the diagnostics
    /// published for the last of them. This is the setup most server tests share.
    /// </summary>
    public static async Task<TestClient> OpenedAsync(
        CancellationToken cancellation, params (string Uri, string Text)[] files)
    {
        var client = await StartAsync(cancellation);
        foreach (var (uri, text) in files)
            await client.OpenAsync(uri, text);
        await client.NextDiagnosticsAsync(files[^1].Uri, cancellation);
        return client;
    }

    /// <summary>
    /// Connects, opens each of <paramref name="files"/> in order, and checks that the last of
    /// them has no diagnostics. A test class uses it for the shared source its tests query, so
    /// that a mistake in that source fails here instead of as a puzzling answer later.
    /// </summary>
    public static async Task<TestClient> OpenedCleanlyAsync(
        CancellationToken cancellation, params (string Uri, string Text)[] files)
    {
        var client = await StartAsync(cancellation);
        foreach (var (uri, text) in files)
            await client.OpenAsync(uri, text);
        Assert.Empty((await client.NextDiagnosticsAsync(files[^1].Uri, cancellation)).Diagnostics);
        return client;
    }

    /// <summary>
    /// Connects with <paramref name="rootUri"/> as the folder the client opened, and
    /// <paramref name="configuration"/> as the configuration its settings choose.
    /// <paramref name="refreshesTokens"/> indicates whether the client can be asked to fetch
    /// semantic tokens again.
    /// </summary>
    public static Task<TestClient> StartAsync(
        string? rootUri, string? configuration, CancellationToken cancellation, string name = "test-client",
        bool refreshesTokens = false) =>
        StartAsync(
            Capable(refreshesTokens), cancellation, rootUri: rootUri, configuration: configuration, name: name);

    /// <summary>
    /// Returns the capabilities declared by the kind of client nt65 is built for. Such a client
    /// accepts an outline as a tree, edits against a named document version and snippets. It
    /// reports the folders it has open and is asked before it moves a file. VS Code declares
    /// these, so most of the suite connects with them.
    /// </summary>
    public static object Capable(bool refreshesTokens = false, bool refreshesHints = false) => new
    {
        workspace = new
        {
            workspaceEdit = new { documentChanges = true },
            workspaceFolders = true,
            fileOperations = new { willRename = true },
            didChangeWatchedFiles = new { dynamicRegistration = true },
            semanticTokens = new { refreshSupport = refreshesTokens },
            inlayHint = new { refreshSupport = refreshesHints },
        },
        textDocument = new
        {
            documentSymbol = new { hierarchicalDocumentSymbolSupport = true },
            completion = new { completionItem = new { snippetSupport = true } },
        },
    };

    /// <summary>
    /// Connects as a client that declares <paramref name="capabilities"/>, an object sent as the
    /// protocol's own <c>capabilities</c> field, so that a test declares what it supports in the
    /// same JSON a real client would send.
    /// </summary>
    public static async Task<TestClient> StartAsync(
        object capabilities, CancellationToken cancellation, string? rootUri = null, string? configuration = null,
        string name = "test-client", Delay? delay = null, int? processId = null, object? inlayHints = null,
        Analyzer? analyzer = null)
    {
        var client = new TestClient(delay, analyzer);
        client.Initialized = await client.rpc.InvokeWithParameterObjectAsync<InitializeResult>("initialize",
            new
            {
                processId,
                clientInfo = new { name, version = "1.0" },
                capabilities,
                rootUri,
                initializationOptions = new { configuration, inlayHints },
            },
            cancellation);
        await client.rpc.NotifyWithParameterObjectAsync("initialized", new { });
        return client;
    }

    /// <summary>Opens a document at version 1.</summary>
    public Task OpenAsync(string uri, string text) =>
        rpc.NotifyWithParameterObjectAsync("textDocument/didOpen",
            new DidOpenTextDocumentParams(new TextDocumentItem(uri, "nt65", 1, text)));

    /// <summary>Sends edits, in order, as document version <paramref name="version"/>.</summary>
    public Task ChangeAsync(string uri, int version, params TextDocumentContentChangeEvent[] changes) =>
        rpc.NotifyWithParameterObjectAsync("textDocument/didChange",
            new DidChangeTextDocumentParams(new VersionedTextDocumentIdentifier(uri, version), changes));

    /// <summary>Closes a document.</summary>
    public Task CloseAsync(string uri) =>
        rpc.NotifyWithParameterObjectAsync("textDocument/didClose",
            new DidCloseTextDocumentParams(new TextDocumentIdentifier(uri)));

    /// <summary>Notifies the server that files changed on disk.</summary>
    public Task ChangedOnDiskAsync(params string[] uris) =>
        rpc.NotifyWithParameterObjectAsync("workspace/didChangeWatchedFiles",
            new DidChangeWatchedFilesParams([.. uris.Select(uri => new FileEvent(uri, FileChangeType.Changed))]));

    /// <summary>Notifies the server that the folders the client has open have changed.</summary>
    public Task FoldersChangedAsync(IEnumerable<string> added, IEnumerable<string> removed) =>
        rpc.NotifyWithParameterObjectAsync("workspace/didChangeWorkspaceFolders",
            new DidChangeWorkspaceFoldersParams(new WorkspaceFoldersChangeEvent(
                [.. added.Select(uri => new WorkspaceFolder(uri, uri))],
                [.. removed.Select(uri => new WorkspaceFolder(uri, uri))])));

    /// <summary>
    /// Notifies the server that the client's <c>nt65</c> settings now choose
    /// <paramref name="configuration"/>.
    /// </summary>
    public Task ConfigureAsync(string? configuration) =>
        rpc.NotifyWithParameterObjectAsync("workspace/didChangeConfiguration",
            new { settings = new { nt65 = new { configuration } } });

    /// <summary>Sends any request. Tests use it for requests that have no method of their own here.</summary>
    public Task<T> RequestAsync<T>(string method, object? parameters, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<T>(method, parameters, cancellation);

    /// <summary>
    /// Waits for and returns the next set of diagnostics the server publishes, for any file. The
    /// server publishes for every file of the program, so a test with more than one file asks
    /// for the file it is about by name.
    /// </summary>
    public async Task<PublishDiagnosticsParams> NextDiagnosticsAsync(CancellationToken cancellation) =>
        await notifications.Published.Reader.ReadAsync(cancellation);

    /// <summary>
    /// Removes and returns every set of diagnostics published and not yet read, without waiting
    /// for more.
    /// </summary>
    public IReadOnlyList<PublishDiagnosticsParams> Pending()
    {
        var found = new List<PublishDiagnosticsParams>();
        while (notifications.Published.Reader.TryRead(out var published))
            found.Add(published);
        return found;
    }

    /// <summary>
    /// Waits for and returns the next set of diagnostics published for <paramref name="uri"/>,
    /// discarding the sets published for other files.
    /// </summary>
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

    /// <summary>
    /// Waits for the server to report that analysis of the program has finished and a file's
    /// output has changed.
    /// </summary>
    public async Task<JsonElement> NextOutputChangedAsync(CancellationToken cancellation) =>
        await notifications.OutputChanged.Reader.ReadAsync(cancellation);

    /// <summary>Waits for and returns the next message the server shows to the user.</summary>
    public async Task<ShowMessageParams> NextShowMessageAsync(CancellationToken cancellation) =>
        await notifications.Shown.Reader.ReadAsync(cancellation);

    /// <summary>Waits for and returns the next message the server logs to the client's window.</summary>
    public async Task<LogMessageParams> NextLogMessageAsync(CancellationToken cancellation) =>
        await notifications.Logged.Reader.ReadAsync(cancellation);

    /// <summary>Requests the document's outline as a tree.</summary>
    public Task<IReadOnlyList<DocumentSymbol>> SymbolsAsync(string uri, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<IReadOnlyList<DocumentSymbol>>("textDocument/documentSymbol",
            new DocumentSymbolParams(new TextDocumentIdentifier(uri)), cancellation);

    /// <summary>Requests the document's outline as a client that takes no tree gets it, as one flat list.</summary>
    public Task<IReadOnlyList<SymbolInformation>> FlatSymbolsAsync(string uri, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<IReadOnlyList<SymbolInformation>>("textDocument/documentSymbol",
            new DocumentSymbolParams(new TextDocumentIdentifier(uri)), cancellation);

    /// <summary>Requests the semantic tokens that classify the document's names.</summary>
    public Task<SemanticTokens> SemanticTokensAsync(string uri, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<SemanticTokens>("textDocument/semanticTokens/full",
            new SemanticTokensParams(new TextDocumentIdentifier(uri)), cancellation);

    /// <summary>Requests the inlay hints for the lines <paramref name="first"/> to <paramref name="last"/>.</summary>
    public Task<IReadOnlyList<InlayHint>> InlayHintsAsync(
        string uri, int first, int last, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<IReadOnlyList<InlayHint>>("textDocument/inlayHint",
            new InlayHintParams(
                new TextDocumentIdentifier(uri),
                new Range(new Position(first, 0), new Position(last, 0))),
            cancellation);

    /// <summary>Resolves one completion item, which fills in the documentation of what the item is for.</summary>
    public Task<CompletionItem> ResolveAsync(CompletionItem item, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<CompletionItem>("completionItem/resolve", item, cancellation);

    /// <summary>
    /// Turns the cycle-count hints on or off for the session, and returns whether they are now on.
    /// </summary>
    public Task<bool> ToggleCycleHintsAsync(CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<bool>("nt65/toggleCycleHints", new { }, cancellation);

    /// <summary>Waits for the server to ask for the inlay hints to be fetched again.</summary>
    public async Task NextHintsRefreshAsync(CancellationToken cancellation) =>
        await notifications.HintsRefreshed.Reader.ReadAsync(cancellation);

    /// <summary>
    /// Requests the semantic tokens that classify the names on the lines <paramref name="first"/>
    /// to <paramref name="last"/>.
    /// </summary>
    public Task<SemanticTokens> SemanticTokensRangeAsync(
        string uri, int first, int last, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<SemanticTokens>("textDocument/semanticTokens/range",
            new SemanticTokensRangeParams(
                new TextDocumentIdentifier(uri),
                new Range(new Position(first, 0), new Position(last, 0))),
            cancellation);

    /// <summary>
    /// Requests the changes to the document's semantic tokens since the result
    /// <paramref name="previous"/>.
    /// </summary>
    public Task<SemanticTokensDelta> SemanticTokensDeltaAsync(
        string uri, string previous, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<SemanticTokensDelta>("textDocument/semanticTokens/full/delta",
            new SemanticTokensDeltaParams(new TextDocumentIdentifier(uri), previous), cancellation);

    /// <summary>Requests the chain of ranges that a selection at <paramref name="position"/> widens through.</summary>
    public Task<IReadOnlyList<SelectionRange>> SelectionRangesAsync(
        string uri, Position position, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<IReadOnlyList<SelectionRange>>("textDocument/selectionRange",
            new SelectionRangeParams(new TextDocumentIdentifier(uri), [position]), cancellation);

    /// <summary>Requests the document's foldable ranges.</summary>
    public Task<IReadOnlyList<FoldingRange>> FoldingRangesAsync(string uri, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<IReadOnlyList<FoldingRange>>("textDocument/foldingRange",
            new FoldingRangeParams(new TextDocumentIdentifier(uri)), cancellation);

    /// <summary>Requests the hover text for the name at a position in the document.</summary>
    public Task<Hover?> HoverAsync(string uri, Position position, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<Hover?>("textDocument/hover",
            new TextDocumentPositionParams(new TextDocumentIdentifier(uri), position), cancellation);

    /// <summary>Requests the location where the name at a position is declared.</summary>
    public Task<Location?> DefinitionAsync(string uri, Position position, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<Location?>("textDocument/definition",
            new TextDocumentPositionParams(new TextDocumentIdentifier(uri), position), cancellation);

    /// <summary>Requests every location where the name at a position appears.</summary>
    public Task<IReadOnlyList<Location>> ReferencesAsync(
        string uri, Position position, bool includeDeclaration, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<IReadOnlyList<Location>>("textDocument/references",
            new ReferenceParams(new TextDocumentIdentifier(uri), position, new ReferenceContext(includeDeclaration)),
            cancellation);

    /// <summary>Requests the locations where the name at a position appears, as the client highlights them.</summary>
    public Task<IReadOnlyList<DocumentHighlight>> HighlightsAsync(
        string uri, Position position, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<IReadOnlyList<DocumentHighlight>>("textDocument/documentHighlight",
            new TextDocumentPositionParams(new TextDocumentIdentifier(uri), position), cancellation);

    /// <summary>Requests the range that a rename at a position would replace.</summary>
    public Task<Range?> PrepareRenameAsync(string uri, Position position, CancellationToken cancellation) =>
        rpc.InvokeWithParameterObjectAsync<Range?>("textDocument/prepareRename",
            new TextDocumentPositionParams(new TextDocumentIdentifier(uri), position), cancellation);

    /// <summary>Requests the edit that renames the name at a position everywhere it appears.</summary>
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

    /// <summary>
    /// Provides the wait the suite uses by default. It takes no real time, but the server still
    /// continues asynchronously instead of on the caller's stack.
    /// </summary>
    private static async Task Yield(TimeSpan quiet) => await Task.Yield();

    /// <summary>Collects the notifications and requests the server sends without being asked.</summary>
    private sealed class Notifications
    {
        public Channel<PublishDiagnosticsParams> Published { get; } =
            Channel.CreateUnbounded<PublishDiagnosticsParams>();

        public Channel<LogMessageParams> Logged { get; } = Channel.CreateUnbounded<LogMessageParams>();

        public Channel<bool> TokensRefreshed { get; } = Channel.CreateUnbounded<bool>();

        public Channel<bool> HintsRefreshed { get; } = Channel.CreateUnbounded<bool>();

        public Channel<JsonElement> OutputChanged { get; } = Channel.CreateUnbounded<JsonElement>();

        public Channel<ShowMessageParams> Shown { get; } = Channel.CreateUnbounded<ShowMessageParams>();

        [JsonRpcMethod("window/showMessage", UseSingleObjectParameterDeserialization = true)]
        public void OnShowMessage(ShowMessageParams parameters) => Shown.Writer.TryWrite(parameters);

        [JsonRpcMethod("nt65/outputChanged", UseSingleObjectParameterDeserialization = true)]
        public void OnOutputChanged(JsonElement parameters) => OutputChanged.Writer.TryWrite(parameters);

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

        [JsonRpcMethod("workspace/inlayHint/refresh")]
        public object? OnRefreshHints()
        {
            HintsRefreshed.Writer.TryWrite(true);
            return null;
        }
    }
}
