using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Reads the lines of a syntax tree without their line breaks. A line of the tree runs up to
/// where the next line starts, so its break is part of it, and an answer about the line's text
/// or its width does not want the break.
/// </summary>
internal static class Lines
{
    /// <summary>
    /// Returns where the 0-based line <paramref name="line"/> ends, before its line break.
    /// </summary>
    public static int EndOf(SyntaxTree tree, int line)
    {
        var end = tree.GetLineEnd(line);
        while (end > tree.LineStarts[line] && tree.Text[end - 1] is '\r' or '\n')
            end--;
        return end;
    }

    /// <summary>Returns the text of the 0-based line <paramref name="line"/>, without its line break.</summary>
    public static string TextOf(SyntaxTree tree, int line) => tree.Text[tree.LineStarts[line]..EndOf(tree, line)];
}
