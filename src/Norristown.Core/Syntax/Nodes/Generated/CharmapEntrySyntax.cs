// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>One entry of a <c>.charmap</c>: a character, or a range of them, and a value.</summary>
public sealed class CharmapEntrySyntax : StatementSyntax
{
    internal CharmapEntrySyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The character, or the first of the range.</summary>
    public ExpressionSyntax First =>
        Green is GreenSyntax ? (ExpressionSyntax)ChildNodes[0] : SlotNode<ExpressionSyntax>(0);

    /// <summary>The <c>..</c>, or null.</summary>
    public SyntaxToken? DotDotToken => Green is GreenSyntax ? FirstToken(SyntaxKind.DotDot) : SlotTokenOrNull(1);

    /// <summary>The last character of the range, or null.</summary>
    public ExpressionSyntax? Last =>
        Green is GreenSyntax ? DotDotToken is null ? null : (ExpressionSyntax)ChildNodes[1] : SlotNodeOrNull<ExpressionSyntax>(2);

    /// <summary>The <c>=</c>, or null.</summary>
    public SyntaxToken? EqualsToken => Green is GreenSyntax ? FirstToken(SyntaxKind.Equals) : SlotToken(3);

    /// <summary>The value the first character maps to.</summary>
    public ExpressionSyntax Value =>
        Green is GreenSyntax ? (ExpressionSyntax)ChildNodes[DotDotToken is null ? 1 : 2] : SlotNode<ExpressionSyntax>(4);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitCharmapEntry(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitCharmapEntry(this);
}
