using System.Collections.Immutable;
using Norristown.Syntax;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Tests.Syntax;

/// <summary>
/// Green nodes put together by hand, and the red nodes over them, for tests of the node, list
/// and token types themselves rather than of what the parser builds. The text the nodes are
/// made from is also parsed, so the tree they belong to holds that text at the same offsets
/// and every span reads as it would in a real file.
/// </summary>
internal static class HandBuilt
{
    /// <summary>The tokens of <paramref name="text"/>, lexed as one line, without its line break.</summary>
    public static ImmutableArray<GreenToken> Tokens(string text) =>
        [.. Lexer.LexLine(text).Tokens.Where(token => token.Kind != SyntaxKind.EndOfLine)];

    /// <summary>A <c>NameExpression</c> over one identifier, the simplest node an item can be.</summary>
    public static GreenNode Name(GreenToken identifier) => new GreenSyntax(SyntaxKind.NameExpression, [identifier]);

    /// <summary>
    /// The red node of <paramref name="kind"/> over <paramref name="children"/>, at the start
    /// of a file holding <paramref name="text"/>.
    /// </summary>
    public static SyntaxNode Node(string text, SyntaxKind kind, params GreenNode[] children) =>
        new GreenSyntax(kind, [.. children]).CreateRed(SyntaxTree.Parse("test.nt65", text), null, 0);

    /// <summary>
    /// The red node over <paramref name="green"/>, at the start of a file holding
    /// <paramref name="text"/>: the way a typed green node is read back.
    /// </summary>
    public static SyntaxNode Over(string text, GreenNode green) =>
        green.CreateRed(SyntaxTree.Parse("test.nt65", text), null, 0);

    /// <summary>The list in slot <paramref name="slot"/> of <paramref name="parent"/>.</summary>
    public static SyntaxList<T> List<T>(SyntaxNode parent, int slot) where T : SyntaxNode =>
        new(parent.SlotRed(slot));

    /// <summary>The separated list in slot <paramref name="slot"/> of <paramref name="parent"/>.</summary>
    public static SeparatedSyntaxList<T> SeparatedList<T>(SyntaxNode parent, int slot) where T : SyntaxNode =>
        new(parent.SlotRed(slot));

    /// <summary>The token list in slot <paramref name="slot"/> of <paramref name="parent"/>.</summary>
    public static SyntaxTokenList TokenList(SyntaxNode parent, int slot) => new(parent.SlotRed(slot));
}
