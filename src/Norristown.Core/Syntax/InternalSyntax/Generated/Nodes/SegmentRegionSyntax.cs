// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.SegmentRegionSyntax"/>.
/// <c>.segment NAME</c>: a region line, which puts what follows it in the segment.
/// </summary>
internal sealed class SegmentRegionSyntax : SegmentStatementSyntax
{
    private readonly GreenToken keyword;
    private readonly GreenToken name;

    internal SegmentRegionSyntax(
        GreenToken keyword,
        GreenToken name)
        : base(SyntaxKind.SegmentRegion, keyword.FullWidth + name.FullWidth)
    {
        this.keyword = keyword;
        this.name = name;
    }

    /// <inheritdoc/>
    public override int SlotCount => 2;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.keyword,
        1 => this.name,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.SegmentRegionSyntax(tree, parent, this, position);
}
