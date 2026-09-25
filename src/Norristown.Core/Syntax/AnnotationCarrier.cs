using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Norristown.Syntax.InternalSyntax;
using NodeMark = Norristown.Syntax.SyntaxTree.NodeMark;

namespace Norristown.Syntax;

/// <summary>
/// Carries the annotations of a rewrite across the reparse that applies it. Before the file is
/// parsed again, the rewrite collects every annotated node and token in the text it writes and in
/// the text between its changes, with its kind and where it will land. After the reparse,
/// <see cref="Reattach"/> gives each one's annotations to the node or token of the same kind and
/// full span in the new tree. If the reparse reads the text differently and there is no such node
/// or token, the annotations are dropped without any diagnostic.
/// </summary>
internal sealed class AnnotationCarrier
{
    private readonly List<Tagged> tagged = [];

    /// <summary>
    /// Gets the number of annotated nodes and tokens collected so far. A rewrite that discards the
    /// changes it collected past some point passes this count, read at that point, to
    /// <see cref="Truncate"/>.
    /// </summary>
    public int Count => tagged.Count;

    /// <summary>
    /// Collects every annotated node and token of <paramref name="green"/>, which is the text of
    /// the change at index <paramref name="change"/>. Only subtrees whose flags show they hold an
    /// annotation are walked.
    /// </summary>
    /// <param name="green">The node or token whose text the change writes.</param>
    /// <param name="change">The index of the change among the rewrite's changes.</param>
    public void Collect(GreenNode green, int change) => Collect(green, change, 0);

    /// <summary>
    /// Collects every annotated node and token of <paramref name="node"/>, which is the text of
    /// the change at index <paramref name="change"/>. A line, a block or the file holds what its
    /// lines parse to in its tree rather than in its green node, so it is walked as a node of its
    /// tree. Any other node is walked as <see cref="Collect(GreenNode, int)"/> walks it.
    /// </summary>
    /// <param name="node">The node whose text the change writes.</param>
    /// <param name="change">The index of the change among the rewrite's changes.</param>
    public void Collect(SyntaxNode node, int change)
    {
        if (node is not (LineSyntax or BlockSyntax or FileSyntax))
        {
            Collect(node.Green, change, 0);
            return;
        }
        var start = node.FullSpan.Start;
        foreach (var piece in node.AnnotatedPieces())
            tagged.Add(new Tagged(change, piece.FullSpan.Start - start, piece.FullSpan.Length, piece.Kind, piece.IsToken, AnnotationsOf(piece)));
    }

    /// <summary>Discards what was collected after the first <paramref name="count"/> entries.</summary>
    /// <param name="count">The value <see cref="Count"/> had when the discarded changes began.</param>
    public void Truncate(int count) => tagged.RemoveRange(count, tagged.Count - count);

    /// <summary>
    /// Collects the annotated nodes and tokens of <paramref name="tree"/> that lie between the
    /// changes. The file is parsed again in one span from the first change to the last. So a line
    /// the rewrite never changed is still parsed again when it lies between two lines that it did
    /// change, and its annotations have to survive that reparse with the rest.
    /// </summary>
    /// <param name="tree">The file before the changes.</param>
    /// <param name="changes">The rewrite's changes, in source order and never overlapping.</param>
    /// <param name="offsets">The offset in the new file where each change's own text lands.</param>
    public void CollectBetween(SyntaxTree tree, IReadOnlyList<TextChange> changes, int[] offsets)
    {
        if (changes.Count < 2 || !tree.Root.ContainsAnnotations)
            return;

        // The annotated pieces come in source order, parent before child, so their starts never
        // decrease, and the gaps come in source order too. One walk of the pieces therefore serves
        // every gap. A piece that starts before a gap cannot fall in any later gap.
        using var pieces = tree.Root.AnnotatedPieces().GetEnumerator();
        var more = pieces.MoveNext();
        for (var i = 0; i + 1 < changes.Count && more; i++)
        {
            var from = changes[i].Start + changes[i].Length;
            var to = changes[i + 1].Start;
            if (to <= from)
                continue;

            // This is how far the gap's text has moved: where it starts in the new text, minus
            // where it started in the old.
            var moved = offsets[i] + changes[i].NewText.Length - from;
            for (; more && pieces.Current.FullSpan.Start < to; more = pieces.MoveNext())
            {
                var piece = pieces.Current;
                var span = piece.FullSpan;
                if (span.Start < from || span.End > to)
                    continue;
                tagged.Add(new Tagged(-1, span.Start + moved, span.Length, piece.Kind, piece.IsToken, AnnotationsOf(piece)));
            }
        }
    }

