using Norristown.Project;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// What moving a file asks the program to change. A module's name is written in its
/// <c>.module</c> line and its output is named after that wherever the source is, so a source
/// that moves changes less than it looks: nothing in any other file names it.
/// <para>
/// Two things do. A <c>files</c> entry that names the file literally has to name it where it
/// now is; one that is a glob either still matches, in which case there is nothing to do, or
/// stops matching, which is not something to rewrite on the programmer's behalf — which glob
/// they meant to widen is theirs to say — and is said instead. And an <c>.incbin</c> path is
/// resolved beside the file that writes it, so it moves when either end does.
/// </para>
/// </summary>
internal static class MovedFiles
{
    /// <summary>
    /// The edits <paramref name="renames"/> call for, and what the programmer is to be told
    /// about the ones nothing can be written for.
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
                        said.Add($"nt65: `{glob}` in {Shown(project.File)} no longer names "
                            + $"{Shown(to)}. Which glob should cover it is yours to say, so nothing was changed.");
                    }
                    continue;
                }
                if (Json.Entry(text, "files", glob) is { } at)
                    Add(edits, project.File, text, at, Json.Quoted(Relative(project.Root, to)));
            }
        }
    }

    /// <summary>
    /// The <c>.incbin</c> paths a move changes: the ones written in a file that moved to another
    /// folder, which are resolved beside it, and the ones naming a binary that moved.
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
                // A path is rewritten where the file writing it moved, so that it is resolved
                // from the new folder, and where it names the file that moved.
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

    /// <summary>A span of a file as the protocol names it: a line and a count of UTF-16 units into it.</summary>
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

    /// <summary>A file as it is reached from a directory, with <c>/</c> separators.</summary>
    private static string Relative(string directory, string path) =>
        Paths.Normalized(Path.GetRelativePath(directory, path));

    /// <summary>A file as a message names it: its own name, without the folders above it.</summary>
    private static string Shown(string path) => path[(path.LastIndexOf('/') + 1)..];
}
