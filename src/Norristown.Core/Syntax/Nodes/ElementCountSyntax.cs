using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>[n]</c>, or <c>[]</c> for as many elements as the values given.</summary>
public sealed class ElementCountSyntax : SyntaxNode
{
    internal ElementCountSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>[</c>.</summary>
    public SyntaxToken OpenBracketToken => ChildTokens[0];

    /// <summary>The count, or null for <c>[]</c>.</summary>
    public ExpressionSyntax? Count => FirstNode<ExpressionSyntax>();

    /// <summary>The <c>]</c>, or null.</summary>
    public SyntaxToken? CloseBracketToken => FirstToken(SyntaxKind.CloseBracket);
}
