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
    public SyntaxToken OpenBraceToken => ChildTokens[0];

    /// <summary>The operand.</summary>
    public OperandSyntax Operand => (OperandSyntax)ChildNodes[0];

    /// <summary>The <c>}</c>, or null.</summary>
    public SyntaxToken? CloseBraceToken => FirstToken(SyntaxKind.CloseBrace);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitBracedOperand(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitBracedOperand(this);
}
