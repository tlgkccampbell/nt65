// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>(expr,x)</c> or <c>(expr,s),y</c>.</summary>
public sealed class IndexedIndirectOperandSyntax : OperandSyntax
{
    internal IndexedIndirectOperandSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>(</c>.</summary>
    public SyntaxToken OpenParenToken => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The address of the pointer.</summary>
    public ExpressionSyntax Address =>
        Green is GreenSyntax ? (ExpressionSyntax)ChildNodes[0] : SlotNode<ExpressionSyntax>(1);

    /// <summary>The <c>x</c> or <c>s</c> inside the parentheses.</summary>
    public SyntaxToken InnerRegister => Green is GreenSyntax ? ChildTokens[2] : SlotToken(3);

    /// <summary>The <c>)</c>.</summary>
    public SyntaxToken CloseParenToken => Green is GreenSyntax ? ChildTokens[3] : SlotToken(4);

    /// <summary>The <c>y</c> after the parentheses, or null.</summary>
    public SyntaxToken? OuterRegister => Green is GreenSyntax ? TokenAt(5) : SlotTokenOrNull(6);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitIndexedIndirectOperand(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitIndexedIndirectOperand(this);
}
