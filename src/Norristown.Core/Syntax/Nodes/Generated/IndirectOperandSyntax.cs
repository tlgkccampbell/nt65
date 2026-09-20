// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>(expr)</c> or <c>(expr),y</c>.</summary>
public sealed class IndirectOperandSyntax : OperandSyntax
{
    internal IndirectOperandSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>(</c>.</summary>
    public SyntaxToken OpenParenToken => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The address of the pointer.</summary>
    public ExpressionSyntax Address =>
        Green is GreenSyntax ? (ExpressionSyntax)ChildNodes[0] : SlotNode<ExpressionSyntax>(1);

    /// <summary>The <c>)</c>.</summary>
    public SyntaxToken CloseParenToken => Green is GreenSyntax ? ChildTokens[1] : SlotToken(2);

    /// <summary>The <c>y</c> after the parentheses, or null.</summary>
    public SyntaxToken? IndexRegister => Green is GreenSyntax ? FirstToken(SyntaxKind.Register) : SlotTokenOrNull(4);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitIndirectOperand(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitIndirectOperand(this);
}
