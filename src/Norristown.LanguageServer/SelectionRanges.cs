using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// The ranges a selection steps through as it is expanded from the caret, and back through as
/// it is shrunk. They come from the syntax tree alone: the operand under the caret, the instruction
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
            // A node whose span is no larger than the node below it would be a step that selects
            // nothing new, so it is left out.
            if (spans.Count == 0 || node.Span.Length > spans[^1].Length)
                spans.Add(node.Span);
        }

        // The whole file is the last step, since the chain of nodes stops at the line.
        var whole = new TextSpan(0, tree.Text.Length);
        if (spans.Count == 0 || spans[^1].Length < whole.Length)
            spans.Add(whole);

        Protocol.SelectionRange? built = null;
        for (var i = spans.Count - 1; i >= 0; i--)
            built = new Protocol.SelectionRange(Lsp.ToRange(tree, spans[i]), built);
        return built;
    }

    /// <summary>
    /// The smallest node containing <paramref name="position"/>, searching down from the line
    /// it is on. A caret in the whitespace between two child nodes stops at their parent, the
    /// deepest node whose span contains it.
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
