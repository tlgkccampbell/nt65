using System.Collections.Concurrent;
using System.Text.Json;
using Norristown.LanguageServer.Protocol;
using Norristown.Semantics;
using Norristown.Syntax;
using StreamJsonRpc;

namespace Norristown.LanguageServer;

/// <summary>
/// Publishes the diagnostics of every file of the workspace's programs to one client. The file
/// the client is editing is published at once, and the rest of the program once typing stops.
/// The edited file is published from an analysis that may leave the program-wide answers for
/// later, so it is published again once typing stops, where those answers change it.
/// </summary>
internal sealed class DiagnosticsPublisher : IDisposable
{
    /// <summary>
    /// The time to wait after the last edit before working out the program-wide answers and
    /// publishing the rest of the program's diagnostics. A shorter wait starts that work in the
    /// pauses between words, only for the next keystroke to discard it, and moves the lenses while
    /// the user is still typing. It sits in the range that language servers for other languages use.
    /// </summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);

    private readonly ServerLog log;
    private readonly Workspace workspace;

    // Asks the client to fetch lenses and hints again after a publish of the whole program, and
    // semantic tokens too when it is told they may have changed.
    private readonly Action<bool> refetch;

    // Publishing diagnostics for every file except the edited one waits for typing to stop. A
    // feature that follows the whole program rather than the caret, such as the output view
    // beside the source, is refreshed after the same wait, so that it updates when the
    // squiggles do.
    private readonly Debounce settling;

    // `published` holds what the diagnostics last published for each URI were worked out from,
    // and a signature of them, so that a file is sent again only when its diagnostics change.
    // `newest` holds the newest version of each open file that the client has sent, so that
    // nothing is published about text that has since changed. Both are concurrent because
    // publishing for a keystroke and publishing after the debounce can run at the same time.
    private readonly ConcurrentDictionary<string, Sent> published = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> newest = new(StringComparer.Ordinal);

    // The publish of each open document's own diagnostics that may still be waiting for its
    // analysis. The document's next edit cancels it, because what it would send is about text
    // that is gone.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> owning = new(StringComparer.Ordinal);

    // Held while every file is published, so that one publish of the whole program finishes
    // before the next starts.
    private readonly SemaphoreSlim publishing = new(1, 1);

    // Whether the client has ever asked for a file's output. Such a client is notified whenever
    // the whole program's diagnostics are published after an edit. A client that never asked is
    // not sent a notification it may have no handler for. The flag is set by a request handler
    // and read by the publishing code, which run on different threads.
    private volatile bool watchingOutput;

    // Whether a document has been opened since the last publish of the whole program. Opening a
    // file adds it to a program, so the client is asked to fetch every file's tokens again. It is
    // 1 for true, so that it can be read and cleared in one step.
    private int opened;

    // The model each file's semantic tokens were last worked out from, with the routines they
    // marked as running under an interrupt, by URI, as of the last publish of the whole program.
    private IReadOnlyDictionary<string, (SemanticModel? Model, string? Marked)> models =
        new Dictionary<string, (SemanticModel? Model, string? Marked)>();

    // The binaries and linker configs the client has been asked to watch. The editor watches the
    // sources and the project files by itself, but which files `.incbin` directives include and
    // which configs a project links are not known until the programs have been read.
    private IReadOnlyList<string> watchedFiles = [];

    /// <summary>
    /// Initializes a publisher for the programs of <paramref name="workspace"/>.
    /// </summary>
    /// <param name="log">The log that failures nothing awaits are recorded in.</param>
    /// <param name="workspace">The workspace whose files are published.</param>
    /// <param name="outgoing">The last step every diagnostic passes through on its way out.</param>
    /// <param name="delay">The function that waits for typing to stop.</param>
    /// <param name="refetch">
    /// The action that asks the client to fetch lenses and hints again, and semantic tokens too
    /// when it is given true.
    /// </param>
    public DiagnosticsPublisher(ServerLog log, Workspace workspace, Outgoing outgoing, Delay delay, Action<bool> refetch)
    {
        this.log = log;
        this.workspace = workspace;
        this.refetch = refetch;
        Outgoing = outgoing;
        settling = new Debounce(Quiet, delay);
    }

    /// <summary>Gets or sets the connection that notifications are sent on.</summary>
    public JsonRpc? Rpc { get; set; }

    /// <summary>Gets or sets what the client supports.</summary>
    public ClientCapabilities Client { get; set; } = ClientCapabilities.None;

    /// <summary>Gets or sets the last step every diagnostic passes through on its way out.</summary>
    public Outgoing Outgoing { get; set; }

    /// <summary>
    /// Gets or sets the longest a line may be before the editor suggests breaking it, or 0 for no
    /// limit.
    /// </summary>
    public int LineLength { get; set; } = LineBreaks.DefaultLength;

