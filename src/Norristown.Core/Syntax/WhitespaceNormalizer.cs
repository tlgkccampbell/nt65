namespace Norristown.Syntax;

/// <summary>
/// Replaces all trivia under a node with standard spacing, for
/// <see cref="SyntaxNode.NormalizeWhitespace"/>. The spacing of every token is computed first
/// from the token that follows it, and the rewrite then puts each spaced token in place of the
/// original token it was computed from.
/// </summary>
internal sealed class WhitespaceNormalizer : SyntaxRewriter
{
    private readonly Dictionary<SyntaxToken, SyntaxToken> spaced;

    private WhitespaceNormalizer(Dictionary<SyntaxToken, SyntaxToken> spaced) => this.spaced = spaced;

    /// <summary>
    /// Returns a copy of <paramref name="node"/> in which all trivia is replaced by standard
    /// spacing. Standard spacing puts one space where two tokens would otherwise run together,
    /// and none elsewhere.
    /// </summary>
    /// <param name="node">The node to space.</param>
    /// <returns>The node, or the node it has become.</returns>
    public static SyntaxNode Normalize(SyntaxNode node)
    {
        var tokens = new List<(SyntaxToken Token, bool Tight)>();
        Flatten(node, tokens);
        var spaced = new Dictionary<SyntaxToken, SyntaxToken>(tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            var (token, tight) = tokens[i];
            SyntaxTrivia[] after = !tight && i + 1 < tokens.Count && Apart(token.Kind, tokens[i + 1].Token.Kind)
                ? [SyntaxFactory.Space]
                : [];
            spaced[token] = token.WithLeadingTrivia().WithTrailingTrivia(after);
        }
        return new WhitespaceNormalizer(spaced).Visit(node) ?? node;
    }

    /// <inheritdoc/>
    public override SyntaxToken VisitToken(SyntaxToken token) =>
        spaced.TryGetValue(token, out var replacement) ? replacement : token;

    /// <summary>
    /// Adds every token under <paramref name="node"/> to <paramref name="tokens"/>, in source
    /// order. Each token is paired with a value indicating whether its parent allows no space
    /// before the next token. nt65 puts no space between a prefix operator and its operand, inside
    /// an instruction operand, or inside an address prefix.
    /// </summary>
    private static void Flatten(SyntaxNode node, List<(SyntaxToken Token, bool Tight)> tokens)
    {
        var tight = node is UnaryExpressionSyntax or OperandSyntax or AddressPrefixSyntax;
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.AsNode() is { } inner)
                Flatten(inner, tokens);
            else
                tokens.Add((child.AsToken(), tight));
        }
    }

    /// <summary>
    /// Checks whether a space goes between a token of kind <paramref name="left"/> and a token of
    /// kind <paramref name="right"/>. Punctuation that binds tightly gets no space on its binding
    /// side, two adjacent words need a space to stay separate, and a binary operator gets a
    /// space on both sides.
    /// </summary>
    private static bool Apart(SyntaxKind left, SyntaxKind right)
    {
        if (right is SyntaxKind.EndOfLine or SyntaxKind.Comma or SyntaxKind.CloseParen or SyntaxKind.CloseBracket
            or SyntaxKind.Colon or SyntaxKind.ColonColon or SyntaxKind.OpenBracket or SyntaxKind.DotDot)
        {
            return false;
        }
        if (left is SyntaxKind.ColonColon or SyntaxKind.Hash or SyntaxKind.OpenParen or SyntaxKind.OpenBracket
            or SyntaxKind.DotDot or SyntaxKind.Bang or SyntaxKind.Tilde)
        {
            return false;
        }
        if (left is SyntaxKind.Colon or SyntaxKind.Comma or SyntaxKind.Mnemonic or SyntaxKind.CloseBrace)
            return true;
        if (left is SyntaxKind.Directive)
            return right != SyntaxKind.OpenParen;
        if (left is SyntaxKind.OpenBrace || right is SyntaxKind.OpenBrace or SyntaxKind.CloseBrace)
            return true;
        return Operator(left) || Operator(right) || (Word(left) && Word(right));
    }

    /// <summary>
    /// Checks whether <paramref name="kind"/> is a binary operator, which gets a space on both
    /// sides.
    /// </summary>
    private static bool Operator(SyntaxKind kind) => kind is SyntaxKind.Star or SyntaxKind.Slash
        or SyntaxKind.Plus or SyntaxKind.Minus or SyntaxKind.LessLess or SyntaxKind.GreaterGreater
        or SyntaxKind.Less or SyntaxKind.LessEquals or SyntaxKind.Greater or SyntaxKind.GreaterEquals
        or SyntaxKind.EqualsEquals or SyntaxKind.BangEquals or SyntaxKind.Ampersand
        or SyntaxKind.AmpersandAmpersand or SyntaxKind.Bar or SyntaxKind.BarBar or SyntaxKind.Caret
        or SyntaxKind.CaretCaret or SyntaxKind.Equals or SyntaxKind.Arrow;

    /// <summary>
    /// Checks whether <paramref name="kind"/> is a word-like token, which needs a space between it
    /// and another word to be read as a separate token.
    /// </summary>
    private static bool Word(SyntaxKind kind) => kind is SyntaxKind.Identifier or SyntaxKind.CheapLocal
        or SyntaxKind.Mnemonic or SyntaxKind.Register or SyntaxKind.Directive or SyntaxKind.NumberLiteral
        or SyntaxKind.CharacterLiteral or SyntaxKind.StringLiteral or SyntaxKind.CpuName or SyntaxKind.BadToken;
}
