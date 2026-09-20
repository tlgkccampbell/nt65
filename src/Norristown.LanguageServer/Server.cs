using System.Text.Json;
using System.Text.Json.Serialization;
using Norristown.LanguageServer.Protocol;
using Norristown.Semantics;
using StreamJsonRpc;

namespace Norristown.LanguageServer;

// The Protocol folder holds hand-written LSP types, only for the messages the server
// handles. Property names are camel-cased by the formatter.

internal sealed class Server
{
    private readonly ServerLog log;
    private readonly Workspace workspace = new();

    // What was last published for each URI, so that a file nobody has open is sent again only
    // when what is wrong with it changed.
    private readonly Dictionary<string, string> published = new(StringComparer.Ordinal);
    private JsonRpc? rpc;

    // Whether the client may be asked to fetch semantic tokens again, which an edit in one file
    // needs when it changes what a name in another refers to.
    private bool refreshesTokens;
    private bool refreshesLenses;

    private Server(ServerLog log) => this.log = log;

    /// <summary>Serves one client until it sends <c>exit</c> or disconnects.</summary>
    public static async Task RunAsync(Stream input, Stream output, ServerLog log)
    {
        var server = new Server(log);
        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(output, input, CreateFormatter()));
        server.rpc = rpc;
        rpc.AddLocalRpcTarget(server, new JsonRpcTargetOptions { UseSingleObjectParameterDeserialization = true });
        rpc.StartListening();
        try
        {
            await rpc.Completion;
        }
        catch (Exception e) when (e is ConnectionLostException or ObjectDisposedException)
        {
            // The client went away without `exit`; nothing to clean up yet.
        }
    }

    [JsonRpcMethod("initialize")]
    public InitializeResult Initialize(InitializeParams request)
    {
        var client = request.ClientInfo is { } info ? $"{info.Name} {info.Version}".TrimEnd() : "unknown client";
        log.Write($"connected: {client}");

        // The projects are found in the folders the client opened, and built as the configuration
        // the client's settings choose: a project's files are the program a name is resolved against.
        IReadOnlyList<string> roots = request.WorkspaceFolders is { Count: > 0 } folders
            ? [.. folders.Select(folder => folder.Uri)]
            : request.RootUri is { } root ? [root] : [];
        workspace.Load(roots, ActiveConfiguration(request.InitializationOptions));
        refreshesTokens = Refreshes(request.Capabilities, "semanticTokens");
        refreshesLenses = Refreshes(request.Capabilities, "codeLens");
        var capabilities = new ServerCapabilities(
            new TextDocumentSyncOptions(OpenClose: true, TextDocumentSyncKind.Incremental),
            DocumentSymbolProvider: true,
            FoldingRangeProvider: true,
            HoverProvider: true,
            DefinitionProvider: true,
            ReferencesProvider: true,
            DocumentHighlightProvider: true,
            RenameProvider: new RenameOptions(PrepareProvider: true),
            CompletionProvider: new CompletionOptions([" ", ".", ":", "@", "!", "#", "(", "[", ","]),
            SignatureHelpProvider: new SignatureHelpOptions(["(", ",", "="]),
            CodeLensProvider: new CodeLensOptions(ResolveProvider: false),
            WorkspaceSymbolProvider: true,
            CodeActionProvider: new CodeActionOptions(CodeActionKinds.All),
            SemanticTokensProvider: new SemanticTokensOptions(NameHighlighting.Legend, Full: true),
            CallHierarchyProvider: true,
            DocumentLinkProvider: new DocumentLinkOptions(ResolveProvider: false),
            DocumentFormattingProvider: true,
            DocumentRangeFormattingProvider: true);
        return new InitializeResult(capabilities, new ServerInfo("Norristown Assembler", "0.0.0"));
    }

    /// <summary>
    /// The client is ready. What is wrong with every file of every project is published
    /// straight away, so a broken export shows in the Problems panel before anything is opened.
    /// </summary>
    [JsonRpcMethod("initialized")]
    public async Task InitializedAsync(JsonElement _)
    {
        await rpc!.NotifyWithParameterObjectAsync("window/logMessage",
            new LogMessageParams(MessageType.Info, "Norristown language server ready"));
        await PublishDiagnosticsAsync();
    }

    /// <summary>The client's settings changed: the project is read again as the configuration they now choose.</summary>
    [JsonRpcMethod("workspace/didChangeConfiguration")]
    public Task DidChangeConfigurationAsync(JsonElement request)
    {
        var settings = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("settings", out var given)
            && given.ValueKind == JsonValueKind.Object && given.TryGetProperty("nt65", out var own)
            ? own
            : (JsonElement?)null;
        var configuration = ActiveConfiguration(settings);
        log.Write($"configuration: {configuration ?? "the project's own"}");
        workspace.Configure(configuration);
        return PublishDiagnosticsAsync();
    }

    /// <summary>
    /// Files changed on disk: a project file, a source no one has open, or a file an <c>.incbin</c>
    /// measured. What is wrong is published again when any of them is one a program reads.
    /// </summary>
    [JsonRpcMethod("workspace/didChangeWatchedFiles")]
    public Task DidChangeWatchedFilesAsync(DidChangeWatchedFilesParams request)
    {
        if (!workspace.ChangedOnDisk(request.Changes.Select(change => change.Uri)))
            return Task.CompletedTask;
        log.Write($"changed on disk: {string.Join(", ", request.Changes.Select(change => change.Uri))}");
        return PublishDiagnosticsAsync();
    }

    /// <summary>The named configurations the workspace's projects have, for the client to offer.</summary>
    [JsonRpcMethod("nt65/configurations")]
    public IReadOnlyList<string> Configurations(JsonElement _) => workspace.Configurations();

    [JsonRpcMethod("textDocument/didOpen")]
    public Task DidOpenAsync(DidOpenTextDocumentParams request)
    {
        var document = workspace.Open(request.TextDocument);
        log.Write($"opened {document.Uri} ({document.Tree.LineCount} lines)");
        return PublishDiagnosticsAsync(document.Uri);
    }

    [JsonRpcMethod("textDocument/didChange")]
    public Task DidChangeAsync(DidChangeTextDocumentParams request)
    {
        if (workspace.Change(request.TextDocument, request.ContentChanges) is null)
        {
            log.Write($"change to a document that is not open: {request.TextDocument.Uri}");
            return Task.CompletedTask;
        }

        // An edit in one file can change what is wrong with another, so every file of the
        // program is republished rather than just the one that changed.
        return PublishDiagnosticsAsync(request.TextDocument.Uri);
    }

    [JsonRpcMethod("textDocument/didClose")]
    public Task DidCloseAsync(DidCloseTextDocumentParams request)
    {
        workspace.Close(request.TextDocument.Uri);
        log.Write($"closed {request.TextDocument.Uri}");

        // A closed file of a program is still reported on; one that belonged to no program
        // leaves with the document, and is cleared by whatever it is no longer among.
        return PublishDiagnosticsAsync();
    }

    [JsonRpcMethod("textDocument/documentSymbol")]
    public IReadOnlyList<DocumentSymbol> DocumentSymbols(DocumentSymbolParams request) =>
        workspace.Find(request.TextDocument.Uri) is { } document ? Lsp.ToSymbols(document.Tree) : [];

    [JsonRpcMethod("textDocument/foldingRange")]
    public IReadOnlyList<FoldingRange> FoldingRanges(FoldingRangeParams request) =>
        workspace.Find(request.TextDocument.Uri) is { } document ? Lsp.ToFoldingRanges(document.Tree) : [];

    [JsonRpcMethod("textDocument/hover")]
    public Hover? Hover(TextDocumentPositionParams request) =>
        At(request) is { } asked ? Lsp.ToHover(asked.Analysis, asked.Model, asked.Position) : null;

    [JsonRpcMethod("textDocument/definition")]
    public Location? Definition(TextDocumentPositionParams request) =>
        At(request) is { } asked ? Lsp.ToDefinition(asked.Program, asked.Model, asked.Position) : null;

    [JsonRpcMethod("textDocument/references")]
    public IReadOnlyList<Location> References(ReferenceParams request) =>
        At(request) is { } asked
            ? Lsp.ToReferences(asked.Program, asked.Model, asked.Position, request.Context.IncludeDeclaration)
            : [];

    [JsonRpcMethod("textDocument/documentHighlight")]
    public IReadOnlyList<DocumentHighlight> DocumentHighlights(TextDocumentPositionParams request) =>
        At(request) is { } asked ? Lsp.ToHighlights(asked.Model, asked.Position) : [];

    /// <summary>What a rename would replace, which a client asks for before offering one.</summary>
    [JsonRpcMethod("textDocument/prepareRename")]
    public Protocol.Range? PrepareRename(TextDocumentPositionParams request) =>
        At(request) is { } asked ? Lsp.ToRenameRange(asked.Model, asked.Position) : null;

    [JsonRpcMethod("textDocument/rename")]
    public WorkspaceEdit? Rename(RenameParams request)
    {
        if (At(request) is not { } asked)
            return null;

        // A name the language will not accept is the client's to show and the programmer's
        // to correct, so it comes back as a failed request rather than as an empty edit.
        var (edit, problem) = Lsp.ToRename(asked.Program, asked.Model, asked.Position, request.NewName);
        return problem is null ? edit : throw new LocalRpcException(problem);
    }

    [JsonRpcMethod("textDocument/completion")]
    public IReadOnlyList<CompletionItem> Completion(TextDocumentPositionParams request) =>
        At(request) is { } asked
            ? LanguageServer.Completion.At(asked.Program, asked.Model, asked.Analysis.Cpu, asked.Position)
            : [];

    [JsonRpcMethod("textDocument/signatureHelp")]
    public SignatureHelp? SignatureHelp(TextDocumentPositionParams request) =>
        At(request) is { } asked ? CallHelp.At(asked.Program, asked.Model, asked.Position) : null;

    [JsonRpcMethod("textDocument/codeLens")]
    public IReadOnlyList<CodeLens> CodeLenses(CodeLensParams request)
    {
        if (workspace.Find(request.TextDocument.Uri) is not { } document)
            return [];
        var path = document.Tree.Path;
        return LanguageServer.CodeLenses.In(document.Tree, workspace.AnalysisFor(path).FlowFor(path));
    }

    [JsonRpcMethod("textDocument/documentLink")]
    public IReadOnlyList<DocumentLink> DocumentLinks(DocumentLinkParams request) =>
        Model(request.TextDocument.Uri) is { } model ? LanguageServer.DocumentLinks.In(model) : [];

    /// <summary>
    /// The file laid out as nt65 writes one. It needs no analysis: what a line is written at is
    /// what its own file's braces say, so a file with a mistake in it still formats.
    /// </summary>
    [JsonRpcMethod("textDocument/formatting")]
    public IReadOnlyList<TextEdit> Formatting(DocumentFormattingParams request) =>
        workspace.Find(request.TextDocument.Uri) is { } document
            ? Lsp.ToFormatting(document.Tree, 0, document.Tree.LineCount - 1)
            : [];

    /// <summary>
    /// The chosen lines laid out. The whole file decides where they go — a run of data lines
    /// says together where its column is — and only the chosen ones move.
    /// </summary>
    [JsonRpcMethod("textDocument/rangeFormatting")]
    public IReadOnlyList<TextEdit> RangeFormatting(DocumentRangeFormattingParams request) =>
        workspace.Find(request.TextDocument.Uri) is { } document
            ? Lsp.ToFormatting(document.Tree, request.Range.Start.Line, request.Range.End.Line)
            : [];

    [JsonRpcMethod("textDocument/prepareCallHierarchy")]
    public IReadOnlyList<CallHierarchyItem> PrepareCallHierarchy(CallHierarchyPrepareParams request) =>
        At(new TextDocumentPositionParams(request.TextDocument, request.Position)) is { } asked
            ? LanguageServer.CallHierarchy.Prepare(asked.Analysis, asked.Model, asked.Position)
            : [];

    /// <summary>
    /// What calls a routine, and what it calls. The item comes back from the client as the
    /// server gave it, so the program it belongs to is found from the file it names.
    /// </summary>
    [JsonRpcMethod("callHierarchy/incomingCalls")]
    public IReadOnlyList<CallHierarchyIncomingCall> IncomingCalls(CallHierarchyIncomingCallsParams request) =>
        LanguageServer.CallHierarchy.Incoming(workspace.AnalysisFor(Workspace.PathOf(request.Item.Uri)), request.Item);

    [JsonRpcMethod("callHierarchy/outgoingCalls")]
    public IReadOnlyList<CallHierarchyOutgoingCall> OutgoingCalls(CallHierarchyOutgoingCallsParams request) =>
        LanguageServer.CallHierarchy.Outgoing(workspace.AnalysisFor(Workspace.PathOf(request.Item.Uri)), request.Item);

    [JsonRpcMethod("textDocument/codeAction")]
    public IReadOnlyList<CodeAction> CodeActions(CodeActionParams request)
    {
        if (At(new TextDocumentPositionParams(request.TextDocument, request.Range.Start)) is not { } asked)
            return [];
        return LanguageServer.CodeActions.In(asked.Analysis, asked.Model, request.Range, request.Context.Only);
    }

    [JsonRpcMethod("textDocument/semanticTokens/full")]
    public Protocol.SemanticTokens SemanticTokens(SemanticTokensParams request) =>
        workspace.Find(request.TextDocument.Uri) is { } document
            && workspace.AnalysisFor(document.Tree.Path).ModelFor(document.Tree.Path) is { } model
            ? NameHighlighting.In(model)
            : new Protocol.SemanticTokens([]);

    [JsonRpcMethod("workspace/symbol")]
    public IReadOnlyList<SymbolInformation> WorkspaceSymbols(WorkspaceSymbolParams request) =>
        LanguageServer.WorkspaceSymbols.Matching(workspace.Files(), request.Query);

    [JsonRpcMethod("shutdown")]
    public object? Shutdown() => null;

    [JsonRpcMethod("exit")]
    public void Exit() => rpc!.Dispose();

    /// <summary>The named configuration the client's <c>nt65</c> settings choose, or null for the project's own settings.</summary>
    private static string? ActiveConfiguration(JsonElement? settings) =>
        settings is { ValueKind: JsonValueKind.Object } options
            && options.TryGetProperty("configuration", out var named)
            && named.ValueKind == JsonValueKind.String && named.GetString() is { Length: > 0 } name
            ? name
            : null;

    internal static SystemTextJsonFormatter CreateFormatter()
    {
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        return formatter;
    }

    /// <summary>
    /// Publishes what is wrong with every file of every program, after anything changes, and
    /// asks the client for the names' classes again, since what a name refers to may have
    /// changed too. Not only the open ones: an export broken in one file is what is wrong with
    /// every module that uses it, and none of them may be open.
    /// </summary>
    /// <param name="changed">
    /// The document the client has just opened or edited, which is always published: the
    /// client asked, and hears back.
    /// </param>
    private async Task PublishDiagnosticsAsync(string? changed = null)
    {
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in workspace.ToPublish())
        {
            current.Add(file.Uri);
            var diagnostics = Lsp.ToDiagnostics(file.Diagnostics, file.Tree, file.Configuration);

            // A program may hold hundreds of files and every keystroke re-analyzes it, so
            // every file but the one the client just asked about is sent again only when what
            // is wrong with it changed.
            var said = Signature(diagnostics);
            if (file.Uri != changed && published.TryGetValue(file.Uri, out var before) && before == said)
                continue;
            published[file.Uri] = said;
            await rpc!.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics",
                new PublishDiagnosticsParams(file.Uri, file.Version, diagnostics));
        }

        // The client holds what it was last told until it is told otherwise, so a file that
        // left the program, or that the client named differently, is emptied by hand.
        foreach (var gone in published.Keys.Where(uri => !current.Contains(uri)).ToList())
        {
            published.Remove(gone);
            await rpc!.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics",
                new PublishDiagnosticsParams(gone, null, []));
        }
        if (refreshesTokens)
            _ = RefreshAsync("workspace/semanticTokens/refresh", "semantic tokens");

        // What a routine costs with its calls is worked out across the program, so an edit to
        // one file moves what the lenses of another say.
        if (refreshesLenses)
            _ = RefreshAsync("workspace/codeLens/refresh", "code lenses");
    }

    /// <summary>
    /// What was published for a file, as one string: enough to tell one set of diagnostics
    /// from another, and nothing more, since nothing reads it back.
    /// </summary>
    private static string Signature(IReadOnlyList<Protocol.Diagnostic> diagnostics) =>
        string.Join("\n", diagnostics.Select(d =>
            $"{d.Range.Start.Line}:{d.Range.Start.Character}:{(int)d.Severity}:{d.Message}"));

    /// <summary>Whether the client asks to be told when what it holds of <paramref name="what"/> is stale.</summary>
    private static bool Refreshes(JsonElement? capabilities, string what) =>
        capabilities is { ValueKind: JsonValueKind.Object } given
        && given.TryGetProperty("workspace", out var workspace) && workspace.ValueKind == JsonValueKind.Object
        && workspace.TryGetProperty(what, out var kind) && kind.ValueKind == JsonValueKind.Object
        && kind.TryGetProperty("refreshSupport", out var refresh) && refresh.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Asks the client to fetch something again. It is not waited for: the client answers after
    /// it has asked, and a client that has gone away has nothing to refresh.
    /// </summary>
    private async Task RefreshAsync(string method, string what)
    {
        try
        {
            await rpc!.InvokeWithParameterObjectAsync<object?>(method);
        }
        catch (Exception e) when (e is RemoteInvocationException or ConnectionLostException or ObjectDisposedException)
        {
            log.Write($"{what} refresh failed: {e.Message}");
        }
    }

    /// <summary>What a file the client named means, or null when the program does not hold it.</summary>
    private SemanticModel? Model(string uri)
    {
        var path = workspace.Find(uri) is { } document ? document.Tree.Path : Workspace.PathOf(uri);
        return workspace.AnalysisFor(path).ModelFor(path);
    }

    /// <summary>
    /// What a request points at: the program, the file it is in, and where in that file. Null
    /// when the client never opened the document, or when the program does not hold it.
    /// </summary>
    private Asked? At(TextDocumentPositionParams request)
    {
        if (workspace.Find(request.TextDocument.Uri) is not { } document)
            return null;
        var analysis = workspace.AnalysisFor(document.Tree.Path);
        if (analysis.ModelFor(document.Tree.Path) is not { } model)
            return null;
        return new Asked(
            analysis,
            analysis.Program,
            model,
            document.Tree.GetPosition(request.Position.Line, request.Position.Character));
    }

    /// <summary>One request, resolved to what it is about.</summary>
    /// <param name="Analysis">Everything the program means, for a question about its layout.</param>
    /// <param name="Program">Every file, for a name that crosses one.</param>
    /// <param name="Model">The file the caret is in.</param>
    /// <param name="Position">Where in that file's text.</param>
    private sealed record Asked(
        ProgramAnalysis Analysis, ProgramModel Program, SemanticModel Model, int Position);
}
