using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

/// <summary>
/// Checks that a syntax tree has full fidelity: the tree's text is its source, and so is the text
/// of every line. The parser never drops a token, and a token it inserts where the source has none
/// has no text. So a line's child elements read back as that line, including comments,
/// whitespace, missing tokens and error nodes. Those elements are the <c>.export</c> that exports
/// what the line declares, its statement, the tokens the statement could not take, and the line
/// break that ends it.
/// </summary>
internal static class Fidelity
{
    /// <summary>Returns a description of each way <paramref name="tree"/> fails to reproduce its source text.</summary>
    public static IEnumerable<string> Problems(SyntaxTree tree)
    {
        if (tree.Root.ToFullString() != tree.Text)
            yield return "the tree does not give back the file's text";

        // A line the parser reads covers one line of the file, or several where an expression
        // continues, and together they cover every line of the file once.
        var lines = tree.Root.DescendantNodes().OfType<LineSyntax>().ToList();
        var covered = lines.Sum(line => line.LastLineIndex - line.LineIndex + 1);
        if (covered != tree.LineCount)
            yield return $"the tree's lines cover {covered} lines, and the file has {tree.LineCount}";

        foreach (var line in lines)
        {
            var text = line.ToFullString();
            var pieces = Pieces(line);
            if (pieces != text)
                yield return $"line {line.LineIndex + 1}: the line's pieces read back as {Quote(pieces)}, not {Quote(text)}";
        }
    }

    /// <summary>Returns the text of a line rebuilt from its child elements, in source order.</summary>
    private static string Pieces(LineSyntax line) =>
        (line.ExportKeyword?.ToFullString() ?? "")
        + line.Statement.ToFullString()
        + (line.SkippedTokens?.ToFullString() ?? "")
        + line.EndOfLineToken.ToFullString();

    private static string Quote(string text) => "\"" + text.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
}
