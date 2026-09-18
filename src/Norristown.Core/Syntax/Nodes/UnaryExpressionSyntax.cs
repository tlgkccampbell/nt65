using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>op operand</c>.</summary>
public sealed class UnaryExpressionSyntax : ExpressionSyntax
{
    internal UnaryExpressionSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The operator.</summary>
    public SyntaxToken OperatorToken => ChildTokens[0];

    /// <summary>What the operator applies to.</summary>
    public ExpressionSyntax Operand => (ExpressionSyntax)ChildNodes[0];
}
