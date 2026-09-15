using System.Text;
using Norristown.Syntax;

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
                if (child.Green is not GreenBlock block)
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

    /// <summary>Everything: every token with its trivia and error, line kinds, blocks and diagnostics.</summary>
    public static string Full(SyntaxTree tree)
    {
        var builder = new StringBuilder();
        foreach (var line in tree.Lines)
        {
            builder.Append($"{line.LineKind} {line.BraceValue} {line.OpensBlockKind}:");
            foreach (var t in line.Tokens)
            {
                builder.Append($" [{string.Concat(t.LeadingTrivia.Select(x => x.Kind + Escape(x.Text)))}");
                builder.Append($"{t.Kind}{Escape(t.Text)}{string.Concat(t.TrailingTrivia.Select(x => x.Kind + Escape(x.Text)))}");
                builder.Append(t.Error is null ? "]" : $" !{t.Error}]");
            }
            builder.Append('\n');
        }
        builder.Append(Blocks(tree));
        foreach (var d in tree.Diagnostics)
            builder.Append($"{d.Span.Line}:{d.Span.StartColumn}-{d.Span.EndColumn} {d.Message}\n");
        return builder.ToString();
    }

    private static string Escape(string text) => "\"" + text.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
}

/// <summary>The nt65 code blocks of DESIGN.md (fences tagged <c>nt65</c>).</summary>
internal static class DesignCorpus
{
    public sealed record CodeBlock(int Line, string Text)
    {
        public override string ToString() => $"DESIGN.md:{Line}";
    }

    public static readonly IReadOnlyList<CodeBlock> Blocks = Load();

    /// <summary>Opening fences with no language tag, which the corpus would silently skip.</summary>
    public static readonly IReadOnlyList<int> UntaggedFences = FindUntagged();

    private static string[] ReadLines() => Repo.ReadText(Repo.Path("DESIGN.md")).ReplaceLineEndings("\n").Split('\n');

    private static List<CodeBlock> Load()
    {
        var lines = ReadLines();
        var blocks = new List<CodeBlock>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i] != "```nt65")
                continue;
            var start = i + 1;
            var body = new StringBuilder();
            for (i++; lines[i] != "```"; i++)
            {
                // `...` stands for elided code in the design's examples.
                body.Append(lines[i].Trim() == "..." ? "" : lines[i]).Append('\n');
            }
            blocks.Add(new CodeBlock(start + 1, body.ToString()));
        }
        return blocks;
    }

    private static List<int> FindUntagged()
    {
        var lines = ReadLines();
        var untagged = new List<int>();
        var inside = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("```", StringComparison.Ordinal))
                continue;
            if (!inside && lines[i] == "```")
                untagged.Add(i + 1);
            inside = !inside;
        }
        return untagged;
    }
}
