using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// One <c>nt65.json</c> in the workspace, and the program it describes: the files its globs
/// name, read from disk when the program is first asked for, and the analysis of them. The
/// <see cref="Workspace"/> holds the lock; nothing here is safe to use without it.
/// </summary>
internal sealed class WorkspaceProject
{
    // A cache of whether the globs match each path, since that is checked for every open
    // document on every analysis.
    private readonly Dictionary<string, bool> owned = new(StringComparer.Ordinal);

    // The files on disk, by logical path, read the first time the program is analyzed.
    private Dictionary<string, SyntaxTree>? onDisk;
    private ProgramAnalysis? analysis;

    // The last analysis, kept past the change that made it stale so the next one can start from it.
    private ProgramAnalysis? previous;

    /// <param name="file">The project file, as a logical path.</param>
    public WorkspaceProject(string file)
    {
        File = file;
        Root = Paths.Directory(file);
        Settings = ProjectFile.Read(file, Workspace.Read(file) ?? "");
        Own = Settings;
    }

    /// <summary>The project file, as a logical path.</summary>
    public string File { get; }

    /// <summary>The directory it is in, which its globs are relative to.</summary>
    public string Root { get; }

    /// <summary>What the project file says, with the active configuration applied.</summary>
    public ProjectSettings Settings { get; private set; }

    /// <summary>What the project file says, before any configuration.</summary>
    public ProjectSettings Own { get; }

    /// <summary>Whether a <c>files</c> glob names <paramref name="path"/>, whether or not it is on disk.</summary>
    public bool Owns(string path)
    {
        if (!owned.TryGetValue(path, out var owns))
            owned[path] = owns = Own.Files.Any(glob => SourceGlobs.Matches(Root, glob, path));
        return owns;
    }

    /// <summary>
    /// Builds the project as the named configuration, or as its own settings for null. A name
    /// the project does not have is reported against the project file.
    /// </summary>
    public void Configure(string? configuration)
    {
        Settings = configuration is { Length: > 0 } ? Own.Configured(configuration, new Span(File, 1, 1, 2)) : Own;
        Invalidate();
    }

    /// <summary>Discards the current analysis; the previous one is kept for the next analysis to start from.</summary>
    public void Invalidate() => analysis = null;

    /// <summary>
    /// The file at <paramref name="path"/> changed on disk, was created or was deleted. It is
    /// read again if the project's files on disk have been read at all.
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

    /// <summary>Whether the last analysis included <paramref name="path"/> through an <c>.incbin</c>.</summary>
    public bool Measured(string path) => analysis?.Binaries.Contains(path) ?? previous?.Binaries.Contains(path) ?? false;

    /// <summary>Every file of the program on disk, read if it has not been.</summary>
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
    /// The program's analysis, with the documents in <paramref name="open"/> that the project
    /// names taking the place of their files on disk, built once and kept until something
    /// changes.
    /// </summary>
    public ProgramAnalysis Analysis(IEnumerable<Document> open)
    {
        if (analysis is not null)
            return analysis;
        var sources = OnDisk().ToDictionary(tree => tree.Path, StringComparer.Ordinal);
        foreach (var document in open.Where(document => Owns(document.Tree.Path)))
            sources[document.Tree.Path] = document.Tree;
        return analysis = previous = Compiler.Analyze([.. sources.Values], Settings, previous);
    }
}
