using System.Text;
using Norristown.Syntax;
using GreenLine = Norristown.Syntax.InternalSyntax.GreenLine;
using GreenNode = Norristown.Syntax.InternalSyntax.GreenNode;
using GreenToken = Norristown.Syntax.InternalSyntax.GreenToken;

namespace Norristown.Tests.Syntax;

/// <summary>Readable renderings of syntax, for assertions and failure messages.</summary>
internal static class SyntaxDump
{
    /// <summary>Token kinds and texts, without trivia or the end-of-line token: <c>Mnemonic:lda Hash:# NumberLiteral:1</c>.</summary>
    public static string Tokens(GreenLine line) =>
        string.Join(" ", line.Tokens.Where(t => t.Kind != SyntaxKind.EndOfLine).Select(t => $"{t.Kind}:{t.Text}"));

    /// <summary>
    /// The block structure, one block per row, indented by depth: the block kind, its first
    /// and last line (1-based), and <c>no closer</c> when it ends without a <c>}</c> line.
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
    /// The statement trees, one node or token per row, indented by depth:
    /// <c>InstructionStatement</c>, then <c>Mnemonic "lda"</c> for each token, and a row for the
    /// node a list slot holds. The pieces the line holds rather than the statement — the
    /// <c>.export</c> before a declaration and whatever the statement could not take — are written
    /// with it; line breaks are left out, since every line ends with one.
    /// <para>
    /// It walks the slots of the nodes the parse handed back, not the red tree over them. That is
    /// what the trees being compared are made of — a reused statement is the same green node, and a
    /// list slot is a node of its own — and it costs no red node for a dump that runs a few
    /// thousand times over a whole corpus.
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
                // A slot holding nothing is a piece that was not written, and writes nothing.
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
    /// A statement on one line, as <c>Kind(child child)</c> with tokens written as their
    /// text: <c>InstructionStatement(lda ImmediateOperand(# NumberExpression($10)))</c>.
    /// </summary>
    public static string Shape(SyntaxNode node)
    {
        var parts = Children(node, Shape, token => token.Kind == SyntaxKind.EndOfLine ? "" : token.Text).Where(p => p.Length > 0);
        return $"{node.Kind}({string.Join(' ', parts)})";
    }

    /// <summary>
    /// An expression with every binding made visible: <c>1 &lt;&lt; i + 1</c> renders as
    /// <c>(1 &lt;&lt; (i + 1))</c>, and what the source itself parenthesized as <c>[...]</c>.
    /// </summary>
    public static string Infix(SyntaxNode node) => node switch
    {
        BinaryExpressionSyntax b => $"({Infix(b.Left)} {b.OperatorToken.Text} {Infix(b.Right)})",
        UnaryExpressionSyntax u => $"({u.OperatorToken.Text}{Infix(u.Operand)})",
        ParenthesizedExpressionSyntax p => $"[{Infix(p.Expression)}]",
        _ => string.Concat(Children(node, Infix, token => token.Text)),
    };

    /// <summary>
    /// The outline, one item per row, indented by depth: the kind, the name, the 1-based
    /// first and last line it covers, and its detail in brackets.
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

    /// <summary>The foldable ranges, one per row, as 1-based line numbers.</summary>
    public static string FoldingRanges(SyntaxTree tree) =>
        string.Concat(Folding.Build(tree).Select(r => $"{r.StartLine + 1}-{r.EndLine + 1}\n"));

    /// <summary>
    /// Everything: every token with its trivia and what it reports, line kinds, blocks, statements
    /// and diagnostics. Two trees of the same text dump the same, however each of them was built,
    /// which is what the incremental tests compare.
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

                // Nearly every token says nothing, and asking one that does not costs a list.
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
