using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Norristown.LanguageServer.Protocol;
using Norristown.Semantics;
using Norristown.Standard;
using StreamJsonRpc;

namespace Norristown.LanguageServer;

// The Protocol folder holds hand-written LSP types for only the messages the server handles.
// The formatter converts property names to camel case.

/// <summary>Serves the Language Server Protocol to one client over a pair of streams.</summary>
internal sealed class Server : IDisposable
{
    /// <summary>
    /// The time to wait after the last edit before publishing the rest of the program's diagnostics.
    /// </summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    private readonly ServerLog log;
    private readonly Framing framing;
    private readonly Workspace workspace;

    // Publishing diagnostics for every file except the edited one waits for typing to stop. A
    // feature that follows the whole program rather than the caret, such as the output view
    // beside the source, is refreshed after the same wait, so that it updates when the
    // squiggles do.
    private readonly Debounce settling;

    // The exit code for the process, set once `exit` arrives or the editor process goes away.
    private readonly TaskCompletionSource<int> leaving =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // `published` holds a signature of the diagnostics last published for each URI, so that a
    // file is sent again only when its diagnostics change. `newest` holds the newest version of
    // each open file that the client has sent, so that nothing is published about text that has
    // since changed. Both are concurrent because publishing for a keystroke and publishing after
    // the debounce can run at the same time.
    private readonly ConcurrentDictionary<string, string> published = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> newest = new(StringComparer.Ordinal);

    // The publish of each open document's own diagnostics that may still be waiting for its
    // analysis. The document's next edit cancels it, because what it would send is about text
    // that is gone.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> owning = new(StringComparer.Ordinal);

    // Held while every file is published, so that one publish of the whole program finishes
    // before the next starts.
    private readonly SemaphoreSlim publishing = new(1, 1);
    private JsonRpc? rpc;

    // What the client supports, and the last step every answer passes through on its way out.
    private ClientCapabilities client = ClientCapabilities.None;
    private Outgoing outgoing;

    // The hints the editor's settings show, and the cycle-hint override that a command toggles
    // for as long as this server runs. The toggle lasts for the session and is not saved,
    // because cycle counts are wanted while a routine is being timed, not permanently.
    private HintSettings hints = HintSettings.Default;

    // The longest a line may be before the editor suggests breaking it, or 0 for no limit.
    private int lineLength = LineBreaks.DefaultLength;
    private bool? cyclesThisSession;

    // The documentation of each item in the last completion list, kept until the next list
    // replaces it. The client resolves only items of the list it is showing, which is always
    // the list most recently returned.
    private IReadOnlyDictionary<string, string> described =
        new Dictionary<string, string>(StringComparer.Ordinal);

    // The semantic tokens last sent for each file, under the result id the client will quote
    // when it asks for a delta. A client quoting an id this server no longer has is sent the
    // full tokens instead.
    private readonly ConcurrentDictionary<string, (string Id, IReadOnlyList<int> Data)> classified =
        new(StringComparer.Ordinal);
    private int classifiedId;

    // The editor process that started this server. It is watched rather than relied on to send
    // `exit`, because an editor that crashes never sends it, and a server with no client should
    // not outlive its editor.
    private Process? parent;

    // Whether the client has ever asked for a file's output. Such a client is notified whenever
    // the whole program's diagnostics are published after an edit. A client that never asked is
    // not sent a notification it may have no handler for. The flag is set by a request handler
    // and read by the publishing code, which run on different threads.
    private volatile bool watchingOutput;

    // The binaries and linker configs the client has been asked to watch. The editor watches the
    // sources and the project files by itself, but which files `.incbin` directives include and
    // which configs a project links are not known until the programs have been read.
    private IReadOnlyList<string> watchedFiles = [];

    private Server(ServerLog log, Framing framing, Delay delay, Analyzer? analyzer)
    {
        this.log = log;
        this.framing = framing;
        workspace = new Workspace(analyzer);
        settling = new Debounce(Quiet, delay);
        outgoing = new Outgoing(workspace, client);
    }

    /// <summary>
    /// Gets the hints to show, which are the editor's settings with this session's cycle-hint
    /// toggle overriding them.
    /// </summary>
    private HintSettings Shown =>
        cyclesThisSession is { } session ? hints with { Cycles = session } : hints;

    /// <summary>
    /// Serves one client until it sends <c>exit</c>, the editor that started the server exits,
    /// or the connection is lost.
    /// </summary>
    /// <param name="input">The stream the client's messages arrive on.</param>
    /// <param name="output">The stream the server's messages are sent on.</param>
    /// <param name="log">The log the server records its activity in.</param>
    /// <param name="delay">
    /// The wait between an edit and publishing the rest of the program. A test supplies its own
    /// so that it can drive the wait itself; a real delay is used when none is given.
    /// </param>
    /// <param name="analyzer">
    /// The function that analyzes a program. A test supplies its own so that it can hold an
    /// analysis back; <see cref="Compiler"/> analyzes when none is given.
    /// </param>
    /// <returns>
    /// The process's exit code. It is 0 when the client sent <c>shutdown</c> before <c>exit</c>
    /// or the connection was lost, and 1 when the client sent <c>exit</c> without
    /// <c>shutdown</c> or the editor went away. It is 70 when nt65 itself failed, which makes the
    /// editor's client start a new server.
    /// </returns>
    public static async Task<int> RunAsync(
        Stream input, Stream output, ServerLog log, Delay? delay = null, Analyzer? analyzer = null)
    {
        using var framing = new Framing(input, output, CreateFormatter());
        using var server = new Server(log, framing, delay ?? Task.Delay, analyzer);
        using var rpc = new JsonRpc(framing);
        server.rpc = rpc;
        rpc.AddLocalRpcTarget(server, new JsonRpcTargetOptions { UseSingleObjectParameterDeserialization = true });
        rpc.StartListening();
        try
        {
            // `exit` is handled by the process exiting, which the handler cannot do from inside
            // the dispatch it runs in, so it completes `leaving` and the exit happens here.
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
            // This is the handler of last resort. The message loop itself failed, which is a bug
            // in nt65. The exception goes to the log for a bug report and is shown to the user,
            // and the process exits with a failure code so that the client does not keep talking
            // to a dead server.
            log.Write($"internal error: {e}");
            await server.ShowAsync(MessageType.Error, $"nt65: internal error: {e.Message}").ConfigureAwait(false);
            return 70;
        }
    }

