// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>member = value</c>, in a braced record or on a line of its own in a multi-line initializer.</summary>
public sealed class MemberValueSyntax : StatementSyntax
{
    internal MemberValueSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The member's name.</summary>
    public SyntaxToken Name => ChildTokens[0];

    /// <summary>The <c>=</c>, or null.</summary>
    public SyntaxToken? EqualsToken => FirstToken(SyntaxKind.Equals);

    /// <summary>The value: an expression, or a braced list or record.</summary>
    public SyntaxNode Value => ChildNodes[0];

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitMemberValue(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitMemberValue(this);
}
