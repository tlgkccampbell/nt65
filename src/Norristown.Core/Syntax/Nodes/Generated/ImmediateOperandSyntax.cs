// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
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
    public SyntaxToken HashToken => ChildTokens[0];

    /// <summary>The value.</summary>
    public ExpressionSyntax Value => (ExpressionSyntax)ChildNodes[0];

    /// <summary>The <c>,</c> before a second value, or null.</summary>
    public SyntaxToken? CommaToken => FirstToken(SyntaxKind.Comma);

    /// <summary>The <c>#</c> of a second value, or null.</summary>
    public SyntaxToken? SecondHashToken => TokenAt(2);

    /// <summary>The second bank of a block move, or null.</summary>
    public ExpressionSyntax? SecondValue => ChildNodes.Length > 1 ? (ExpressionSyntax)ChildNodes[1] : null;

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitImmediateOperand(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitImmediateOperand(this);
}
