// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.StateListSyntax"/>.
/// Processor-state items separated by commas.
/// </summary>
internal sealed class StateListSyntax : GreenNode
{
    private readonly GreenSeparatedList? items;

    internal StateListSyntax(GreenSeparatedList? items)
        : base(SyntaxKind.StateList, (items?.FullWidth ?? 0))
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
        new Red.StateListSyntax(tree, parent, this, position);
}
