// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
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
    public ExpressionSyntax Left =>
        Green is GreenSyntax ? (ExpressionSyntax)ChildNodes[0] : SlotNode<ExpressionSyntax>(0);

    /// <summary>The operator.</summary>
    public SyntaxToken OperatorToken => Green is GreenSyntax ? ChildTokens[0] : SlotToken(1);

    /// <summary>The right operand.</summary>
    public ExpressionSyntax Right =>
        Green is GreenSyntax ? (ExpressionSyntax)ChildNodes[1] : SlotNode<ExpressionSyntax>(2);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitBinaryExpression(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitBinaryExpression(this);
}
