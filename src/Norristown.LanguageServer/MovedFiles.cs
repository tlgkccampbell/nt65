using Norristown.Project;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Computes the edits that moving or renaming a file requires. A module's name comes from its
/// <c>.module</c> line, and its output is named after the module wherever the source file is.
/// Moving a source file therefore needs fewer edits than one might expect, because no other
/// source file refers to it by path.
/// <para>
/// Two things do refer to paths. A <c>files</c> entry in <c>nt65.json</c> that names the file
/// literally is rewritten to its new path. A glob entry either still matches the new path and
/// needs nothing, or stops matching. That case is reported rather than rewritten, because only
/// the programmer knows which glob they meant to widen. An <c>.incbin</c> path is resolved
/// relative to the file that contains it, so it changes when either that file or the binary it
/// names moves.
/// </para>
/// <para>
/// Moving a folder moves every file beneath it that a program reads, all at once. A path between
/// two files that move together is left alone, because it still reaches the same file.
/// </para>
/// </summary>
internal static class MovedFiles
{
    /// <summary>
    /// Returns the edits <paramref name="renames"/> require, and the messages to show the
    /// programmer about changes that cannot be made automatically.
    /// </summary>
    /// <param name="workspace">The workspace the editor is working on.</param>
    /// <param name="programs">The analysis of every program the workspace holds.</param>
    /// <param name="renames">
    /// Each file or folder's current path and new path, as logical paths. A folder is one that
    /// exists on disk at its current path, since the client asks before it moves anything.
    /// </param>
    public static (Protocol.WorkspaceEdit? Edit, IReadOnlyList<string> Messages) For(
        Workspace workspace, IReadOnlyList<ProgramAnalysis> programs, IReadOnlyList<(string From, string To)> renames)
    {
        var moved = Expanded(workspace, programs, renames);
        var edits = new Dictionary<string, List<Protocol.TextEdit>>(StringComparer.Ordinal);
        var messages = new List<string>();
        Named(workspace, moved, edits, messages);
        Included(programs, moved, edits);
        if (edits.Count == 0)
            return (null, messages);
        return (
            new Protocol.WorkspaceEdit(edits.ToDictionary(
                file => file.Key,
                file => (IReadOnlyList<Protocol.TextEdit>)[.. file.Value.OrderBy(edit => edit.Range.Start.Line)],
                StringComparer.Ordinal)),
            messages);
    }

    /// <summary>
    /// Returns the new path of every file that moves, keyed by its current path. A folder stands
    /// for every file beneath it that a program reads, which are the sources, the binaries and the
    /// project files.
    /// </summary>
    private static Dictionary<string, string> Expanded(
        Workspace workspace, IReadOnlyList<ProgramAnalysis> programs, IReadOnlyList<(string From, string To)> renames)
    {
        var moved = new Dictionary<string, string>(FilePaths.Comparer);
        foreach (var (from, to) in renames)
        {
            if (!Directory.Exists(from))
            {
                moved[from] = to;
                continue;
            }
            var known = programs
                .SelectMany(analysis => analysis.Program.Files.Select(model => model.Tree.Path).Concat(analysis.Binaries))
                .Concat(workspace.Projects().Select(project => project.File));
            foreach (var path in known)
            {
                if (path.StartsWith(from + "/", FilePaths.Comparison))
                    moved[path] = to + path[from.Length..];
            }
        }
        return moved;
    }

