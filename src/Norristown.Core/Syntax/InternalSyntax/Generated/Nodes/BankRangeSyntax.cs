// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.BankRangeSyntax"/>.
/// <c>$80</c> or <c>$00..$3f</c>: one bank or a range of them.
/// </summary>
internal sealed class BankRangeSyntax : GreenNode
{
    private readonly ExpressionSyntax first;
    private readonly GreenToken? dotDotToken;
    private readonly ExpressionSyntax? last;

    internal BankRangeSyntax(
        ExpressionSyntax first,
        GreenToken? dotDotToken,
        ExpressionSyntax? last)
        : base(SyntaxKind.BankRange, first.FullWidth + (dotDotToken?.FullWidth ?? 0) + (last?.FullWidth ?? 0))
    {
        this.first = first;
        this.dotDotToken = dotDotToken;
        this.last = last;
    }

    /// <inheritdoc/>
    public override int SlotCount => 3;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.first,
        1 => this.dotDotToken,
        2 => this.last,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.BankRangeSyntax(tree, parent, this, position);
}
