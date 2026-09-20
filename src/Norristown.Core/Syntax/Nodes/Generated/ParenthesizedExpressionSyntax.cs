// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>(expr)</c>.</summary>
public sealed class ParenthesizedExpressionSyntax : ExpressionSyntax
{
    internal ParenthesizedExpressionSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>(</c>.</summary>
    public SyntaxToken OpenParenToken => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The expression in the parentheses.</summary>
    public ExpressionSyntax Expression =>
        Green is GreenSyntax ? (ExpressionSyntax)ChildNodes[0] : SlotNode<ExpressionSyntax>(1);

    /// <summary>The <c>)</c>, or null.</summary>
    public SyntaxToken? CloseParenToken => Green is GreenSyntax ? FirstToken(SyntaxKind.CloseParen) : SlotToken(2);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitParenthesizedExpression(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitParenthesizedExpression(this);
}
