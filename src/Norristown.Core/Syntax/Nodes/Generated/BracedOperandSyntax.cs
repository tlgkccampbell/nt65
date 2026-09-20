// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>{buf,x}</c>: a whole operand as a macro argument.</summary>
public sealed class BracedOperandSyntax : SyntaxNode
{
    internal BracedOperandSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>{</c>.</summary>
    public SyntaxToken OpenBraceToken => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The operand.</summary>
    public OperandSyntax Operand => Green is GreenSyntax ? (OperandSyntax)ChildNodes[0] : SlotNode<OperandSyntax>(1);

    /// <summary>The <c>}</c>, or null.</summary>
    public SyntaxToken? CloseBraceToken => Green is GreenSyntax ? FirstToken(SyntaxKind.CloseBrace) : SlotToken(2);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitBracedOperand(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitBracedOperand(this);
}