    /// <summary>
    /// Collects the annotations that belong to the lines the changes are on, rather than to what
    /// those lines parse to. These are the annotations of each line node, of each block such a line
    /// opens, and of each line's <c>.export</c> and line break. A line with a change on it is lexed
    /// again, so they have to survive the reparse like the rest. Any that a change replaces whole
    /// is skipped, because the change's own text holds what it becomes.
    /// </summary>
    /// <param name="tree">The file before the changes.</param>
    /// <param name="changes">The rewrite's changes, in source order and never overlapping.</param>
    public void CollectAround(SyntaxTree tree, IReadOnlyList<TextChange> changes)
    {
        if (changes.Count == 0 || !tree.Root.ContainsAnnotations)
            return;
        var first = tree.GetLineIndex(changes[0].Start);
        var last = tree.GetLineIndex(changes[^1].Start + changes[^1].Length);
        for (var i = first; i <= last; i++)
        {
            // The lines of a continued statement are one line node, which is visited once.
            if (tree.StatementStart(i) != i)
                continue;
            var line = tree.GetLine(i);
            if (!line.Green.Annotations.IsEmpty)
                Around(line, changes);
            if (line.Parent is BlockSyntax block && block.Opener == line && !block.Green.Annotations.IsEmpty)
                Around(block, changes);
            if (line.ExportKeyword is { ContainsAnnotations: true } export)
                Around(export, changes);
            if (line.EndOfLineToken.ContainsAnnotations)
                Around(line.EndOfLineToken, changes);
        }
    }

    /// <summary>
    /// Returns <paramref name="tree"/> with the collected annotations restored on its nodes and
    /// tokens. Each is matched by kind and position. The match is the node or token of the same
    /// kind whose full span equals the recorded span of the annotated node or token. A reparse
    /// that read the text differently leaves no match, and those annotations are dropped.
    /// </summary>
    /// <param name="tree">The file after the changes.</param>
    /// <param name="offsets">The offset in that file where each change's own text lands.</param>
    /// <returns>The tree with the annotations, or <paramref name="tree"/> if none was restored.</returns>
    public SyntaxTree Reattach(SyntaxTree tree, int[] offsets)
    {
        if (tagged.Count == 0)
            return tree;
        var byLine = new Dictionary<int, List<Tagged>>();
        var marks = tree.NodeMarks.ToList();
        var physical = tree.PhysicalLines.ToBuilder();
        var any = false;
        foreach (var mark in tagged)
        {
            var at = Math.Clamp(mark.Change < 0 ? mark.At : offsets[mark.Change] + mark.At, 0, tree.Text.Length);
            if (tree.GetLineIndex(at) >= tree.LineCount)
                continue;

            // A line, a block and the file are built afresh with the tree, so their annotations
            // are kept by the tree. The `.export` and the line break belong to the line the lexer
            // read rather than to its statement, so theirs are kept on that line's tokens.
            if (!mark.IsToken && mark.Kind is SyntaxKind.Line or SyntaxKind.Block or SyntaxKind.File)
            {
                any |= Marked(marks, tree, mark with { At = at });
                continue;
            }
            if (mark.IsToken && LineToken(tree, physical, mark with { At = at }))
            {
                any = true;
                continue;
            }

            // A statement continued over several lines of the file is parsed as one line, and its
            // kept parse belongs to the first of them, wherever on it the mark lands.
            var line = tree.StatementStart(tree.GetLineIndex(at));
            if (!byLine.TryGetValue(line, out var wanted))
                byLine[line] = wanted = [];
            wanted.Add(mark with { At = at });
        }

        // Lines that already have annotations keep the parse that holds them. Lines just parsed
        // again get their annotations back on the nodes and tokens the reparse produced. Each
        // parse is kept at the first line of the file that its statement starts on.
        var kept = new Parser.Result?[tree.LineCount];
        for (var i = 0; i < kept.Length; i++)
        {
            if (tree.StatementStart(i) == i && tree.LinesContainAnnotations(i, i))
                kept[i] = tree.Parsed(i);
        }
        foreach (var (line, wanted) in byLine)
        {
            // The line's skipped tokens are searched too, since an annotated token may be among
            // them.
            var read = tree.GetLine(line);
            var reattacher = new Reattacher(wanted);
            var statement = read.Statement;
            var found = reattacher.Visit(statement);
            var skipped = read.SkippedTokens is { } left ? reattacher.Visit(left) : null;
            var node = found is not null && !ReferenceEquals(found.Green, statement.Green) ? found.Green : null;
            var rest = skipped is not null && !ReferenceEquals(skipped.Green, read.SkippedTokens!.Green)
                ? skipped.Green
                : null;
            if (node is null && rest is null)
                continue;
            var parsed = tree.Parsed(line);
            kept[line] = parsed with { Node = node ?? parsed.Node, SkippedTokens = rest ?? parsed.SkippedTokens };
            any = true;
        }
        return any ? tree.WithAnnotated(physical.MoveToImmutable(), ImmutableCollectionsMarshal.AsImmutableArray(kept), [.. marks]) : tree;
    }

