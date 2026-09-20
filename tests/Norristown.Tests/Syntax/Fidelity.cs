using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

/// <summary>
/// The full-fidelity check: a tree's text is its source, and so is every line's. The parser never
/// drops a token and never invents one, so the pieces a line is written in — the <c>.export</c>
/// that exports what it declares, its statement, whatever the statement could not take, and the
/// line break that ends it — read back as that line, comments, whitespace, error nodes and all.
/// </summary>
internal static class Fidelity
{
    public static IEnumerable<string> Problems(SyntaxTree tree)
    {
        if (tree.Root.ToFullString() != tree.Text)
            yield return "the tree does not give back the file's text";

        var lines = tree.Root.DescendantNodes().OfType<LineSyntax>().ToList();
        if (lines.Count != tree.LineCount)
            yield return $"the tree holds {lines.Count} lines, and the file has {tree.LineCount}";

        foreach (var line in lines)
        {
            var written = line.ToFullString();
            var pieces = Pieces(line);
            if (pieces != written)
                yield return $"line {line.LineIndex + 1}: the line's pieces read back as {Quote(pieces)}, not {Quote(written)}";
        }
    }

    /// <summary>A line as the pieces it is written in spell it, in the order they are written.</summary>
    private static string Pieces(LineSyntax line) =>
        (line.ExportKeyword?.ToFullString() ?? "")
        + line.Statement.ToFullString()
        + (line.SkippedTokens?.ToFullString() ?? "")
        + line.EndOfLineToken.ToFullString();

    private static string Quote(string text) => "\"" + text.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
}
