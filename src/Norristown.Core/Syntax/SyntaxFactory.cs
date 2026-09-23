using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// Builds syntax rather than parsing it: one method per kind of node, generated from the same
/// Syntax.xml rows as the node classes, plus the tokens, trivia and lists that nodes are built
/// from. Code fixes and refactorings use it to make the pieces they insert into a file.
/// <para>
/// What is built here belongs to no file. Each result sits alone in a tree of its own, so its
/// spans and its text behave like those of a node in a file; a rewrite — <c>Update</c>, a
/// <see cref="SyntaxRewriter"/>, <see cref="SyntaxNode.ReplaceNode"/> — is what puts it into one.
/// </para>
/// <para>
/// A token gets exactly the trivia it is given. Nothing here invents a space: the spacing of a
/// rewritten piece is either kept from what it replaces, with
/// <see cref="SyntaxToken.WithTriviaFrom"/>, or added afterwards by
/// <see cref="SyntaxNode.NormalizeWhitespace"/>. The one exception is
/// <see cref="SeparatedList{T}(IEnumerable{T})"/>, whose commas are written <c>, </c> because
/// there is nothing else in a list to take the spacing from.
/// </para>
/// </summary>
public static partial class SyntaxFactory
{
    /// <summary>One space, to separate two tokens that would otherwise run together.</summary>
    public static SyntaxTrivia Space => Whitespace(" ");

    /// <summary>Spaces or tabs, which mean nothing to the language and everything to a reader.</summary>
    /// <param name="text">The spaces and tabs.</param>
    /// <returns>The trivia.</returns>
    public static SyntaxTrivia Whitespace(string text) =>
        new(default, new GreenTrivia(SyntaxKind.WhitespaceTrivia, text), 0);

    /// <summary>A comment, written as it stands in the file, <c>;</c> and all.</summary>
    /// <param name="text">The comment, starting with <c>;</c>.</param>
    /// <returns>The trivia.</returns>
    public static SyntaxTrivia Comment(string text) =>
        new(default, new GreenTrivia(SyntaxKind.CommentTrivia, text), 0);

