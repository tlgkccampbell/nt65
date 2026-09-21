using System.Collections.Concurrent;
using System.Diagnostics;
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
    /// <summary>How long without an edit before what is wrong with the rest of the program is published.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    private readonly ServerLog log;
    private readonly Framing framing;
    private readonly Workspace workspace = new();

    // What is published for every file but the edited one waits for the typing to stop. A
    // feature that follows the program rather than the caret — the output beside the source —
    // hangs off the same wait, so that it moves when the squiggles do.
    private readonly Debounce settling;

    // The exit code the process is to leave with, once `exit` or the editor's going says so.
    private readonly TaskCompletionSource<int> leaving =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // What was last published for each URI, so that a file nobody has open is sent again only
    // when what is wrong with it changed, and the newest revision the client has sent of each
    // open one, so that nothing is published about text that is gone. A keystroke's own
    // publishing and the settled program's can be under way at once, so both are concurrent.
    private readonly ConcurrentDictionary<string, string> published = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> newest = new(StringComparer.Ordinal);
    private JsonRpc? rpc;

    // What the client can take, and the edge every answer goes out by.
    private ClientCapabilities client = ClientCapabilities.None;
    private Outgoing outgoing;

    // The editor that started this server. It is watched rather than asked: an editor that
    // crashes never sends `exit`, and a server nobody is talking to should not outlive it.
    private Process? parent;

    private Server(ServerLog log, Framing framing, Delay delay)
    {
        this.log = log;
        this.framing = framing;
        settling = new Debounce(Quiet, delay);
        outgoing = new Outgoing(workspace, client);
    }

    /// <summary>
    /// Serves one client until it sends <c>exit</c>, the editor that started it goes, or the
    /// connection is lost, and answers with the code the process is to leave with: 0 where the
    /// client shut it down and said goodbye, 1 where it said goodbye without shutting down, and
    /// 70 where nt65 itself failed, which is what has the editor's client start a new server.
    /// </summary>
    /// <param name="input">Where the client's messages arrive.</param>
    /// <param name="output">Where the server's messages go.</param>
    /// <param name="log">Where the server says what it is doing.</param>
    /// <param name="delay">
    /// How the wait between an edit and publishing the rest of the program is done, for a test
    /// that drives it itself; the real wait where nothing says otherwise.
    /// </param>
    public static async Task<int> RunAsync(Stream input, Stream output, ServerLog log, Delay? delay = null)
    {
        using var framing = new Framing(input, output, CreateFormatter());
        var server = new Server(log, framing, delay ?? Task.Delay);
        using var rpc = new JsonRpc(framing);
        server.rpc = rpc;
        rpc.AddLocalRpcTarget(server, new JsonRpcTargetOptions { UseSingleObjectParameterDeserialization = true });
        rpc.StartListening();
        try
        {
            // `exit` is answered by the process leaving, which the handler cannot do from
            // inside the dispatch it is running in, so it says so here instead.
            if (await Task.WhenAny(rpc.Completion, server.leaving.Task).ConfigureAwait(false) == server.leaving.Task)
                return await server.leaving.Task.ConfigureAwait(false);
            await rpc.Completion.ConfigureAwait(false);
            return 0;
        }
        catch (Exception e) when (e is ConnectionLostException or ObjectDisposedException)
        {
            // The client went away without `exit`; nothing to clean up yet.
            return 0;
        }
        catch (Exception e)
        {
            // The handler of last resort: the loop itself failed, which is a bug in nt65. It
            // goes to the log for the report and to the person for the news, and the process
            // leaves with a failure so that the client does not talk to a server that is gone.
            log.Write($"internal error: {e}");
            await server.ShowAsync(MessageType.Error, $"nt65: internal error: {e.Message}").ConfigureAwait(false);
            return 70;
        }
        finally
        {
            server.parent?.Dispose();
        }
    }

    [JsonRpcMethod("initialize")]
    public InitializeResult Initialize(InitializeParams request)
    {
        var named = request.ClientInfo is { } info ? $"{info.Name} {info.Version}".TrimEnd() : "unknown client";
        log.Write($"connected: {named}");

        // The projects are found in the folders the client opened, and built as the configuration
        // the client's settings choose: a project's files are the program a name is resolved against.
        IReadOnlyList<string> roots = request.WorkspaceFolders is { Count: > 0 } folders
            ? [.. folders.Select(folder => folder.Uri)]
            : request.RootUri is { } root ? [root] : [];
        workspace.Load(roots, ActiveConfiguration(request.InitializationOptions));
        client = ClientCapabilities.Of(request.Capabilities);
        outgoing = new Outgoing(workspace, client);
        Watch(request.ProcessId);
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
            DocumentRangeFormattingProvider: true,

            // A position is a UTF-16 offset in a line, which is what the protocol's own default
            // is and what nt65 has always measured; saying so lets a client that would rather
            // count differently know not to.
            PositionEncoding: "utf-16",

            // A client that opened folders is asked to say when they change, so a folder added
            // to the workspace brings its projects with it.
            Workspace: client.WorkspaceFolders
                ? new WorkspaceServerCapabilities(new WorkspaceFoldersServerCapabilities(true, true))
                : null);
        return new InitializeResult(capabilities, new ServerInfo("Norristown Assembler", "0.0.0"));
    }

    /// <summary>
    /// The client is ready. What is wrong with every file of every project is published
    /// straight away, so a broken export shows in the Problems panel before anything is opened.
    /// </summary>
    [JsonRpcMethod("initialized")]
    public async Task InitializedAsync(JsonElement _, CancellationToken cancellation)
    {
        await rpc!.NotifyWithParameterObjectAsync("window/logMessage",
            new LogMessageParams(MessageType.Info, "Norristown language server ready")).ConfigureAwait(false);
        await PublishEverythingAsync(null, cancellation, refresh: false).ConfigureAwait(false);
    }

    /// <summary>The client's settings changed: the project is read again as the configuration they now choose.</summary>
    [JsonRpcMethod("workspace/didChangeConfiguration")]
    public Task DidChangeConfigurationAsync(JsonElement request, CancellationToken cancellation)
    {
        var settings = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("settings", out var given)
            && given.ValueKind == JsonValueKind.Object && given.TryGetProperty("nt65", out var own)
            ? own
            : (JsonElement?)null;
        var configuration = ActiveConfiguration(settings);
        log.Write($"configuration: {configuration ?? "the project's own"}");
        workspace.Configure(configuration);
        return PublishEverythingAsync(null, cancellation);
    }

    /// <summary>
    /// A folder was added to the workspace or taken out of it. The projects in the folders the
    /// client now has are the workspace, so they are looked for again from scratch.
    /// </summary>
    [JsonRpcMethod("workspace/didChangeWorkspaceFolders")]
    public Task DidChangeWorkspaceFoldersAsync(
        DidChangeWorkspaceFoldersParams request, CancellationToken cancellation)
    {
        var folders = workspace.WithFolders(
            request.Event.Added.Select(folder => folder.Uri),
            request.Event.Removed.Select(folder => folder.Uri));
        log.Write($"workspace folders: {(folders.Count == 0 ? "none" : string.Join(", ", folders))}");
        return PublishEverythingAsync(null, cancellation);
    }

    /// <summary>
    /// Files changed on disk: a project file, a source no one has open, or a file an <c>.incbin</c>
    /// measured. What is wrong is published again when any of them is one a program reads.
    /// </summary>
    [JsonRpcMethod("workspace/didChangeWatchedFiles")]
    public Task DidChangeWatchedFilesAsync(DidChangeWatchedFilesParams request, CancellationToken cancellation)
    {
        if (!workspace.ChangedOnDisk(request.Changes.Select(change => change.Uri)))
            return Task.CompletedTask;
        log.Write($"changed on disk: {string.Join(", ", request.Changes.Select(change => change.Uri))}");
        return PublishEverythingAsync(null, cancellation);
    }

    /// <summary>The named configurations the workspace's projects have, for the client to offer.</summary>
    [JsonRpcMethod("nt65/configurations")]
    public IReadOnlyList<string> Configurations(JsonElement _) => workspace.Configurations();

    [JsonRpcMethod("textDocument/didOpen")]
    public async Task DidOpenAsync(DidOpenTextDocumentParams request, CancellationToken cancellation)
    {
        var document = workspace.Open(request.TextDocument);
        newest[document.Uri] = document.Version;
        log.Write($"opened {document.Uri} ({document.Tree.LineCount} lines)");
        await PublishOwnAsync(document.Uri, cancellation).ConfigureAwait(false);
        PublishTheRestSoon(document.Uri);
    }

    [JsonRpcMethod("textDocument/didChange")]
    public async Task DidChangeAsync(DidChangeTextDocumentParams request, CancellationToken cancellation)
    {
        if (workspace.Change(request.TextDocument, request.ContentChanges) is null)
        {
            log.Write($"change to a document that is not open: {request.TextDocument.Uri}");
            return;
        }

        // The file the caret is in hears at once, from its own analysis; the rest of the
        // program hears once the typing stops, because an edit in one file can change what is
        // wrong with another and a squiggle that comes and goes on every keystroke is worse
        // than one that arrives a moment late.
        newest[request.TextDocument.Uri] = request.TextDocument.Version;
        await PublishOwnAsync(request.TextDocument.Uri, cancellation).ConfigureAwait(false);
        PublishTheRestSoon(request.TextDocument.Uri);
    }

    [JsonRpcMethod("textDocument/didClose")]
    public Task DidCloseAsync(DidCloseTextDocumentParams request, CancellationToken cancellation)
    {
        workspace.Close(request.TextDocument.Uri);
        newest.TryRemove(request.TextDocument.Uri, out _);
        log.Write($"closed {request.TextDocument.Uri}");

        // A closed file of a program is still reported on; one that belonged to no program
        // leaves with the document, and is cleared by whatever it is no longer among.
        return PublishEverythingAsync(null, cancellation);
    }

    /// <summary>
    /// What a file declares. A client that takes a tree gets the one the segments and scopes
    /// make; one that does not gets the flat list the protocol had first.
    /// </summary>
    [JsonRpcMethod("textDocument/documentSymbol")]
    public object DocumentSymbols(DocumentSymbolParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return outgoing.Spell(
            request.TextDocument.Uri,
            workspace.Find(request.TextDocument.Uri) is { } document ? Lsp.ToSymbols(document.Tree) : []);
    }

    [JsonRpcMethod("textDocument/foldingRange")]
    public IReadOnlyList<FoldingRange> FoldingRanges(FoldingRangeParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return workspace.Find(request.TextDocument.Uri) is { } document ? Lsp.ToFoldingRanges(document.Tree) : [];
    }

    [JsonRpcMethod("textDocument/hover")]
    public Hover? Hover(TextDocumentPositionParams request, CancellationToken cancellation) =>
        At(request, cancellation) is { } asked ? Lsp.ToHover(asked.Analysis, asked.Model, asked.Position) : null;

    [JsonRpcMethod("textDocument/definition")]
    public Location? Definition(TextDocumentPositionParams request, CancellationToken cancellation) =>
        At(request, cancellation) is { } asked
            && Lsp.ToDefinition(asked.Program, asked.Model, asked.Position) is { } where
            ? outgoing.Spell(where)
            : null;

    [JsonRpcMethod("textDocument/references")]
    public IReadOnlyList<Location> References(ReferenceParams request, CancellationToken cancellation) =>
        At(request, cancellation) is { } asked
            ? outgoing.Spell(
                Lsp.ToReferences(asked.Program, asked.Model, asked.Position, request.Context.IncludeDeclaration))
            : [];

    [JsonRpcMethod("textDocument/documentHighlight")]
    public IReadOnlyList<DocumentHighlight> DocumentHighlights(
        TextDocumentPositionParams request, CancellationToken cancellation) =>
        At(request, cancellation) is { } asked ? Lsp.ToHighlights(asked.Model, asked.Position) : [];

    /// <summary>What a rename would replace, which a client asks for before offering one.</summary>
    [JsonRpcMethod("textDocument/prepareRename")]
    public Protocol.Range? PrepareRename(TextDocumentPositionParams request, CancellationToken cancellation) =>
        At(request, cancellation) is { } asked ? Lsp.ToRenameRange(asked.Model, asked.Position) : null;

    [JsonRpcMethod("textDocument/rename")]
    public WorkspaceEdit? Rename(RenameParams request, CancellationToken cancellation)
    {
        if (At(request, cancellation) is not { } asked)
            return null;

        // A name the language will not accept is the client's to show and the programmer's
        // to correct, so it comes back as a failed request rather than as an empty edit.
        var (edit, problem) = Lsp.ToRename(asked.Program, asked.Model, asked.Position, request.NewName);
        return problem is null ? outgoing.Spell(edit) : throw new LocalRpcException(problem);
    }

    [JsonRpcMethod("textDocument/completion")]
    public IReadOnlyList<CompletionItem> Completion(TextDocumentPositionParams request, CancellationToken cancellation) =>
        At(request, cancellation) is { } asked
            ? LanguageServer.Completion.At(asked.Program, asked.Model, asked.Analysis.Cpu, asked.Position)
            : [];

    [JsonRpcMethod("textDocument/signatureHelp")]
    public SignatureHelp? SignatureHelp(TextDocumentPositionParams request, CancellationToken cancellation) =>
        At(request, cancellation) is { } asked ? CallHelp.At(asked.Program, asked.Model, asked.Position) : null;

    [JsonRpcMethod("textDocument/codeLens")]
    public IReadOnlyList<CodeLens> CodeLenses(CodeLensParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (workspace.Find(request.TextDocument.Uri) is not { } document)
            return [];
        var path = document.Tree.Path;
        return LanguageServer.CodeLenses.In(document.Tree, workspace.AnalysisFor(path).FlowFor(path));
    }

    [JsonRpcMethod("textDocument/documentLink")]
    public IReadOnlyList<DocumentLink> DocumentLinks(DocumentLinkParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return Model(request.TextDocument.Uri) is { } model
            ? outgoing.Spell(LanguageServer.DocumentLinks.In(model))
            : [];
    }

    /// <summary>
    /// The file laid out as nt65 writes one. It needs no analysis: what a line is written at is
    /// what its own file's braces say, so a file with a mistake in it still formats.
    /// </summary>
    [JsonRpcMethod("textDocument/formatting")]
    public IReadOnlyList<TextEdit> Formatting(DocumentFormattingParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return workspace.Find(request.TextDocument.Uri) is { } document
            ? Lsp.ToFormatting(document.Tree, 0, document.Tree.LineCount - 1)
            : [];
    }

    /// <summary>
    /// The chosen lines laid out. The whole file decides where they go — a run of data lines
    /// says together where its column is — and only the chosen ones move.
    /// </summary>
    [JsonRpcMethod("textDocument/rangeFormatting")]
    public IReadOnlyList<TextEdit> RangeFormatting(
        DocumentRangeFormattingParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return workspace.Find(request.TextDocument.Uri) is { } document
            ? Lsp.ToFormatting(document.Tree, request.Range.Start.Line, request.Range.End.Line)
            : [];
    }

    [JsonRpcMethod("textDocument/prepareCallHierarchy")]
    public IReadOnlyList<CallHierarchyItem> PrepareCallHierarchy(
        CallHierarchyPrepareParams request, CancellationToken cancellation) =>
        At(new TextDocumentPositionParams(request.TextDocument, request.Position), cancellation) is { } asked
            ? outgoing.Spell(LanguageServer.CallHierarchy.Prepare(asked.Analysis, asked.Model, asked.Position))
            : [];

    /// <summary>
    /// What calls a routine, and what it calls. The item comes back from the client as the
    /// server gave it, so the program it belongs to is found from the file it names.
    /// </summary>
    [JsonRpcMethod("callHierarchy/incomingCalls")]
    public IReadOnlyList<CallHierarchyIncomingCall> IncomingCalls(
        CallHierarchyIncomingCallsParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return outgoing.Spell(LanguageServer.CallHierarchy.Incoming(
            workspace.AnalysisFor(Workspace.PathOf(request.Item.Uri)), request.Item, cancellation));
    }

    [JsonRpcMethod("callHierarchy/outgoingCalls")]
    public IReadOnlyList<CallHierarchyOutgoingCall> OutgoingCalls(
        CallHierarchyOutgoingCallsParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return outgoing.Spell(LanguageServer.CallHierarchy.Outgoing(
            workspace.AnalysisFor(Workspace.PathOf(request.Item.Uri)), request.Item, cancellation));
    }

    [JsonRpcMethod("textDocument/codeAction")]
    public IReadOnlyList<CodeAction> CodeActions(CodeActionParams request, CancellationToken cancellation)
    {
        var start = new TextDocumentPositionParams(request.TextDocument, request.Range.Start);
        if (At(start, cancellation) is not { } asked)
            return [];
        return outgoing.Spell(
            LanguageServer.CodeActions.In(asked.Analysis, asked.Model, request.Range, request.Context.Only));
    }

    [JsonRpcMethod("textDocument/semanticTokens/full")]
    public Protocol.SemanticTokens SemanticTokens(SemanticTokensParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return workspace.Find(request.TextDocument.Uri) is { } document
            && workspace.AnalysisFor(document.Tree.Path).ModelFor(document.Tree.Path) is { } model
            ? NameHighlighting.In(model)
            : new Protocol.SemanticTokens([]);
    }

    [JsonRpcMethod("workspace/symbol")]
    public IReadOnlyList<SymbolInformation> WorkspaceSymbols(
        WorkspaceSymbolParams request, CancellationToken cancellation) =>
        outgoing.Spell(LanguageServer.WorkspaceSymbols.Matching(workspace.Files(), request.Query, cancellation));

    [JsonRpcMethod("shutdown")]
    public object? Shutdown() => null;

    /// <summary>
    /// Goodbye. The process leaves with 0 where the client shut the server down first and 1
    /// where it did not, which is what the protocol asks for; the loop is told rather than
    /// stopped, because a handler cannot end the dispatch it is running in.
    /// </summary>
    [JsonRpcMethod("exit")]
    public void Exit() => Leave(framing.Phase == ServerPhase.ShuttingDown ? 0 : 1, "the client said goodbye");

    internal static SystemTextJsonFormatter CreateFormatter()
    {
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        return formatter;
    }

    /// <summary>The named configuration the client's <c>nt65</c> settings choose, or null for the project's own settings.</summary>
    private static string? ActiveConfiguration(JsonElement? settings) =>
        settings is { ValueKind: JsonValueKind.Object } options
            && options.TryGetProperty("configuration", out var named)
            && named.ValueKind == JsonValueKind.String && named.GetString() is { Length: > 0 } name
            ? name
            : null;

    /// <summary>
    /// What was published for a file, as one string: enough to tell one set of diagnostics
    /// from another, and nothing more, since nothing reads it back.
    /// </summary>
    private static string Signature(IReadOnlyList<Protocol.Diagnostic> diagnostics) =>
        string.Join("\n", diagnostics.Select(d =>
            $"{d.Range.Start.Line}:{d.Range.Start.Character}:{(int)d.Severity}:{d.Message}"));

    /// <summary>
    /// Watches the editor that started this server, where it named itself. An editor that
    /// crashes never sends <c>exit</c>, and a server nobody is talking to should not outlive
    /// it; one that has already gone is not waited for at all.
    /// </summary>
    /// <param name="processId">The editor's process id, or null where it gave none.</param>
    private void Watch(int? processId)
    {
        if (processId is not { } id || id <= 0)
            return;
        try
        {
            parent = Process.GetProcessById(id);
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) => Leave(1, $"the editor that started it (pid {id}) has gone");
            if (parent.HasExited)
                Leave(1, $"the editor that started it (pid {id}) has gone");
        }
        catch (ArgumentException)
        {
            // There is no such process: the editor went between starting this server and being
            // named by it, which is the case the watch exists for.
            Leave(1, $"the editor that started it (pid {id}) has gone");
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A process this server may not watch. Carrying on is better than leaving over it.
            log.Write($"the editor that started it (pid {id}) cannot be watched: {e.Message}");
        }
    }

    /// <summary>Says that the process is to leave, and why.</summary>
    private void Leave(int code, string why)
    {
        if (leaving.TrySetResult(code))
            log.Write($"{why}; leaving with {code}");
    }

    /// <summary>
    /// What is wrong with the file the client has just opened or edited, published at once,
    /// from the analysis that edit asked for. The file the caret is in is the one the person
    /// is looking at, and it hears without waiting for anything.
    /// </summary>
    private async Task PublishOwnAsync(string uri, CancellationToken cancellation)
    {
        if (workspace.ToPublish(uri) is { } file)
            _ = await SendAsync(file, always: true, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// The rest of the program, once the typing has stopped. <paramref name="changed"/> is the
    /// file the client is editing, which has already heard and does not hear again.
    /// </summary>
    private void PublishTheRestSoon(string changed) =>
        settling.After(async () =>
        {
            try
            {
                await PublishEverythingAsync(changed, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is ConnectionLostException or ObjectDisposedException)
            {
                // The client went away while the typing was settling; nobody is waiting.
                log.Write($"the rest of the program was not published: {e.Message}");
            }
        });

    /// <summary>
    /// Publishes what is wrong with every file of every program. Not only the open ones: an
    /// export broken in one file is what is wrong with every module that uses it, and none of
    /// them may be open.
    /// </summary>
    /// <param name="changed">
    /// The file the client is editing, which has already been published from its own analysis
    /// and is passed over here; null when this is not an edit.
    /// </param>
    /// <param name="cancellation">Asked between files, since a program may hold hundreds.</param>
    /// <param name="refresh">
    /// Whether the client may be asked to fetch what it holds again. It is not worth asking as
    /// the client connects, when it is holding nothing yet.
    /// </param>
    private async Task PublishEverythingAsync(
        string? changed, CancellationToken cancellation, bool refresh = true)
    {
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in workspace.ToPublish())
        {
            cancellation.ThrowIfCancellationRequested();
            current.Add(file.Uri);
            if (file.Uri != changed)
                _ = await SendAsync(file, always: false, cancellation).ConfigureAwait(false);
        }

        // The client holds what it was last told until it is told otherwise, so a file that
        // left the program, or that the client named differently, is emptied by hand. A file
        // that is still in the program is never emptied: its squiggles stand until the ones
        // that replace them arrive.
        foreach (var gone in published.Keys.Where(uri => !current.Contains(uri)).ToList())
        {
            published.TryRemove(gone, out _);
            await rpc!.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics",
                new PublishDiagnosticsParams(gone, null, [])).ConfigureAwait(false);
        }

        // What a name in another file refers to, and what a routine costs with its calls, moved
        // only if the edit reached past the file it was made in; the edited file is not asked
        // to fetch anything, because the client asks about the document it is showing itself.
        if (!refresh || (changed is not null && !workspace.ReachedOtherFiles(Workspace.PathOf(changed))))
            return;
        if (client.RefreshesTokens)
            _ = RefreshAsync("workspace/semanticTokens/refresh", "semantic tokens");
        if (client.RefreshesLenses)
            _ = RefreshAsync("workspace/codeLens/refresh", "code lenses");
    }

    /// <summary>
    /// Publishes one file, unless what is wrong with it is what was published last time — a
    /// program may hold hundreds of files and every keystroke re-analyzes it — or unless the
    /// client has since sent a newer revision of it, in which case this is about text that is
    /// already gone.
    /// </summary>
    /// <param name="file">The file and what is wrong with it.</param>
    /// <param name="always">Whether to send even where nothing changed, which the edited file does.</param>
    /// <param name="cancellation">Asked before anything is sent.</param>
    /// <returns>Whether the client was told anything.</returns>
    private async Task<bool> SendAsync(Published file, bool always, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (file.Version is { } version && newest.TryGetValue(file.Uri, out var latest) && latest > version)
            return false;
        var diagnostics = outgoing.Spell(
            Lsp.ToDiagnostics(file.Diagnostics, file.Tree, file.Configuration));
        var said = Signature(diagnostics);
        if (!always && published.TryGetValue(file.Uri, out var before) && before == said)
            return false;
        published[file.Uri] = said;
        await rpc!.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics",
            new PublishDiagnosticsParams(file.Uri, file.Version, diagnostics)).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Puts a message in front of the person, where the client shows one. A client that is
    /// already gone hears nothing, which is not worth saying twice.
    /// </summary>
    /// <param name="type">How bad the news is.</param>
    /// <param name="message">What to show.</param>
    private async Task ShowAsync(MessageType type, string message)
    {
        try
        {
            await rpc!.NotifyWithParameterObjectAsync("window/showMessage", new ShowMessageParams(type, message))
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is ConnectionLostException or ObjectDisposedException)
        {
            log.Write($"the client did not hear: {message}");
        }
    }

    /// <summary>
    /// Asks the client to fetch something again. It is not waited for: the client answers after
    /// it has asked, and a client that has gone away has nothing to refresh.
    /// </summary>
    private async Task RefreshAsync(string method, string what)
    {
        try
        {
            await rpc!.InvokeWithParameterObjectAsync<object?>(method).ConfigureAwait(false);
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
    private Asked? At(TextDocumentPositionParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (workspace.Find(request.TextDocument.Uri) is not { } document)
            return null;
        var analysis = workspace.AnalysisFor(document.Tree.Path);
        cancellation.ThrowIfCancellationRequested();
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
