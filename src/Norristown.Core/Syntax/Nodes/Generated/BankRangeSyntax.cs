// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>$80</c> or <c>$00..$3f</c>: one bank or a range of them.</summary>
public sealed class BankRangeSyntax : SyntaxNode
{
    internal BankRangeSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The bank, or the first of the range.</summary>
    public ExpressionSyntax First =>
        Green is GreenSyntax ? (ExpressionSyntax)ChildNodes[0] : SlotNode<ExpressionSyntax>(0);

    /// <summary>The <c>..</c>, or null.</summary>
    public SyntaxToken? DotDotToken => Green is GreenSyntax ? FirstToken(SyntaxKind.DotDot) : SlotTokenOrNull(1);

    /// <summary>The last bank of the range, or null.</summary>
    public ExpressionSyntax? Last =>
        Green is GreenSyntax ? ChildNodes.Length > 1 ? (ExpressionSyntax)ChildNodes[1] : null : SlotNodeOrNull<ExpressionSyntax>(2);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitBankRange(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitBankRange(this);
}
