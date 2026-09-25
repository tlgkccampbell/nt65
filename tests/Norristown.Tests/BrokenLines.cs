using System.Collections.Concurrent;
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
    /// The variants of each file already built. Several sweeps cut the same sources, and cutting
    /// parses every line of every source, so each file's variants are built once per run.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<string>>> built = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the variants of the file at <paramref name="path"/>, as <see cref="Variants"/>
    /// builds them from its text, building them only the first time they are asked for.
    /// </summary>
    public static IReadOnlyList<string> Of(string path) =>
        built.GetOrAdd(path, key => new Lazy<IReadOnlyList<string>>(() => Variants(Repo.ReadText(key)))).Value;

    /// <summary>
    /// Returns <paramref name="text"/> followed by one variant per way of cutting its lines. The
    /// first cut keeps the first token of every line, the next keeps the first two, and so on up
    /// to five. The last two cuts keep all but the last token and all but the last two.
    /// </summary>
    public static IReadOnlyList<string> Variants(string text)
    {
        var lines = text.Split('\n').Select(line => (Text: line, Ends: TokenEnds(line))).ToList();
        string Cut(int keep) => string.Join('\n', lines.Select(line => CutLine(line.Text, line.Ends, keep)));
        return [text, Cut(1), Cut(2), Cut(3), Cut(4), Cut(5), Cut(-1), Cut(-2)];
    }

    /// <summary>
    /// Returns where each token on the line ends, leaving out the end of the line, and whether
    /// the last token opens a block.
    /// </summary>
    private static (int[] Ends, bool Opens) TokenEnds(string line)
    {
        var tokens = SyntaxTree.Parse("cut", line.TrimEnd('\r')).Root.DescendantNodes()
            .OfType<LineSyntax>().First().ChildTokens.Where(token => token.Kind != SyntaxKind.EndOfLine).ToList();
        return ([.. tokens.Select(token => token.Span.End)], tokens.Count > 0 && tokens[^1].Kind == SyntaxKind.OpenBrace);
    }

    /// <summary>
    /// Returns the line's first <paramref name="keep"/> tokens or, when <paramref name="keep"/>
    /// is negative, all but its last -<paramref name="keep"/>, always keeping at least one. A line
    /// that ends by opening a block keeps its brace, so the blocks are unchanged and the lines
    /// inside them are still parsed as part of the block's body.
    /// </summary>
    private static string CutLine(string line, (int[] Ends, bool Opens) tokens, int keep)
    {
        var ends = tokens.Ends;
        if (ends.Length == 0)
            return line;
        var count = keep > 0 ? Math.Min(keep, ends.Length) : Math.Max(ends.Length + keep, 1);
        var cut = line[..ends[count - 1]];
        return count < ends.Length && tokens.Opens ? cut + " {" : cut;
    }
}
