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
/// </summary>
internal sealed class DiagnosticsPublisher : IDisposable
{
    /// <summary>
    /// The time to wait after the last edit before publishing the rest of the program's diagnostics.
    /// </summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    private readonly ServerLog log;
    private readonly Workspace workspace;

    // Asks the client to fetch semantic tokens, lenses and hints again, after a publish of the
    // whole program that an edit reaching past its own file set off.
    private readonly Action refetch;

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
    /// The action that asks the client to fetch semantic tokens, lenses and hints again.
    /// </param>
    public DiagnosticsPublisher(ServerLog log, Workspace workspace, Outgoing outgoing, Delay delay, Action refetch)
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
    public async Task PublishEditedAsync(string uri, int version, CancellationToken cancellation)
    {
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
    /// and is skipped here; null when this is not an edit.
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
        refetch();
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

        // The workspace hands back the same list for a file whose program has not been analyzed
        // again since, and what the client is sent is worked out from that list, the tree and
        // the line length alone. When none of them has changed, neither has what was sent.
        var length = LineLength;
        var had = published.GetValueOrDefault(file.Uri);
        if (!always && had is not null && had.From == file.Diagnostics && had.Tree == file.Tree
            && had.Configuration == file.Configuration && had.LineLength == length)
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
