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

    /// <summary>The <c>:</c> before the address size, or null.</summary>
    public SyntaxToken? ColonToken => FirstToken(SyntaxKind.Colon);

    /// <summary>The <c>zp</c>, <c>abs</c> or <c>far</c>, or null.</summary>
    public SyntaxToken? AddressSize => TokenAfter(ColonToken) is { Kind: SyntaxKind.Identifier } size ? size : null;

    /// <summary>The attributes after the address size.</summary>
    public ImmutableArray<SegmentAttributeSyntax> Attributes => Nodes(ref attributes);
}
