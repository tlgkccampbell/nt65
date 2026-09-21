using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// What a caret grows to take in as the selection is widened, and shrinks back to as it is
/// narrowed. It is the tree and nothing else: the operand under the caret, the instruction
/// written around it, the block that holds the line, the routine that holds the block, and the
/// file. A reader widening a selection is walking the shape of the program, and the shape of
/// the program is what the tree already is.
/// </summary>
internal static class SelectionRanges
{
    /// <summary>The chain at <paramref name="position"/>, innermost first, or null past the end of the file.</summary>
    public static Protocol.SelectionRange? At(SyntaxTree tree, int position)
    {
        if (position > tree.Text.Length)
            return null;
        var spans = new List<TextSpan>();
        foreach (var node in Innermost(tree, position).AncestorsAndSelf())
        {
            // A node that adds nothing to what the one under it selected is a step nobody
            // would notice taking, so it is left out.
            if (spans.Count == 0 || node.Span.Length > spans[^1].Length)
                spans.Add(node.Span);
        }

        // The file itself is the last step, for a node chain that stops at the line.
        var whole = new TextSpan(0, tree.Text.Length);
        if (spans.Count == 0 || spans[^1].Length < whole.Length)
            spans.Add(whole);

        Protocol.SelectionRange? built = null;
        for (var i = spans.Count - 1; i >= 0; i--)
            built = new Protocol.SelectionRange(Lsp.ToRange(tree, spans[i]), built);
        return built;
    }

    /// <summary>
    /// The smallest node holding <paramref name="position"/>, from the line it is on down. A
    /// caret in the whitespace between two nodes belongs to the one holding it, which is
    /// whatever the line's own node is at that depth.
    /// </summary>
    private static SyntaxNode Innermost(SyntaxTree tree, int position)
    {
        SyntaxNode node = tree.GetLine(tree.GetLineIndex(Math.Min(position, Math.Max(tree.Text.Length - 1, 0))));
        while (true)
        {
            var inside = node.ChildNodes
                .FirstOrDefault(child => child.Span.Start <= position && position < child.Span.End);
            if (inside is null)
                return node;
            node = inside;
        }
    }
}
