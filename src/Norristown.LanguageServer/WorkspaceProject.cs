using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Represents one <c>nt65.json</c> in the workspace and the program it describes. The program is
/// the files the project's globs name, read from disk when the program is first requested, and
/// their analysis. The <see cref="Workspace"/> holds the lock, and nothing here is safe to use
/// without it. The analysis itself runs outside the lock, in a <see cref="LiveAnalysis"/>.
/// </summary>
internal sealed class WorkspaceProject
{
    // A cache of whether the globs match each path, since that is checked for every open
    // document on every analysis.
    private readonly Dictionary<string, bool> owned = new(StringComparer.Ordinal);

    // The program's analysis, which keeps the last analysis past the change that made it stale
    // so that the next analysis can start from it.
    private readonly LiveAnalysis analysis;

    // The files on disk, by logical path, read the first time the program is analyzed.
    private Dictionary<string, SyntaxTree>? onDisk;

    /// <summary>Creates the project for a project file and reads the file's settings.</summary>
    /// <param name="file">The project file, as a logical path.</param>
    /// <param name="analyzer">The function that analyzes the program.</param>
    public WorkspaceProject(string file, Analyzer analyzer)
    {
        analysis = new LiveAnalysis(analyzer);
        File = file;
        Root = Paths.Directory(file);
        Settings = ProjectFile.Read(file, Workspace.Read(file) ?? "");
        Own = Settings;
    }

    /// <summary>Gets the project file, as a logical path.</summary>
    public string File { get; }

    /// <summary>Gets the directory that holds the project file, which its globs are relative to.</summary>
    public string Root { get; }

    /// <summary>Gets the project's settings, with the active configuration applied.</summary>
    public ProjectSettings Settings { get; private set; }

    /// <summary>
    /// Gets the project's settings as the project file states them, before any configuration.
    /// </summary>
    public ProjectSettings Own { get; }

    /// <summary>
    /// Returns whether a <c>files</c> glob names <paramref name="path"/>, whether or not the file
    /// is on disk.
    /// </summary>
    public bool Owns(string path)
    {
        if (!owned.TryGetValue(path, out var owns))
            owned[path] = owns = Own.Files.Any(glob => SourceGlobs.Matches(Root, glob, path));
        return owns;
    }

    /// <summary>
    /// Builds the project in the named configuration, or with its own settings for null. A name
    /// the project does not have is reported against the project file.
    /// </summary>
    public void Configure(string? configuration)
    {
        Settings = configuration is { Length: > 0 } ? Own.Configured(configuration, new Span(File, 1, 1, 2)) : Own;
        Invalidate();
    }

    /// <summary>
    /// Discards the current analysis. The previous analysis is kept for the next analysis to start
    /// from.
    /// </summary>
    public void Invalidate() => analysis.Invalidate();

    /// <summary>
    /// Handles a change on disk to the file at <paramref name="path"/>, which may have changed,
    /// been created or been deleted. The analysis is discarded, and the file is read again if the
    /// project's files on disk have been read at all.
    /// </summary>
    public void Reread(string path)
    {
        Invalidate();
        if (onDisk is null)
            return;
        if (Workspace.Read(path) is { } text)
            onDisk[path] = SyntaxTree.Parse(path, text);
        else
            onDisk.Remove(path);
    }

    /// <summary>
    /// Returns whether the last analysis included <paramref name="path"/> through an
    /// <c>.incbin</c>.
    /// </summary>
    public bool Measured(string path) => analysis.Latest?.Binaries.Contains(path) ?? false;

    /// <summary>
    /// Returns every file of the program on disk, reading the files if they have not been read.
    /// </summary>
    public IReadOnlyCollection<SyntaxTree> OnDisk()
    {
        onDisk ??= Own.Files
            .SelectMany(glob => SourceGlobs.Matching(Root, glob))
            .Select(path => Paths.Normalized(Path.GetFullPath(path, Root)))
            .Distinct(StringComparer.Ordinal)
            .Select(path => Workspace.Read(path) is { } text ? SyntaxTree.Parse(path, text) : null)
            .OfType<SyntaxTree>()
            .ToDictionary(tree => tree.Path, StringComparer.Ordinal);
        return onDisk.Values;
    }

    /// <summary>
    /// Returns the program's analysis, in which the documents in <paramref name="open"/> that the
    /// project names replace their files on disk. The analysis is built once and kept until
    /// something changes. The files are taken before this method returns, and the analysis runs on
    /// the thread pool.
    /// </summary>
    /// <param name="open">The documents the client has open.</param>
    /// <param name="cancellation">Stops this request waiting for the analysis.</param>
    public Task<ProgramAnalysis> AnalysisAsync(IEnumerable<Document> open, CancellationToken cancellation) =>
        analysis.AnalysisAsync(
            () =>
            {
                var sources = OnDisk().ToDictionary(tree => tree.Path, StringComparer.Ordinal);
                foreach (var document in open.Where(document => Owns(document.Tree.Path)))
                    sources[document.Tree.Path] = document.Tree;
                return ([.. sources.Values], Settings);
            },
            cancellation);
}
