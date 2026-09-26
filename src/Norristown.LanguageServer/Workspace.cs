using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Norristown.LanguageServer.Protocol;
using Norristown.Project;
using Norristown.Standard;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Represents what the editor is working on, which is every <c>nt65.json</c> in the folders the
/// client opened, the program each describes, and the documents the client has open. An open
/// document's text lives here rather than on disk, and an edit re-parses it incrementally, so the
/// lines a change did not touch keep the green nodes they already had.
/// <para>
/// A file is not analyzed on its own, because a name it uses may be one another file exports.
/// A document belongs to the project whose <c>files</c> name it; the open documents no project
/// names together form one more program. A program is analyzed the first time anything asks,
/// and again after a change, starting from the previous analysis. An edit that does not change
/// what other files can see of a file re-analyzes only that file.
/// </para>
/// <para>
/// The lock is held only to read and change what the workspace holds. An analysis runs outside
/// it, on the files as they stood when it was asked for, so a request that needs no analysis, or
/// the analysis of another program, does not wait for one. Requests that ask about the same files
/// share one analysis.
/// </para>
/// </summary>
internal sealed class Workspace
{
    private readonly Analyzer analyzer;
    private readonly Lock gate = new();
    private readonly Dictionary<string, Document> open = new(StringComparer.Ordinal);

    // The document each tree was parsed from, which gives the version an edit computed from an
    // analysis of that tree applies to, however the document has changed since.
    private readonly ConditionalWeakTable<SyntaxTree, Document> parsedFrom = new();

    // The URI the client uses for each file it has named, by logical path. VS Code escapes a
    // drive's colon and nt65 does not, so a file the client has opened keeps the client's form of
    // its URI for the rest of the session. Two forms of one file's URI would list its diagnostics
    // in the Problems panel twice.
    private readonly Dictionary<string, string> named = new(FilePaths.Comparer);
    private readonly List<WorkspaceProject> projects = [];
    private IReadOnlyList<string> roots = [];
    private string? configuration;

    // The diagnostics last published for each file, by logical path, with the analysis they came
    // from. A file whose program has not been analyzed again since gets the same list back, and
    // its suggestions are not looked for again.
    private IReadOnlyDictionary<string, (ProgramAnalysis Analysis, IReadOnlyList<Diagnostic> Diagnostics)> reported =
        new Dictionary<string, (ProgramAnalysis, IReadOnlyList<Diagnostic>)>(StringComparer.Ordinal);

    // The analysis of the program formed by the open documents no project names.
    private readonly LiveAnalysis loose;

    /// <summary>Creates an empty workspace.</summary>
    /// <param name="analyzer">
    /// The function that analyzes a program. A test supplies its own so that it can hold an
    /// analysis back. <see cref="Compiler"/> analyzes when none is given.
    /// </param>
    /// <param name="failed">
    /// Is told of each analysis that fails for any reason but being cancelled, which is a bug in
    /// nt65 that no request would otherwise say anything about.
    /// </param>
    public Workspace(Analyzer? analyzer = null, Action<Exception>? failed = null)
    {
        var analyze = analyzer
            ?? ((files, project, previous, cancellation) =>
                Compiler.Analyze(files, project, binaryLength: null, previous, cancellation));
        this.analyzer = failed is null
            ? analyze
            : (files, project, previous, cancellation) =>
            {
                try
                {
                    return analyze(files, project, previous, cancellation);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    failed(e);
                    throw;
                }
            };
        loose = new LiveAnalysis(this.analyzer);
    }

