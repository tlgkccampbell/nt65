using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// The comment above a declaration, which is what an editor shows about it beyond what the
/// analysis worked out. There is no doc-comment syntax of its own: the <c>;</c> lines directly
/// above a declaration, each on a line of its own, are the comment, and a blank line or any
/// code between ends it. That is how assembly is commented already.
/// </summary>
internal static class DocComments
{
    /// <summary>
    /// The comment above what <paramref name="symbol"/> declares, or null when it has none.
    /// Every instance of a family is declared on the family's line, so each shows the
    /// family's comment.
    /// </summary>
    public static string? Of(Symbol symbol) => Above(symbol.Tree, symbol.NameSpan.Start);

    /// <summary>The comment on the lines directly above the one <paramref name="position"/> is on.</summary>
    public static string? Above(SyntaxTree tree, int position)
    {
        var lines = new List<string>();
        for (var i = tree.GetLineIndex(position) - 1; i >= 0; i--)
        {
            var written = Text(tree, i).Trim();
            if (!written.StartsWith(';'))
                break;
            lines.Add(Stripped(written));
        }
        lines.Reverse();

        // A rule of `;` characters over a declaration is a separator rather than a comment,
        // and so is a comment that says nothing at all.
        var text = string.Join("\n", lines).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>One line of the file, without its line break.</summary>
    private static string Text(SyntaxTree tree, int line)
    {
        var start = tree.LineStarts[line];
        var end = line + 1 < tree.LineStarts.Length ? tree.LineStarts[line + 1] : tree.Text.Length;
        return tree.Text[start..end].TrimEnd('\n', '\r');
    }

    /// <summary>
    /// A comment line as it is read: the <c>;</c> that marks it and the one space that
    /// usually follows come off, and whatever else the line was indented by stays, so a list
    /// or an example keeps its shape.
    /// </summary>
    private static string Stripped(string written)
    {
        var text = written.TrimStart(';');
        return text.StartsWith(' ') ? text[1..] : text;
    }
}
