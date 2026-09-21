using System.Collections.Immutable;
using Norristown.LanguageServer.Protocol;
using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// What the editor is working on: every <c>nt65.json</c> in the folders the client opened, the
/// program each describes, and the documents the client has open. An open document's text lives
/// here rather than on disk, and an edit re-parses it incrementally, so the lines a change did
/// not touch keep the green nodes they already had.
/// <para>
/// A file is not analyzed on its own, because a name it uses may be one another file exports.
/// A document belongs to the project whose <c>files</c> name it; one no project names is part of
/// a program of every such document. A program is analyzed the first time anything asks, and
/// again after a change, starting from the analysis before it: an edit that leaves what other
/// files see of a file alone analyzes only that file.
/// </para>
/// </summary>
internal sealed class Workspace
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Document> open = new(StringComparer.Ordinal);

    // How the client spells the URI of each file it has named, by logical path. VS Code
    // escapes a drive's colon and nt65 does not, so a file the client has opened keeps the
    // client's spelling for the rest of the session: two spellings of one file would leave
    // what is wrong with it in the problem list twice.
    private readonly Dictionary<string, string> named = new(StringComparer.Ordinal);
    private readonly List<WorkspaceProject> projects = [];
    private IReadOnlyList<string> roots = [];
    private string? configuration;

    // The program of the open documents no project names, and the analysis before it.
    private ProgramAnalysis? loose;
    private ProgramAnalysis? loosePrevious;

    /// <summary>
    /// The logical path a URI names, with <c>/</c> separators, which is what diagnostics and
    /// output carry. A URI that is not a file keeps its own spelling.
    /// </summary>
    public static string PathOf(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
            return uri;
        // VS Code escapes a drive's colon, `file:///c%3A/src`, which .NET does not take for a
        // drive and gives back as `/c:/src`.
        var path = parsed.LocalPath.Replace('\\', '/');
        return path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':' ? path[1..] : path;
    }

    /// <summary>A file's text, or null when it cannot be read.</summary>
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
    /// Finds the projects in the folders the client opened, the folders themselves and every
    /// folder beneath them, built as the named <paramref name="active"/> configuration where a
    /// project has one, as the editor's setting chooses. Their files are read from disk when
    /// first needed; open documents replace them as the client sends them.
    /// </summary>
    public void Load(IReadOnlyList<string> rootUris, string? active = null)
    {
        lock (gate)
        {
            roots = [.. rootUris.Select(PathOf).Where(root => root.Length > 0)];
            configuration = active;
            projects.Clear();
            foreach (var root in roots)
                projects.AddRange(ProjectFiles(root).Select(file => new WorkspaceProject(file)));
            projects.Sort((a, b) => string.CompareOrdinal(a.File, b.File));
            ConfigureAll();
        }
    }

    /// <summary>The same, for a client that opened one folder, or none.</summary>
    public void Load(string? rootUri, string? active = null) => Load(rootUri is null ? [] : [rootUri], active);

    /// <summary>
    /// The folders the client opened, with <paramref name="added"/> among them and
    /// <paramref name="removed"/> no longer. The projects are looked for again in what is left,
    /// because a folder that joined brings whatever projects are in it.
    /// </summary>
    /// <returns>The folders the workspace now has, as logical paths.</returns>
    public IReadOnlyList<string> WithFolders(IEnumerable<string> added, IEnumerable<string> removed)
    {
        lock (gate)
        {
            var gone = removed.Select(PathOf).ToHashSet(StringComparer.Ordinal);
            IReadOnlyList<string> folders = [.. roots.Where(root => !gone.Contains(root))
                .Concat(added.Select(PathOf))
                .Where(root => root.Length > 0)
                .Distinct(StringComparer.Ordinal)];
            Load(folders, configuration);
            return folders;
        }
    }

    /// <summary>Builds every project as the named configuration from here on, or as its own settings for null.</summary>
    public void Configure(string? active)
    {
        lock (gate)
        {
            configuration = active;
            ConfigureAll();
        }
    }

    /// <summary>The named configurations the projects have, for a client to offer.</summary>
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
        var document = new Document(item.Uri, item.Version, SyntaxTree.Parse(PathOf(item.Uri), item.Text));
        lock (gate)
        {
            open[item.Uri] = document;
            named[document.Tree.Path] = item.Uri;
            Invalidate(document.Tree.Path);
        }
        return document;
    }

    /// <summary>
    /// Applies <paramref name="changes"/> in order, or null when the client changed a
    /// document it never opened, which is the client's mistake and not worth a crash.
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

    /// <summary>Forgets a document. From here on the file on disk is what it is.</summary>
    public void Close(string uri)
    {
        lock (gate)
        {
            if (open.Remove(uri, out var document))
                Invalidate(document.Tree.Path);
        }
    }

    /// <summary>
    /// An open document, or null when it is not open. A URI spelled another way than the client
    /// spelled it at <c>didOpen</c> names the same file and finds the same document: a link the
    /// server wrote into a hover comes back spelled the server's way, not the client's.
    /// </summary>
    public Document? Find(string uri)
    {
        lock (gate)
        {
            if (open.TryGetValue(uri, out var document))
                return document;
            return named.TryGetValue(PathOf(uri), out var spelled) ? open.GetValueOrDefault(spelled) : null;
        }
    }

    /// <summary>
    /// Files that changed on disk: a project file, a source, or a file an <c>.incbin</c> measured.
    /// Whether any of them is one the workspace reads, and so whether what is wrong may have
    /// changed.
    /// </summary>
    public bool ChangedOnDisk(IEnumerable<string> uris)
    {
        lock (gate)
        {
            var changed = false;
            foreach (var path in uris.Select(PathOf))
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
    /// What the program <paramref name="path"/> belongs to means, built once and kept until
    /// something changes. Files the client has open stand in for whatever is on disk.
    /// </summary>
    public ProgramAnalysis AnalysisFor(string path)
    {
        lock (gate)
        {
            return Owner(path) is { } project ? project.Analysis(open.Values) : Loose();
        }
    }

    /// <summary>
    /// The files an <c>.incbin</c> was measured from, across every program, as logical paths.
    /// They are what the editor has to be asked to watch beyond the sources and the project
    /// files: which of them a program includes is the program's to say.
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

    /// <summary>The projects the workspace holds, as they stand.</summary>
    public IReadOnlyList<WorkspaceProject> Projects()
    {
        lock (gate)
        {
            return [.. projects];
        }
    }

    /// <summary>
    /// Every program the workspace holds: one per project, and the one of the open documents no
    /// project names. Each is built once and kept, so asking is what publishing already did.
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
    /// The project a file is built as, or the settings a file no project names is built as,
    /// which is what says where its output would go.
    /// </summary>
    public ProjectSettings SettingsFor(string path)
    {
        lock (gate)
        {
            return Owner(path) is { } project ? project.Settings : ProjectSettings.None;
        }
    }

    /// <summary>
    /// The program of the open documents no project names. An open document stands in for
    /// whatever is on disk, and brings its own tree, which an edit re-parsed only in the lines
    /// it touched.
    /// </summary>
    private ProgramAnalysis Loose()
    {
        if (loose is not null)
            return loose;
        var sources = open.Values.Where(document => Owner(document.Tree.Path) is null).Select(document => document.Tree);
        return loose = loosePrevious = Compiler.Analyze([.. sources], ProjectSettings.None, loosePrevious);
    }

    /// <summary>The open document for a logical path, or null when it is not open.</summary>
    private Document? Opened(string path) =>
        open.Values.FirstOrDefault(document => document.Tree.Path == path);

    /// <summary>
    /// How the client names a file: its own spelling where it has given one. Every URI the
    /// server sends goes through this, so that one file is one file to the editor however the
    /// two of them would have written its path.
    /// </summary>
    public string UriOf(string path)
    {
        lock (gate)
        {
            return named.GetValueOrDefault(path) ?? Lsp.ToUri(path);
        }
    }

    /// <summary>The revision the client holds of a document, or null for one it has not opened.</summary>
    public int? VersionOf(string uri)
    {
        lock (gate)
        {
            return open.GetValueOrDefault(uri)?.Version;
        }
    }

    /// <summary>
    /// What is wrong with one open document, for the file the client has just asked about. It
    /// is the answer of the program's analysis, so a mistake another file makes about this one
    /// is in it; what is wrong with the other files is their own to publish.
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
    /// Whether the last analysis of the program <paramref name="path"/> belongs to had to read
    /// more than that one file, which is exactly when what another file's names refer to, and
    /// so its colours and its lenses, can have moved.
    /// </summary>
    public bool ReachedOtherFiles(string path)
    {
        var analysis = AnalysisFor(path);
        return analysis.WholeProgram is not null || analysis.Reanalyzed > 1;
    }

    /// <summary>
    /// Every file the editor is told about, with what is wrong with it: each file of each
    /// project, each project file, and each open document that belongs to no project. A file
    /// two projects share is reported by the one nearest it, which is the project every other
    /// answer about it comes from.
    /// </summary>
    public IReadOnlyList<Published> ToPublish()
    {
        lock (gate)
        {
            var found = new Dictionary<string, (ProgramAnalysis Analysis, SyntaxTree? Tree)>(StringComparer.Ordinal);
            foreach (var project in projects)
            {
                var analysis = project.Analysis(open.Values);

                // The project file is not a program's file, and what is wrong with it is what
                // stops the program being read at all, so it is worth the same squiggle.
                found[project.File] = (analysis, null);
                foreach (var file in analysis.Program.Files)
                {
                    if (Owner(file.Tree.Path) == project)
                        found[file.Tree.Path] = (analysis, file.Tree);
                }
            }
            if (open.Values.Any(document => Owner(document.Tree.Path) is null))
            {
                var analysis = Loose();
                foreach (var file in analysis.Program.Files)
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
    /// Every file of every program, as the editor has it: open documents, and the files the
    /// projects name, for a search across the workspace.
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
    /// The project a file belongs to, or null for none. A library two projects share is part of
    /// both, and belongs, for what the editor shows of it, to the one nearest it.
    /// </summary>
    private WorkspaceProject? Owner(string path) =>
        projects.Where(project => project.Owns(path))
            .OrderByDescending(project => Within(project.Root, path) ? project.Root.Length : -1)
            .FirstOrDefault();

    /// <summary>A file changed, so every program that names it is analyzed again.</summary>
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
    /// Builds each project as the active configuration. A project without it builds its own
    /// settings, unless no project has it, when it is a mistake each project reports.
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
    /// Every project file in <paramref name="root"/> and the folders beneath it, as logical paths.
    /// A folder whose name starts with <c>.</c>, and <c>node_modules</c>, hold no projects of the
    /// programmer's.
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
    /// <paramref name="tree"/> with the edits of one notification applied in order. They are
    /// turned into text changes together and the tree is rebuilt once, because each of them
    /// names a place in what the one before it left and the lines between the first and the
    /// last are the only ones any of them can have touched.
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
    /// The offset of a 0-based line and character in <paramref name="text"/>, clamped to it, as
    /// <see cref="SyntaxTree.GetPosition"/> clamps. An editor may name a position past the end
    /// of a line or of the file, and that is not an error here.
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
