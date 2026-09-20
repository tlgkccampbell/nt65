// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.DataValuesSyntax"/>.
/// One line of a data body: values separated by commas, one element each.
/// </summary>
internal sealed class DataValuesSyntax : StatementSyntax
{
    private readonly GreenSeparatedList? values;

    internal DataValuesSyntax(
        GreenSeparatedList? values)
        : base(SyntaxKind.DataValues, (values?.FullWidth ?? 0))
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
        new Red.DataValuesSyntax(tree, parent, this, position);
}
