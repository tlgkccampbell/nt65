// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Red = Norristown.Syntax;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The green node of <see cref="Norristown.Syntax.StateItemSyntax"/>.
/// One processor-state item: <c>a16</c>, <c>i*</c>, <c>dp = 0</c>, <c>args 2</c>, <c>inline .strz</c>, <c>keeps a, x</c>,
/// <c>?</c> on its own, or the name of a signature set.
/// </summary>
internal class StateItemSyntax : GreenNode
{
    internal StateItemSyntax()
        : base(SyntaxKind.StateItem, 0)
    {
    }

    private protected StateItemSyntax(SyntaxKind kind, int fullWidth) : base(kind, fullWidth)
    {
    }

    /// <inheritdoc/>
    public override int SlotCount => 0;

    /// <inheritdoc/>
    public override GreenNode? GetSlot(int index) => throw new ArgumentOutOfRangeException(nameof(index));

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) =>
        new Red.StateItemSyntax(tree, parent, this, position);
}
