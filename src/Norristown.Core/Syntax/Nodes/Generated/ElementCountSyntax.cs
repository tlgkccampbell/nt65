// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>[n]</c>, or <c>[]</c> for as many elements as the values given.</summary>
public sealed class ElementCountSyntax : SyntaxNode
{
    internal ElementCountSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>[</c>.</summary>
    public SyntaxToken OpenBracketToken => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The count, or null for <c>[]</c>.</summary>
    public ExpressionSyntax? Count =>
        Green is GreenSyntax ? FirstNode<ExpressionSyntax>() : SlotNodeOrNull<ExpressionSyntax>(1);

    /// <summary>The <c>]</c>, or null.</summary>
    public SyntaxToken? CloseBracketToken => Green is GreenSyntax ? FirstToken(SyntaxKind.CloseBracket) : SlotToken(2);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitElementCount(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitElementCount(this);
}