    /// <summary>
    /// A token of a <paramref name="kind"/> that has only one spelling: punctuation and
    /// operators. A token whose text varies — a name, a number, a mnemonic — is built by the
    /// overload that takes the text.
    /// </summary>
    /// <param name="kind">What the token is.</param>
    /// <returns>The token, with no trivia.</returns>
    public static SyntaxToken Token(SyntaxKind kind) =>
        Token(kind, SyntaxFacts.FixedText(kind)
            ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "the kind has no one spelling"));

    /// <summary>A token of <paramref name="kind"/> written as <paramref name="text"/>.</summary>
    /// <param name="kind">What the token is.</param>
    /// <param name="text">Its text, exactly as it would stand in the file.</param>
    /// <returns>The token, with no trivia.</returns>
    public static SyntaxToken Token(SyntaxKind kind, string text) => Token(kind, text, [], []);

    /// <summary>A token of <paramref name="kind"/> with the trivia around it.</summary>
    /// <param name="kind">What the token is.</param>
    /// <param name="text">Its text, exactly as it would stand in the file.</param>
    /// <param name="leading">The trivia before it.</param>
    /// <param name="trailing">The trivia after it.</param>
    /// <returns>The token.</returns>
    public static SyntaxToken Token(
        SyntaxKind kind, string text, IEnumerable<SyntaxTrivia> leading, IEnumerable<SyntaxTrivia> trailing) =>
        Detached(new GreenToken(kind, text, Green(leading), Green(trailing), null));

    /// <summary>
    /// A missing token of <paramref name="kind"/>: one the syntax requires but the source does
    /// not write, with no text, no trivia and no width.
    /// </summary>
    /// <param name="kind">What the token would have been.</param>
    /// <returns>The token.</returns>
    public static SyntaxToken MissingToken(SyntaxKind kind) => Detached(GreenToken.Missing(kind));

    /// <summary>A name.</summary>
    /// <param name="name">The name, as it is written.</param>
    /// <returns>The token.</returns>
    public static SyntaxToken Identifier(string name) => Token(SyntaxKind.Identifier, name);

    /// <summary>A mnemonic.</summary>
    /// <param name="name">The mnemonic, as it is written.</param>
    /// <returns>The token.</returns>
    public static SyntaxToken Mnemonic(string name) => Token(SyntaxKind.Mnemonic, name);

    /// <summary>A <c>.name</c> directive.</summary>
    /// <param name="name">The directive, <c>.</c> and all.</param>
    /// <returns>The token.</returns>
    public static SyntaxToken Directive(string name) => Token(SyntaxKind.Directive, name);

    /// <summary>A number, written as the file would write it: <c>$1f</c>, <c>%1010</c> or <c>255</c>.</summary>
    /// <param name="text">The number, as it is written.</param>
    /// <returns>The token.</returns>
    public static SyntaxToken Number(string text) => Token(SyntaxKind.NumberLiteral, text);

    /// <summary>The line break that ends a line.</summary>
    /// <param name="text">The break, <c>\n</c> unless the file is written with another.</param>
    /// <returns>The token.</returns>
    public static SyntaxToken EndOfLine(string text = "\n") => Token(SyntaxKind.EndOfLine, text);

    /// <summary>The items of a list-valued slot, with nothing between them.</summary>
    /// <typeparam name="T">What the items are.</typeparam>
    /// <param name="items">The items, in source order.</param>
    /// <returns>The list, which is the empty one when there are no items.</returns>
    public static SyntaxList<T> List<T>(IEnumerable<T> items) where T : SyntaxNode
    {
        var children = Green(items);
        return children.IsEmpty ? default : new SyntaxList<T>(SyntaxTree.Detached(new GreenList(children)));
    }

    /// <summary>
    /// The items of a list written with a separator between them, with a <c>,</c> and a space
    /// between each two. A list's commas are the one place the factory adds a space of its own:
    /// there is no other trivia to take one from, and a list written without them runs together.
    /// </summary>
    /// <typeparam name="T">What the items are.</typeparam>
    /// <param name="items">The items, in source order.</param>
    /// <returns>The list, which is the empty one when there are no items.</returns>
    public static SeparatedSyntaxList<T> SeparatedList<T>(IEnumerable<T> items) where T : SyntaxNode
    {
        var written = items as IReadOnlyList<T> ?? [.. items];
        return SeparatedList(written, Enumerable.Range(0, Math.Max(written.Count - 1, 0))
            .Select(_ => Token(SyntaxKind.Comma, ",", [], [Space])));
    }

    /// <summary>The items of a separated list and the separators written between them.</summary>
    /// <typeparam name="T">What the items are.</typeparam>
    /// <param name="items">The items, in source order.</param>
    /// <param name="separators">One fewer than the items, or as many to end the list with a separator.</param>
    /// <returns>The list, which is the empty one when there are no items.</returns>
    public static SeparatedSyntaxList<T> SeparatedList<T>(IEnumerable<T> items, IEnumerable<SyntaxToken> separators)
        where T : SyntaxNode
    {
        var written = items as IReadOnlyList<T> ?? [.. items];
        var between = separators as IReadOnlyList<SyntaxToken> ?? [.. separators];
        if (between.Count != written.Count && between.Count != written.Count - 1)
        {
            throw new ArgumentException(
                "a separated list has one fewer separator than items, or as many", nameof(separators));
        }
        if (written.Count == 0)
            return default;

        var children = ImmutableArray.CreateBuilder<GreenNode>(written.Count + between.Count);
        for (var i = 0; i < written.Count; i++)
        {
            children.Add(written[i].Green);
            if (i < between.Count)
                children.Add(between[i].Green);
        }
        return new SeparatedSyntaxList<T>(SyntaxTree.Detached(new GreenSeparatedList(children.ToImmutable())));
    }

    /// <summary>A token list, for a slot that holds a sequence of tokens.</summary>
    /// <param name="tokens">The tokens, in source order.</param>
    /// <returns>The list, which is the empty one when there are no tokens.</returns>
    public static SyntaxTokenList TokenList(params IEnumerable<SyntaxToken> tokens)
    {
        var children = ImmutableArray.CreateRange(tokens.Select(token => (GreenNode)token.Green));
        return children.IsEmpty ? default : new SyntaxTokenList(SyntaxTree.Detached(new GreenList(children)));
    }

    /// <summary>The red node for <paramref name="built"/>, in a tree of its own.</summary>
    /// <param name="built">The green node the factory just made.</param>
    private static SyntaxNode Detached(GreenNode built) => SyntaxTree.Detached(built);

    /// <summary>A red token for <paramref name="built"/>, placed in a one-item list so that it has a parent.</summary>
    /// <param name="built">The green token the factory just made.</param>
    internal static SyntaxToken Detached(GreenToken built) =>
        SyntaxTree.Detached(new GreenList([built])).SlotToken(0);

    /// <summary>The green nodes of <paramref name="items"/>, in order.</summary>
    private static ImmutableArray<GreenNode> Green<T>(IEnumerable<T> items) where T : SyntaxNode =>
        [.. items.Select(item => item.Green)];

    /// <summary>The green trivia of <paramref name="trivia"/>, in order.</summary>
    private static ImmutableArray<GreenTrivia> Green(IEnumerable<SyntaxTrivia> trivia) =>
        [.. trivia.Select(one => one.Green)];
}