    /// <summary>
    /// Serves one client on the process's standard input and output, as <see cref="RunAsync"/>
    /// does, and records the server's start and exit in the log.
    /// </summary>
    /// <param name="error">
    /// The writer that receives the log's lines, which is standard error when a process serves.
    /// </param>
    /// <returns>The process's exit code, as <see cref="RunAsync"/> returns it.</returns>
    public static async Task<int> ServeStandardStreamsAsync(TextWriter error)
    {
        // Standard output carries the protocol, so anything human-readable goes to standard
        // error, which an editor shows in the server's output channel. NT65_SERVER_LOG names a
        // file that receives the same lines, for looking at a server an editor started.
        var logPath = Environment.GetEnvironmentVariable("NT65_SERVER_LOG");
        using var log = new ServerLog(error, string.IsNullOrEmpty(logPath) ? null : logPath);

        log.Write($"Norristown language server starting (pid {Environment.ProcessId})");
        var exit = await RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), log).ConfigureAwait(false);
        log.Write($"Norristown language server exiting ({exit})");
        return exit;
    }

    /// <summary>
    /// Cancels any publish still waiting for typing to stop, and releases the editor process
    /// handle and the publishing lock.
    /// </summary>
    public void Dispose()
    {
        settling.Dispose();
        publishing.Dispose();
        parent?.Dispose();
    }

    [JsonRpcMethod("initialize")]
    public InitializeResult Initialize(InitializeParams request)
    {
        var named = request.ClientInfo is { } info ? $"{info.Name} {info.Version}".TrimEnd() : "unknown client";
        log.Write($"connected: {named}");

        // The projects are found in the folders the client opened, and built in the configuration
        // the client's settings choose. A project's files are the program that a name is resolved
        // against.
        IReadOnlyList<string> roots = request.WorkspaceFolders is { Count: > 0 } folders
            ? [.. folders.Select(folder => folder.Uri)]
            : request.RootUri is { } root ? [root] : [];
        workspace.Load(roots, ActiveConfiguration(request.InitializationOptions));
        hints = HintSettings.Of(request.InitializationOptions);
        lineLength = LineLengthOf(request.InitializationOptions);
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
            // No commit characters: a completion that accepts itself on a `,` or a space, in a
            // language where both follow a name on most lines, is wrong more often than right.
            CompletionProvider: new CompletionOptions(
                [" ", ".", ":", "@", "!", "#", "(", "[", ","], ResolveProvider: true),
            SignatureHelpProvider: new SignatureHelpOptions(["(", ",", "="]),
            CodeLensProvider: new CodeLensOptions(ResolveProvider: false),
            WorkspaceSymbolProvider: true,
            CodeActionProvider: new CodeActionOptions(CodeActionKinds.All),
            SemanticTokensProvider: new SemanticTokensOptions(
                NameHighlighting.Legend, new SemanticTokensFullOptions(Delta: true), Range: true),
            SelectionRangeProvider: true,
            InlayHintProvider: new InlayHintOptions(ResolveProvider: false),
            CallHierarchyProvider: true,
            DocumentLinkProvider: new DocumentLinkOptions(ResolveProvider: false),
            DocumentFormattingProvider: true,
            DocumentRangeFormattingProvider: true,

            // Positions are UTF-16 offsets within a line, which is the protocol's own default and
            // what nt65 has always used. Stating it tells a client that would prefer another
            // encoding not to use one.
            PositionEncoding: "utf-16",

            // A client that supports workspace folders is asked to report changes to them, so a
            // folder added to the workspace brings its projects with it. A client that asks
            // before moving a file is asked about every file, because which files a program
            // includes is decided by the program and is not known until it has been read.
            Workspace: client.WorkspaceFolders || client.WillRenameFiles
                ? new WorkspaceServerCapabilities(
                    client.WorkspaceFolders ? new WorkspaceFoldersServerCapabilities(true, true) : null,
                    client.WillRenameFiles
                        ? new FileOperationsServerCapabilities(new FileOperationRegistrationOptions(
                            [new FileOperationFilter(new FileOperationPattern("**/*", "file"))]))
                        : null)
                : null);
        return new InitializeResult(capabilities, new ServerInfo("Norristown Assembler", "0.0.0"));
    }

    /// <summary>
    /// Handles the notification that the client is ready. The diagnostics of every file of every
    /// project are published immediately, so that a broken export shows in the Problems panel
    /// before anything is opened.
    /// </summary>
    [JsonRpcMethod("initialized")]
    public async Task InitializedAsync(JsonElement _, CancellationToken cancellation)
    {
        await rpc!.NotifyWithParameterObjectAsync("window/logMessage",
            new LogMessageParams(MessageType.Info, "Norristown language server ready")).ConfigureAwait(false);
        await PublishEverythingAsync(null, cancellation, refresh: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles a change to the client's settings by reading the project again in the configuration
    /// the settings now choose.
    /// </summary>
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

        // Which hints are shown is a setting like any other, and the editor is holding hints
        // computed under the old value, so it is asked to fetch them again.
        var shown = HintSettings.Of(settings);
        if (shown != hints)
        {
            hints = shown;
            RefreshHints();
        }
        lineLength = LineLengthOf(settings);
        return PublishEverythingAsync(null, cancellation);
    }

    /// <summary>
    /// Toggles the cycle-count hints for as long as this server runs, and returns the new state
    /// for the editor to show. It is a command rather than a setting because cycle counts are
    /// wanted while a routine is being timed, not permanently.
    /// </summary>
    [JsonRpcMethod("nt65/toggleCycleHints")]
    public bool ToggleCycleHints(JsonElement _)
    {
        cyclesThisSession = !(cyclesThisSession ?? hints.Cycles);
        log.Write($"cycle hints: {(cyclesThisSession.Value ? "on" : "off")}");
        RefreshHints();
        return cyclesThisSession.Value;
    }

    /// <summary>
    /// Returns the inlay hints for the lines the editor is showing. Only those lines are computed,
    /// because the client fetches hints again as a file is scrolled, and a keystroke should not
    /// pay for lines nobody is looking at.
    /// </summary>
    [JsonRpcMethod("textDocument/inlayHint")]
    public async Task<IReadOnlyList<InlayHint>> InlayHintsAsync(InlayHintParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (workspace.Find(request.TextDocument.Uri) is not { } document)
            return [];
        var analysis = await workspace.AnalysisForAsync(document.Tree.Path, cancellation).ConfigureAwait(false);
        return analysis.ModelFor(document.Tree.Path) is not { } model
            ? []
            : LanguageServer.InlayHints.In(
                analysis, model, Shown, request.Range.Start.Line, request.Range.End.Line, cancellation);
    }

    /// <summary>
    /// Handles a folder being added to or removed from the workspace. The workspace consists of
    /// the projects in the folders the client now has open, so the projects are looked for again
    /// from scratch.
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
    /// Handles files that changed on disk, such as a project file, a source no one has open, or a
    /// binary an <c>.incbin</c> includes. Diagnostics are published again when any of them is a
    /// file that a program reads.
    /// </summary>
    [JsonRpcMethod("workspace/didChangeWatchedFiles")]
    public Task DidChangeWatchedFilesAsync(DidChangeWatchedFilesParams request, CancellationToken cancellation)
    {
        if (!workspace.ChangedOnDisk(request.Changes.Select(change => change.Uri)))
            return Task.CompletedTask;
        log.Write($"changed on disk: {string.Join(", ", request.Changes.Select(change => change.Uri))}");
        return PublishEverythingAsync(null, cancellation);
    }

    /// <summary>
    /// Returns the edits to make before files are moved or renamed. A module's name comes from its
    /// <c>.module</c> line and its output is named after that, so moving a source needs few
    /// edits. A <c>files</c> entry that names the source literally is updated, and so are
    /// <c>.incbin</c> paths, which are resolved relative to the including file and so change when
    /// either end moves. A glob that stops matching is reported rather than rewritten, because
    /// only the programmer knows which glob was meant to cover the file.
    /// </summary>
    [JsonRpcMethod("workspace/willRenameFiles")]
    public async Task<WorkspaceEdit?> WillRenameFilesAsync(
        RenameFilesParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var programs = await workspace.ProgramsAsync(cancellation).ConfigureAwait(false);
        var (edit, messages) = MovedFiles.For(
            workspace, programs,
            [.. request.Files.Select(file => (Uris.ToPath(file.OldUri), Uris.ToPath(file.NewUri)))]);
        foreach (var message in messages)
            await ShowAsync(MessageType.Warning, message).ConfigureAwait(false);
        return outgoing.ToClient(edit);
    }

    /// <summary>
    /// Returns the named configurations of the workspace's projects, for the client to offer.
    /// </summary>
    [JsonRpcMethod("nt65/configurations")]
    public IReadOnlyList<string> Configurations(JsonElement _) => workspace.Configurations();

    /// <summary>
    /// Returns the source of a module that comes with nt65, which no file on disk holds. A
    /// definition or reference that leads into such a module gives an <c>nt65:</c> URI, and the
    /// client requests the text to show under it, read-only.
    /// </summary>
    [JsonRpcMethod("nt65/standardModule")]
    public string? StandardModule(StandardModuleParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return StandardModules.Text(Uris.ToPath(request.TextDocument.Uri));
    }

    /// <summary>
    /// Returns the output for a file, which is the ca65 source a build would write for it from the
    /// program as it stands in the editor, unsaved edits included, together with which output
    /// lines each source line produced. For a file that no program holds, the result is null and
    /// the client decides what to show.
    /// <para>
    /// Once this has been requested, the server sends <c>nt65/outputChanged</c> whenever the whole
    /// program's diagnostics are published after an edit, so that a view beside the source
    /// updates when the squiggles do.
    /// </para>
    /// </summary>
    [JsonRpcMethod("nt65/output")]
    public async Task<OutputResult?> OutputAsync(OutputParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        watchingOutput = true;
        var uri = request.TextDocument.Uri;
        var path = workspace.Find(uri) is { } document ? document.Tree.Path : Uris.ToPath(uri);

        // The settings and the version are taken with the files the analysis is of, so that the
        // answer does not mix an analysis with a later edit.
        var settings = workspace.SettingsFor(path);
        var version = workspace.VersionOf(uri);
        var analysis = await workspace.AnalysisForAsync(path, cancellation).ConfigureAwait(false);
        return LanguageServer.Output.Of(analysis, settings, path, outgoing.ToClient(uri), version);
    }

    /// <summary>
    /// Returns what the macro call at a position expands to, rendered as nt65 rather than ca65,
    /// which is the macro body with the arguments substituted. Nested calls are left unexpanded,
    /// one level at a time, and each comes with the index path to send back to have it expanded
    /// as well.
    /// </summary>
    [JsonRpcMethod("nt65/expansion")]
    public async Task<ExpansionResult?> ExpansionAsync(ExpansionParams request, CancellationToken cancellation)
    {
        if (await AtAsync(new TextDocumentPositionParams(request.TextDocument, request.Position), cancellation)
            .ConfigureAwait(false) is not { } asked)
        {
            return null;
        }
        var expansion = MacroExpansion.At(
            asked.Analysis, asked.Model, asked.Position, request.Into, request.All);
        return expansion is null
            ? null
            : new ExpansionResult(
                expansion.Call.GetText().Trim().TrimEnd('{').TrimEnd(),
                expansion.Summary(),
                string.Join("\n", expansion.Lines) + "\n",
                [.. expansion.Links.Select(link => new ExpansionLink(link.Line, link.Text, link.Into))],
                expansion.Refusal);
    }

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

        // The edited file's diagnostics are published at once, from its own analysis; the rest
        // of the program's are published once typing stops. An edit in one file can change what
        // is wrong with another, and a squiggle that flickers on every keystroke is worse than
        // one that arrives a moment late.
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

        // A closed file that belongs to a program is still reported on. One that belonged to no
        // program goes away with the document, and since it is no longer among the files
        // published, the publish below clears its diagnostics.
        return PublishEverythingAsync(null, cancellation);
    }

    /// <summary>
    /// Returns the file's outline, which is a tree of its segments and scopes for a client that
    /// supports one, or the protocol's older flat list otherwise.
    /// </summary>
    [JsonRpcMethod("textDocument/documentSymbol")]
    public object DocumentSymbols(DocumentSymbolParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return outgoing.ToClient(
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
    public async Task<Hover?> HoverAsync(TextDocumentPositionParams request, CancellationToken cancellation) =>
        await AtAsync(request, cancellation).ConfigureAwait(false) is { } asked
            ? Hovers.At(asked.Analysis, asked.Model, asked.Position)
            : null;

    [JsonRpcMethod("textDocument/definition")]
    public async Task<Location?> DefinitionAsync(TextDocumentPositionParams request, CancellationToken cancellation) =>
        await AtAsync(request, cancellation).ConfigureAwait(false) is { } asked
            && (Lsp.ToDefinition(asked.Program, asked.Model, asked.Position)
                ?? Lsp.ToPlacedDefinition(asked.Analysis, asked.Model, asked.Position)) is { } where
            ? outgoing.ToClient(where)
            : null;

    [JsonRpcMethod("textDocument/references")]
    public async Task<IReadOnlyList<Location>> ReferencesAsync(ReferenceParams request, CancellationToken cancellation) =>
        await AtAsync(request, cancellation).ConfigureAwait(false) is { } asked
            ? outgoing.ToClient(
                Lsp.ToReferences(asked.Program, asked.Model, asked.Position, request.Context.IncludeDeclaration))
            : [];

    [JsonRpcMethod("textDocument/documentHighlight")]
    public async Task<IReadOnlyList<DocumentHighlight>> DocumentHighlightsAsync(
        TextDocumentPositionParams request, CancellationToken cancellation) =>
        await AtAsync(request, cancellation).ConfigureAwait(false) is { } asked
            ? Lsp.ToHighlights(asked.Model, asked.Position)
            : [];

    /// <summary>
    /// Returns the range a rename would replace, which a client requests before offering a rename.
    /// </summary>
    [JsonRpcMethod("textDocument/prepareRename")]
    public async Task<Protocol.Range?> PrepareRenameAsync(
        TextDocumentPositionParams request, CancellationToken cancellation) =>
        await AtAsync(request, cancellation).ConfigureAwait(false) is { } asked
            ? LanguageServer.Rename.RangeAt(asked.Model, asked.Position)
            : null;

    [JsonRpcMethod("textDocument/rename")]
    public async Task<WorkspaceEdit?> RenameAsync(RenameParams request, CancellationToken cancellation)
    {
        if (await AtAsync(request, cancellation).ConfigureAwait(false) is not { } asked)
            return null;

        // A new name the language will not accept is returned as a failed request, which the
        // client shows for the programmer to correct, rather than as an empty edit.
        var (edit, problem) = LanguageServer.Rename.EditAt(asked.Program, asked.Model, asked.Position, request.NewName);
        return problem is null ? outgoing.ToClient(edit) : throw new LocalRpcException(problem);
    }

    [JsonRpcMethod("textDocument/completion")]
    public async Task<IReadOnlyList<CompletionItem>> CompletionAsync(
        TextDocumentPositionParams request, CancellationToken cancellation)
    {
        if (await AtAsync(request, cancellation).ConfigureAwait(false) is not { } asked)
            return [];
        var (items, about) = LanguageServer.Completion.At(
            asked.Program, asked.Model, asked.Analysis.Cpu, asked.Position, client.Snippets);
        described = about;
        return items;
    }

    /// <summary>
    /// Fills in the documentation of one item of the last completion list. Each name may carry a
    /// paragraph of comment, and a list of hundreds would be mostly prose nobody reads, so the
    /// comment above a declaration is fetched only for the item the client highlights.
    /// </summary>
    [JsonRpcMethod("completionItem/resolve")]
    public CompletionItem Resolve(CompletionItem request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return described.TryGetValue(request.Label, out var documentation)
            ? request with { Documentation = MarkupContent.Markdown(documentation) }
            : request;
    }

    [JsonRpcMethod("textDocument/signatureHelp")]
    public async Task<SignatureHelp?> SignatureHelpAsync(
        TextDocumentPositionParams request, CancellationToken cancellation) =>
        await AtAsync(request, cancellation).ConfigureAwait(false) is { } asked
            ? CallHelp.At(asked.Program, asked.Model, asked.Position)
            : null;

    [JsonRpcMethod("textDocument/codeLens")]
    public async Task<IReadOnlyList<CodeLens>> CodeLensesAsync(CodeLensParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (workspace.Find(request.TextDocument.Uri) is not { } document)
            return [];
        var path = document.Tree.Path;
        var analysis = await workspace.AnalysisForAsync(path, cancellation).ConfigureAwait(false);
        return LanguageServer.CodeLenses.In(document.Tree, analysis.ModelFor(path)?.Families ?? [], analysis.FlowFor(path));
    }

    [JsonRpcMethod("textDocument/documentLink")]
    public async Task<IReadOnlyList<DocumentLink>> DocumentLinksAsync(
        DocumentLinkParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return await ModelAsync(request.TextDocument.Uri, cancellation).ConfigureAwait(false) is { } model
            ? outgoing.ToClient(LanguageServer.DocumentLinks.In(model))
            : [];
    }

    /// <summary>
    /// Returns edits that format the whole file in nt65's layout. Formatting needs no analysis,
    /// because a line's indentation depends only on the braces in its own file, so a file with
    /// errors in it still formats.
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
    /// Returns edits that format the selected lines. Layout is computed over the whole file,
    /// because a run of data lines, for instance, shares one column, but only the selected lines
    /// are changed.
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
    public async Task<IReadOnlyList<CallHierarchyItem>> PrepareCallHierarchyAsync(
        CallHierarchyPrepareParams request, CancellationToken cancellation) =>
        await AtAsync(new TextDocumentPositionParams(request.TextDocument, request.Position), cancellation)
            .ConfigureAwait(false) is { } asked
            ? outgoing.ToClient(LanguageServer.CallHierarchy.Prepare(asked.Analysis, asked.Model, asked.Position))
            : [];

    /// <summary>
    /// Returns the routines that call a routine; <see cref="OutgoingCallsAsync"/> returns the routines
    /// it calls. The client sends the item back as the server gave it, so the program it belongs
    /// to is found from the file it names.
    /// </summary>
    [JsonRpcMethod("callHierarchy/incomingCalls")]
    public async Task<IReadOnlyList<CallHierarchyIncomingCall>> IncomingCallsAsync(
        CallHierarchyIncomingCallsParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var analysis = await workspace.AnalysisForAsync(Uris.ToPath(request.Item.Uri), cancellation).ConfigureAwait(false);
        return outgoing.ToClient(LanguageServer.CallHierarchy.Incoming(analysis, request.Item, cancellation));
    }

    [JsonRpcMethod("callHierarchy/outgoingCalls")]
    public async Task<IReadOnlyList<CallHierarchyOutgoingCall>> OutgoingCallsAsync(
        CallHierarchyOutgoingCallsParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var analysis = await workspace.AnalysisForAsync(Uris.ToPath(request.Item.Uri), cancellation).ConfigureAwait(false);
        return outgoing.ToClient(LanguageServer.CallHierarchy.Outgoing(analysis, request.Item, cancellation));
    }

    [JsonRpcMethod("textDocument/codeAction")]
    public async Task<IReadOnlyList<CodeAction>> CodeActionsAsync(CodeActionParams request, CancellationToken cancellation)
    {
        var start = new TextDocumentPositionParams(request.TextDocument, request.Range.Start);
        if (await AtAsync(start, cancellation).ConfigureAwait(false) is not { } asked)
            return [];
        return outgoing.ToClient(
            LanguageServer.CodeActions.In(asked.Analysis, asked.Model, request.Range, request.Context.Only, lineLength));
    }

    [JsonRpcMethod("textDocument/semanticTokens/full")]
    public Task<Protocol.SemanticTokens> SemanticTokensAsync(SemanticTokensParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return ClassifiedAsync(request.TextDocument.Uri, cancellation);
    }

    /// <summary>
    /// Returns the semantic tokens for the lines the editor is showing. For a file of thousands of
    /// lines, the client requests the visible screenful, which is coloured while the rest of the
    /// file is computed.
    /// </summary>
    [JsonRpcMethod("textDocument/semanticTokens/range")]
    public async Task<Protocol.SemanticTokens> SemanticTokensRangeAsync(
        SemanticTokensRangeParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return await ModelAsync(request.TextDocument.Uri, cancellation).ConfigureAwait(false) is { } model
            ? NameHighlighting.In(model, request.Range.Start.Line, request.Range.End.Line)
            : new Protocol.SemanticTokens([]);
    }

    /// <summary>
    /// Returns what changed since the tokens the client holds. An edit in one place changes a
    /// handful of numbers in a file of thousands. A client quoting a result id this server no
    /// longer has gets the full tokens instead.
    /// </summary>
    [JsonRpcMethod("textDocument/semanticTokens/full/delta")]
    public async Task<object> SemanticTokensDeltaAsync(SemanticTokensDeltaParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var holding = classified.TryGetValue(request.TextDocument.Uri, out var before)
            && before.Id == request.PreviousResultId;
        var held = before.Data;
        var answer = await ClassifiedAsync(request.TextDocument.Uri, cancellation).ConfigureAwait(false);
        return holding ? NameHighlighting.Changed(answer.ResultId!, held, answer.Data) : answer;
    }

    /// <summary>
    /// Returns the ranges each caret's selection steps through as it is expanded, which are the
    /// operand, the instruction, the block and the routine, taken directly from the syntax tree.
    /// </summary>
    [JsonRpcMethod("textDocument/selectionRange")]
    public IReadOnlyList<SelectionRange> SelectionRanges(
        SelectionRangeParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (workspace.Find(request.TextDocument.Uri) is not { } document)
            return [];
        var tree = document.Tree;
        return
        [
            .. request.Positions
                .Select(at => LanguageServer.SelectionRanges.At(tree, tree.GetPosition(at.Line, at.Character)))
                .OfType<SelectionRange>(),
        ];
    }

    [JsonRpcMethod("workspace/symbol")]
    public IReadOnlyList<SymbolInformation> WorkspaceSymbols(
        WorkspaceSymbolParams request, CancellationToken cancellation) =>
        outgoing.ToClient(LanguageServer.WorkspaceSymbols.Matching(workspace.Files(), request.Query, cancellation));

    [JsonRpcMethod("shutdown")]
    public object? Shutdown() => null;

    /// <summary>
    /// Handles the notification that the client is done. The process exits with 0 if the client
    /// sent <c>shutdown</c> first and 1 if it did not, as the protocol requires. The message loop
    /// is signalled rather than stopped, because a handler cannot end the dispatch it is running
    /// in.
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

    /// <summary>
    /// Returns the longest a line may be before breaking it is suggested, from an editor's
    /// <c>nt65</c> settings. A length the settings do not give, or give as other than a whole
    /// number from 0, is the default, and 0 turns the suggestion off.
    /// </summary>
    private static int LineLengthOf(JsonElement? settings) =>
        settings is { ValueKind: JsonValueKind.Object } options
            && options.TryGetProperty("lineLength", out var given)
            && given.ValueKind == JsonValueKind.Number && given.TryGetInt32(out var length) && length >= 0
            ? length
            : LineBreaks.DefaultLength;

    /// <summary>
    /// Returns the named configuration the client's <c>nt65</c> settings choose, or null for the
    /// project's own settings.
    /// </summary>
    private static string? ActiveConfiguration(JsonElement? settings) =>
        settings is { ValueKind: JsonValueKind.Object } options
            && options.TryGetProperty("configuration", out var named)
            && named.ValueKind == JsonValueKind.String && named.GetString() is { Length: > 0 } name
            ? name
            : null;

    /// <summary>
    /// Returns a string that stands for the diagnostics published for a file. It holds enough to
    /// tell one set of diagnostics from another and nothing more, because nothing reads it back.
    /// </summary>
    private static string Signature(IReadOnlyList<Protocol.Diagnostic> diagnostics) =>
        string.Join("\n", diagnostics.Select(d =>
            $"{d.Range.Start.Line}:{d.Range.Start.Character}:{(int)d.Severity}:{d.Message}"));

    /// <summary>
    /// Watches the editor process that started this server, when the client gave its process
    /// id. An editor that crashes never sends <c>exit</c>, and a server with no client should
    /// not outlive it; if the editor has already exited, the server leaves at once.
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
            // There is no such process: the editor exited between starting this server and the
            // server looking it up, which is exactly the case the watch exists for.
            Leave(1, $"the editor that started it (pid {id}) has gone");
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // This server is not allowed to watch that process. Carrying on without the watch is
            // better than exiting over it.
            log.Write($"the editor that started it (pid {id}) cannot be watched: {e.Message}");
        }
    }

    /// <summary>Signals that the process should exit with <paramref name="code"/>, and logs why.</summary>
    private void Leave(int code, string why)
    {
        if (leaving.TrySetResult(code))
            log.Write($"{why}; leaving with {code}");
    }

    /// <summary>
    /// Publishes the diagnostics of the file the client has just opened or edited, at once, from
    /// the analysis that edit triggered. It is the file the user is looking at, so it is
    /// published without waiting for anything.
    /// </summary>
    private async Task PublishOwnAsync(string uri, CancellationToken cancellation)
    {
        // Handlers start in the order the client's messages arrive, so the publish replaced here
        // is always one for an earlier version of the document.
        var mine = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if (owning.TryGetValue(uri, out var before))
            Cancel(before);
        owning[uri] = mine;
        try
        {
            if (await workspace.ToPublishAsync(uri, mine.Token).ConfigureAwait(false) is { } file)
                _ = await SendAsync(file, always: true, mine.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (mine.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            // A later edit to the document replaced this publish. The client already holds a newer
            // version, so these diagnostics would not have been sent.
        }
        finally
        {
            owning.TryRemove(new KeyValuePair<string, CancellationTokenSource>(uri, mine));
            mine.Dispose();
        }

        static void Cancel(CancellationTokenSource source)
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // That publish finished while this one was starting, and there is nothing to stop.
            }
        }
    }

    /// <summary>
    /// Publishes the rest of the program once typing has stopped. <paramref name="changed"/> is
    /// the file the client is editing, whose diagnostics were already published and are not
    /// sent again.
    /// </summary>
    private void PublishTheRestSoon(string changed) =>
        settling.After(async cancellation =>
        {
            try
            {
                await PublishEverythingAsync(changed, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // A later edit replaced this publish, and that edit's publish sends every file.
            }
            catch (Exception e) when (e is ConnectionLostException or ObjectDisposedException)
            {
                // The client disconnected during the debounce wait; nobody is waiting for this.
                log.Write($"the rest of the program was not published: {e.Message}");
            }
        });

    /// <summary>
    /// Publishes diagnostics for every file of every program, not only the open ones. An export
    /// broken in one file breaks every module that uses it, and none of those modules may be open.
    /// </summary>
    /// <param name="changed">
    /// The file the client is editing, which has already been published from its own analysis
    /// and is skipped here; null when this is not an edit.
    /// </param>
    /// <param name="cancellation">Checked between files, because a program may hold hundreds.</param>
    /// <param name="refresh">
    /// Whether the client may be asked to fetch semantic tokens, lenses and hints again. There
    /// is no point asking as the client connects, when it holds nothing yet.
    /// </param>
    private async Task PublishEverythingAsync(
        string? changed, CancellationToken cancellation, bool refresh = true)
    {
        // One publish runs at a time. Two at once would interleave their notifications, and
        // both could register the watch on the binaries.
        await publishing.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await PublishEverythingLockedAsync(changed, refresh, cancellation).ConfigureAwait(false);
        }
        finally
        {
            publishing.Release();
        }
    }

    /// <summary>
    /// Does the work of <see cref="PublishEverythingAsync"/> while it holds the publishing lock.
    /// </summary>
    private async Task PublishEverythingLockedAsync(string? changed, bool refresh, CancellationToken cancellation)
    {
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in await workspace.ToPublishAsync(cancellation).ConfigureAwait(false))
        {
            cancellation.ThrowIfCancellationRequested();
            current.Add(file.Uri);
            if (file.Uri != changed)
                _ = await SendAsync(file, always: false, cancellation).ConfigureAwait(false);
        }

        // The client keeps the diagnostics it was last sent until told otherwise, so a file that
        // has left the program, or is now named by a different URI, is explicitly cleared. A
        // file still in the program is never cleared: its squiggles stay until their
        // replacements arrive.
        foreach (var gone in published.Keys.Where(uri => !current.Contains(uri)).ToList())
        {
            published.TryRemove(gone, out _);
            await rpc!.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics",
                new PublishDiagnosticsParams(gone, null, [])).ConfigureAwait(false);
        }

        // The set of binaries and linker configs the programs read may have changed, and the
        // editor watches only what it can know about without reading the program.
        if (client.WatchesWhatItIsAsked)
            await WatchReadFilesAsync(await workspace.ReadFilesAsync(cancellation).ConfigureAwait(false)).ConfigureAwait(false);

        // The output view follows the whole program rather than the caret, so it is notified
        // once typing has stopped, after the same wait as the rest of the diagnostics.
        if (watchingOutput)
        {
            await rpc!.NotifyWithParameterObjectAsync("nt65/outputChanged",
                new OutputChangedParams(changed)).ConfigureAwait(false);
        }

        // What a name in another file refers to, and what a routine costs including its calls,
        // can only have changed if the edit reached past the file it was made in. An edit that
        // did not needs no refresh, because the client re-fetches for the document it is
        // showing by itself.
        if (!refresh
            || (changed is not null
                && !await workspace.ReachedOtherFilesAsync(Uris.ToPath(changed), cancellation).ConfigureAwait(false)))
        {
            return;
        }
        if (client.RefreshesTokens)
            _ = RefreshAsync("workspace/semanticTokens/refresh", "semantic tokens");
        if (client.RefreshesLenses)
            _ = RefreshAsync("workspace/codeLens/refresh", "code lenses");

        // Hints show what a call costs and what state it leaves behind, and both can change with
        // an edit in another file.
        RefreshHints();
    }

    /// <summary>
    /// Asks the client to fetch the hints it is showing again, where it supports that. Hints
    /// change when a setting changes and when an edit elsewhere in the program lands.
    /// </summary>
    private void RefreshHints()
    {
        if (client.RefreshesHints)
            _ = RefreshAsync("workspace/inlayHint/refresh", "inlay hints");
    }

    /// <summary>
    /// Publishes one file's diagnostics, unless they match what was published last time or the
    /// client has since sent a newer version of the file. Unchanged diagnostics are skipped
    /// because a program may hold hundreds of files and every keystroke re-analyzes it.
    /// Diagnostics for an older version are skipped because they are about text that is already
    /// gone.
    /// </summary>
    /// <param name="file">The file and what is wrong with it.</param>
    /// <param name="always">Whether to send even when nothing changed, as for the edited file.</param>
    /// <param name="cancellation">Checked before anything is sent.</param>
    /// <returns>Whether anything was sent to the client.</returns>
    private async Task<bool> SendAsync(Published file, bool always, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (file.Version is { } version && newest.TryGetValue(file.Uri, out var latest) && latest > version)
            return false;
        var diagnostics = outgoing.ToClient(
            Lsp.ToDiagnostics(file.Diagnostics, file.Tree, file.Configuration, lineLength));
        var signature = Signature(diagnostics);
        if (!always && published.TryGetValue(file.Uri, out var before) && before == signature)
            return false;
        published[file.Uri] = signature;
        await rpc!.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics",
            new PublishDiagnosticsParams(file.Uri, file.Version, diagnostics)).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Asks the client to watch the binaries this workspace's programs include and the linker
    /// configs its projects link. The caller checks that the client supports that. The editor
    /// watches the sources and the project files by itself. The other files are known only from
    /// reading the programs, so the watch is registered here and registered again whenever that
    /// set changes.
    /// </summary>
    /// <param name="files">The files the programs read, as logical paths.</param>
    private async Task WatchReadFilesAsync(IReadOnlyList<string> files)
    {
        if (files.SequenceEqual(watchedFiles, StringComparer.Ordinal))
            return;
        const string id = "nt65-read-files";
        try
        {
            if (watchedFiles.Count > 0)
            {
                await rpc!.InvokeWithParameterObjectAsync<object?>("client/unregisterCapability",
                    new UnregistrationParams([new Unregistration(id, "workspace/didChangeWatchedFiles")]))
                    .ConfigureAwait(false);
            }
            watchedFiles = files;
            if (files.Count == 0)
                return;
            await rpc!.InvokeWithParameterObjectAsync<object?>("client/registerCapability",
                new RegistrationParams([new Registration(id, "workspace/didChangeWatchedFiles",
                    new DidChangeWatchedFilesRegistrationOptions(
                        [.. files.Select(path => new Protocol.FileSystemWatcher(path))]))]))
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is RemoteInvocationException or ConnectionLostException or ObjectDisposedException)
        {
            log.Write($"the client did not take the files to watch: {e.Message}");
        }
    }

    /// <summary>
    /// Shows a message to the user in the client. If the client has already gone, the message
    /// is only written to the log.
    /// </summary>
    /// <param name="type">The severity of the message.</param>
    /// <param name="message">The text to show.</param>
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
    /// Asks the client to fetch something again. Callers do not await it: the client replies only
    /// after it has sent its own requests to re-fetch, and a client that has gone away has
    /// nothing to refresh.
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

    /// <summary>
    /// Returns the semantic tokens of a whole file, and stores them under a new result id so that
    /// the client can request a delta against them next time.
    /// </summary>
    private async Task<Protocol.SemanticTokens> ClassifiedAsync(string uri, CancellationToken cancellation)
    {
        if (await ModelAsync(uri, cancellation).ConfigureAwait(false) is not { } model)
            return new Protocol.SemanticTokens([]);
        var data = NameHighlighting.In(model).Data;
        var id = Interlocked.Increment(ref classifiedId)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        classified[uri] = (id, data);
        return new Protocol.SemanticTokens(data, id);
    }

    /// <summary>
    /// Returns the semantic model of the file a URI names, or null when no program holds it.
    /// </summary>
    private async Task<SemanticModel?> ModelAsync(string uri, CancellationToken cancellation)
    {
        var path = workspace.Find(uri) is { } document ? document.Tree.Path : Uris.ToPath(uri);
        return (await workspace.AnalysisForAsync(path, cancellation).ConfigureAwait(false)).ModelFor(path);
    }

    /// <summary>
    /// Returns what a request points at, which is the program, the file the position is in and
    /// the offset in that file. Returns null when the client never opened the document or when
    /// the program does not hold it. The document and the files the analysis is of are taken
    /// before the method first waits, so that a later edit does not change what the request is
    /// about.
    /// </summary>
    private async Task<Asked?> AtAsync(TextDocumentPositionParams request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (workspace.Find(request.TextDocument.Uri) is not { } document)
            return null;
        var analysis = await workspace.AnalysisForAsync(document.Tree.Path, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (analysis.ModelFor(document.Tree.Path) is not { } model)
            return null;
        return new Asked(
            analysis,
            analysis.Program,
            model,
            document.Tree.GetPosition(request.Position.Line, request.Position.Character));
    }

    /// <summary>Represents a request resolved to what it is about.</summary>
    /// <param name="Analysis">The analysis of the whole program, for questions about layout and flow.</param>
    /// <param name="Program">Every file, for names that cross from one file to another.</param>
    /// <param name="Model">The file the caret is in.</param>
    /// <param name="Position">The offset in that file's text.</param>
    private sealed record Asked(
        ProgramAnalysis Analysis, ProgramModel Program, SemanticModel Model, int Position);
}
