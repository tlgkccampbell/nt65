// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>{}</c>: the default of a block parameter a call may leave out.</summary>
public sealed class EmptyBlockSyntax : SyntaxNode
{
    internal EmptyBlockSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>{</c>.</summary>
    public SyntaxToken OpenBraceToken => ChildTokens[0];

    /// <summary>The <c>}</c>.</summary>
    public SyntaxToken CloseBraceToken => ChildTokens[1];

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitEmptyBlock(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitEmptyBlock(this);
}
