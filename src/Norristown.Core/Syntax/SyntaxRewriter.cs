using System.Collections.Immutable;

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
    // The run this rewrite is in the middle of, or null when it is not running over a file, a
    // block or a line. A line, a block or a file reached during a run only adds its changes to it.
    private RewriteSession? session;

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
        if (session is { } running)
        {
            running.Collect(node);
            return node;
        }

        session = new RewriteSession(this);
        try
        {
            return session.Run(node);
        }
        finally
        {
            session = null;
        }
    }
}
