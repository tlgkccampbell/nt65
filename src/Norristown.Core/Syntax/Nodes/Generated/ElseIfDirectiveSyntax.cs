// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>} .elseif expr {</c>.</summary>
public sealed class ElseIfDirectiveSyntax : ConditionalDirectiveSyntax
{
    internal ElseIfDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>}</c> that closes the branch before.</summary>
    public SyntaxToken CloseBraceToken => ChildTokens[0];

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitElseIfDirective(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitElseIfDirective(this);
}
