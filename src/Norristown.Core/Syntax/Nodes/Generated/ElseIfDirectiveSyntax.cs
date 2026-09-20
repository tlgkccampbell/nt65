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
    public SyntaxToken CloseBraceToken => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <inheritdoc/>
    public override SyntaxToken Keyword =>
        Green is GreenSyntax ? FirstToken(SyntaxKind.Directive)!.Value : SlotToken(1);

    /// <inheritdoc/>
    public override ExpressionSyntax Condition =>
        Green is GreenSyntax ? FirstNode<ExpressionSyntax>()! : SlotNode<ExpressionSyntax>(2);

    /// <inheritdoc/>
    public override SyntaxToken? OpenBraceToken =>
        Green is GreenSyntax ? FirstToken(SyntaxKind.OpenBrace) : SlotToken(3);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitElseIfDirective(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitElseIfDirective(this);
}
