// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.SegmentDeclarationSyntax"/>.
/// <c>.segment NAME: size</c>, with its attributes.
/// </summary>
internal sealed class SegmentDeclarationSyntax : SegmentStatementSyntax
{
    private readonly GreenToken keyword;
    private readonly GreenToken name;
    private readonly GreenToken colonToken;
    private readonly GreenToken addressSize;
    private readonly GreenToken? commaToken;
    private readonly GreenSeparatedList? attributes;

    internal SegmentDeclarationSyntax(
        GreenToken keyword,
        GreenToken name,
        GreenToken colonToken,
        GreenToken addressSize,
        GreenToken? commaToken,
        GreenSeparatedList? attributes)
        : base(SyntaxKind.SegmentDeclaration, keyword.FullWidth + name.FullWidth + colonToken.FullWidth + addressSize.FullWidth + (commaToken?.FullWidth ?? 0) + (attributes?.FullWidth ?? 0))
    {
        this.keyword = keyword;
        this.name = name;
        this.colonToken = colonToken;
        this.addressSize = addressSize;
        this.commaToken = commaToken;
        this.attributes = attributes;
    }

    /// <inheritdoc/>
    public override int SlotCount => 6;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.name,
        2 => this.colonToken,
        3 => this.addressSize,
        4 => this.commaToken,
        5 => this.attributes,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.SegmentDeclarationSyntax(tree, parent, this, position);
}
