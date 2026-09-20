// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.StateKeepsItemSyntax"/>.
/// <c>keeps a, x</c>: the registers a routine leaves as it found them.
/// </summary>
internal sealed class StateKeepsItemSyntax : StateItemSyntax
{
    private readonly GreenToken name;
    private readonly GreenSeparatedList? registers;

    internal StateKeepsItemSyntax(
        GreenToken name,
        GreenSeparatedList? registers)
        : base(SyntaxKind.StateKeepsItem, name.FullWidth + (registers?.FullWidth ?? 0))
    {
        this.name = name;
        this.registers = registers;
    }

    /// <inheritdoc/>
    public override int SlotCount => 2;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => index switch
    {
        0 => this.name,
        1 => this.registers,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.StateKeepsItemSyntax(tree, parent, this, position);
}
