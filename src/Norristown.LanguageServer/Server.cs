using System.Text.Json;
using System.Text.Json.Serialization;
using Norristown.LanguageServer.Protocol;
using StreamJsonRpc;

namespace Norristown.LanguageServer;

// The Protocol folder holds hand-written LSP types, only for the messages the server
// handles. Property names are camel-cased by the formatter.

internal sealed class Server
{
    private readonly ServerLog log;
    private readonly Documents documents = new();
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
        var capabilities = new ServerCapabilities(
            new TextDocumentSyncOptions(OpenClose: true, TextDocumentSyncKind.Incremental),
            DocumentSymbolProvider: true,
            FoldingRangeProvider: true,
            HoverProvider: true,
            DefinitionProvider: true,
            ReferencesProvider: true,
            DocumentHighlightProvider: true,
            RenameProvider: new RenameOptions(PrepareProvider: true));
        return new InitializeResult(capabilities, new ServerInfo("Norristown Assembler", "0.0.0"));
    }

    [JsonRpcMethod("initialized")]
    public Task InitializedAsync(JsonElement _) =>
        rpc!.NotifyWithParameterObjectAsync("window/logMessage",
            new LogMessageParams(MessageType.Info, "Norristown language server ready"));

    [JsonRpcMethod("textDocument/didOpen")]
    public Task DidOpenAsync(DidOpenTextDocumentParams request)
    {
        var document = documents.Open(request.TextDocument);
        log.Write($"opened {document.Uri} ({document.Tree.Lines.Length} lines)");
        return PublishDiagnosticsAsync(document);
    }

    [JsonRpcMethod("textDocument/didChange")]
    public Task DidChangeAsync(DidChangeTextDocumentParams request)
    {
        if (documents.Change(request.TextDocument, request.ContentChanges) is not { } document)
        {
            log.Write($"change to a document that is not open: {request.TextDocument.Uri}");
            return Task.CompletedTask;
        }
        return PublishDiagnosticsAsync(document);
    }

    [JsonRpcMethod("textDocument/didClose")]
    public Task DidCloseAsync(DidCloseTextDocumentParams request)
    {
        documents.Close(request.TextDocument.Uri);
        log.Write($"closed {request.TextDocument.Uri}");

        // The client holds what was last published until it is told otherwise, and a closed
        // document is no longer the server's to report on.
        return rpc!.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics",
            new PublishDiagnosticsParams(request.TextDocument.Uri, null, []));
    }

    [JsonRpcMethod("textDocument/documentSymbol")]
    public IReadOnlyList<DocumentSymbol> DocumentSymbols(DocumentSymbolParams request) =>
        documents.Find(request.TextDocument.Uri) is { } document ? Lsp.ToSymbols(document.Tree) : [];

    [JsonRpcMethod("textDocument/foldingRange")]
    public IReadOnlyList<FoldingRange> FoldingRanges(FoldingRangeParams request) =>
        documents.Find(request.TextDocument.Uri) is { } document ? Lsp.ToFoldingRanges(document.Tree) : [];

    [JsonRpcMethod("textDocument/hover")]
    public Hover? Hover(TextDocumentPositionParams request) =>
        Find(request) is { } document ? Lsp.ToHover(document.Model, Offset(document, request)) : null;

    [JsonRpcMethod("textDocument/definition")]
    public Location? Definition(TextDocumentPositionParams request) =>
        Find(request) is { } document
            ? Lsp.ToDefinition(document.Model, document.Uri, Offset(document, request))
            : null;

    [JsonRpcMethod("textDocument/references")]
    public IReadOnlyList<Location> References(ReferenceParams request) =>
        Find(request) is { } document
            ? Lsp.ToReferences(document.Model, document.Uri, Offset(document, request),
                request.Context.IncludeDeclaration)
            : [];

    [JsonRpcMethod("textDocument/documentHighlight")]
    public IReadOnlyList<DocumentHighlight> DocumentHighlights(TextDocumentPositionParams request) =>
        Find(request) is { } document ? Lsp.ToHighlights(document.Model, Offset(document, request)) : [];

    /// <summary>What a rename would replace, which a client asks for before offering one.</summary>
    [JsonRpcMethod("textDocument/prepareRename")]
    public Protocol.Range? PrepareRename(TextDocumentPositionParams request) =>
        Find(request) is { } document ? Lsp.ToRenameRange(document.Model, Offset(document, request)) : null;

    [JsonRpcMethod("textDocument/rename")]
    public WorkspaceEdit? Rename(RenameParams request)
    {
        if (Find(request) is not { } document)
            return null;

        // A name the language will not accept is the client's to show and the programmer's
        // to correct, so it comes back as a failed request rather than as an empty edit.
        var (edit, problem) = Lsp.ToRename(document.Model, document.Uri, Offset(document, request), request.NewName);
        return problem is null ? edit : throw new LocalRpcException(problem);
    }

    [JsonRpcMethod("shutdown")]
    public object? Shutdown() => null;

    [JsonRpcMethod("exit")]
    public void Exit() => rpc!.Dispose();

    internal static SystemTextJsonFormatter CreateFormatter()
    {
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        return formatter;
    }

    private Task PublishDiagnosticsAsync(Document document) =>
        rpc!.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics",
            new PublishDiagnosticsParams(document.Uri, document.Version, Lsp.ToDiagnostics(document.Diagnostics)));

    /// <summary>The document a request names, or null when the client never opened it.</summary>
    private Document? Find(TextDocumentPositionParams request) => documents.Find(request.TextDocument.Uri);

    /// <summary>Where in the document's text the request points.</summary>
    private static int Offset(Document document, TextDocumentPositionParams request) =>
        document.Tree.GetPosition(request.Position.Line, request.Position.Character);
}
