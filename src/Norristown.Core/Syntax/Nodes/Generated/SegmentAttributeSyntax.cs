// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>dp = expr</c>, <c>bank = expr</c> or <c>mirrors = [$00..$3f, $80..$bf]</c>.</summary>
public sealed class SegmentAttributeSyntax : SyntaxNode
{
    private ImmutableArray<BankRangeSyntax> ranges;

    internal SegmentAttributeSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>dp</c>, <c>bank</c> or <c>mirrors</c>, or null.</summary>
    public SyntaxToken? Name => Green is GreenSyntax ? TokenAt(0) : SlotToken(0);

    /// <summary>The <c>=</c>, or null.</summary>
    public SyntaxToken? EqualsToken => Green is GreenSyntax ? FirstToken(SyntaxKind.Equals) : SlotToken(1);

    /// <summary>The value of a <c>dp</c> or a <c>bank</c>, or null.</summary>
    public ExpressionSyntax? Value =>
        Green is GreenSyntax ? FirstNode<ExpressionSyntax>() : SlotNodeOrNull<ExpressionSyntax>(2);

    /// <summary>The <c>[</c> of a <c>mirrors</c>, or null.</summary>
    public SyntaxToken? OpenBracketToken =>
        Green is GreenSyntax ? FirstToken(SyntaxKind.OpenBracket) : SlotTokenOrNull(3);

    /// <summary>The banks of a <c>mirrors</c>.</summary>
    public ImmutableArray<BankRangeSyntax> Ranges => Nodes(ref ranges);

    /// <summary>The <c>]</c> of a <c>mirrors</c>, or null.</summary>
    public SyntaxToken? CloseBracketToken =>
        Green is GreenSyntax ? FirstToken(SyntaxKind.CloseBracket) : SlotTokenOrNull(5);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitSegmentAttribute(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitSegmentAttribute(this);
}