    /// <summary>
    /// Cancels any publish still waiting for typing to stop, and releases the publishing lock.
    /// </summary>
    public void Dispose()
    {
        settling.Dispose();
        publishing.Dispose();
    }

    /// <summary>
    /// Records that the client has asked for a file's output, so that it is notified whenever the
    /// whole program's diagnostics are published after an edit.
    /// </summary>
    public void WatchOutput() => watchingOutput = true;

    /// <summary>
    /// Publishes the diagnostics of a document the client has just opened or edited, at once, and
    /// the rest of the program's once typing stops. An edit in one file can change what is wrong
    /// with another, and a squiggle that flickers on every keystroke is worse than one that
    /// arrives a moment late.
    /// </summary>
    /// <param name="uri">The document's URI.</param>
    /// <param name="version">The version of the document the client now holds.</param>
    /// <param name="cancellation">The cancellation of the request that opened or edited it.</param>
    /// <param name="open">Whether the client has just opened the document rather than edited it.</param>
    public async Task PublishEditedAsync(string uri, int version, CancellationToken cancellation, bool open = false)
    {
        if (open)
            Interlocked.Exchange(ref opened, 1);
        newest[uri] = version;
        await PublishOwnAsync(uri, cancellation).ConfigureAwait(false);
        PublishTheRestSoon(uri);
    }

    /// <summary>
    /// Forgets the newest version of a document the client has closed.
    /// </summary>
    /// <param name="uri">The document's URI.</param>
    public void Closed(string uri) => newest.TryRemove(uri, out _);

