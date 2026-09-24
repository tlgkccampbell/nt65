namespace Norristown.Syntax;

/// <summary>
/// Computes the ranges of lines an editor can fold, one per block, from the block's opening line
/// to its last line. The block layer already recovers from unbalanced braces, so a half-typed file
/// still folds. A block left open by a missing <c>}</c> runs to the point where the block layer
/// closed it.
/// </summary>
public static class Folding
{
    /// <summary>Returns every foldable range in <paramref name="tree"/>, outermost first.</summary>
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
            if (child is not BlockSyntax block)
                continue;

            // A block on one line has nothing to hide.
            var start = block.LineIndex;
            var end = block.Tree.GetLineIndex(block.FullSpan.End - 1);
            if (end > start)
                ranges.Add(new LineRange(start, end));
            Walk(block, ranges);
        }
    }
}
