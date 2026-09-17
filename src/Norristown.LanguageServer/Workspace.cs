using Norristown.LanguageServer.Protocol;
using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// The program the editor is working on: the project file, every source file it names, and
/// the documents the client has open. An open document's text lives here rather than on
/// disk, and an edit re-parses it incrementally, so the lines a change did not touch keep
/// the green nodes they already had.
/// <para>
/// A file is not analyzed on its own, because a name it uses may be one another file exports.
/// The program is analyzed the first time anything asks, and again after an edit, starting
/// from the analysis before it: an edit that leaves what other files see of a file alone
/// analyzes only that file.
/// </para>
/// </summary>
internal sealed class Workspace
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Document> open = new(StringComparer.Ordinal);
    private ProjectSettings project = ProjectSettings.None;
    private IReadOnlyList<SyntaxTree> onDisk = [];
    private ProgramAnalysis? analysis;

    // The last analysis, kept past the edit that made it stale so the next one can start from it.
    private ProgramAnalysis? previous;

    /// <summary>
    /// The logical path a URI names, with <c>/</c> separators, which is what diagnostics and
    /// output carry. A URI that is not a file keeps its own spelling.
    /// </summary>
    public static string PathOf(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile
            ? parsed.LocalPath.Replace('\\', '/')
            : uri;

    /// <summary>
    /// Reads the project the client opened, if there is one, built as the named
    /// <paramref name="configuration"/> when one is given, as the editor's setting chooses. Its
    /// files are read from disk; open documents replace them as the client sends them.
    /// </summary>
    public void Load(string? rootUri, string? configuration = null)
    {
        lock (gate)
        {
            project = ProjectSettings.None;
            onDisk = [];
            analysis = null;
            previous = null;
            if (rootUri is null || PathOf(rootUri) is not { Length: > 0 } root)
                return;

            var file = Path.Combine(root, ProjectFile.Name);
            if (!File.Exists(file))
                return;

            // What is wrong with the project file travels with the settings, and is reported
            // when the program is analyzed.
            var path = PathOf(new Uri(file).AbsoluteUri);
            project = ProjectFile.Read(path, Read(file) ?? "");
            if (configuration is { Length: > 0 })
                project = project.Configured(configuration, new Span(path, 1, 1, 2));
            onDisk = [.. project.Files
                .SelectMany(glob => Matching(root, glob))
                .Distinct(StringComparer.Ordinal)
                .Select(path => Read(path) is { } text ? SyntaxTree.Parse(path, text) : null)
                .OfType<SyntaxTree>()];
        }
    }

    /// <summary>Takes a newly opened document and parses it.</summary>
    public Document Open(TextDocumentItem item)
    {
        var document = new Document(item.Uri, item.Version, SyntaxTree.Parse(PathOf(item.Uri), item.Text));
        lock (gate)
        {
            open[item.Uri] = document;
            analysis = null;
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
            var tree = document.Tree;
            foreach (var change in changes)
                tree = Apply(tree, change);
            analysis = null;
            return open[id.Uri] = new Document(id.Uri, id.Version, tree);
        }
    }

    /// <summary>Forgets a document. From here on the file on disk is what it is.</summary>
    public void Close(string uri)
    {
        lock (gate)
        {
            open.Remove(uri);
            analysis = null;
        }
    }

    /// <summary>An open document, or null when it is not open.</summary>
    public Document? Find(string uri)
    {
        lock (gate)
        {
            return open.GetValueOrDefault(uri);
        }
    }

    /// <summary>Every open document, for republishing what a change elsewhere made wrong.</summary>
    public IReadOnlyList<Document> Open()
    {
        lock (gate)
        {
            return [.. open.Values];
        }
    }

    /// <summary>
    /// What the program means, built once and kept until something changes. Files the client
    /// has open stand in for whatever is on disk.
    /// </summary>
    public ProgramAnalysis Analysis()
    {
        lock (gate)
        {
            if (analysis is not null)
                return analysis;

            // An open document stands in for whatever is on disk, and brings its own tree,
            // which an edit re-parsed only in the lines it touched.
            var sources = new Dictionary<string, SyntaxTree>(StringComparer.Ordinal);
            foreach (var tree in onDisk)
                sources[tree.Path] = tree;
            foreach (var document in open.Values)
                sources[document.Tree.Path] = document.Tree;
            return analysis = previous = Compiler.Analyze([.. sources.Values], project, previous);
        }
    }

    private static string? Read(string path)
    {
        // A file that moved or is being written while the editor asks is not a crash; the
        // next analysis will find it.
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The files one <c>files</c> glob names, as logical paths.</summary>
    private static IEnumerable<string> Matching(string root, string glob)
    {
        var normalized = glob.Replace('\\', '/');
        var at = normalized.IndexOf("**/", StringComparison.Ordinal);
        var (under, pattern, search) = at >= 0
            ? (normalized[..at], normalized[(at + 3)..], SearchOption.AllDirectories)
            : (Folder(normalized), Leaf(normalized), SearchOption.TopDirectoryOnly);

        var from = Path.GetFullPath(Path.Combine(root, under.Replace('/', Path.DirectorySeparatorChar)));
        if (!Directory.Exists(from) || pattern.Contains('/'))
            return [];
        return Directory.EnumerateFiles(from, pattern, search).Select(path => path.Replace('\\', '/'));

        static string Folder(string path) => path.LastIndexOf('/') is var i && i >= 0 ? path[..i] : "";
        static string Leaf(string path) => path.LastIndexOf('/') is var i && i >= 0 ? path[(i + 1)..] : path;
    }

    private static SyntaxTree Apply(SyntaxTree tree, TextDocumentContentChangeEvent change)
    {
        // No range means the whole document, which a client sends when it cannot describe the
        // edit; there is nothing to reuse then.
        if (change.Range is not { } range)
            return SyntaxTree.Parse(tree.Path, change.Text);

        var start = tree.GetPosition(range.Start.Line, range.Start.Character);
        var end = tree.GetPosition(range.End.Line, range.End.Character);
        return tree.WithChange(new TextChange(start, Math.Max(0, end - start), change.Text));
    }
}
