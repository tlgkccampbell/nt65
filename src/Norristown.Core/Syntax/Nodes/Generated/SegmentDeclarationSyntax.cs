// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.segment NAME: size</c>, with its attributes.</summary>
public sealed class SegmentDeclarationSyntax : SegmentStatementSyntax
{
    private ImmutableArray<SegmentAttributeSyntax> attributes;

    internal SegmentDeclarationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <inheritdoc/>
    public override SyntaxToken Keyword => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <inheritdoc/>
    public override SyntaxToken? Name =>
        Green is GreenSyntax ? TokenAt(1) is { Kind: SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic or SyntaxKind.StringLiteral } name ? name : null : SlotToken(1);

    /// <summary>The <c>:</c> before the address size, or null.</summary>
    public SyntaxToken? ColonToken => Green is GreenSyntax ? FirstToken(SyntaxKind.Colon) : SlotToken(2);

    /// <summary>The <c>zp</c>, <c>abs</c> or <c>far</c>, or null.</summary>
    public SyntaxToken? AddressSize =>
        Green is GreenSyntax ? TokenAfter(ColonToken) is { Kind: SyntaxKind.Identifier } size ? size : null : SlotToken(3);

    /// <summary>The attributes after the address size.</summary>
    public ImmutableArray<SegmentAttributeSyntax> Attributes => Nodes(ref attributes);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitSegmentDeclaration(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitSegmentDeclaration(this);
}
