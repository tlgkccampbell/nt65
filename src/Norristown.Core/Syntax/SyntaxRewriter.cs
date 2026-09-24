using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// Represents a <see cref="SyntaxVisitor{TResult}"/> that produces a rewritten tree. Every method
/// rewrites the child nodes and tokens of its node and calls that node's <c>Update</c>, which
/// returns the node unchanged when nothing under it changed. Override the methods for the kinds
/// a fix concerns, return the replacement nodes, and leave the rest. A rewrite that changes
/// nothing returns the tree it was given, as the very same objects.
/// <para>
/// A statement and everything under it is rebuilt in place, from the green nodes its children
/// already hold, so a rewrite costs only what it changes. A line, a block and the file are
/// handled differently. A line holds the tokens the lexer read rather than what they parse to, so
/// the changes on a line are applied to the file's text as text changes, and the file is parsed
/// again from there. For that reason a <c>Visit</c> of a file returns the root of a <em>new</em>
/// tree. Every other character of the file stays the same, and the lines the change did not
/// touch keep the nodes they had.
/// </para>
/// <para>
/// A <see cref="SyntaxAnnotation"/> survives that reparse. Every annotated node or token in the
/// rewrite's output is recorded with its kind and its position in the new text. After the file
/// is parsed again, the node or token of the same kind at the same position gets the annotations
/// back. If the reparse reads the text differently and there is no such node or token, the
/// annotations are dropped without any diagnostic.
/// </para>
/// </summary>
public abstract partial class SyntaxRewriter : SyntaxVisitor<SyntaxNode>
{
    // The text changes a rewrite of a file, a block or a line has made so far, in source order
    // and never overlapping. There is one change per child of a line that came back different.
    // The list is null except while such a rewrite is running, and a non-null list tells a nested
    // rewrite to only collect changes.
    private List<TextChange>? changes;

    // The annotated nodes and tokens in the text of those changes. Each annotation is restored on
    // its node or token once the file has been parsed again. This is null exactly when `changes`
    // is.
    private List<Tagged>? tagged;

    /// <summary>
    /// Returns a node that no method is overridden for unchanged, with everything under it.
    /// </summary>
    /// <param name="node">The node visited.</param>
    /// <returns>That same node.</returns>
    public override SyntaxNode? DefaultVisit(SyntaxNode node) => node;

    /// <summary>
    /// Rewrites a token. The default leaves it unchanged. An override returns the replacement
    /// token, usually built with <see cref="SyntaxToken.WithText"/>,
    /// <see cref="SyntaxToken.WithTriviaFrom"/> or <see cref="SyntaxFactory"/>.
    /// </summary>
    /// <param name="token">The token visited.</param>
    /// <returns>The replacement token.</returns>
    public virtual SyntaxToken VisitToken(SyntaxToken token) => token;

    /// <summary>Rewrites the items of a list, dropping any item rewritten to null.</summary>
    /// <typeparam name="T">The type of the items.</typeparam>
    /// <param name="list">The list visited.</param>
    /// <returns>The list itself, or a new list of the items that came back.</returns>
    public virtual SyntaxList<T> VisitList<T>(SyntaxList<T> list) where T : SyntaxNode
    {
        if (list.Count == 0)
            return list;
        var items = ImmutableArray.CreateBuilder<T>(list.Count);
        var moved = false;
        var dropped = false;
        foreach (var item in list)
        {
            if (Item<T>(item) is not { } rewritten)
            {
                moved = true;
                dropped = true;
                continue;
            }
            dropped = false;
            moved |= !ReferenceEquals(rewritten.Green, item.Green);
            items.Add(rewritten);
        }
        if (dropped && items.Count > 0)
            items[^1] = WithTrailingTriviaOf(items[^1], list[^1]);
        return moved ? SyntaxFactory.List<T>(items) : list;
    }

