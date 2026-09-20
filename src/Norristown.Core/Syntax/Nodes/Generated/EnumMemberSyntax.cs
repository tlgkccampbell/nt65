// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>One member of an <c>.enum</c>: a name, or a name and the value it is given.</summary>
public sealed class EnumMemberSyntax : StatementSyntax
{
    internal EnumMemberSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The member's name.</summary>
    public SyntaxToken Name => ChildTokens[0];

    /// <summary>The <c>=</c>, or null.</summary>
    public SyntaxToken? EqualsToken => FirstToken(SyntaxKind.Equals);

    /// <summary>The value the member is given, or null.</summary>
    public ExpressionSyntax? Value => FirstNode<ExpressionSyntax>();

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitEnumMember(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitEnumMember(this);
}
