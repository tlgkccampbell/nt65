using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>[i]</c> after a name: which element of a counted declaration it stands for.</summary>
public sealed class ElementIndexSyntax : SyntaxNode
{
    internal ElementIndexSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>[</c>.</summary>
    public SyntaxToken OpenBracketToken => ChildTokens[0];

    /// <summary>The element, or null.</summary>
    public ExpressionSyntax? Index => FirstNode<ExpressionSyntax>();

    /// <summary>The <c>]</c>, or null.</summary>
    public SyntaxToken? CloseBracketToken => FirstToken(SyntaxKind.CloseBracket);
}
