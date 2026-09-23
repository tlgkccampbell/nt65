using Norristown.Syntax;

namespace Norristown.Tests;

/// <summary>
/// A source as it looks while it is being written: as it stands, and with every line cut short
/// after its first few tokens or before its last. Half a line is what an editor asks about most
/// of the time, so these variants are the input to every test that has to hold on a file nobody
/// has finished typing.
/// </summary>
internal static class BrokenLines
{
    /// <summary>
    /// <paramref name="text"/> and one variant per way of cutting its lines: the first token of
    /// every line, then the first two, and so on up to five, then all but the last token, then
    /// all but the last two.
    /// </summary>
    public static IEnumerable<string> Variants(string text)
    {
        yield return text;
        var lines = text.Split('\n');
        for (var keep = 1; keep <= 5; keep++)
            yield return string.Join('\n', lines.Select(line => Cut(line, keep)));
        yield return string.Join('\n', lines.Select(line => Cut(line, -1)));
        yield return string.Join('\n', lines.Select(line => Cut(line, -2)));
    }

    /// <summary>
    /// The line's first <paramref name="keep"/> tokens or, when <paramref name="keep"/> is
    /// negative, all but its last -<paramref name="keep"/> (always at least one). A line that ends
    /// by opening a block keeps its brace, so the blocks are unchanged and the lines inside them
    /// are still parsed as part of the block's body.
    /// </summary>
    private static string Cut(string line, int keep)
    {
        var tokens = SyntaxTree.Parse("cut", line.TrimEnd('\r')).Root.DescendantNodes()
            .OfType<LineSyntax>().First().ChildTokens.Where(token => token.Kind != SyntaxKind.EndOfLine).ToList();
        if (tokens.Count == 0)
            return line;
        var count = keep > 0 ? Math.Min(keep, tokens.Count) : Math.Max(tokens.Count + keep, 1);
        var cut = line[..tokens[count - 1].Span.End];
        return count < tokens.Count && tokens[^1].Kind == SyntaxKind.OpenBrace ? cut + " {" : cut;
    }
}
