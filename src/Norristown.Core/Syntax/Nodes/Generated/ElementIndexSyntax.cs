// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>[i]</c> after a name: which element of a counted declaration it stands for.</summary>
public sealed class ElementIndexSyntax : SyntaxNode
{
    internal ElementIndexSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>[</c>.</summary>
    public SyntaxToken OpenBracketToken => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The element, or null.</summary>
    public ExpressionSyntax? Index =>
        Green is GreenSyntax ? FirstNode<ExpressionSyntax>() : SlotNode<ExpressionSyntax>(1);

    /// <summary>The <c>]</c>, or null.</summary>
    public SyntaxToken? CloseBracketToken => Green is GreenSyntax ? FirstToken(SyntaxKind.CloseBracket) : SlotToken(2);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitElementIndex(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitElementIndex(this);
}
