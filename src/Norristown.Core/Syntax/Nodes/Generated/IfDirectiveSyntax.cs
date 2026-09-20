// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.if expr {</c>.</summary>
public sealed class IfDirectiveSyntax : ConditionalDirectiveSyntax
{
    internal IfDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <inheritdoc/>
    public override SyntaxToken Keyword =>
        Green is GreenSyntax ? FirstToken(SyntaxKind.Directive)!.Value : SlotToken(0);

    /// <inheritdoc/>
    public override ExpressionSyntax Condition =>
        Green is GreenSyntax ? FirstNode<ExpressionSyntax>()! : SlotNode<ExpressionSyntax>(1);

    /// <inheritdoc/>
    public override SyntaxToken? OpenBraceToken =>
        Green is GreenSyntax ? FirstToken(SyntaxKind.OpenBrace) : SlotToken(2);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitIfDirective(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitIfDirective(this);
}
