using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Reads the comment above a declaration, which an editor shows in addition to what the analysis
/// worked out. The language has no doc-comment syntax of its own. The <c>;</c> lines directly
/// above a declaration, each on a line of its own, form the comment, and a blank line or any code
/// between them ends it. That is how assembly is already commented.
/// </summary>
internal static class DocComments
{
    /// <summary>
    /// Returns the comment above the declaration of <paramref name="symbol"/>, or null when it
    /// has none. Every instance of a <see cref="Family"/> (the declarations a repetition makes)
    /// is declared on the family's line, so each instance shows the family's comment.
    /// </summary>
    public static string? Of(Symbol symbol) => Above(symbol.Tree, symbol.NameSpan.Start);

    /// <summary>
    /// Returns the comment on the lines directly above the line <paramref name="position"/> is
    /// on, or null if there is none.
    /// </summary>
    public static string? Above(SyntaxTree tree, int position)
    {
        var lines = new List<string>();
        for (var i = tree.GetLineIndex(position) - 1; i >= 0; i--)
        {
            var line = Text(tree, i).Trim();
            if (!line.StartsWith(';'))
                break;
            lines.Add(Stripped(line));
        }
        lines.Reverse();

        // A separator line made only of `;` characters strips to nothing, as does an empty
        // comment, so a declaration with only those above it has no doc comment.
        var text = string.Join("\n", lines).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>Returns one line of the file, without its line break.</summary>
    private static string Text(SyntaxTree tree, int line)
    {
        return tree.Text[tree.LineStarts[line]..tree.GetLineEnd(line)].TrimEnd('\n', '\r');
    }

    /// <summary>
    /// Returns the text of a comment line. The <c>;</c> that marks it and the single space that
    /// usually follows are removed, and any further indentation is kept, so that a list or an
    /// example keeps its shape.
    /// </summary>
    private static string Stripped(string line)
    {
        var text = line.TrimStart(';');
        return text.StartsWith(' ') ? text[1..] : text;
    }
}
