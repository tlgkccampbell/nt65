using Norristown.Syntax;

namespace Norristown.Tests;

/// <summary>
/// Builds variants of a source as it looks while it is being typed. The variants are the source
/// as it stands, and the source with every line cut short after its first few tokens or before
/// its last. Half a line is what an editor asks about most of the time, so these variants are
/// the input to every test that has to hold on a file nobody has finished typing.
/// </summary>
internal static class BrokenLines
{
    /// <summary>
    /// Returns <paramref name="text"/> followed by one variant per way of cutting its lines. The
    /// first cut keeps the first token of every line, the next keeps the first two, and so on up
    /// to five. The last two cuts keep all but the last token and all but the last two.
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
    /// Returns the line's first <paramref name="keep"/> tokens or, when <paramref name="keep"/>
    /// is negative, all but its last -<paramref name="keep"/>, always keeping at least one. A line
    /// that ends by opening a block keeps its brace, so the blocks are unchanged and the lines
    /// inside them are still parsed as part of the block's body.
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
