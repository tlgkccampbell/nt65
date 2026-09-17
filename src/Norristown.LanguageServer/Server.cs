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
    private JsonRpc? rpc;

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
        var capabilities = new ServerCapabilities(
            new TextDocumentSyncOptions(OpenClose: true, TextDocumentSyncKind.Incremental),
            DocumentSymbolProvider: true,
            FoldingRangeProvider: true,
            HoverProvider: true,
            DefinitionProvider: true,
            ReferencesProvider: true,
            DocumentHighlightProvider: true,
            RenameProvider: new RenameOptions(PrepareProvider: true),
            CompletionProvider: new CompletionOptions([":", "@", "!", "(", ","]),
            SignatureHelpProvider: new SignatureHelpOptions(["(", ",", "="]),
            InlayHintProvider: true,
            WorkspaceSymbolProvider: true,
            CodeActionProvider: true);
        return new InitializeResult(capabilities, new ServerInfo("Norristown Assembler", "0.0.0"));
    }

    [JsonRpcMethod("initialized")]
    public Task InitializedAsync(JsonElement _) =>
        rpc!.NotifyWithParameterObjectAsync("window/logMessage",
            new LogMessageParams(MessageType.Info, "Norristown language server ready"));

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
        log.Write($"opened {document.Uri} ({document.Tree.Lines.Length} lines)");
        return PublishDiagnosticsAsync();
    }

    [JsonRpcMethod("textDocument/didChange")]
    public Task DidChangeAsync(DidChangeTextDocumentParams request)
    {
        if (workspace.Change(request.TextDocument, request.ContentChanges) is null)
        {
            log.Write($"change to a document that is not open: {request.TextDocument.Uri}");
            return Task.CompletedTask;
        }

        // An edit in one file can change what is wrong with another, so every open
        // document is republished rather than just the one that changed.
        return PublishDiagnosticsAsync();
    }

    [JsonRpcMethod("textDocument/didClose")]
    public Task DidCloseAsync(DidCloseTextDocumentParams request)
    {
        workspace.Close(request.TextDocument.Uri);
        log.Write($"closed {request.TextDocument.Uri}");

        // The client holds what was last published until it is told otherwise, and a closed
        // document is no longer the server's to report on.
        return rpc!.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics",
            new PublishDiagnosticsParams(request.TextDocument.Uri, null, []));
    }

    [JsonRpcMethod("textDocument/documentSymbol")]
    public IReadOnlyList<DocumentSymbol> DocumentSymbols(DocumentSymbolParams request) =>
        workspace.Find(request.TextDocument.Uri) is { } document ? Lsp.ToSymbols(document.Tree) : [];

    [JsonRpcMethod("textDocument/foldingRange")]
    public IReadOnlyList<FoldingRange> FoldingRanges(FoldingRangeParams request) =>
        workspace.Find(request.TextDocument.Uri) is { } document ? Lsp.ToFoldingRanges(document.Tree) : [];

    [JsonRpcMethod("textDocument/hover")]
    public Hover? Hover(TextDocumentPositionParams request) =>
        At(request) is { } asked
            ? Lsp.ToHover(
                asked.Model,
                asked.Analysis.LayoutFor(asked.Model.Tree.Path),
                asked.Analysis.FlowFor(asked.Model.Tree.Path),
                asked.Analysis.StatesFor(asked.Model.Tree.Path),
                asked.Position)
            : null;

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
        At(request) is { } asked ? LanguageServer.Completion.At(asked.Program, asked.Model, asked.Position) : [];

    [JsonRpcMethod("textDocument/signatureHelp")]
    public SignatureHelp? SignatureHelp(TextDocumentPositionParams request) =>
        At(request) is { } asked ? CallHelp.At(asked.Program, asked.Model, asked.Position) : null;

    [JsonRpcMethod("textDocument/inlayHint")]
    public IReadOnlyList<InlayHint> InlayHints(InlayHintParams request)
    {
        if (At(new TextDocumentPositionParams(request.TextDocument, request.Range.Start)) is not { } asked)
            return [];
        var tree = asked.Model.Tree;
        var end = tree.GetPosition(request.Range.End.Line, request.Range.End.Character);
        return LanguageServer.InlayHints.In(
            asked.Model,
            asked.Analysis.LayoutFor(tree.Path),
            asked.Analysis.FlowFor(tree.Path),
            asked.Analysis.StatesFor(tree.Path),
            new Syntax.TextSpan(asked.Position, Math.Max(0, end - asked.Position)));
    }

    [JsonRpcMethod("textDocument/codeAction")]
    public IReadOnlyList<CodeAction> CodeActions(CodeActionParams request)
    {
        if (At(new TextDocumentPositionParams(request.TextDocument, request.Range.Start)) is not { } asked)
            return [];
        return LanguageServer.CodeActions.In(asked.Analysis, asked.Model, request.Range);
    }

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

    /// <summary>Publishes what is wrong with every open document, after anything changes.</summary>
    private async Task PublishDiagnosticsAsync()
    {
        foreach (var document in workspace.Open())
        {
            var analysis = workspace.AnalysisFor(document.Tree.Path);
            await rpc!.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics",
                new PublishDiagnosticsParams(document.Uri, document.Version,
                    Lsp.ToDiagnostics(
                        analysis.DiagnosticsFor(document.Tree.Path), document.Tree, analysis.Configuration)));
        }
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