    /// <summary>Returns a file's text, or null when it cannot be read.</summary>
    public static string? Read(string path)
    {
        // A file that moved or is being written while the editor asks is not a crash; the
        // next analysis will find it.
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the projects in the folders the client opened and every folder beneath them, and
    /// builds each in the named <paramref name="active"/> configuration, which the editor's
    /// setting chooses, when the project has it. Their files are read from disk when first
    /// needed, and open documents replace them as the client sends them.
    /// </summary>
    public void Load(IReadOnlyList<string> rootUris, string? active = null)
    {
        lock (gate)
        {
            roots = [.. rootUris.Select(Uris.ToPath).Where(root => root.Length > 0)];
            configuration = active;
            projects.Clear();
            foreach (var root in roots)
                projects.AddRange(ProjectFiles(root).Select(file => new WorkspaceProject(file, analyzer)));
            projects.Sort((a, b) => string.CompareOrdinal(a.File, b.File));
            ConfigureAll();
        }
    }

    /// <summary>
    /// Finds the projects in the one folder the client opened, or in none, and builds each in the
    /// named <paramref name="active"/> configuration when the project has it.
    /// </summary>
    public void Load(string? rootUri, string? active = null) => Load(rootUri is null ? [] : [rootUri], active);

    /// <summary>
    /// Updates the folders the client has open, adding <paramref name="added"/> and dropping
    /// <paramref name="removed"/>. Projects are searched for again across the resulting folders,
    /// because a folder that joined brings whatever projects are in it.
    /// </summary>
    /// <returns>The folders the workspace now has, as logical paths.</returns>
    public IReadOnlyList<string> WithFolders(IEnumerable<string> added, IEnumerable<string> removed)
    {
        lock (gate)
        {
            var gone = removed.Select(Uris.ToPath).ToHashSet(FilePaths.Comparer);
            IReadOnlyList<string> folders = [.. roots.Where(root => !gone.Contains(root))
                .Concat(added.Select(Uris.ToPath))
                .Where(root => root.Length > 0)
                .Distinct(FilePaths.Comparer)];
            Load(folders, configuration);
            return folders;
        }
    }

    /// <summary>
    /// Builds every project in the named configuration from now on, or in its default for null.
    /// Choosing the configuration already active changes nothing. The editor sends every
    /// setting when any one changes, and analyzing again in a named configuration redoes the whole
    /// program.
    /// </summary>
    public void Configure(string? active)
    {
        lock (gate)
        {
            if (active == configuration)
                return;
            configuration = active;
            ConfigureAll();
        }
    }

    /// <summary>Returns the named configurations the projects have, for a client to offer.</summary>
    public IReadOnlyList<string> Configurations()
    {
        lock (gate)
        {
            return [.. projects.SelectMany(project => project.Own.Configurations.Select(c => c.Name))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>Takes a newly opened document and parses it.</summary>
    public Document Open(TextDocumentItem item)
    {
        var document = new Document(item.Uri, item.Version, SyntaxTree.Parse(Uris.ToPath(item.Uri), item.Text));
        lock (gate)
        {
            open[item.Uri] = document;
            parsedFrom.AddOrUpdate(document.Tree, document);
            named[document.Tree.Path] = item.Uri;
            Invalidate(document.Tree.Path);
        }
        return document;
    }

    /// <summary>
    /// Applies <paramref name="changes"/> in order and returns the updated document. Returns null
    /// when the client changed a document it never opened, which is the client's mistake and not
    /// worth a crash.
    /// </summary>
    public Document? Change(VersionedTextDocumentIdentifier id, IReadOnlyList<TextDocumentContentChangeEvent> changes)
    {
        lock (gate)
        {
            if (!open.TryGetValue(id.Uri, out var document))
                return null;
            var tree = Applied(document.Tree, changes);
            Invalidate(tree.Path);
            var changed = new Document(id.Uri, id.Version, tree);
            parsedFrom.AddOrUpdate(tree, changed);
            return open[id.Uri] = changed;
        }
    }

    /// <summary>Forgets a document. From now on the file's contents are taken from disk.</summary>
    public void Close(string uri)
    {
        lock (gate)
        {
            if (open.Remove(uri, out var document))
                Invalidate(document.Tree.Path);
        }
    }

    /// <summary>
    /// Returns an open document, or null when it is not open. A URI in a different form from the
    /// one the client used at <c>didOpen</c> names the same file and finds the same document. For
    /// example, a link the server wrote into a hover comes back in the server's form, not the
    /// client's.
    /// </summary>
    public Document? Find(string uri)
    {
        lock (gate)
        {
            if (open.TryGetValue(uri, out var document))
                return document;
            return named.TryGetValue(Uris.ToPath(uri), out var canonical) ? open.GetValueOrDefault(canonical) : null;
        }
    }

    /// <summary>
    /// Handles files that changed on disk, such as a project file, a source, or a binary an
    /// <c>.incbin</c> includes. Returns whether any of them is a file the workspace reads, and so
    /// whether diagnostics may have changed.
    /// <para>
    /// A folder that appears stands for every file beneath it, and a folder that is gone stands
    /// for every file beneath it that the workspace was reading. An editor reports a folder that
    /// is renamed or deleted once, and not each of its files.
    /// </para>
    /// </summary>
    public bool ChangedOnDisk(IEnumerable<string> uris)
    {
        // A file that was created, deleted or renamed may now be spelled differently.
        Uris.Forget();
        lock (gate)
        {
            var changed = false;
            foreach (var path in Expanded(uris.Select(Uris.ToPath)))
            {
                if (Paths.Normalized(path).Split('/')[^1] == ProjectFile.Name)
                {
                    changed |= projects.RemoveAll(project => SamePath(project.File, path)) > 0;
                    if (File.Exists(path) && roots.Any(root => Within(root, path)))
                    {
                        var added = new WorkspaceProject(path, analyzer);
                        added.Configure(Named(added, configuration, AnyNames()));
                        projects.Add(added);
                        projects.Sort((a, b) => string.CompareOrdinal(a.File, b.File));
                        changed = true;
                    }
                    ConfigureAll();
                    loose.Invalidate();
                    continue;
                }

                // A linked config declares the segments of the projects that link it, so those
                // projects are read again, as if their project files had changed.
                var linking = projects.Where(project => project.Links(path)).ToList();
                if (linking.Count > 0)
                {
                    foreach (var project in linking)
                        projects[projects.IndexOf(project)] = new WorkspaceProject(project.File, analyzer);
                    ConfigureAll();
                    changed = true;
                    continue;
                }

                foreach (var project in projects.Where(project => project.Owns(path)))
                {
                    project.Reread(path);
                    changed = true;
                }
                foreach (var project in projects.Where(project => project.Measured(path)))
                {
                    project.Invalidate();
                    changed = true;
                }
                if (loose.Finished?.Binaries.Contains(path) == true)
                {
                    loose.Invalidate();
                    changed = true;
                }
            }
            return changed;
        }
    }

    /// <summary>
    /// Returns the analysis of the program that <paramref name="path"/> belongs to, built once and
    /// kept until something changes. Documents the client has open replace the files on disk. The
    /// files are taken as they stand when this method is called, and the analysis runs outside the
    /// lock.
    /// </summary>
    /// <param name="path">The logical path of a file of the program.</param>
    /// <param name="cancellation">
    /// Stops this request waiting. The analysis stops too when no other request is waiting for it.
    /// </param>
    public Task<ProgramAnalysis> AnalysisForAsync(string path, CancellationToken cancellation)
    {
        lock (gate)
        {
            return Owner(path) is { } project ? project.AnalysisAsync(open.Values, cancellation) : LooseAsync(cancellation);
        }
    }

    /// <summary>
    /// Returns the analysis of every program that <paramref name="path"/> belongs to. A file that
    /// several projects name, such as a library they share, belongs to each of them, and a file
    /// no project names belongs to the program of the open documents that no project names.
    /// </summary>
    /// <param name="path">The logical path of a file of the programs.</param>
    /// <param name="cancellation">Stops this request waiting.</param>
    public async Task<IReadOnlyList<ProgramAnalysis>> AnalysesForAsync(string path, CancellationToken cancellation)
    {
        List<Task<ProgramAnalysis>> analyses;
        lock (gate)
        {
            var owners = projects.Where(project => project.Owns(path)).ToList();
            analyses = owners.Count > 0
                ? [.. owners.Select(project => project.AnalysisAsync(open.Values, cancellation))]
                : [LooseAsync(cancellation)];
        }
        return await Task.WhenAll(analyses).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the files the programs read besides their sources and project files, as logical
    /// paths. These are the binaries that <c>.incbin</c> directives include and the linker configs
    /// the projects link. The editor has to be asked to watch them, because only the programs say
    /// which files they are.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReadFilesAsync(CancellationToken cancellation)
    {
        List<Task<ProgramAnalysis>> analyses;
        ProgramAnalysis? looseAnalysis;
        List<string> linked;
        lock (gate)
        {
            analyses = [.. projects.Select(project => project.AnalysisAsync(open.Values, cancellation))];
            looseAnalysis = loose.Finished;
            linked = [.. projects.SelectMany(project => project.Own.LinkedFiles)];
        }
        return [.. (await Task.WhenAll(analyses).ConfigureAwait(false))
            .Concat(looseAnalysis is null ? [] : [looseAnalysis])
            .SelectMany(analysis => analysis.Binaries)
            .Concat(linked)
            .Distinct(FilePaths.Comparer)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>Returns the projects the workspace holds, as they currently stand.</summary>
    public IReadOnlyList<WorkspaceProject> Projects()
    {
        lock (gate)
        {
            return [.. projects];
        }
    }

    /// <summary>
    /// Returns every program the workspace holds, which is one per project plus the program of the
    /// open documents that no project names. Each is built once and kept, so this normally reuses
    /// the analyses that publishing already built.
    /// </summary>
    public async Task<IReadOnlyList<ProgramAnalysis>> ProgramsAsync(CancellationToken cancellation)
    {
        List<Task<ProgramAnalysis>> analyses;
        lock (gate)
        {
            analyses = [.. projects.Select(project => project.AnalysisAsync(open.Values, cancellation))];
            if (open.Values.Any(document => Owner(document.Tree.Path) is null))
                analyses.Add(LooseAsync(cancellation));
        }
        return await Task.WhenAll(analyses).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the settings a file is built with, which are its project's settings, or the default
    /// settings for a file that no project names. They determine where its output would go.
    /// </summary>
    public ProjectSettings SettingsFor(string path)
    {
        lock (gate)
        {
            return Owner(path) is { } project ? project.Settings : ProjectSettings.None;
        }
    }

    /// <summary>
    /// Returns the URI the client uses for a file, which is the client's own form when it has given
    /// one. Every URI the server sends goes through this method, so that the editor sees one URI
    /// per file even when the client and the server would format its path differently.
    /// </summary>
    public string UriOf(string path)
    {
        lock (gate)
        {
            return named.GetValueOrDefault(path) ?? Uris.ToUri(path);
        }
    }

    /// <summary>
    /// Returns the version of a document that the client holds, or null for a document it has not
    /// opened.
    /// </summary>
    public int? VersionOf(string uri)
    {
        lock (gate)
        {
            return open.GetValueOrDefault(uri)?.Version;
        }
    }

    /// <summary>
    /// Returns the version of the document <paramref name="tree"/> was parsed from, or null when
    /// it was read from disk. An edit computed from an analysis of the tree applies to that
    /// version, even when the client has changed the document since.
    /// </summary>
    public int? VersionOf(SyntaxTree tree) => parsedFrom.TryGetValue(tree, out var document) ? document.Version : null;

    /// <summary>
    /// Returns the diagnostics of one open document, which is the file the client has just opened
    /// or edited, or null when the document is not open. They come from the whole program's
    /// analysis, so they include problems another file causes in this one; the other files'
    /// diagnostics are published separately.
    /// </summary>
    public async Task<Published?> ToPublishAsync(string uri, CancellationToken cancellation)
    {
        Document? document;
        Task<ProgramAnalysis> analyzing;
        lock (gate)
        {
            if (!open.TryGetValue(uri, out document))
                return null;
            analyzing = AnalysisForAsync(document.Tree.Path, cancellation);
        }
        var analysis = await analyzing.ConfigureAwait(false);
        var path = document.Tree.Path;
        return new Published(
            UriOf(path), document.Version, document.Tree, [.. analysis.DiagnosticsFor(path), .. analysis.SuggestionsFor(path)],
            analysis.Configuration);
    }

    /// <summary>
    /// Returns whether the last analysis of the program that <paramref name="path"/> belongs to had
    /// to re-analyze more than that one file. That is exactly when what names in other files refer
    /// to can have changed, and with it those files' semantic colouring and lenses.
    /// </summary>
    public async Task<bool> ReachedOtherFilesAsync(string path, CancellationToken cancellation)
    {
        var analysis = await AnalysisForAsync(path, cancellation).ConfigureAwait(false);
        return analysis.WholeProgram is not null || analysis.Reanalyzed > 1;
    }

    /// <summary>
    /// Returns every file whose diagnostics are published, with those diagnostics. The files are
    /// each file of each project, each project file, and each open document that belongs to no
    /// project. A file two projects share is reported by the nearer project, which is the one
    /// every other answer about the file comes from.
    /// <para>
    /// Which project each file belongs to, and the version of each open document, are taken
    /// together with the files the analyses are of, so that nothing published mixes an analysis
    /// with a later edit.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<Published>> ToPublishAsync(CancellationToken cancellation)
    {
        var programs = new List<(WorkspaceProject? Project, Task<ProgramAnalysis> Analysis)>();
        Dictionary<string, WorkspaceProject?> owners;
        var versions = new Dictionary<string, int>(FilePaths.Comparer);
        lock (gate)
        {
            foreach (var project in projects)
                programs.Add((project, project.AnalysisAsync(open.Values, cancellation)));
            if (open.Values.Any(document => Owner(document.Tree.Path) is null))
                programs.Add((null, LooseAsync(cancellation)));
            owners = projects.SelectMany(project => project.OnDisk().Select(tree => tree.Path))
                .Concat(open.Values.Select(document => document.Tree.Path))
                .Distinct(FilePaths.Comparer)
                .ToDictionary(path => path, Owner, FilePaths.Comparer);
            foreach (var document in open.Values)
                versions.TryAdd(document.Tree.Path, document.Version);
        }
        await Task.WhenAll(programs.Select(program => program.Analysis)).ConfigureAwait(false);

        var found = new Dictionary<string, (ProgramAnalysis Analysis, SyntaxTree? Tree)>(FilePaths.Comparer);
        foreach (var (project, analyzing) in programs)
        {
            var analysis = await analyzing.ConfigureAwait(false);
            if (project is null)
            {
                foreach (var file in analysis.Program.Files.Where(file => !StandardModules.IsStandard(file.Tree.Path)))
                    found[file.Tree.Path] = (analysis, file.Tree);
                continue;
            }

            // The project file is not one of the program's sources, but its errors can stop the
            // program being read at all, so they are published like any other file's.
            found[project.File] = (analysis, null);
            foreach (var file in analysis.Program.Files)
            {
                if (owners.GetValueOrDefault(file.Tree.Path) == project && !StandardModules.IsStandard(file.Tree.Path))
                    found[file.Tree.Path] = (analysis, file.Tree);
            }
        }
        // The diagnostics depend on nothing but the analysis and the file, so a program that has
        // not been analyzed again since the last publish costs nothing to publish again.
        IReadOnlyDictionary<string, (ProgramAnalysis Analysis, IReadOnlyList<Diagnostic> Diagnostics)> before;
        lock (gate)
        {
            before = reported;
        }
        var now = new Dictionary<string, (ProgramAnalysis Analysis, IReadOnlyList<Diagnostic> Diagnostics)>(StringComparer.Ordinal);
        var lookups = new Dictionary<ProgramAnalysis, ILookup<string, Diagnostic>>(ReferenceEqualityComparer.Instance);
        foreach (var (path, (analysis, _)) in found)
        {
            if (before.TryGetValue(path, out var had) && had.Analysis == analysis)
            {
                now[path] = had;
                continue;
            }
            if (!lookups.TryGetValue(analysis, out var byFile))
                lookups[analysis] = byFile = analysis.Diagnostics.ToLookup(diagnostic => diagnostic.Span.File, StringComparer.Ordinal);
            now[path] = (analysis, [.. byFile[path], .. analysis.SuggestionsFor(path)]);
        }
        lock (gate)
        {
            reported = now;
        }
        return [.. found
            .OrderBy(file => file.Key, StringComparer.Ordinal)
            .Select(file => new Published(
                UriOf(file.Key),
                versions.TryGetValue(file.Key, out var version) ? version : null,
                file.Value.Tree,
                now[file.Key].Diagnostics,
                file.Value.Analysis.Configuration))];
    }

    /// <summary>
    /// Returns every file of every program as the editor has it, for a search across the
    /// workspace. The files are the open documents and the files the projects name.
    /// </summary>
    public IReadOnlyList<SyntaxTree> Files()
    {
        lock (gate)
        {
            var trees = new Dictionary<string, SyntaxTree>(FilePaths.Comparer);
            foreach (var tree in projects.SelectMany(project => project.OnDisk()))
                trees[tree.Path] = tree;
            foreach (var document in open.Values)
                trees[document.Tree.Path] = document.Tree;
            return [.. trees.Values.OrderBy(tree => tree.Path, StringComparer.Ordinal)];
        }
    }

    private static string? Named(WorkspaceProject project, string? configuration, bool anyNames) =>
        configuration is { Length: > 0 } && (!anyNames || project.Own.Configurations.Any(c => c.Name == configuration))
            ? configuration
            : null;

    /// <summary>
    /// Returns every project file in <paramref name="root"/> and the folders beneath it, as logical
    /// paths.
    /// </summary>
    private static IEnumerable<string> ProjectFiles(string root) =>
        Folders(root)
            .Select(directory => Path.Combine(directory, ProjectFile.Name))
            .Where(File.Exists)
            .Select(Paths.Normalized);

    /// <summary>
    /// Returns every file in <paramref name="root"/> and the folders beneath it, as logical paths.
    /// </summary>
    private static IEnumerable<string> FilesBeneath(string root)
    {
        foreach (var directory in Folders(root))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
                yield return Paths.Normalized(file);
        }
    }

    /// <summary>
    /// Returns <paramref name="root"/> and every folder beneath it. Folders whose names start with
    /// <c>.</c>, and <c>node_modules</c> folders, are skipped, because they hold nothing of the
    /// programmer's.
    /// </summary>
    private static IEnumerable<string> Folders(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.TryPop(out var directory))
        {
            if (!Directory.Exists(directory))
                continue;
            yield return directory;
            IEnumerable<string> inner;
            try
            {
                inner = Directory.EnumerateDirectories(directory).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var folder in inner)
            {
                var name = Path.GetFileName(folder);
                if (!name.StartsWith('.') && name != "node_modules")
                    pending.Push(folder);
            }
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Paths.Normalized(a), Paths.Normalized(b), FilePaths.Comparison);

    private static bool Within(string directory, string path) =>
        Paths.Normalized(path).StartsWith(Paths.Normalized(directory) + "/", FilePaths.Comparison);

    /// <summary>
    /// Returns <paramref name="tree"/> with the edits of one notification applied in order. The
    /// edits are converted to text changes together and the tree is rebuilt once. Each edit's range
    /// refers to the text as the previous edit left it, and only the lines between the first and
    /// the last edit can have been touched.
    /// </summary>
    private static SyntaxTree Applied(SyntaxTree tree, IReadOnlyList<TextDocumentContentChangeEvent> changes)
    {
        var text = tree.Text;
        var starts = tree.LineStarts;
        var applied = new List<TextChange>(changes.Count);
        foreach (var change in changes)
        {
            // No range means the whole document, which a client sends when it cannot describe
            // the edit; there is nothing to reuse then, and nothing before it to keep either.
            if (change.Range is not { } range)
            {
                applied.Clear();
                applied.Add(new TextChange(0, text.Length, change.Text));
                text = change.Text;
                starts = SyntaxTree.LineOffsets(text);
                continue;
            }

            var start = SyntaxTree.GetPosition(text, starts, range.Start.Line, range.Start.Character);
            var end = Math.Max(start, SyntaxTree.GetPosition(text, starts, range.End.Line, range.End.Character));
            applied.Add(new TextChange(start, end - start, change.Text));
            text = string.Concat(text.AsSpan(0, start), change.Text, text.AsSpan(end));
            starts = SyntaxTree.LineOffsets(text);
        }
        return tree.WithChanges(applied);
    }

    /// <summary>
    /// Returns the analysis of the open documents that no project names. Each open document
    /// supplies its own tree in place of the file on disk, and an edit re-parsed only the lines it
    /// touched. The caller holds the lock.
    /// </summary>
    private Task<ProgramAnalysis> LooseAsync(CancellationToken cancellation) =>
        loose.AnalysisAsync(
            () => ([.. open.Values
                    .Where(document => Owner(document.Tree.Path) is null)
                    .DistinctBy(document => document.Tree.Path, FilePaths.Comparer)
                    .Select(document => document.Tree)],
                ProjectSettings.None),
            cancellation);

    /// <summary>
    /// Returns the project a file belongs to, or null for none. A library two projects share is part of
    /// both, but for what the editor shows about it, it belongs to the project whose root is
    /// nearest.
    /// </summary>
    private WorkspaceProject? Owner(string path) =>
        projects.Where(project => project.Owns(path))
            .OrderByDescending(project => Within(project.Root, path) ? project.Root.Length : -1)
            .FirstOrDefault();

    /// <summary>
    /// Returns the files that <paramref name="paths"/> stand for. A folder on disk stands for the
    /// files beneath it. A path that is gone stands for the files beneath it that the workspace
    /// was reading, or for itself when there are none. The caller holds the lock.
    /// </summary>
    private List<string> Expanded(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                files.AddRange(FilesBeneath(path));
                continue;
            }
            List<string> beneath = File.Exists(path)
                ? []
                : [.. projects.SelectMany(project => project.Reads())
                    .Concat(loose.Finished?.Binaries ?? [])
                    .Where(file => Within(path, file))
                    .Distinct(StringComparer.Ordinal)];
            if (beneath.Count > 0)
                files.AddRange(beneath);
            else
                files.Add(path);
        }
        return files;
    }

    /// <summary>Marks every program that names a changed file to be analyzed again.</summary>
    private void Invalidate(string path)
    {
        var owned = false;
        foreach (var project in projects.Where(project => project.Owns(path)))
        {
            project.Invalidate();
            owned = true;
        }
        if (!owned)
            loose.Invalidate();
    }

    /// <summary>
    /// Builds each project in the active configuration. A project that lacks it builds in its
    /// default, unless no project has it at all, in which case every project reports the
    /// unknown name.
    /// </summary>
    private void ConfigureAll()
    {
        var anyNames = AnyNames();
        foreach (var project in projects)
            project.Configure(Named(project, configuration, anyNames));
        loose.Invalidate();
    }

    private bool AnyNames() =>
        projects.Any(project => project.Own.Configurations.Any(c => c.Name == configuration));
}
