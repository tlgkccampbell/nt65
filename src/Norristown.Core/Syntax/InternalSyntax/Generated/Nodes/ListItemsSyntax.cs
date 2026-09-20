// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.ListItemsSyntax"/>.
/// One line of a <c>.list</c>, which holds one or more comma-separated items.
/// </summary>
internal sealed class ListItemsSyntax : StatementSyntax
{
    private readonly GreenSeparatedList? items;

    internal ListItemsSyntax(GreenSeparatedList? items)
        : base(SyntaxKind.ListItems, (items?.FullWidth ?? 0))
    {
        this.items = items;
    }

    /// <inheritdoc/>
    public override int SlotCount => 1;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.items,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.ListItemsSyntax(tree, parent, this, position);
}