    /// <summary>
    /// Publishes diagnostics for every file of every program, not only the open ones. An export
    /// broken in one file breaks every module that uses it, and none of those modules may be open.
    /// </summary>
    /// <param name="changed">
    /// The file the client is editing, which has already been published from its own analysis
    /// and is sent again only where the program-wide answers change its diagnostics; null when
    /// this is not an edit.
    /// </param>
    /// <param name="cancellation">Checked between files, because a program may hold hundreds.</param>
    /// <param name="refresh">
    /// Whether the client may be asked to fetch semantic tokens, lenses and hints again. There
    /// is no point asking as the client connects, when it holds nothing yet.
    /// </param>
    public async Task PublishEverythingAsync(
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
    /// Returns a string that stands for the diagnostics published for a file. Two sets that the
    /// client would show differently have different signatures, so every field the client reads
    /// is part of it, down to a diagnostic's end, code, tags and related locations.
    /// </summary>
    internal static string Signature(IReadOnlyList<Protocol.Diagnostic> diagnostics) =>
        JsonSerializer.Serialize(diagnostics);

    /// <summary>
    /// Determines whether two lists of diagnostics hold equal diagnostics in the same order. The
    /// workspace hands back the same list for a file whose program has not been analyzed again,
    /// which is checked first.
    /// </summary>
    private static bool Alike(IReadOnlyList<Diagnostic> a, IReadOnlyList<Diagnostic> b) =>
        ReferenceEquals(a, b) || a.SequenceEqual(b);

    /// <summary>
    /// Determines whether two configurations omit the same branches of <paramref name="tree"/>,
    /// which is all that converting its diagnostics reads of them.
    /// </summary>
    private static bool OmitsAlike(Configuration a, Configuration b, SyntaxTree? tree) =>
        ReferenceEquals(a, b) || tree is null || a.Omitted(tree).SequenceEqual(b.Omitted(tree));

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
            catch (Exception e)
            {
                // Nothing awaits this work, so a failure logged here is the only trace it leaves.
                log.Write($"the rest of the program was not published: {e}");
            }
        });

    /// <summary>
    /// Does the work of <see cref="PublishEverythingAsync"/> while it holds the publishing lock.
    /// </summary>
    private async Task PublishEverythingLockedAsync(string? changed, bool refresh, CancellationToken cancellation)
    {
        var current = new HashSet<string>(StringComparer.Ordinal);
        var now = new Dictionary<string, (SemanticModel? Model, string? Marked)>(StringComparer.Ordinal);
        foreach (var file in await workspace.ToPublishAsync(cancellation).ConfigureAwait(false))
        {
            cancellation.ThrowIfCancellationRequested();
            current.Add(file.Uri);
            now[file.Uri] = (file.Model, file.Marked);
            _ = await SendAsync(file, always: false, cancellation).ConfigureAwait(false);
        }

        // The client keeps the diagnostics it was last sent until told otherwise, so a file that
        // has left the program, or is now named by a different URI, is explicitly cleared. A
        // file still in the program is never cleared: its squiggles stay until their
        // replacements arrive.
        foreach (var gone in published.Keys.Where(uri => !current.Contains(uri)).ToList())
        {
            published.TryRemove(gone, out _);
            await Rpc!.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics",
                new PublishDiagnosticsParams(gone, null, [])).ConfigureAwait(false);
        }

        // The set of binaries and linker configs the programs read may have changed, and the
        // editor watches only what it can know about without reading the program.
        if (Client.WatchesWhatItIsAsked)
            await WatchReadFilesAsync(await workspace.ReadFilesAsync(cancellation).ConfigureAwait(false)).ConfigureAwait(false);

        // The output view follows the whole program rather than the caret, so it is notified
        // once typing has stopped, after the same wait as the rest of the diagnostics.
        if (watchingOutput)
        {
            await Rpc!.NotifyWithParameterObjectAsync("nt65/outputChanged",
                new OutputChangedParams(changed)).ConfigureAwait(false);
        }

        // The client re-fetches the lenses and hints of the document it is showing as it is
        // edited, from an analysis without the program-wide answers. What a routine costs with
        // its calls and what the registers hold come from those answers, so the client is asked
        // to fetch again once they are worked out, whether or not the edit reached past its file.
        //
        // Semantic tokens come from a file's model and from which routines run under an
        // interrupt, neither of which working those answers out changes, and the client fetches
        // the edited document's tokens by itself. So after an edit it is asked for tokens only
        // where another file's model, or the routines its tokens mark, have changed since the
        // last publish, which covers every edit made meanwhile. A file that an edit does not
        // analyze again keeps its model, and an edit that changes how another file's names are
        // colored changes what that file sees, so that file is analyzed again. An edit that adds
        // or removes a call can still change which routines run under an interrupt in a file it
        // does not analyze again, which is why the marked routines are compared as well. Tokens
        // that came to read anything else would need this check to change with them.
        var wasOpened = Interlocked.Exchange(ref opened, 0) == 1;
        var before = models;
        models = now;
        if (refresh)
            refetch(changed is null || wasOpened || OthersChanged(before, now, changed));
    }

    /// <summary>
    /// Determines whether a file other than <paramref name="changed"/> has a model in
    /// <paramref name="now"/> other than the one it had <paramref name="before"/>, marks other
    /// routines as running under an interrupt, or has joined the files published since.
    /// </summary>
    private static bool OthersChanged(
        IReadOnlyDictionary<string, (SemanticModel? Model, string? Marked)> before,
        Dictionary<string, (SemanticModel? Model, string? Marked)> now,
        string changed) =>
        now.Any(file => file.Key != changed
            && (!before.TryGetValue(file.Key, out var had) || !ReferenceEquals(had.Model, file.Value.Model)
                || had.Marked != file.Value.Marked));

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

        // What the client is sent is worked out from the diagnostics, the tree, the branches the
        // build omits from it and the line length alone. When none of them has changed, neither
        // has what was sent. A file the edit did not reach usually gets equal diagnostics from
        // the new analysis, so it is skipped without being converted again.
        var length = LineLength;
        var had = published.GetValueOrDefault(file.Uri);
        if (!always && had is not null && had.Tree == file.Tree && had.LineLength == length
            && Alike(had.From, file.Diagnostics) && OmitsAlike(had.Configuration, file.Configuration, file.Tree))
        {
            return false;
        }
        var diagnostics = Outgoing.ToClient(
            Lsp.ToDiagnostics(file.Diagnostics, file.Tree, file.Configuration, length));
        var signature = Signature(diagnostics);
        published[file.Uri] = new Sent(file.Diagnostics, file.Tree, file.Configuration, length, signature);
        if (!always && had?.Signature == signature)
            return false;
        await Rpc!.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics",
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
                await Rpc!.InvokeWithParameterObjectAsync<object?>("client/unregisterCapability",
                    new UnregistrationParams([new Unregistration(id, "workspace/didChangeWatchedFiles")]))
                    .ConfigureAwait(false);
            }
            watchedFiles = files;
            if (files.Count == 0)
                return;
            await Rpc!.InvokeWithParameterObjectAsync<object?>("client/registerCapability",
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

    /// <summary>Represents the diagnostics last published for a file and what they came from.</summary>
    /// <param name="From">The diagnostics the workspace gave, before conversion for the client.</param>
    /// <param name="Tree">The file's syntax tree, or null for a project file.</param>
    /// <param name="Configuration">The configuration used to fade the branches the build omits.</param>
    /// <param name="LineLength">The line length the long-line suggestions were found with.</param>
    /// <param name="Signature">The signature of what the client was sent.</param>
    private sealed record Sent(
        IReadOnlyList<Diagnostic> From, SyntaxTree? Tree, Configuration Configuration, int LineLength, string Signature);
}
