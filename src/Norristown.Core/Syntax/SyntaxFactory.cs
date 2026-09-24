using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// Builds syntax without parsing it. The class has one method per kind of node, generated from
/// the same Syntax.xml rows as the node classes, plus methods for the tokens, trivia and lists
/// that nodes are built from. Code fixes and refactorings use it to create the pieces they insert
/// into a file.
/// <para>
/// A node built here belongs to no file. Each result is alone in a tree of its own, so its spans
/// and its text behave like those of a node in a file. A rewrite puts it into a file, whether
/// through <c>Update</c>, a <see cref="SyntaxRewriter"/> or <see cref="SyntaxNode.ReplaceNode"/>.
/// </para>
/// <para>
/// A token gets exactly the trivia it is given, and no method here adds a space. The spacing of a
/// rewritten piece is either kept from the piece it replaces, with
/// <see cref="SyntaxToken.WithTriviaFrom"/>, or added afterwards by
/// <see cref="SyntaxNode.NormalizeWhitespace"/>. The one exception is
/// <see cref="SeparatedList{T}(IEnumerable{T})"/>, whose commas come out as <c>, </c> because
/// there is nothing else in a list to take the spacing from.
/// </para>
/// </summary>
public static partial class SyntaxFactory
{
    /// <summary>Gets a single space, which separates two tokens that would otherwise run together.</summary>
    public static SyntaxTrivia Space => Whitespace(" ");

    /// <summary>
    /// Creates whitespace trivia of spaces or tabs, which have no meaning in the language but
    /// matter to a reader.
    /// </summary>
    /// <param name="text">The spaces and tabs.</param>
    /// <returns>The trivia.</returns>
    public static SyntaxTrivia Whitespace(string text) =>
        new(default, new GreenTrivia(SyntaxKind.WhitespaceTrivia, text), 0);

    /// <summary>
    /// Creates comment trivia from its text exactly as it appears in the file, including the
    /// <c>;</c>.
    /// </summary>
    /// <param name="text">The comment, starting with <c>;</c>.</param>
    /// <returns>The trivia.</returns>
    public static SyntaxTrivia Comment(string text) =>
        new(default, new GreenTrivia(SyntaxKind.CommentTrivia, text), 0);

