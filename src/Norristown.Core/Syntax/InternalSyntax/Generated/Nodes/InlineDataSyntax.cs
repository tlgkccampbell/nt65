// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.InlineDataSyntax"/>.
/// The values of a data directive, written on its own line after the element type.
/// </summary>
internal sealed class InlineDataSyntax : DataTailSyntax
{
    private readonly GreenSeparatedList? values;

    internal InlineDataSyntax(
        GreenSeparatedList? values)
        : base(SyntaxKind.InlineData, (values?.FullWidth ?? 0))
    {
        this.values = values;
    }

    /// <inheritdoc/>
    public override int SlotCount => 1;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.values,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.InlineDataSyntax(tree, parent, this, position);
}
