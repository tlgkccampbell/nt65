namespace Norristown.Syntax;

/// <summary>
/// The ranges of lines an editor can fold: one per block, from its opener line to its last
/// line. The block layer already recovers from unbalanced braces (§4), so a half-typed file
/// still folds — a block left open by a missing <c>}</c> simply runs to where it was closed
/// for it.
/// </summary>
public static class Folding
{
    /// <summary>Every foldable range in <paramref name="tree"/>, outermost first.</summary>
    public static IReadOnlyList<LineRange> Build(SyntaxTree tree)
    {
        var ranges = new List<LineRange>();
        Walk(tree.Root, ranges);
        return ranges;
    }

    private static void Walk(SyntaxNode node, List<LineRange> ranges)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.Green is not GreenBlock)
                continue;

            // A block on one line has nothing to hide.
            var start = child.LineIndex;
            var end = child.Tree.GetLineIndex(child.FullSpan.End - 1);
            if (end > start)
                ranges.Add(new LineRange(start, end));
            Walk(child, ranges);
        }
    }
}