    /// <summary>
    /// Handles each <c>files</c> entry that names a moved file. An entry that names it literally
    /// is rewritten, and a glob that no longer matches is reported once. An entry is relative to
    /// its project file, which may itself be moving with a folder.
    /// </summary>
    private static void Named(
        Workspace workspace, Dictionary<string, string> moved,
        Dictionary<string, List<Protocol.TextEdit>> edits, List<string> messages)
    {
        foreach (var project in workspace.Projects())
        {
            var text = Workspace.Read(project.File);
            if (text is null)
                continue;
            var root = Paths.Directory(moved.GetValueOrDefault(project.File, project.File));
            foreach (var glob in project.Own.Files)
            {
                foreach (var (from, to) in moved.Where(file => SourceGlobs.Matches(project.Root, glob, file.Key)))
                {
                    if (glob.Contains('*', StringComparison.Ordinal) || glob.Contains('?', StringComparison.Ordinal))
                    {
                        if (!SourceGlobs.Matches(root, glob, to))
                        {
                            messages.Add($"nt65: `{glob}` in {Shown(project.File)} does not match {Shown(to)}, the file's new path. "
                                + "The glob was left unchanged: edit `files` by hand if the moved file should still be built.");
                            break;
                        }
                        continue;
                    }
                    var now = Relative(root, to);
                    if (now != Paths.Normalized(glob) && Json.Entry(text, "files", glob) is { } at)
                        Add(edits, project.File, Lsp.ToRange(text, at), Json.Quoted(now));
                }
            }
        }
    }

    /// <summary>
    /// Rewrites the <c>.incbin</c> paths a move changes. These are the paths in a file that moved
    /// to another folder, since they are resolved relative to that file, and the paths naming a
    /// binary that moved.
    /// </summary>
    private static void Included(
        IReadOnlyList<ProgramAnalysis> programs, Dictionary<string, string> moved,
        Dictionary<string, List<Protocol.TextEdit>> edits)
    {
        // A file that several programs share, such as a library, is in each of their analyses,
        // but its paths are rewritten once. Edits that overlap make a client reject the whole edit.
        var seen = new HashSet<string>(FilePaths.Comparer);
        foreach (var analysis in programs)
        {
            foreach (var model in analysis.Program.Files)
            {
                if (!seen.Add(model.Tree.Path))
                    continue;
                // A path is rewritten when the file containing it moved, so that it resolves
                // from the new folder, and when it names a file that moved.
                var file = model.Tree.Path;
                var newFile = moved.GetValueOrDefault(file, file);
                foreach (var (operand, included) in Includes(model))
                {
                    if (Paths.IsRooted(included))
                        continue;
                    var names = Paths.Beside(file, included);
                    var target = moved.GetValueOrDefault(names, names);
                    if (newFile == file && target == names)
                        continue;
                    var now = Relative(Paths.Directory(newFile), target);
                    if (now == included)
                        continue;
                    Add(edits, file, Lsp.ToRange(model.Tree, operand.Span), Json.Quoted(now));
                }
            }
        }
    }

    /// <summary>
    /// Returns every <c>.incbin</c> in a file, as the operand that gives the path and the path
    /// itself.
    /// </summary>
    private static IEnumerable<(SyntaxNode Operand, string Included)> Includes(SemanticModel model)
    {
        foreach (var directive in model.Tree.Root.DescendantNodes().OfType<DataDirectiveSyntax>())
        {
            if (directive.Directive.Text.Equals(".incbin", StringComparison.OrdinalIgnoreCase)
                && directive.Tail is InlineDataSyntax { Values: [var operand, ..] }
                && model.ValueOf(operand) is { Kind: ValueKind.String, Text: { } included })
            {
                yield return (operand, included);
            }
        }
    }

    /// <summary>
    /// Adds one edit, naming the file by the URI the client knows it as. A file two programs share
    /// is visited once for each, and the second visit's edit is dropped.
    /// </summary>
    private static void Add(
        Dictionary<string, List<Protocol.TextEdit>> edits, string path, Protocol.Range at, string included)
    {
        var uri = Uris.ToUri(path);
        if (!edits.TryGetValue(uri, out var found))
            edits[uri] = found = [];
        if (!found.Any(edit => edit.Range == at))
            found.Add(new Protocol.TextEdit(at, included));
    }

    /// <summary>Returns the path of a file relative to a directory, with <c>/</c> separators.</summary>
    private static string Relative(string directory, string path) =>
        Paths.Normalized(Path.GetRelativePath(directory, path));

    /// <summary>Returns a file's name without its folders, as a message shows it.</summary>
    private static string Shown(string path) => path[(path.LastIndexOf('/') + 1)..];
}