    /// <summary>
    /// Creates a token of a <paramref name="kind"/> that has only one spelling, such as
    /// punctuation or an operator. A token whose text varies, such as a name, a number or a
    /// mnemonic, is built by the overload that takes the text.
    /// </summary>
    /// <param name="kind">The kind of token.</param>
    /// <returns>The token, with no trivia.</returns>
    public static SyntaxToken Token(SyntaxKind kind) =>
        Token(kind, SyntaxFacts.FixedText(kind)
            ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "the kind has no one spelling"));

    /// <summary>Creates a token of kind <paramref name="kind"/> with the text <paramref name="text"/>.</summary>
    /// <param name="kind">The kind of token.</param>
    /// <param name="text">The token's text, exactly as it would appear in the file.</param>
    /// <returns>The token, with no trivia.</returns>
    public static SyntaxToken Token(SyntaxKind kind, string text) => Token(kind, text, [], []);

    /// <summary>
    /// Creates a token of kind <paramref name="kind"/> with the text <paramref name="text"/> and
    /// the given trivia around it.
    /// </summary>
    /// <param name="kind">The kind of token.</param>
    /// <param name="text">The token's text, exactly as it would appear in the file.</param>
    /// <param name="leading">The trivia before the token.</param>
    /// <param name="trailing">The trivia after the token.</param>
    /// <returns>The token.</returns>
    public static SyntaxToken Token(
        SyntaxKind kind, string text, IEnumerable<SyntaxTrivia> leading, IEnumerable<SyntaxTrivia> trailing) =>
        Detached(new GreenToken(kind, text, Green(leading), Green(trailing), null));

    /// <summary>
    /// Creates a missing token of kind <paramref name="kind"/>. A missing token stands for a token
    /// that the syntax requires but the source omits, and it has no text, no trivia and no width.
    /// </summary>
    /// <param name="kind">The kind the token would have had.</param>
    /// <returns>The token.</returns>
    public static SyntaxToken MissingToken(SyntaxKind kind) => Detached(GreenToken.Missing(kind));

    /// <summary>Creates an identifier token.</summary>
    /// <param name="name">The name, as it appears in source.</param>
    /// <returns>The token.</returns>
    public static SyntaxToken Identifier(string name) => Token(SyntaxKind.Identifier, name);

    /// <summary>Creates a mnemonic token.</summary>
    /// <param name="name">The mnemonic, as it appears in source.</param>
    /// <returns>The token.</returns>
    public static SyntaxToken Mnemonic(string name) => Token(SyntaxKind.Mnemonic, name);

    /// <summary>Creates a <c>.name</c> directive token.</summary>
    /// <param name="name">The directive, including the <c>.</c>.</param>
    /// <returns>The token.</returns>
    public static SyntaxToken Directive(string name) => Token(SyntaxKind.Directive, name);

    /// <summary>
    /// Creates a number token with the text as it would appear in the file, such as <c>$1f</c>,
    /// <c>%1010</c> or <c>255</c>.
    /// </summary>
    /// <param name="text">The number, as it appears in source.</param>
    /// <returns>The token.</returns>
    public static SyntaxToken Number(string text) => Token(SyntaxKind.NumberLiteral, text);

    /// <summary>Creates the line break token that ends a line.</summary>
    /// <param name="text">The line break, which is <c>\n</c> unless the file uses another.</param>
    /// <returns>The token.</returns>
    public static SyntaxToken EndOfLine(string text = "\n") => Token(SyntaxKind.EndOfLine, text);

    /// <summary>Creates the items of a list-valued slot, with no separators between them.</summary>
    /// <typeparam name="T">The type of the items.</typeparam>
    /// <param name="items">The items, in source order.</param>
    /// <returns>The list, which is empty if there are no items.</returns>
    public static SyntaxList<T> List<T>(IEnumerable<T> items) where T : SyntaxNode
    {
        var children = Green(items);
        return children.IsEmpty ? default : new SyntaxList<T>(SyntaxTree.Detached(new GreenList(children)));
    }

    /// <summary>
    /// Creates a separated list with a <c>,</c> and a space between each pair of items. A list's
    /// commas are the one place where the factory adds a space of its own. There is no other
    /// trivia to take a space from, and a list without them runs together.
    /// </summary>
    /// <typeparam name="T">The type of the items.</typeparam>
    /// <param name="items">The items, in source order.</param>
    /// <returns>The list, which is empty if there are no items.</returns>
    public static SeparatedSyntaxList<T> SeparatedList<T>(IEnumerable<T> items) where T : SyntaxNode
    {
        var itemList = items as IReadOnlyList<T> ?? [.. items];
        return SeparatedList(itemList, Enumerable.Range(0, Math.Max(itemList.Count - 1, 0))
            .Select(_ => Token(SyntaxKind.Comma, ",", [], [Space])));
    }

    /// <summary>Creates a separated list from its items and the separators between them.</summary>
    /// <typeparam name="T">The type of the items.</typeparam>
    /// <param name="items">The items, in source order.</param>
    /// <param name="separators">
    /// The separators, one fewer than the items, or as many as the items to end the list with a
    /// separator.
    /// </param>
    /// <returns>The list, which is empty if there are no items.</returns>
    public static SeparatedSyntaxList<T> SeparatedList<T>(IEnumerable<T> items, IEnumerable<SyntaxToken> separators)
        where T : SyntaxNode
    {
        var itemList = items as IReadOnlyList<T> ?? [.. items];
        var between = separators as IReadOnlyList<SyntaxToken> ?? [.. separators];
        if (between.Count != itemList.Count && between.Count != itemList.Count - 1)
        {
            throw new ArgumentException(
                "a separated list has one fewer separator than items, or as many", nameof(separators));
        }
        if (itemList.Count == 0)
            return default;

        var children = ImmutableArray.CreateBuilder<GreenNode>(itemList.Count + between.Count);
        for (var i = 0; i < itemList.Count; i++)
        {
            children.Add(itemList[i].Green);
            if (i < between.Count)
                children.Add(between[i].Green);
        }
        return new SeparatedSyntaxList<T>(SyntaxTree.Detached(new GreenSeparatedList(children.ToImmutable())));
    }

    /// <summary>Creates a token list for a slot that holds a sequence of tokens.</summary>
    /// <param name="tokens">The tokens, in source order.</param>
    /// <returns>The list, which is empty if there are no tokens.</returns>
    public static SyntaxTokenList TokenList(params IEnumerable<SyntaxToken> tokens)
    {
        var children = ImmutableArray.CreateRange(tokens.Select(token => (GreenNode)token.Green));
        return children.IsEmpty ? default : new SyntaxTokenList(SyntaxTree.Detached(new GreenList(children)));
    }

    /// <summary>
    /// Returns a red token for <paramref name="built"/>, put in a one-item list so that it has a
    /// parent.
    /// </summary>
    /// <param name="built">The green token the factory just created.</param>
    internal static SyntaxToken Detached(GreenToken built) =>
        SyntaxTree.Detached(new GreenList([built])).SlotToken(0);

    /// <summary>Returns the red node for <paramref name="built"/>, in a tree of its own.</summary>
    /// <param name="built">The green node the factory just created.</param>
    private static SyntaxNode Detached(GreenNode built) => SyntaxTree.Detached(built);

    /// <summary>Returns the green nodes of <paramref name="items"/>, in order.</summary>
    private static ImmutableArray<GreenNode> Green<T>(IEnumerable<T> items) where T : SyntaxNode =>
        [.. items.Select(item => item.Green)];

    /// <summary>Returns the green trivia of <paramref name="trivia"/>, in order.</summary>
    private static ImmutableArray<GreenTrivia> Green(IEnumerable<SyntaxTrivia> trivia) =>
        [.. trivia.Select(one => one.Green)];
}
