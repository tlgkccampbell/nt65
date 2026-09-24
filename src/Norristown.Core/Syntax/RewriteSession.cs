using System.Text;

namespace Norristown.Syntax;

/// <summary>
/// Represents one run of a <see cref="SyntaxRewriter"/> over a file, a block or a line. The run
/// collects the text changes the rewrite makes on each line, in source order and never
/// overlapping, with one change per child of a line that came back different. It then applies
/// them as one change, parses the file again, and carries the rewrite's annotations across that
/// reparse with an <see cref="AnnotationCarrier"/>.
/// <para>
/// A rewrite that reaches another file, block or line while a run is going adds that node's
/// changes to the same run through <see cref="Collect"/>, so that only one new tree is built, no
/// matter how deep the walk went.
/// </para>
/// </summary>
/// <param name="rewriter">The rewrite whose methods decide what each node and token becomes.</param>
internal sealed class RewriteSession(SyntaxRewriter rewriter)
{
    private readonly List<TextChange> changes = [];
    private readonly AnnotationCarrier annotations = new();

    /// <summary>
    /// Rewrites <paramref name="node"/>, which is a file, a block or a line, and returns the node
    /// of the new tree that corresponds to it.
    /// </summary>
    /// <param name="node">The node the run starts from.</param>
    /// <returns>The node in the new tree, or <paramref name="node"/> if nothing changed.</returns>
    public SyntaxNode Run(SyntaxNode node)
    {
        Collect(node);
        if (changes.Count == 0)
            return node;

        // A single change spans from the first changed node or token to the last, so that the
        // file is parsed once no matter how many the rewrite touched, and the lines outside
        // that range keep the green nodes they have.
        var offsets = new int[changes.Count];
        var joined = Joined(node.Tree.Text, changes, offsets);
        annotations.CollectBetween(node.Tree, changes, offsets);
        var tree = node.Tree.WithChange(joined);
        return Corresponding(node, annotations.Reattach(tree, offsets));
    }

    /// <summary>
    /// Adds the changes a rewrite of <paramref name="node"/> makes to this run, in order.
    /// </summary>
    /// <param name="node">A file, a block or a line.</param>
    public void Collect(SyntaxNode node)
    {
        if (node is LineSyntax own)
        {
            CollectLine(own);
            return;
        }
        foreach (var child in node.ChildNodes)
        {
            // Each line is visited as a whole first, so that a rewrite can replace or remove it;
            // if it comes back unchanged, the changes collected from its children stand.
            if (child is not LineSyntax line)
            {
                Collect(child);
                continue;
            }
            var before = changes.Count;
            var marked = annotations.Count;
            var rewritten = rewriter.Visit(line);
            if (ReferenceEquals(rewritten, line))
                continue;

            // The whole line is being replaced, so any changes collected from inside it are
            // discarded. One change covers the line, and no change may fall inside a span that is
            // already replaced.
            changes.RemoveRange(before, changes.Count - before);
            annotations.Truncate(marked);
            Changed(line, rewritten);
        }
    }

    /// <summary>
    /// Returns a single change equivalent to all of <paramref name="changes"/>. It spans from the
    /// start of the first change to the end of the last, keeping the original text between them
    /// where nothing changed. The method fills <paramref name="offsets"/> with the offset in the
    /// file where each change's own text lands.
    /// </summary>
    private static TextChange Joined(string text, List<TextChange> changes, int[] offsets)
    {
        var start = changes[0].Start;
        var end = changes[^1].Start + changes[^1].Length;
        var built = new StringBuilder(end - start);
        var at = start;
        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            built.Append(text, at, change.Start - at);
            offsets[i] = start + built.Length;
            built.Append(change.NewText);
            at = change.Start + change.Length;
        }
        return new TextChange(start, end - start, built.Append(text, at, end - at).ToString());
    }

    /// <summary>
    /// Returns the node of <paramref name="tree"/> that corresponds to <paramref name="node"/>
    /// after the rewrite.
    /// </summary>
    private static SyntaxNode Corresponding(SyntaxNode node, SyntaxTree tree) => node switch
    {
        LineSyntax line => tree.GetLine(Math.Min(line.LineIndex, tree.LineCount - 1)),
        BlockSyntax block => tree.Root.DescendantNodes().OfType<BlockSyntax>()
            .FirstOrDefault(other => other.LineIndex == block.LineIndex) ?? (SyntaxNode)tree.Root,
        _ => tree.Root,
    };

    /// <summary>
    /// Collects the changes a rewrite makes on one line, covering its <c>.export</c>, its
    /// statement, its skipped tokens and its line break. A line with any change is parsed again
    /// whole. So a statement or skipped tokens that have an annotation but were not themselves
    /// rewritten are emitted again unchanged, so that their annotations are found where they land.
    /// </summary>
    private void CollectLine(LineSyntax line)
    {
        // Each child is visited in source order, before the file's text is rewritten. A rewrite
        // that counts tokens or passes state from one token to the next relies on that order.
        if (line.ExportKeyword is { } exported)
            Changed(exported, rewriter.VisitToken(exported));
        var statement = rewriter.Visit(line.Statement);
        var left = line.SkippedTokens is { } skipped ? rewriter.Visit(skipped) : null;
        var moved = !ReferenceEquals(statement?.Green, line.Statement.Green)
            || (line.SkippedTokens is { } before && !ReferenceEquals(left?.Green, before.Green));
        Kept(line.Statement, statement, moved);
        if (line.SkippedTokens is { } rest)
            Kept(rest, left, moved);
        Changed(line.EndOfLineToken, rewriter.VisitToken(line.EndOfLineToken));
    }

    /// <summary>
    /// Records a change for a child of a line that the rewrite returned as
    /// <paramref name="rewritten"/>. Suppose the child is unchanged, but <paramref name="moved"/>
    /// shows the line will be parsed again, and the child has an annotation. Then a change that
    /// reproduces it unchanged is recorded, so that its annotations are tracked.
    /// </summary>
    private void Kept(SyntaxNode original, SyntaxNode? rewritten, bool moved)
    {
        if (moved && rewritten is not null && ReferenceEquals(original.Green, rewritten.Green) && original.ContainsAnnotations)
        {
            annotations.Collect(original.Green, changes.Count);
            changes.Add(new TextChange(original.FullSpan.Start, original.FullSpan.Length, original.ToFullString()));
            return;
        }
        Changed(original, rewritten);
    }

    /// <summary>
    /// Records a change for a token of a line that the rewrite returned as
    /// <paramref name="rewritten"/>, unless it came back unchanged.
    /// </summary>
    private void Changed(SyntaxToken original, SyntaxToken rewritten)
    {
        if (ReferenceEquals(original.Green, rewritten.Green))
            return;
        annotations.Collect(rewritten.Green, changes.Count);
        changes.Add(new TextChange(original.FullSpan.Start, original.FullSpan.Length, rewritten.ToFullString()));
    }

    /// <summary>
    /// Records a change for a node that the rewrite returned as <paramref name="rewritten"/>,
    /// unless it came back unchanged. A null <paramref name="rewritten"/> removes the node's text.
    /// </summary>
    private void Changed(SyntaxNode original, SyntaxNode? rewritten)
    {
        if (rewritten is not null && ReferenceEquals(original.Green, rewritten.Green))
            return;
        if (rewritten is not null)
            annotations.Collect(rewritten.Green, changes.Count);
        changes.Add(new TextChange(
            original.FullSpan.Start, original.FullSpan.Length, rewritten?.ToFullString() ?? ""));
    }
}
