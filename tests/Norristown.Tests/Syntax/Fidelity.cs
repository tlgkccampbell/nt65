using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

/// <summary>
/// The full-fidelity check: a tree's text is its source, and so is every statement's. The
/// parser never drops a token and never invents one, so each line's statement reads back as
/// that line, comments, whitespace, error nodes and all.
/// </summary>
internal static class Fidelity
{
    public static IEnumerable<string> Problems(SyntaxTree tree)
    {
        if (tree.Root.ToFullString() != tree.Text)
            yield return "the tree does not give back the file's text";

        for (var i = 0; i < tree.Lines.Length; i++)
        {
            var line = tree.Lines[i].ToFullString();
            var statement = tree.Statement(i).ToFullString();
            if (statement != line)
                yield return $"line {i + 1}: the statement reads back as {Quote(statement)}, not {Quote(line)}";
        }
    }

    private static string Quote(string text) => "\"" + text.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
}
