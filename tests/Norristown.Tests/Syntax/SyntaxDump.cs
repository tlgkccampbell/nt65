using System.Text;
using Norristown.Syntax;
using GreenLine = Norristown.Syntax.InternalSyntax.GreenLine;
using GreenNode = Norristown.Syntax.InternalSyntax.GreenNode;
using GreenToken = Norristown.Syntax.InternalSyntax.GreenToken;

namespace Norristown.Tests.Syntax;

/// <summary>Provides readable renderings of syntax, for assertions and failure messages.</summary>
internal static class SyntaxDump
{
    /// <summary>
    /// Renders a line's token kinds and texts, without trivia or the end-of-line token, as in
    /// <c>Mnemonic:lda Hash:# NumberLiteral:1</c>.
    /// </summary>
    public static string Tokens(GreenLine line) =>
        string.Join(" ", line.Tokens.Where(t => t.Kind != SyntaxKind.EndOfLine).Select(t => $"{t.Kind}:{t.Text}"));

    /// <summary>
    /// Renders the block structure, one block per row, indented by depth. Each row gives the block
    /// kind and its first and last line (1-based). It ends with <c>no closer</c> when the block
    /// ends without a <c>}</c> line.
    /// </summary>
    public static string Blocks(SyntaxTree tree)
    {
        var builder = new StringBuilder();
        void Walk(SyntaxNode node, int depth)
        {
            foreach (var child in node.ChildNodes)
            {
                if (child is not BlockSyntax block)
                    continue;
                var first = child.LineIndex + 1;
                var last = tree.GetLineIndex(child.FullSpan.End - 1) + 1;
                builder.Append(' ', depth * 2).Append($"{block.BlockKind} {first}-{last}");
                builder.Append(block.HasCloser ? "\n" : " no closer\n");
                Walk(child, depth + 1);
            }
        }
        Walk(tree.Root, 0);
        return builder.ToString();
    }

    /// <summary>
    /// Renders the statement trees, one node or token per row, indented by depth. A node row
    /// gives its kind, such as <c>InstructionStatement</c>, and a token row gives its kind and
    /// text, such as <c>Mnemonic "lda"</c>. The node a list slot holds gets a row of its own. The
    /// child elements that belong to the line rather than the statement are rendered with it:
    /// the <c>.export</c> before a declaration and the tokens the statement could not take. Line
    /// breaks are left out, since every line ends with one.
    /// <para>
    /// The dump walks the slots of the green nodes the parse returned, not the red tree over them.
    /// Those green nodes are what the compared trees are made of: a reused statement is the same
    /// green node, and a list slot is a node of its own. Walking them also creates no red nodes,
    /// which matters for a dump that runs a few thousand times over a whole corpus.
    /// </para>
    /// </summary>
    public static string Statements(SyntaxTree tree)
    {
        var builder = new StringBuilder();
        void Walk(GreenNode node, int depth)
        {
            builder.Append(' ', depth * 2).Append(node.Kind);
            if (node is GreenToken token)
                builder.Append(' ').Append(Escape(token.Text));
            builder.Append('\n');
            for (var i = 0; i < node.SlotCount; i++)
            {
                // An empty slot is a child element absent from the source, and renders nothing.
                if (node.GetSlot(i) is { } slot)
                    Walk(slot, depth + 1);
            }
        }
        for (var i = 0; i < tree.LineCount; i++)
        {
            var parsed = tree.Parsed(i);
            if (parsed.ExportKeyword is { } export)
                Walk(export, 0);
            Walk(parsed.Node, 0);
            if (parsed.SkippedTokens is { } skipped)
                Walk(skipped, 0);
        }
        return builder.ToString();
    }

    /// <summary>
    /// Renders a statement on one line as <c>Kind(child child)</c>, with tokens rendered as their
    /// text, as in <c>InstructionStatement(lda ImmediateOperand(# NumberExpression($10)))</c>.
    /// </summary>
    public static string Shape(SyntaxNode node)
    {
        var parts = Children(node, Shape, token => token.Kind == SyntaxKind.EndOfLine ? "" : token.Text).Where(p => p.Length > 0);
        return $"{node.Kind}({string.Join(' ', parts)})";
    }

