// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.SegmentAttributeSyntax"/>.
/// <c>dp = expr</c>, <c>bank = expr</c> or <c>mirrors = [$00..$3f, $80..$bf]</c>.
/// </summary>
internal sealed class SegmentAttributeSyntax : GreenNode
{
    private readonly GreenToken name;
    private readonly GreenToken equalsToken;
    private readonly GreenNode? value;
    private readonly GreenToken? openBracketToken;
    private readonly GreenSeparatedList? ranges;
    private readonly GreenToken? closeBracketToken;

    internal SegmentAttributeSyntax(
        GreenToken name,
        GreenToken equalsToken,
        GreenNode? value,
        GreenToken? openBracketToken,
        GreenSeparatedList? ranges,
        GreenToken? closeBracketToken)
        : base(SyntaxKind.SegmentAttribute, name.FullWidth + equalsToken.FullWidth + (value?.FullWidth ?? 0) + (openBracketToken?.FullWidth ?? 0) + (ranges?.FullWidth ?? 0) + (closeBracketToken?.FullWidth ?? 0))
    {
        this.name = name;
        this.equalsToken = equalsToken;
        this.value = value;
        this.openBracketToken = openBracketToken;
        this.ranges = ranges;
        this.closeBracketToken = closeBracketToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 6;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.name,
        1 => this.equalsToken,
        2 => this.value,
        3 => this.openBracketToken,
        4 => this.ranges,
        5 => this.closeBracketToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.SegmentAttributeSyntax(tree, parent, this, position);
}
