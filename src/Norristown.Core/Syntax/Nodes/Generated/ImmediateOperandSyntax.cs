// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>#expr</c>, or the <c>#src, #dst</c> of a block move.</summary>
public sealed class ImmediateOperandSyntax : OperandSyntax
{
    internal ImmediateOperandSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>#</c>.</summary>
    public SyntaxToken HashToken => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The value.</summary>
    public ExpressionSyntax Value =>
        Green is GreenSyntax ? (ExpressionSyntax)ChildNodes[0] : SlotNode<ExpressionSyntax>(1);

    /// <summary>The <c>,</c> before a second value, or null.</summary>
    public SyntaxToken? CommaToken => Green is GreenSyntax ? FirstToken(SyntaxKind.Comma) : SlotTokenOrNull(2);

    /// <summary>The <c>#</c> of a second value, or null.</summary>
    public SyntaxToken? SecondHashToken => Green is GreenSyntax ? TokenAt(2) : SlotTokenOrNull(3);

    /// <summary>The second bank of a block move, or null.</summary>
    public ExpressionSyntax? SecondValue =>
        Green is GreenSyntax ? ChildNodes.Length > 1 ? (ExpressionSyntax)ChildNodes[1] : null : SlotNodeOrNull<ExpressionSyntax>(4);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitImmediateOperand(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitImmediateOperand(this);
}
