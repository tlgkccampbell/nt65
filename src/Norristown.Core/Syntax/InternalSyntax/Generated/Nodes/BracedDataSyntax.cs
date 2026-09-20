// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.BracedDataSyntax"/>.
/// The one braced value of a counted or record data directive, written on its line.
/// </summary>
internal sealed class BracedDataSyntax : DataTailSyntax
{
    private readonly GreenNode value;

    internal BracedDataSyntax(GreenNode value)
        : base(SyntaxKind.BracedData, value.FullWidth)
    {
        this.value = value;
    }

    /// <inheritdoc/>
    public override int SlotCount => 1;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.value,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.BracedDataSyntax(tree, parent, this, position);
}
