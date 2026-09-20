// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
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
    public SyntaxToken OperatorToken => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>What the operator applies to.</summary>
    public ExpressionSyntax Operand =>
        Green is GreenSyntax ? (ExpressionSyntax)ChildNodes[0] : SlotNode<ExpressionSyntax>(1);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitUnaryExpression(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitUnaryExpression(this);
}
