using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>left op right</c>.</summary>
public sealed class BinaryExpressionSyntax : ExpressionSyntax
{
    internal BinaryExpressionSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The left operand.</summary>
    public ExpressionSyntax Left => (ExpressionSyntax)ChildNodes[0];

    /// <summary>The operator.</summary>
    public SyntaxToken OperatorToken => ChildTokens[0];

    /// <summary>The right operand.</summary>
    public ExpressionSyntax Right => (ExpressionSyntax)ChildNodes[1];
}
