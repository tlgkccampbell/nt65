// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.CharmapEntrySyntax"/>.
/// One entry of a <c>.charmap</c>: a character, or a range of them, and a value.
/// </summary>
internal sealed class CharmapEntrySyntax : StatementSyntax
{
    private readonly ExpressionSyntax first;
    private readonly GreenToken? dotDotToken;
    private readonly ExpressionSyntax? last;
    private readonly GreenToken equalsToken;
    private readonly ExpressionSyntax value;

    internal CharmapEntrySyntax(
        ExpressionSyntax first,
        GreenToken? dotDotToken,
        ExpressionSyntax? last,
        GreenToken equalsToken,
        ExpressionSyntax value)
        : base(SyntaxKind.CharmapEntry, first.FullWidth + (dotDotToken?.FullWidth ?? 0) + (last?.FullWidth ?? 0) + equalsToken.FullWidth + value.FullWidth)
    {
        this.first = first;
        this.dotDotToken = dotDotToken;
        this.last = last;
        this.equalsToken = equalsToken;
        this.value = value;
    }

    /// <inheritdoc/>
    public override int SlotCount => 5;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.first,
        1 => this.dotDotToken,
        2 => this.last,
        3 => this.equalsToken,
        4 => this.value,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.CharmapEntrySyntax(tree, parent, this, position);
}
