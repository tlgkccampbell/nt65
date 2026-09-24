using System.Collections.Immutable;
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
/// </summary>
internal sealed class Workspace
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Document> open = new(StringComparer.Ordinal);

    // The URI the client uses for each file it has named, by logical path. VS Code escapes a
    // drive's colon and nt65 does not, so a file the client has opened keeps the client's form of
    // its URI for the rest of the session. Two forms of one file's URI would list its diagnostics
    // in the Problems panel twice.
    private readonly Dictionary<string, string> named = new(StringComparer.Ordinal);
    private readonly List<WorkspaceProject> projects = [];
    private IReadOnlyList<string> roots = [];
    private string? configuration;

    // The analysis of the program formed by the open documents no project names, and the
    // previous one, which the next analysis starts from.
    private ProgramAnalysis? loose;
    private ProgramAnalysis? loosePrevious;

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
                projects.AddRange(ProjectFiles(root).Select(file => new WorkspaceProject(file)));
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
            var gone = removed.Select(Uris.ToPath).ToHashSet(StringComparer.Ordinal);
            IReadOnlyList<string> folders = [.. roots.Where(root => !gone.Contains(root))
                .Concat(added.Select(Uris.ToPath))
                .Where(root => root.Length > 0)
                .Distinct(StringComparer.Ordinal)];
            Load(folders, configuration);
            return folders;
        }
    }

    /// <summary>
    /// Builds every project in the named configuration from now on, or with its own settings for
    /// null.
    /// </summary>
    public void Configure(string? active)
    {
        lock (gate)
        {
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
            return open[id.Uri] = new Document(id.Uri, id.Version, tree);
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
    /// </summary>
    public bool ChangedOnDisk(IEnumerable<string> uris)
    {
        lock (gate)
        {
            var changed = false;
            foreach (var path in uris.Select(Uris.ToPath))
            {
                if (Paths.Normalized(path).Split('/')[^1] == ProjectFile.Name)
                {
                    changed |= projects.RemoveAll(project => SamePath(project.File, path)) > 0;
                    if (File.Exists(path) && roots.Any(root => Within(root, path)))
                    {
                        var added = new WorkspaceProject(path);
                        added.Configure(Named(added, configuration, AnyNames()));
                        projects.Add(added);
                        projects.Sort((a, b) => string.CompareOrdinal(a.File, b.File));
                        changed = true;
                    }
                    ConfigureAll();
                    loose = null;
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
                if (loose?.Binaries.Contains(path) == true)
                {
                    loose = null;
                    changed = true;
                }
            }
            return changed;
        }
    }

    /// <summary>
    /// Returns the analysis of the program that <paramref name="path"/> belongs to, built once and
    /// kept until something changes. Documents the client has open replace the files on disk.
    /// </summary>
    public ProgramAnalysis AnalysisFor(string path)
    {
        lock (gate)
        {
            return Owner(path) is { } project ? project.Analysis(open.Values) : Loose();
        }
    }

    /// <summary>
    /// Returns the binaries that <c>.incbin</c> directives include, across every program, as
    /// logical paths.
    /// The editor has to be asked to watch these in addition to the sources and project files,
    /// because only the program says which files it includes.
    /// </summary>
    public IReadOnlyList<string> Binaries()
    {
        lock (gate)
        {
            return [.. projects.Select(project => project.Analysis(open.Values))
                .Concat(loose is null ? [] : [loose])
                .SelectMany(analysis => analysis.Binaries)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];
        }
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
    public IReadOnlyList<ProgramAnalysis> Programs()
    {
        lock (gate)
        {
            return
            [
                .. projects.Select(project => project.Analysis(open.Values)),
                .. open.Values.Any(document => Owner(document.Tree.Path) is null) ? (ProgramAnalysis[])[Loose()] : [],
            ];
        }
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
    /// Returns the analysis of the open documents that no project names. Each open document supplies its own
    /// tree in place of the file on disk, and an edit re-parsed only the lines it touched.
    /// </summary>
    private ProgramAnalysis Loose()
    {
        if (loose is not null)
            return loose;
        var sources = open.Values.Where(document => Owner(document.Tree.Path) is null).Select(document => document.Tree);
        return loose = loosePrevious = Compiler.Analyze([.. sources], ProjectSettings.None, loosePrevious);
    }

    /// <summary>Returns the open document for a logical path, or null when it is not open.</summary>
    private Document? Opened(string path) =>
        open.Values.FirstOrDefault(document => document.Tree.Path == path);

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
    /// Returns the diagnostics of one open document, which is the file the client has just opened
    /// or edited, or null when the document is not open. They come from the whole program's
    /// analysis, so they include problems another file causes in this one; the other files'
    /// diagnostics are published separately.
    /// </summary>
    public Published? ToPublish(string uri)
    {
        lock (gate)
        {
            if (!open.TryGetValue(uri, out var document))
                return null;
            var path = document.Tree.Path;
            var analysis = Owner(path) is { } project ? project.Analysis(open.Values) : Loose();
            return new Published(
                UriOf(path), document.Version, document.Tree,
                analysis.DiagnosticsFor(path), analysis.Configuration);
        }
    }

    /// <summary>
    /// Returns whether the last analysis of the program that <paramref name="path"/> belongs to had
    /// to re-analyze more than that one file. That is exactly when what names in other files refer
    /// to can have changed, and with it those files' semantic colouring and lenses.
    /// </summary>
    public bool ReachedOtherFiles(string path)
    {
        var analysis = AnalysisFor(path);
        return analysis.WholeProgram is not null || analysis.Reanalyzed > 1;
    }

    /// <summary>
    /// Returns every file whose diagnostics are published, with those diagnostics. The files are
    /// each file of each project, each project file, and each open document that belongs to no
    /// project. A file two projects share is reported by the nearer project, which is the one
    /// every other answer about the file comes from.
    /// </summary>
    public IReadOnlyList<Published> ToPublish()
    {
        lock (gate)
        {
            var found = new Dictionary<string, (ProgramAnalysis Analysis, SyntaxTree? Tree)>(StringComparer.Ordinal);
            foreach (var project in projects)
            {
                var analysis = project.Analysis(open.Values);

                // The project file is not one of the program's sources, but its errors can stop
                // the program being read at all, so they are published like any other file's.
                found[project.File] = (analysis, null);
                foreach (var file in analysis.Program.Files)
                {
                    if (Owner(file.Tree.Path) == project && !StandardModules.IsStandard(file.Tree.Path))
                        found[file.Tree.Path] = (analysis, file.Tree);
                }
            }
            if (open.Values.Any(document => Owner(document.Tree.Path) is null))
            {
                var analysis = Loose();
                foreach (var file in analysis.Program.Files.Where(file => !StandardModules.IsStandard(file.Tree.Path)))
                    found[file.Tree.Path] = (analysis, file.Tree);
            }
            return [.. found
                .OrderBy(file => file.Key, StringComparer.Ordinal)
                .Select(file => new Published(
                    UriOf(file.Key),
                    Opened(file.Key)?.Version,
                    file.Value.Tree,
                    file.Value.Analysis.DiagnosticsFor(file.Key),
                    file.Value.Analysis.Configuration))];
        }
    }

    /// <summary>
    /// Returns every file of every program as the editor has it, for a search across the
    /// workspace. The files are the open documents and the files the projects name.
    /// </summary>
    public IReadOnlyList<SyntaxTree> Files()
    {
        lock (gate)
        {
            var trees = new Dictionary<string, SyntaxTree>(StringComparer.Ordinal);
            foreach (var tree in projects.SelectMany(project => project.OnDisk()))
                trees[tree.Path] = tree;
            foreach (var document in open.Values)
                trees[document.Tree.Path] = document.Tree;
            return [.. trees.Values.OrderBy(tree => tree.Path, StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// Returns the project a file belongs to, or null for none. A library two projects share is part of
    /// both, but for what the editor shows about it, it belongs to the project whose root is
    /// nearest.
    /// </summary>
    private WorkspaceProject? Owner(string path) =>
        projects.Where(project => project.Owns(path))
            .OrderByDescending(project => Within(project.Root, path) ? project.Root.Length : -1)
            .FirstOrDefault();

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
            loose = null;
    }

    /// <summary>
    /// Builds each project in the active configuration. A project that lacks it builds with its
    /// own settings, unless no project has it at all, in which case every project reports the
    /// unknown name.
    /// </summary>
    private void ConfigureAll()
    {
        var anyNames = AnyNames();
        foreach (var project in projects)
            project.Configure(Named(project, configuration, anyNames));
        loose = null;
    }

    private bool AnyNames() =>
        projects.Any(project => project.Own.Configurations.Any(c => c.Name == configuration));

    private static string? Named(WorkspaceProject project, string? configuration, bool anyNames) =>
        configuration is { Length: > 0 } && (!anyNames || project.Own.Configurations.Any(c => c.Name == configuration))
            ? configuration
            : null;

    /// <summary>
    /// Returns every project file in <paramref name="root"/> and the folders beneath it, as logical
    /// paths. Folders whose names start with <c>.</c>, and <c>node_modules</c> folders, are
    /// skipped, because they hold no projects of the programmer's.
    /// </summary>
    private static IEnumerable<string> ProjectFiles(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.TryPop(out var directory))
        {
            if (!Directory.Exists(directory))
                continue;
            var file = Path.Combine(directory, ProjectFile.Name);
            if (File.Exists(file))
                yield return Paths.Normalized(file);
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
        string.Equals(Paths.Normalized(a), Paths.Normalized(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool Within(string directory, string path) =>
        Paths.Normalized(path).StartsWith(Paths.Normalized(directory) + "/",
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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

            var start = Position(text, starts, range.Start.Line, range.Start.Character);
            var end = Math.Max(start, Position(text, starts, range.End.Line, range.End.Character));
            applied.Add(new TextChange(start, end - start, change.Text));
            text = string.Concat(text.AsSpan(0, start), change.Text, text.AsSpan(end));
            starts = SyntaxTree.LineOffsets(text);
        }
        return tree.WithChanges(applied);
    }

    /// <summary>
    /// Returns the offset of a zero-based line and character in <paramref name="text"/>, clamped to
    /// the text in the same way that <see cref="SyntaxTree.GetPosition"/> clamps. An editor may
    /// name a position past the end of a line or of the file, and that is not an error here.
    /// </summary>
    private static int Position(string text, ImmutableArray<int> starts, int line, int character)
    {
        if (line < 0)
            return 0;
        if (line >= starts.Length)
            return text.Length;
        var start = starts[line];
        var end = line + 1 < starts.Length ? starts[line + 1] : text.Length;
        return character <= 0 ? start : Math.Min(start + character, end);
    }
}
