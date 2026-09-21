using System.Collections.Immutable;
using System.Text;

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
/// </summary>
public abstract partial class SyntaxRewriter : SyntaxVisitor<SyntaxNode>
{
    // The text a rewrite of a file, a block or a line has changed so far, in source order and
    // never overlapping: one per piece of a line that came back different. It is null except
    // while such a rewrite is running, which is also what tells a nested one to only collect.
    private List<TextChange>? changes;

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
        try
        {
            Collect(node);
            if (changes.Count == 0)
                return node;

            // One change, from the first piece that moved to the last, so that the file is parsed
            // once however many pieces the rewrite touched and the lines outside that range keep
            // the green nodes they have.
            return Corresponding(node, node.Tree.WithChange(Joined(node.Tree.Text, changes)));
        }
        finally
        {
            changes = null;
        }
    }

    /// <summary>
    /// The one change that makes all of <paramref name="changes"/>: from the start of the first
    /// to the end of the last, with the text between them as it stands where nothing moved.
    /// </summary>
    private static TextChange Joined(string text, List<TextChange> changes)
    {
        var start = changes[0].Start;
        var end = changes[^1].Start + changes[^1].Length;
        var built = new StringBuilder(end - start);
        var at = start;
        foreach (var change in changes)
        {
            built.Append(text, at, change.Start - at).Append(change.NewText);
            at = change.Start + change.Length;
        }
        return new TextChange(start, end - start, built.Append(text, at, end - at).ToString());
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
            var rewritten = Visit(line);
            if (!ReferenceEquals(rewritten, line))
                Changed(line, rewritten);
        }
    }

    /// <summary>What a rewrite changes on one line: its <c>.export</c>, its statement, what was left over, its break.</summary>
    private void CollectLine(LineSyntax line)
    {
        if (line.ExportKeyword is { } exported)
            Changed(exported, VisitToken(exported));
        Changed(line.Statement, Visit(line.Statement));
        if (line.SkippedTokens is { } left)
            Changed(left, Visit(left));
        Changed(line.EndOfLineToken, VisitToken(line.EndOfLineToken));
    }

    private void Changed(SyntaxToken written, SyntaxToken rewritten)
    {
        if (!ReferenceEquals(written.Green, rewritten.Green))
            changes!.Add(new TextChange(written.FullSpan.Start, written.FullSpan.Length, rewritten.ToFullString()));
    }

    private void Changed(SyntaxNode written, SyntaxNode? rewritten)
    {
        if (rewritten is not null && ReferenceEquals(written.Green, rewritten.Green))
            return;
        changes!.Add(new TextChange(
            written.FullSpan.Start, written.FullSpan.Length, rewritten?.ToFullString() ?? ""));
    }
}
