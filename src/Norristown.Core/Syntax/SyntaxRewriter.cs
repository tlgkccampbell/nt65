using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// A <see cref="SyntaxVisitor{TResult}"/> that writes a tree back out: every method rewrites the
/// pieces of its node and calls that node's <c>Update</c>, which hands the node back unchanged
/// when nothing under it moved. Override the kinds a fix is about, return what they are to
/// become, and leave the rest; a rewrite that changes nothing gives back the tree it was given,
/// the same objects and all.
/// <para>
/// A statement and everything under it is rebuilt where it stands, out of the green nodes the
/// pieces already hold, so a rewritten node costs what it changed and no more. A line, a block
/// and the file are not: a line holds the tokens the lexer read rather than what they parse to,
/// so what changed on a line is written back as text and the file is parsed again from there.
/// That is why a <c>Visit</c> of a file gives the root of a <em>new</em> tree — every other
/// character of the file is the same character, and the lines the change did not touch keep the
/// nodes they had.
/// </para>
/// <para>
/// A <see cref="SyntaxAnnotation"/> crosses that reparse. Every annotated piece of what the
/// rewrite writes is remembered with what it is and where it stands in the new text, and after
/// the file is read again the piece of the same kind at the same place is given the annotations
/// back. Where the reparse read the text another way and there is no such piece, the annotations
/// are dropped, and nothing is said about it.
/// </para>
/// </summary>
public abstract partial class SyntaxRewriter : SyntaxVisitor<SyntaxNode>
{
    // The text a rewrite of a file, a block or a line has changed so far, in source order and
    // never overlapping: one per piece of a line that came back different. It is null except
    // while such a rewrite is running, which is also what tells a nested one to only collect.
    private List<TextChange>? changes;

    // The annotated pieces of what those changes write, which is what puts an annotation back on
    // the piece it was on once the file has been parsed again. Null and non-null with `changes`.
    private List<Tagged>? tagged;

    /// <summary>A node no method is overridden for is left as it is, with everything under it.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>That same node.</returns>
    public override SyntaxNode? DefaultVisit(SyntaxNode node) => node;

    /// <summary>
    /// Rewrites a token. The default leaves it alone; an override gives back the token it is to
    /// become, which <see cref="SyntaxToken.WithText"/>, <see cref="SyntaxToken.WithTriviaFrom"/>
    /// and <see cref="SyntaxFactory"/> are how to write.
    /// </summary>
    /// <param name="token">The token visited.</param>
    /// <returns>What it is to become.</returns>
    public virtual SyntaxToken VisitToken(SyntaxToken token) => token;

