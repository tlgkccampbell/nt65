// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.StateSetItemSyntax"/>.
/// The name of a signature set, which stands for the items it was declared with.
/// </summary>
internal sealed class StateSetItemSyntax : StateItemSyntax
{
    private readonly GreenNode name;

    internal StateSetItemSyntax(GreenNode name)
        : base(SyntaxKind.StateSetItem, name.FullWidth)
    {
        this.name = name;
    }

    /// <inheritdoc/>
    public override int SlotCount => 1;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.name,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.StateSetItemSyntax(tree, parent, this, position);
}