    /// <summary>
    /// Records <paramref name="piece"/>, which lies on a line with a change on it, at the place it
    /// lands once the changes are applied, unless a change replaces it whole or runs into it.
    /// </summary>
    private void Around(SyntaxNodeOrToken piece, IReadOnlyList<TextChange> changes)
    {
        var span = piece.FullSpan;
        var shift = 0;
        foreach (var change in changes)
        {
            var end = change.Start + change.Length;
            if (change.Length > 0 && change.Start <= span.Start && end >= span.End)
                return;
            if (end <= span.Start)
            {
                shift += change.NewText.Length - change.Length;
                continue;
            }
            if (change.Start < span.Start)
                return;
            break;
        }
        tagged.Add(new Tagged(-1, span.Start + shift, span.Length, piece.Kind, piece.IsToken, AnnotationsOf(piece)));
    }

    /// <summary>Returns the annotations of a node or a token.</summary>
    private static ImmutableArray<SyntaxAnnotation> AnnotationsOf(SyntaxNodeOrToken piece) =>
        piece.AsNode() is { } node ? node.Green.Annotations : piece.AsToken().Green.Annotations;

    /// <summary>
    /// Adds the annotations of a line, a block or the file to <paramref name="marks"/>, the
    /// annotations the tree keeps for such nodes. A node that starts anywhere but the start of a
    /// line is not there to be given them.
    /// </summary>
    /// <returns>Whether the annotations were added.</returns>
    private static bool Marked(List<NodeMark> marks, SyntaxTree tree, Tagged mark)
    {
        var line = tree.GetLineIndex(mark.At);
        if (tree.LineStarts[line] != mark.At)
            return false;
        var at = marks.FindIndex(other => other.Line == line && other.Kind == mark.Kind);
        if (at < 0)
        {
            marks.Add(new NodeMark(line, mark.Kind, mark.Annotations));
        }
        else
        {
            var own = marks[at].Annotations;
            marks[at] = marks[at] with { Annotations = own.AddRange(mark.Annotations.Where(annotation => !own.Contains(annotation))) };
        }
        return true;
    }

    /// <summary>
    /// Gives the annotations of <paramref name="mark"/> to the <c>.export</c> that starts a line
    /// of <paramref name="physical"/> or the line break that ends it, when the mark is one of those
    /// tokens.
    /// </summary>
    /// <returns>Whether the mark was such a token.</returns>
    private static bool LineToken(SyntaxTree tree, ImmutableArray<GreenLine>.Builder physical, Tagged mark)
    {
        var line = tree.GetLineIndex(mark.At);
        var tokens = physical[line].Tokens;
        var index = mark.Kind == SyntaxKind.EndOfLine ? tokens.Length - 1
            : tree.StatementStart(line) == line && tree.Parsed(line).ExportKeyword is not null ? 0
            : -1;
        if (index < 0 || tokens[index].Kind != mark.Kind || tokens[index].FullWidth != mark.Width
            || tree.LineStarts[line] + physical[line].TextOffset(index) - tokens[index].LeadingWidth != mark.At)
        {
            return false;
        }
        var token = (GreenToken)tokens[index].WithAdditionalAnnotations(mark.Annotations);
        physical[line] = new GreenLine(tokens.SetItem(index, token));
        return true;
    }