    /// <summary>
    /// Rewrites the items of a separated list and the separators between them. An item rewritten
    /// to null is dropped along with the separator after it, so that the result is still a list
    /// of items with a separator between each two.
    /// </summary>
    /// <typeparam name="T">The type of the items.</typeparam>
    /// <param name="list">The list visited.</param>
    /// <returns>The list itself, or a new list of the items that came back.</returns>
    public virtual SeparatedSyntaxList<T> VisitList<T>(SeparatedSyntaxList<T> list) where T : SyntaxNode
    {
        if (list.Count == 0)
            return list;
        var items = ImmutableArray.CreateBuilder<T>(list.Count);
        var separators = ImmutableArray.CreateBuilder<SyntaxToken>(list.SeparatorCount);
        var moved = false;
        var dropped = false;
        for (var i = 0; i < list.Count; i++)
        {
            var item = list[i];
            if (Item<T>(item) is not { } rewritten)
            {
                moved = true;
                dropped = true;
                continue;
            }
            dropped = false;
            moved |= !ReferenceEquals(rewritten.Green, item.Green);
            items.Add(rewritten);
            if (i >= list.SeparatorCount)
                continue;
            var separator = list.GetSeparator(i);
            var visited = VisitToken(separator);
            moved |= !ReferenceEquals(visited.Green, separator.Green);
            separators.Add(visited);
        }

        // A list the source did not end with a separator must not end with one now, because the
        // item before the last separator may have been dropped.
        if (list.SeparatorCount < list.Count && items.Count > 0 && separators.Count == items.Count)
            separators.RemoveAt(separators.Count - 1);

        // The trivia after the last item separated the list from the rest of the line, so
        // dropping that item hands its trailing whitespace to the item now at the end.
        if (dropped && items.Count > 0)
            items[^1] = WithTrailingTriviaOf(items[^1], list[^1]);
        return moved ? SyntaxFactory.SeparatedList<T>(items, separators) : list;
    }

    /// <summary>Rewrites a run of tokens.</summary>
    /// <param name="list">The list visited.</param>
    /// <returns>The list itself, or a new list of the tokens that came back.</returns>
    public virtual SyntaxTokenList VisitList(SyntaxTokenList list)
    {
        if (list.Count == 0)
            return list;
        var tokens = ImmutableArray.CreateBuilder<SyntaxToken>(list.Count);
        var moved = false;
        foreach (var token in list)
        {
            var visited = VisitToken(token);
            moved |= !ReferenceEquals(visited.Green, token.Green);
            tokens.Add(visited);
        }
        return moved ? SyntaxFactory.TokenList(tokens) : list;
    }

    /// <inheritdoc/>
    public override SyntaxNode? VisitFile(FileSyntax node) => Rewritten(node);

    /// <inheritdoc/>
    public override SyntaxNode? VisitBlock(BlockSyntax node) => Rewritten(node);

    /// <inheritdoc/>
    public override SyntaxNode? VisitLine(LineSyntax node) => Rewritten(node);

    /// <summary>
    /// Returns the rewritten node for a required slot after checking it. A required slot cannot be
    /// rewritten to null, and a node of another kind does not fit where Syntax.xml specifies one
    /// kind.
    /// </summary>
    /// <typeparam name="T">The type the slot holds.</typeparam>
    /// <param name="rewritten">The node the rewrite returned.</param>
    /// <param name="slot">The slot's name, used in the exception message when the node does not fit.</param>
    /// <returns>The node.</returns>
    private protected static T Required<T>(SyntaxNode? rewritten, string slot) where T : SyntaxNode =>
        rewritten as T ?? throw new InvalidOperationException(rewritten is null
            ? $"a rewrite left {slot} as nothing, and every node of its kind has one"
            : $"a rewrite put a {rewritten.Kind} where {slot} belongs");

    /// <summary>
    /// Returns <paramref name="kept"/> with the trailing trivia of <paramref name="removed"/>
    /// appended. A list that loses its last item keeps the trivia that separated the list from the
    /// rest of the line, so that the text that follows does not run up against the new last item.
    /// </summary>
    private static T WithTrailingTriviaOf<T>(T kept, T removed) where T : SyntaxNode
    {
        if (removed.GetLastToken(includeZeroWidth: true) is not { } after || after.TrailingTrivia.Count == 0)
            return kept;
        if (kept.GetLastToken(includeZeroWidth: true) is not { } end || end.IsMissing)
            return kept;
        return (T)kept.ReplaceToken(end, end.WithTrailingTrivia([.. end.TrailingTrivia, .. after.TrailingTrivia]));
    }

