using Norristown.Project;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// The edits that moving or renaming a file requires. A module's name comes from its
/// <c>.module</c> line, and its output is named after the module wherever the source file is,
/// so moving a source file needs fewer edits than one might expect: no other source file
/// refers to it by path.
/// <para>
/// Two things do refer to paths. A <c>files</c> entry in <c>nt65.json</c> that names the file
/// literally is rewritten to its new path. A glob entry either still matches the new path, and
/// needs nothing, or stops matching; that case is reported rather than rewritten, because only
/// the programmer knows which glob they meant to widen. And an <c>.incbin</c> path is resolved
/// relative to the file that writes it, so it changes when either that file or the binary it
/// names moves.
/// </para>
/// </summary>
internal static class MovedFiles
{
    /// <summary>
    /// The edits <paramref name="renames"/> require, and the messages to show the programmer
    /// about changes that cannot be made automatically.
    /// </summary>
    /// <param name="workspace">What the editor is working on.</param>
    /// <param name="renames">Each file, where it is now and where it is going, as logical paths.</param>
    public static (Protocol.WorkspaceEdit? Edit, IReadOnlyList<string> Said) For(
        Workspace workspace, IReadOnlyList<(string From, string To)> renames)
    {
        var edits = new Dictionary<string, List<Protocol.TextEdit>>(StringComparer.Ordinal);
        var said = new List<string>();
        foreach (var (from, to) in renames)
        {
            Named(workspace, from, to, edits, said);
            Included(workspace, from, to, edits);
        }
        if (edits.Count == 0)
            return (null, said);
        return (
            new Protocol.WorkspaceEdit(edits.ToDictionary(
                file => file.Key,
                file => (IReadOnlyList<Protocol.TextEdit>)[.. file.Value.OrderBy(edit => edit.Range.Start.Line)],
                StringComparer.Ordinal)),
            said);
    }

    /// <summary>
    /// A <c>files</c> entry that names the moved file, rewritten where it names it literally
    /// and reported where it is a glob that no longer matches.
    /// </summary>
    private static void Named(
        Workspace workspace, string from, string to,
        Dictionary<string, List<Protocol.TextEdit>> edits, List<string> said)
    {
        foreach (var project in workspace.Projects())
        {
            var text = Workspace.Read(project.File);
            if (text is null)
                continue;
            foreach (var glob in project.Own.Files)
            {
                if (!SourceGlobs.Matches(project.Root, glob, from))
                    continue;
                if (glob.Contains('*', StringComparison.Ordinal) || glob.Contains('?', StringComparison.Ordinal))
                {
                    if (!SourceGlobs.Matches(project.Root, glob, to))
                    {
                        said.Add($"nt65: `{glob}` in {Shown(project.File)} does not match {Shown(to)}, the file's new path. "
                            + "The glob was left unchanged: edit `files` by hand if the moved file should still be built.");
                    }
                    continue;
                }
                if (Json.Entry(text, "files", glob) is { } at)
                    Add(edits, project.File, text, at, Json.Quoted(Relative(project.Root, to)));
            }
        }
    }

    /// <summary>
    /// The <c>.incbin</c> paths a move changes: those written in a file that moved to another
    /// folder, since they are resolved relative to that file, and those naming a binary that
    /// moved.
    /// </summary>
    private static void Included(
        Workspace workspace, string from, string to, Dictionary<string, List<Protocol.TextEdit>> edits)
    {
        var moved = Paths.Directory(from) != Paths.Directory(to);
        var renamedInPlace = !moved && from != to;
        foreach (var analysis in workspace.Programs())
        {
            foreach (var model in analysis.Program.Files)
            {
                // A path is rewritten when the file containing it moved, so that it resolves
                // from the new folder, and when it names the file that moved.
                var inThisFile = model.Tree.Path == from;
                if (!inThisFile && !moved && !renamedInPlace)
                    continue;
                foreach (var (operand, written) in Includes(model))
                {
                    if (Paths.IsRooted(written))
                        continue;
                    var names = Paths.Beside(model.Tree.Path, written);
                    var target = names == from ? to : names;
                    var beside = inThisFile ? Paths.Directory(to) : Paths.Directory(model.Tree.Path);
                    var now = Relative(beside, target);
                    if (now == written)
                        continue;
                    var text = model.Tree.Text;
                    Add(edits, model.Tree.Path, text, operand.Span, Json.Quoted(now));
                }
            }
        }
    }

    /// <summary>Every <c>.incbin</c> in a file, as the operand that writes the path and the path.</summary>
    private static IEnumerable<(SyntaxNode Operand, string Written)> Includes(SemanticModel model)
    {
        foreach (var directive in model.Tree.Root.DescendantNodes().OfType<DataDirectiveSyntax>())
        {
            if (directive.Directive.Text.Equals(".incbin", StringComparison.OrdinalIgnoreCase)
                && directive.Tail is InlineDataSyntax { Values: [var operand, ..] }
                && model.ValueOf(operand) is { Kind: ValueKind.String, Text: { } written })
            {
                yield return (operand, written);
            }
        }
    }

    /// <summary>Adds one edit, naming the file by the URI the client knows it as.</summary>
    private static void Add(
        Dictionary<string, List<Protocol.TextEdit>> edits, string path, string text, TextSpan at, string written)
    {
        var uri = Lsp.ToUri(path);
        if (!edits.TryGetValue(uri, out var found))
            edits[uri] = found = [];
        found.Add(new Protocol.TextEdit(Range(text, at), written));
    }

    /// <summary>A span of a file as a protocol range: a line, and UTF-16 code-unit offsets within it.</summary>
    private static Protocol.Range Range(string text, TextSpan at)
    {
        var line = 0;
        var start = 0;
        for (var i = 0; i < at.Start; i++)
        {
            if (text[i] != '\n')
                continue;
            line++;
            start = i + 1;
        }
        return new Protocol.Range(
            new Protocol.Position(line, at.Start - start),
            new Protocol.Position(line, at.End - start));
    }

    /// <summary>The path of a file relative to a directory, with <c>/</c> separators.</summary>
    private static string Relative(string directory, string path) =>
        Paths.Normalized(Path.GetRelativePath(directory, path));

    /// <summary>A file's name without its folders, as a message shows it.</summary>
    private static string Shown(string path) => path[(path.LastIndexOf('/') + 1)..];
}
