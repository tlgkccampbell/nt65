// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ValueListSyntax"/>.
/// <c>{ value, … }</c>: a list of elements.
/// </summary>
internal sealed class ValueListSyntax : GreenNode
{
    private readonly GreenToken openBraceToken;
    private readonly GreenSeparatedList? values;
    private readonly GreenToken closeBraceToken;

    internal ValueListSyntax(
        GreenToken openBraceToken,
        GreenSeparatedList? values,
        GreenToken closeBraceToken)
        : base(SyntaxKind.ValueList, openBraceToken.FullWidth + (values?.FullWidth ?? 0) + closeBraceToken.FullWidth)
    {
        this.openBraceToken = openBraceToken;
        this.values = values;
        this.closeBraceToken = closeBraceToken;
    }

    /// <inheritdoc/>
    public override int SlotCount => 3;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.openBraceToken,
        1 => this.values,
        2 => this.closeBraceToken,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ValueListSyntax(tree, parent, this, position);
}