    /// <summary>
    /// Renders an expression with every binding made visible. For example, <c>1 &lt;&lt; i + 1</c>
    /// renders as <c>(1 &lt;&lt; (i + 1))</c>. Parentheses that the source itself contains render
    /// as <c>[...]</c>.
    /// </summary>
    public static string Infix(SyntaxNode node) => node switch
    {
        BinaryExpressionSyntax b => $"({Infix(b.Left)} {b.OperatorToken.Text} {Infix(b.Right)})",
        UnaryExpressionSyntax u => $"({u.OperatorToken.Text}{Infix(u.Operand)})",
        ParenthesizedExpressionSyntax p => $"[{Infix(p.Expression)}]",
        _ => string.Concat(Children(node, Infix, token => token.Text)),
    };

    /// <summary>
    /// Renders the outline, one item per row, indented by depth. Each row gives the item's kind,
    /// its name, the 1-based first and last line it covers, and its detail in brackets.
    /// </summary>
    public static string Symbols(SyntaxTree tree)
    {
        var builder = new StringBuilder();
        void Walk(IReadOnlyList<OutlineItem> items, int depth)
        {
            foreach (var item in items)
            {
                var first = tree.GetLineIndex(item.Span.Start) + 1;
                var last = tree.GetLineIndex(Math.Max(item.Span.Start, item.Span.End - 1)) + 1;
                builder.Append(' ', depth * 2).Append($"{item.Kind} {item.Name} {first}-{last}");
                if (item.Detail is { } detail)
                    builder.Append($" [{detail}]");
                builder.Append('\n');
                Walk(item.Children, depth + 1);
            }
        }
        Walk(Outline.Build(tree), 0);
        return builder.ToString();
    }

    /// <summary>Renders the foldable ranges, one per row, as 1-based line numbers.</summary>
    public static string FoldingRanges(SyntaxTree tree) =>
        string.Concat(Folding.Build(tree).Select(r => $"{r.StartLine + 1}-{r.EndLine + 1}\n"));

    /// <summary>
    /// Renders everything about a tree: every token with its trivia and diagnostics, the line
    /// kinds, the blocks, the statements and the tree's diagnostics. Two trees of the same text
    /// produce the same dump no matter how each was built. The incremental tests compare these
    /// dumps.
    /// </summary>
    public static string Full(SyntaxTree tree)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < tree.LineCount; i++)
        {
            var line = tree.GetLine(i);
            builder.Append($"{line.LineKind} {line.OpensBlockKind}:");
            foreach (var t in line.Tokens)
            {
                builder.Append($" [{string.Concat(t.LeadingTrivia.Select(x => x.Kind + Escape(x.Text)))}");
                builder.Append($"{t.Kind}{Escape(t.Text)}{string.Concat(t.TrailingTrivia.Select(x => x.Kind + Escape(x.Text)))}");

                // Nearly every token has no diagnostics, and asking one that has none would
                // allocate a list.
                if (t.ContainsDiagnostics)
                {
                    builder.Append(string.Concat(t.GetDiagnostics().Select(
                        d => $" !{d.Span.StartColumn}-{d.Span.EndColumn} {d.Message}")));
                }
                builder.Append(']');
            }
            builder.Append('\n');
        }
        builder.Append(Blocks(tree));
        builder.Append(Statements(tree));
        foreach (var d in tree.Diagnostics)
            builder.Append($"{d.Span.Line}:{d.Span.StartColumn}-{d.Span.EndColumn} {d.Message}\n");
        return builder.ToString();
    }

    /// <summary>A node's child nodes and tokens rendered in source order.</summary>
    private static IEnumerable<string> Children(SyntaxNode node, Func<SyntaxNode, string> ofNode, Func<SyntaxToken, string> ofToken) =>
        node.ChildNodesAndTokens().Select(child => child.AsNode() is { } inner ? ofNode(inner) : ofToken(child.AsToken()));

    private static string Escape(string text) => "\"" + text.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
}
