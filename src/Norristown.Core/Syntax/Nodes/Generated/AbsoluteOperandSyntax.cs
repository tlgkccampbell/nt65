// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>expr</c>, <c>expr,x</c>, <c>z:expr</c>, or the <c>expr, expr</c> of a bit branch.</summary>
public sealed class AbsoluteOperandSyntax : OperandSyntax
{
    internal AbsoluteOperandSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>z:</c>, <c>a:</c>, <c>f:</c> or <c>d:</c>, or null.</summary>
    public AddressPrefixSyntax? Prefix =>
        Green is GreenSyntax ? FirstNode<AddressPrefixSyntax>() : SlotNodeOrNull<AddressPrefixSyntax>(0);

    /// <summary>The address.</summary>
    public ExpressionSyntax Address =>
        Green is GreenSyntax ? FirstNode<ExpressionSyntax>()! : SlotNode<ExpressionSyntax>(1);

    /// <summary>The <c>,</c>, or null.</summary>
    public SyntaxToken? CommaToken => Green is GreenSyntax ? FirstToken(SyntaxKind.Comma) : SlotTokenOrNull(2);

    /// <summary>The <c>x</c>, <c>y</c> or <c>s</c> after the <c>,</c>, or null.</summary>
    public SyntaxToken? IndexRegister => Green is GreenSyntax ? FirstToken(SyntaxKind.Register) : SlotTokenOrNull(3);

    /// <summary>The second expression of a bit branch, or null.</summary>
    public ExpressionSyntax? Second =>
        Green is GreenSyntax ? NodeAfter(CommaToken) as ExpressionSyntax : SlotNodeOrNull<ExpressionSyntax>(4);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitAbsoluteOperand(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitAbsoluteOperand(this);
}
