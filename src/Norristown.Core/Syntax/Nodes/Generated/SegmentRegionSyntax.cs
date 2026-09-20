// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.segment NAME</c>: a region line, which puts what follows it in the segment.</summary>
public sealed class SegmentRegionSyntax : SegmentStatementSyntax
{
    internal SegmentRegionSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <inheritdoc/>
    public override SyntaxToken Keyword => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <inheritdoc/>
    public override SyntaxToken? Name =>
        Green is GreenSyntax ? TokenAt(1) is { Kind: SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic or SyntaxKind.StringLiteral } name ? name : null : SlotToken(1);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitSegmentRegion(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitSegmentRegion(this);
}