    /// <summary>
    /// Collects every annotated node and token of <paramref name="green"/>, with its offset in the
    /// text of the change at index <paramref name="change"/>.
    /// </summary>
    /// <param name="green">The node whose text the change contains.</param>
    /// <param name="change">The index of the change.</param>
    /// <param name="at">The offset where <paramref name="green"/> starts in that change's text.</param>
    private void Collect(GreenNode green, int change, int at)
    {
        if (!green.ContainsAnnotations)
            return;
        if (green.Annotations.Length > 0)
        {
            tagged.Add(new Tagged(
                change, at, green.FullWidth, green.Kind, green is GreenToken, green.Annotations));
        }
        for (var i = 0; i < green.SlotCount; i++)
        {
            if (green.GetSlot(i) is not { } slot)
                continue;
            Collect(slot, change, at);
            at += slot.FullWidth;
        }
    }

    /// <summary>
    /// Represents an annotated node or token in a rewrite's output. It is recorded so that the
    /// node or token the reparse makes of that same text can be given the annotations back.
    /// </summary>
    /// <param name="Change">
    /// The index of the rewrite's change that contains it. The value is −1 for a node or token
    /// that lies between two changes and is only parsed again; its <paramref name="At"/> is
    /// already its offset in the new file.
    /// </param>
    /// <param name="At">
    /// The offset where it starts. While the rewrite is collecting, the offset is in that change's
    /// own text. Once the change has been applied, the offset is in the file.
    /// </param>
    /// <param name="Width">The width, trivia included.</param>
    /// <param name="Kind">The kind of the node or token.</param>
    /// <param name="IsToken">Whether it is a token rather than a node.</param>
    /// <param name="Annotations">The annotations it has.</param>
    private readonly record struct Tagged(
        int Change,
        int At,
        int Width,
        SyntaxKind Kind,
        bool IsToken,
        ImmutableArray<SyntaxAnnotation> Annotations);

    /// <summary>
    /// Represents a rewrite that returns the nodes and tokens it visits with their annotations
    /// restored. A node or token with the same kind and position as a recorded annotated node or
    /// token is taken to be that node or token. It is only ever given a statement or a line's
    /// skipped tokens, so it never reaches a line and never starts a reparse of its own.
    /// </summary>
    /// <param name="wanted">The annotated nodes and tokens to look for, all on one line.</param>
    private sealed class Reattacher(List<Tagged> wanted) : SyntaxRewriter
    {
        /// <inheritdoc/>
        public override SyntaxNode? Visit(SyntaxNode? node)
        {
            if (node is null)
                return null;

            // Look the node up before visiting its children, while it still has its position in
            // the file; the rewritten node that comes back is detached and has no position yet.
            var found = Wanted(node.FullSpan, node.Kind, isToken: false);
            var rewritten = base.Visit(node);
            return found.IsEmpty || rewritten is null ? rewritten : rewritten.WithAdditionalAnnotations(found);
        }

        /// <inheritdoc/>
        public override SyntaxToken VisitToken(SyntaxToken token)
        {
            var found = Wanted(token.FullSpan, token.Kind, isToken: true);
            return found.IsEmpty ? token : token.WithAdditionalAnnotations(found);
        }

        /// <summary>
        /// Returns the annotations to restore on the node or token of <paramref name="kind"/> at
        /// <paramref name="span"/>.
        /// </summary>
        private ImmutableArray<SyntaxAnnotation> Wanted(TextSpan span, SyntaxKind kind, bool isToken)
        {
            var found = ImmutableArray<SyntaxAnnotation>.Empty;
            foreach (var mark in wanted)
            {
                if (mark.IsToken == isToken && mark.Kind == kind
                    && mark.At == span.Start && mark.Width == span.Length)
                {
                    found = found.AddRange(mark.Annotations);
                }
            }
            return found;
        }
    }
}