    /// <summary>Rewrites the items of a list; an item rewritten to null is dropped.</summary>
    /// <typeparam name="T">What the items are.</typeparam>
    /// <param name="list">The list visited.</param>
    /// <returns>The list, or a new one over the items that came back.</returns>
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
            items[^1] = Carrying(items[^1], list[^1]);
        return moved ? SyntaxFactory.List<T>(items) : list;
    }

    /// <summary>
    /// Rewrites the items of a separated list and the separators between them. An item rewritten
    /// to null is dropped, and the separator written after it goes with it, so that what is left
    /// is still a list of items with a separator between each two.
    /// </summary>
    /// <typeparam name="T">What the items are.</typeparam>
    /// <param name="list">The list visited.</param>
    /// <returns>The list, or a new one over the items that came back.</returns>
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
            var written = VisitToken(separator);
            moved |= !ReferenceEquals(written.Green, separator.Green);
            separators.Add(written);
        }

        // A list the source did not end with a separator must not end with one now: the item the
        // last separator was written after may have been dropped.
        if (list.SeparatorCount < list.Count && items.Count > 0 && separators.Count == items.Count)
            separators.RemoveAt(separators.Count - 1);

        // What stood after the last item stood between the list and whatever the line writes
        // next, so dropping that item hands its trailing whitespace to the item now at the end.
        if (dropped && items.Count > 0)
            items[^1] = Carrying(items[^1], list[^1]);
        return moved ? SyntaxFactory.SeparatedList<T>(items, separators) : list;
    }

    /// <summary>Rewrites a run of tokens.</summary>
    /// <param name="list">The list visited.</param>
    /// <returns>The list, or a new one over the tokens that came back.</returns>
    public virtual SyntaxTokenList VisitList(SyntaxTokenList list)
    {
        if (list.Count == 0)
            return list;
        var tokens = ImmutableArray.CreateBuilder<SyntaxToken>(list.Count);
        var moved = false;
        foreach (var token in list)
        {
            var written = VisitToken(token);
            moved |= !ReferenceEquals(written.Green, token.Green);
            tokens.Add(written);
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
    /// The node a required slot came back as. A slot that must hold something cannot be rewritten
    /// to nothing, and a node of another kind does not fit where the table says one kind goes.
    /// </summary>
    /// <typeparam name="T">What the slot holds.</typeparam>
    /// <param name="rewritten">What the rewrite gave back.</param>
    /// <param name="slot">The slot's name, for what to say when it does not fit.</param>
    /// <returns>The node.</returns>
    private protected static T Required<T>(SyntaxNode? rewritten, string slot) where T : SyntaxNode =>
        rewritten as T ?? throw new InvalidOperationException(rewritten is null
            ? $"a rewrite left {slot} as nothing, and every node of its kind has one"
            : $"a rewrite put a {rewritten.Kind} where {slot} belongs");

    /// <summary>
    /// <paramref name="kept"/> written with the whitespace <paramref name="removed"/> had after
    /// it: a list that loses its last item keeps whatever stood between the list and the rest of
    /// the line, so that what is left is not written up against it.
    /// </summary>
    private static T Carrying<T>(T kept, T removed) where T : SyntaxNode
    {
        if (removed.GetLastToken(includeZeroWidth: true) is not { } after || after.TrailingTrivia.Count == 0)
            return kept;
        if (kept.GetLastToken(includeZeroWidth: true) is not { } end || end.IsMissing)
            return kept;
        return (T)kept.ReplaceToken(end, end.WithTrailingTrivia([.. end.TrailingTrivia, .. after.TrailingTrivia]));
    }

    /// <summary>The token an optional slot came back as, which is nothing where the slot is empty.</summary>
    private SyntaxToken? VisitToken(SyntaxToken? token) => token is { } written ? VisitToken(written) : null;

    /// <summary>The item a list came back with, checked to be of the list's own kind.</summary>
    private T? Item<T>(T item) where T : SyntaxNode =>
        Visit(item) is { } rewritten
            ? rewritten as T ?? throw new InvalidOperationException(
                $"a rewrite put a {rewritten.Kind} in a list of {typeof(T).Name}")
            : null;

    /// <summary>
    /// A file, a block or a line rewritten: what changed on each of its lines, written back into
    /// the file's text, and the file parsed again from there. A rewrite nested inside another
    /// only collects, so that one tree is built however deep the walk went.
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

            // One change, from the first piece that moved to the last, so that the file is parsed
            // once however many pieces the rewrite touched and the lines outside that range keep
            // the green nodes they have.
            var written = new int[changes.Count];
            var joined = Joined(node.Tree.Text, changes, written);
            Between(node.Tree, changes, written);
            var tree = node.Tree.WithChange(joined);
            return Corresponding(node, tagged.Count == 0 ? tree : Reattached(tree, written, tagged));
        }
        finally
        {
            changes = null;
            tagged = null;
        }
    }

    /// <summary>
    /// The one change that makes all of <paramref name="changes"/>: from the start of the first
    /// to the end of the last, with the text between them as it stands where nothing moved.
    /// <paramref name="written"/> is filled with where each change's own text lands in the file.
    /// </summary>
    private static TextChange Joined(string text, List<TextChange> changes, int[] written)
    {
        var start = changes[0].Start;
        var end = changes[^1].Start + changes[^1].Length;
        var built = new StringBuilder(end - start);
        var at = start;
        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            built.Append(text, at, change.Start - at);
            written[i] = start + built.Length;
            built.Append(change.NewText);
            at = change.Start + change.Length;
        }
        return new TextChange(start, end - start, built.Append(text, at, end - at).ToString());
    }

    /// <summary>
    /// <paramref name="tree"/> with the annotations of <paramref name="tagged"/> put back on the
    /// pieces they were written on. A piece is looked for by what it is and where it stands: the
    /// node or the token of the same kind whose full span is the one the annotated piece was
    /// written at. A reparse that read the text another way leaves no such piece, and those
    /// annotations are dropped.
    /// </summary>
    /// <param name="tree">The file as it reads after the change.</param>
    /// <param name="written">Where each change's own text lands in that file.</param>
    /// <param name="tagged">The annotated pieces of what the rewrite wrote.</param>
    private static SyntaxTree Reattached(SyntaxTree tree, int[] written, List<Tagged> tagged)
    {
        var byLine = new Dictionary<int, List<Tagged>>();
        foreach (var mark in tagged)
        {
            var at = Math.Clamp(mark.Change < 0 ? mark.At : written[mark.Change] + mark.At, 0, tree.Text.Length);
            var line = tree.GetLineIndex(at);
            if (line >= tree.LineCount)
                continue;
            if (!byLine.TryGetValue(line, out var wanted))
                byLine[line] = wanted = [];
            wanted.Add(mark with { At = at });
        }

        // The lines already carrying annotations keep the parse they carry them on; the lines
        // just read again are given theirs back on the pieces the reparse made of them.
        var kept = new Parser.Result?[tree.LineCount];
        for (var i = 0; i < kept.Length; i++)
        {
            if (tree.LinesContainAnnotations(i, i))
                kept[i] = tree.Parsed(i);
        }
        var any = false;
        foreach (var (line, wanted) in byLine)
        {
            // What the statement could not take is the line's too, and a piece of it may be
            // what was annotated.
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
        return any ? tree.WithCarried(ImmutableCollectionsMarshal.AsImmutableArray(kept)) : tree;
    }

    /// <summary>The node of <paramref name="tree"/> that <paramref name="node"/> has become.</summary>
    private static SyntaxNode Corresponding(SyntaxNode node, SyntaxTree tree) => node switch
    {
        LineSyntax line => tree.GetLine(Math.Min(line.LineIndex, tree.LineCount - 1)),
        BlockSyntax block => tree.Root.DescendantNodes().OfType<BlockSyntax>()
            .FirstOrDefault(other => other.LineIndex == block.LineIndex) ?? (SyntaxNode)tree.Root,
        _ => tree.Root,
    };

    /// <summary>What a rewrite of <paramref name="node"/> changes, added to <see cref="changes"/> in order.</summary>
    private void Collect(SyntaxNode node)
    {
        if (node is LineSyntax own)
        {
            CollectLine(own);
            return;
        }
        foreach (var child in node.ChildNodes)
        {
            // A line is visited whole first, so that a rewrite may swap or drop one; where it
            // comes back as itself, the pieces it is written in are what the rewrite reached.
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

            // The whole line is being written over, so whatever the walk down it found is part
            // of what goes: one change for the line, never one inside a span already replaced.
            changes.RemoveRange(before, changes.Count - before);
            tagged.RemoveRange(marked, tagged.Count - marked);
            Changed(line, rewritten);
        }
    }

    /// <summary>
    /// What a rewrite changes on one line: its <c>.export</c>, its statement, what was left over,
    /// its break. A line any of them changes is read again whole, so the statement or what was
    /// left over, where it carries an annotation and was not itself rewritten, is written again
    /// as it stands, for its annotations to be found where it lands.
    /// </summary>
    private void CollectLine(LineSyntax line)
    {
        // Each piece is visited in the order it is written, which a rewrite that counts or
        // carries something from token to token relies on, before anything is written.
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
    /// A piece of a line the rewrite gave back as <paramref name="rewritten"/>: a change where it
    /// changed, and where it did not but <paramref name="moved"/> says the line is read again, a
    /// change writing it as it stands when it carries an annotation.
    /// </summary>
    private void Kept(SyntaxNode written, SyntaxNode? rewritten, bool moved)
    {
        if (moved && rewritten is not null && ReferenceEquals(written.Green, rewritten.Green) && written.ContainsAnnotations)
        {
            Tag(written.Green, changes!.Count, 0);
            changes.Add(new TextChange(written.FullSpan.Start, written.FullSpan.Length, written.ToFullString()));
            return;
        }
        Changed(written, rewritten);
    }

    private void Changed(SyntaxToken written, SyntaxToken rewritten)
    {
        if (ReferenceEquals(written.Green, rewritten.Green))
            return;
        Tag(rewritten.Green, changes!.Count, 0);
        changes.Add(new TextChange(written.FullSpan.Start, written.FullSpan.Length, rewritten.ToFullString()));
    }

    private void Changed(SyntaxNode written, SyntaxNode? rewritten)
    {
        if (rewritten is not null && ReferenceEquals(written.Green, rewritten.Green))
            return;
        if (rewritten is not null)
            Tag(rewritten.Green, changes!.Count, 0);
        changes!.Add(new TextChange(
            written.FullSpan.Start, written.FullSpan.Length, rewritten?.ToFullString() ?? ""));
    }

    /// <summary>
    /// Remembers the annotated pieces of <paramref name="tree"/> that stand between the changes.
    /// The file is parsed again from the first change to the last, in one span, so a line the
    /// rewrite never wrote over is still read again where it lies between two that it did; what
    /// it carries crosses that reparse with the rest.
    /// </summary>
    /// <param name="tree">The file as it stands.</param>
    /// <param name="changes">What the rewrite writes, in source order.</param>
    /// <param name="written">Where each change's own text lands in the file it gives.</param>
    private void Between(SyntaxTree tree, List<TextChange> changes, int[] written)
    {
        if (changes.Count < 2 || !tree.Root.ContainsAnnotations)
            return;
        for (var i = 0; i + 1 < changes.Count; i++)
        {
            var from = changes[i].Start + changes[i].Length;
            var to = changes[i + 1].Start;
            if (to <= from)
                continue;

            // How far the text of this gap has moved: everything written before it, against
            // everything it stood after.
            var moved = written[i] + changes[i].NewText.Length - from;
            foreach (var piece in tree.Root.AnnotatedPieces())
            {
                var span = piece.FullSpan;
                if (span.Start < from || span.End > to)
                    continue;
                var carried = piece.AsNode() is { } inner ? inner.Green.Annotations : piece.AsToken().Green.Annotations;
                tagged!.Add(new Tagged(-1, span.Start + moved, span.Length, piece.Kind, piece.IsToken, carried));
            }
        }
    }

    /// <summary>
    /// Remembers every annotated piece of <paramref name="green"/>, with where it stands in the
    /// text the change is about to write. Only the subtrees that say they hold one are walked.
    /// </summary>
    /// <param name="green">What the change writes.</param>
    /// <param name="change">Which of the changes is being written.</param>
    /// <param name="at">Where <paramref name="green"/> starts in that change's text.</param>
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
    /// An annotated piece of what a rewrite writes, remembered so that the piece the reparse
    /// makes of that same text can be given the annotations back.
    /// </summary>
    /// <param name="Change">
    /// Which of the rewrite's changes writes it, or −1 for a piece that stands between two of
    /// them and is only read again, whose <paramref name="At"/> is already where it lands.
    /// </param>
    /// <param name="At">
    /// Where it starts: in that change's own text while the rewrite is collecting, and in the
    /// file once the change has been written into it.
    /// </param>
    /// <param name="Width">How wide it is, trivia included.</param>
    /// <param name="Kind">What the piece is.</param>
    /// <param name="IsToken">Whether it is a token rather than a node.</param>
    /// <param name="Annotations">What it carries.</param>
    private readonly record struct Tagged(
        int Change,
        int At,
        int Width,
        SyntaxKind Kind,
        bool IsToken,
        ImmutableArray<SyntaxAnnotation> Annotations);

    /// <summary>
    /// A rewrite that hands back the pieces it is given, annotated: a node or a token standing
    /// where an annotated one stood, and of the same kind, is the piece that was annotated.
    /// </summary>
    /// <param name="wanted">The annotated pieces to look for, all on one line.</param>
    private sealed class Reattacher(List<Tagged> wanted) : SyntaxRewriter
    {
        /// <inheritdoc/>
        public override SyntaxNode? Visit(SyntaxNode? node)
        {
            if (node is null)
                return null;

            // The piece is looked for before the walk goes down, because that is where the node
            // still stands in the file; what comes back is a node of its own with no place yet.
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

        /// <summary>The annotations wanted on the piece of <paramref name="kind"/> at <paramref name="span"/>.</summary>
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