    /// <summary>Rewrites the token in an optional slot; an empty slot stays empty.</summary>
    private SyntaxToken? VisitToken(SyntaxToken? token) => token is { } present ? VisitToken(present) : null;

    /// <summary>
    /// Returns the rewritten list item, after checking that it has the type of the list's items.
    /// </summary>
    private T? Item<T>(T item) where T : SyntaxNode =>
        Visit(item) is { } rewritten
            ? rewritten as T ?? throw new InvalidOperationException(
                $"a rewrite put a {rewritten.Kind} in a list of {typeof(T).Name}")
            : null;

    /// <summary>
    /// Rewrites a file, a block or a line. The changes on each of its lines are applied to the
    /// file's text, and the file is parsed again from there. A rewrite nested inside another only
    /// collects its changes, so that only one new tree is built, no matter how deep the walk went.
    /// </summary>
    private SyntaxNode Rewritten(SyntaxNode node)
    {
        if (changes is not null)
        {
            Collect(node);
            return node;
        }

        changes = [];
        tagged = [];
        try
        {
            Collect(node);
            if (changes.Count == 0)
                return node;

            // A single change spans from the first changed node or token to the last, so that the
            // file is parsed once no matter how many the rewrite touched, and the lines outside
            // that range keep the green nodes they have.
            var offsets = new int[changes.Count];
            var joined = Joined(node.Tree.Text, changes, offsets);
            Between(node.Tree, changes, offsets);
            var tree = node.Tree.WithChange(joined);
            return Corresponding(node, tagged.Count == 0 ? tree : Reattached(tree, offsets, tagged));
        }
        finally
        {
            changes = null;
            tagged = null;
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
    /// Returns <paramref name="tree"/> with the annotations of <paramref name="tagged"/> restored
    /// on their nodes and tokens. Each is matched by kind and position. The match is the node or
    /// token of the same kind whose full span equals the recorded span of the annotated node or
    /// token. A reparse that read the text differently leaves no match, and those annotations are
    /// dropped.
    /// </summary>
    /// <param name="tree">The file after the change.</param>
    /// <param name="offsets">The offset in that file where each change's own text lands.</param>
    /// <param name="tagged">The annotated nodes and tokens in the rewrite's output.</param>
    private static SyntaxTree Reattached(SyntaxTree tree, int[] offsets, List<Tagged> tagged)
    {
        var byLine = new Dictionary<int, List<Tagged>>();
        foreach (var mark in tagged)
        {
            var at = Math.Clamp(mark.Change < 0 ? mark.At : offsets[mark.Change] + mark.At, 0, tree.Text.Length);
            var line = tree.GetLineIndex(at);
            if (line >= tree.LineCount)
                continue;
            if (!byLine.TryGetValue(line, out var wanted))
                byLine[line] = wanted = [];
            wanted.Add(mark with { At = at });
        }

        // Lines that already have annotations keep the parse that holds them. Lines just parsed
        // again get their annotations back on the nodes and tokens the reparse produced.
        var kept = new Parser.Result?[tree.LineCount];
        for (var i = 0; i < kept.Length; i++)
        {
            if (tree.LinesContainAnnotations(i, i))
                kept[i] = tree.Parsed(i);
        }
        var any = false;
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
        return any ? tree.WithKeptParses(ImmutableCollectionsMarshal.AsImmutableArray(kept)) : tree;
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
    /// Adds the changes a rewrite of <paramref name="node"/> makes to <see cref="changes"/>, in
    /// order.
    /// </summary>
    private void Collect(SyntaxNode node)
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
            var before = changes!.Count;
            var marked = tagged!.Count;
            var rewritten = Visit(line);
            if (ReferenceEquals(rewritten, line))
                continue;

            // The whole line is being replaced, so any changes collected from inside it are
            // discarded. One change covers the line, and no change may fall inside a span that is
            // already replaced.
            changes.RemoveRange(before, changes.Count - before);
            tagged.RemoveRange(marked, tagged.Count - marked);
            Changed(line, rewritten);
        }
    }

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
            Changed(exported, VisitToken(exported));
        var statement = Visit(line.Statement);
        var left = line.SkippedTokens is { } skipped ? Visit(skipped) : null;
        var moved = !ReferenceEquals(statement?.Green, line.Statement.Green)
            || (line.SkippedTokens is { } before && !ReferenceEquals(left?.Green, before.Green));
        Kept(line.Statement, statement, moved);
        if (line.SkippedTokens is { } rest)
            Kept(rest, left, moved);
        Changed(line.EndOfLineToken, VisitToken(line.EndOfLineToken));
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
            Tag(original.Green, changes!.Count, 0);
            changes.Add(new TextChange(original.FullSpan.Start, original.FullSpan.Length, original.ToFullString()));
            return;
        }
        Changed(original, rewritten);
    }

    private void Changed(SyntaxToken original, SyntaxToken rewritten)
    {
        if (ReferenceEquals(original.Green, rewritten.Green))
            return;
        Tag(rewritten.Green, changes!.Count, 0);
        changes.Add(new TextChange(original.FullSpan.Start, original.FullSpan.Length, rewritten.ToFullString()));
    }

    private void Changed(SyntaxNode original, SyntaxNode? rewritten)
    {
        if (rewritten is not null && ReferenceEquals(original.Green, rewritten.Green))
            return;
        if (rewritten is not null)
            Tag(rewritten.Green, changes!.Count, 0);
        changes!.Add(new TextChange(
            original.FullSpan.Start, original.FullSpan.Length, rewritten?.ToFullString() ?? ""));
    }

    /// <summary>
    /// Records the annotated nodes and tokens of <paramref name="tree"/> that lie between the
    /// changes. The file is parsed again in one span from the first change to the last. So a line
    /// the rewrite never changed is still parsed again when it lies between two lines that it did
    /// change, and its annotations have to survive that reparse with the rest.
    /// </summary>
    /// <param name="tree">The file before the changes.</param>
    /// <param name="changes">The rewrite's changes, in source order.</param>
    /// <param name="offsets">The offset in the new file where each change's own text lands.</param>
    private void Between(SyntaxTree tree, List<TextChange> changes, int[] offsets)
    {
        if (changes.Count < 2 || !tree.Root.ContainsAnnotations)
            return;
        for (var i = 0; i + 1 < changes.Count; i++)
        {
            var from = changes[i].Start + changes[i].Length;
            var to = changes[i + 1].Start;
            if (to <= from)
                continue;

            // This is how far the gap's text has moved: where it starts in the new text, minus
            // where it started in the old.
            var moved = offsets[i] + changes[i].NewText.Length - from;
            foreach (var piece in tree.Root.AnnotatedPieces())
            {
                var span = piece.FullSpan;
                if (span.Start < from || span.End > to)
                    continue;
                var annotations = piece.AsNode() is { } inner ? inner.Green.Annotations : piece.AsToken().Green.Annotations;
                tagged!.Add(new Tagged(-1, span.Start + moved, span.Length, piece.Kind, piece.IsToken, annotations));
            }
        }
    }

    /// <summary>
    /// Records every annotated node and token of <paramref name="green"/>, with its offset in the
    /// text of the change about to be applied. Only subtrees whose flags show they hold an
    /// annotation are walked.
    /// </summary>
    /// <param name="green">The node whose text the change contains.</param>
    /// <param name="change">The index of the change.</param>
    /// <param name="at">The offset where <paramref name="green"/> starts in that change's text.</param>
    private void Tag(GreenNode green, int change, int at)
    {
        if (!green.ContainsAnnotations)
            return;
        if (green.Annotations.Length > 0)
        {
            tagged!.Add(new Tagged(
                change, at, green.FullWidth, green.Kind, green is GreenToken, green.Annotations));
        }
        for (var i = 0; i < green.SlotCount; i++)
        {
            if (green.GetSlot(i) is not { } slot)
                continue;
            Tag(slot, change, at);
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
    /// token is taken to be that node or token.
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
